using System.Net;
using System.Net.Sockets;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// 每连接客户端泵（自 EntranceService.HandleClientAsync 封装迁入，
/// docs/design/07 §11）：监听连接的完整生命周期——会话装配、WhenAny
/// 定时等待链（副本选择/死亡/翻牌/会员到期四点）、命令派发、跨会话
/// 推送与断开落盘。依赖由组合根在 ExecuteAsync 装配后一次性注入。
/// </summary>
public sealed class ClientWhenAnyHandler(
    ServerOptions options,
    RuntimeState runtime,
    IGameStore store,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    GameplayDatagramService gameplayDatagram,
    ItemCatalog itemCatalog,
    DungeonCatalog dungeonCatalog,
    GameCommandRegistry gameCommands,
    GameCommandDispatcher gameCommandDispatcher,
    ChannelSessionSenders sessionSenders,
    PacketTraceLogger packetTrace)
{
    public async Task HandleClientWhenAny(TcpClient client, int localPort, CancellationToken serverToken)
    {
        using (client)
        {
            client.NoDelay = true;
            using var connectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            serverToken = connectionCancellation.Token;

            void KickConnection()
            {
                connectionCancellation.Cancel();
                client.Close();
            }

            var remoteEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
            var remote = remoteEndpoint?.ToString() ?? "unknown";
            var isChannelPort = localPort == options.Channel.Port;
            var runtimeSession = runtime.Add($"{(isChannelPort ? "channel" : "entrance")}:{localPort}", remote);
            if (isChannelPort && remoteEndpoint is not null)
            {
                gameplayDatagram.RegisterChannelSession(
                    runtimeSession,
                    remoteEndpoint.Address);
            }
            var protocolSession = new EntranceSessionState();
            var gameClientCipher = isChannelPort ? new GameClientCipherState() : null;
            // Session state object (07 §4.3): the former closure variables
            // of this method live on the state domains now; `session` is the
            // method-wide alias that handlers reach through ctx.State as well.
            var sessionState = new ChannelSessionState();
            var session = sessionState;
            // Per-run dungeon dependencies: the run recorder needs its
            // catalog and the return-to-town anchor snapshots the
            // pre-dungeon position, exactly as the former initializers did.
            session.Dungeon.Run = new DungeonRunState(dungeonCatalog);
            session.Dungeon.ReturnTownId = session.Character.TownId;
            session.Dungeon.ReturnAreaId = session.Character.AreaId;
            session.Dungeon.ReturnX = session.Character.X;
            session.Dungeon.ReturnY = session.Character.Y;
            session.Dungeon.ReturnDirection = session.Character.Direction;

            RuntimeLogger.Info(
                "Entrance client {SessionId} connected from {Remote} on local port {Port}",
                runtimeSession.Id, remote, localPort);

            using var stream = new SerializedWriteStream(client.GetStream());
            // Protocol-layer per-connection plumbing (07 §4.2/§4.4): the
            // outbound queue pumps every handler send through the
            // single-writer channel; the context replaces the former
            // closure world for handlers migrated off the legacy chain.
            // Declared before the try so teardown drains the pump before
            // the stream is disposed.
            var connectionQueue = new ConnectionQueue(
                stream,
                runtimeSession,
                packet => packetTrace.LogPacket("TX", runtimeSession, packet),
                disconnecting: serverToken);
            var connectionContext = new ConnectionContext(
                runtimeSession,
                remoteEndpoint,
                isChannelPort,
                sessionState,
                connectionQueue,
                serverToken);
            connectionContext.KickConnection = KickConnection;
            PacketFrame? queuedRequest = null;
            Task<PacketFrame?>? pendingPacketRead = null;
            // Pump-side adjacent-frame read for the CHANGE_SKILLSLOT swap
            // pair: delay, availability probe, decode and accounting stay in
            // the transport layer; a non-matching frame is queued back.
            connectionContext.ReadAdjacentFrame = async (delay, accept, token) =>
            {
                await Task.Delay(delay, token);
                if (client.Available <= 0)
                {
                    return default;
                }

                var adjacentRaw = await PacketFrame.ReadAsync(
                    stream,
                    options.MaximumPacketLength,
                    token);
                if (adjacentRaw is null)
                {
                    return default;
                }

                if (!accept(adjacentRaw))
                {
                    queuedRequest = adjacentRaw;
                    return default;
                }

                if (!gameClientCipher!.TryDecode(adjacentRaw, out var adjacentDecoded))
                {
                    packetTrace.LogPacket("RX-ENC", runtimeSession, adjacentRaw);
                    RuntimeLogger.Warn("Could not decode the adjacent skill quick-slot command.");
                    return new AdjacentFrameResult(null, DecodeFailed: true);
                }

                runtime.CountPacket(runtimeSession);
                runtimeSession.LastProtocolId = adjacentDecoded.ProtocolId;
                packetTrace.LogPacket("RX", runtimeSession, adjacentDecoded, adjacentRaw);
                return new AdjacentFrameResult(adjacentDecoded, DecodeFailed: false);
            };

            try
            {
                async Task<bool> TryDispatchGameCommandAsync(PacketFrame frame)
                {
                    if (frame.Type != GameProtocolConstants.CommandPacketType
                        || !gameCommands.TryGet(frame.ProtocolId, out var handler))
                    {
                        return false;
                    }

                    return await gameCommandDispatcher.TryDispatchAsync(
                        connectionContext,
                        frame,
                        handler,
                        serverToken);
                }

                // Cross-session delivery channel: PartyCoordinator queues
                // packets here from other sessions' read loops; the write
                // itself stays on this connection's serialized stream.
                characterSessions.AttachPacketChannel(
                    runtimeSession.Id,
                    packet => _ = SendCrossSessionPacketAsync(packet));

                async Task SendCrossSessionPacketAsync(GameServerPacket packet)
                {
                    try
                    {
                        await packet.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        packetTrace.LogPacket("TX", runtimeSession, packet);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        RuntimeLogger.Warn(
                            exception,
                            "Could not deliver a cross-session packet {ProtocolId} on session {SessionId}.",
                            packet.ProtocolId,
                            runtimeSession.Id);
                    }
                }

                // Registers this session's town entity with the party
                // coordinator so same-area residents can see and invite this
                // character. The appearance packet is built here because only
                // this connection owns the equipped-item dictionaries.
                async Task SendNewMailAlarmAsync(
                    IReadOnlyCollection<CharacterMailRecord>? knownMails = null)
                {
                    if (session.Character.Active is null)
                    {
                        return;
                    }

                    var mails = knownMails
                        ?? await store.GetCharacterMailsAsync(session.Character.Active.Id, serverToken);
                    var newMailCount = mails.Count(mail => mail.IsNew);
                    if (newMailCount == 0)
                    {
                        return;
                    }

                    var alarm = MailboxPacketBuilders.CreateMailboxAlarm(
                        checked((short)Math.Min(newMailCount, short.MaxValue)));
                    await alarm.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    packetTrace.LogPacket("TX", runtimeSession, alarm);
                    await store.AcknowledgeCharacterMailNotificationsAsync(
                        session.Character.Active.Id,
                        serverToken);
                }

                Task SendPremiumServiceInfoAsync(byte serviceType, uint remainSeconds) =>
                    sessionSenders.SendPremiumServiceInfoAsync(connectionContext, serviceType, remainSeconds, serverToken);
                Task SendTownFatigueStateAsync() =>
                    sessionSenders.SendTownFatigueStateAsync(connectionContext, serverToken);
                Task<bool> TryAutomaticallySelectFreeCardAsync(string source) =>
                    sessionSenders.TryAutomaticallySelectFreeCardAsync(connectionContext, source, serverToken);

                Task ReturnToTownFromDungeonAsync(string reason, bool forcedDungeonExit = false) =>
                    sessionSenders.ReturnToTownFromDungeonAsync(connectionContext, reason, forcedDungeonExit, serverToken);

                if (isChannelPort)
                {
                    // The launcher applies the process-local channel callback
                    // after the client's startup integrity pass. This client
                    // can reach port 7001 a few hundred milliseconds earlier,
                    // and it never retries a greeting handled by the retired
                    // callback. Hold the first channel packet until that patch
                    // window has safely completed.
                    await Task.Delay(TimeSpan.FromMilliseconds(750), serverToken);
                    var greeting = ConnectionPacketBuilders.CreateCheckConnection(protocolSession.SessionToken);
                    await greeting.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    packetTrace.LogPacket("TX", runtimeSession, greeting);
                }

                while (!serverToken.IsCancellationRequested)
                {
                    var request = queuedRequest;
                    queuedRequest = null;
                    if (request is null)
                    {
                        pendingPacketRead ??= PacketFrame.ReadAsync(
                            stream,
                            options.MaximumPacketLength,
                            serverToken).AsTask();
                        if (isChannelPort && session.Dungeon.InSelection
                            && session.Dungeon.SelectionDeadline is { } selectionDeadline)
                        {
                            var remaining = selectionDeadline - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead, Task.Delay(remaining, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                // DF2008's CMD 16 failure handler (0x410B05) retries
                                // after 30.3 s, but returns without unlocking controls
                                // when selected index +0x200 is zero (0x410B1C).
                                // Never rely solely on the client's CMD 142 to exit.
                                await ReturnToTownFromDungeonAsync("selection-countdown-expired");
                                continue;
                            }
                        }
                        else if (isChannelPort && session.Dungeon.DeathTimeout is { } deathTimeout)
                        {
                            var remaining = deathTimeout.Deadline - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead,
                                    Task.Delay(remaining, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                if (session.Dungeon.DeathTimeout is { } currentTimeout
                                    && currentTimeout.Generation == deathTimeout.Generation
                                    && !session.Dungeon.CharacterAlive
                                    && session.Dungeon.CurrentDungeonId != 0)
                                {
                                    session.Dungeon.DeathTimeout = null;
                                    RuntimeLogger.Info(
                                        "Dungeon death countdown expired for user {UserId} in dungeon {DungeonId}; returning to town.",
                                        session.Character.LocalUserId,
                                        session.Dungeon.CurrentDungeonId);
                                    await ReturnToTownFromDungeonAsync(
                                        "death-countdown-expired",
                                        forcedDungeonExit: true);
                                }

                                continue;
                            }
                        }
                        else if (isChannelPort
                            && session.Dungeon.CardSelectionDeadline is { } cardSelectionDeadline)
                        {
                            var remaining = cardSelectionDeadline - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead,
                                    Task.Delay(remaining, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                if (session.Dungeon.CardSelectionDeadline
                                        == cardSelectionDeadline)
                                {
                                    session.Dungeon.CardSelectionDeadline = null;
                                    if (session.Dungeon.CardRightsSent
                                        && session.Dungeon.ClearReward is not null
                                        && !session.Dungeon.FreeCardIndex.HasValue
                                        && !await TryAutomaticallySelectFreeCardAsync(
                                            "card-selection-timer"))
                                    {
                                        session.Dungeon.CardSelectionDeadline =
                                            DateTimeOffset.UtcNow
                                            + TimeSpan.FromSeconds(1);
                                    }
                                }

                                continue;
                            }
                        }
                        else if (isChannelPort && !session.Premium.ExpiryUnix.IsEmpty)
                        {
                            var observedExpiries = session.Premium.ExpiryUnix.ToArray();
                            var nextExpiry = observedExpiries.Min(pair => pair.Value);
                            var remaining = DateTimeOffset
                                .FromUnixTimeSeconds(nextExpiry)
                                - DateTimeOffset.UtcNow;
                            var waitTime = remaining > TimeSpan.FromDays(1)
                                ? TimeSpan.FromDays(1)
                                : remaining;
                            if (waitTime > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead,
                                    Task.Delay(waitTime, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                foreach (var expired in observedExpiries.Where(pair =>
                                             pair.Value <= now))
                                {
                                    if (session.Premium.ExpiryUnix.TryGetValue(
                                            expired.Key,
                                            out var currentExpiry)
                                        && currentExpiry <= now
                                        && session.Premium.ExpiryUnix.TryRemove(expired.Key, out _)
                                        && session.Character.Active is not null)
                                    {
                                        await SendPremiumServiceInfoAsync(expired.Key, 0);
                                    }
                                }

                                continue;
                            }
                        }
                        else
                        {
                            request = await pendingPacketRead;
                            pendingPacketRead = null;
                        }
                    }

                    if (request is null)
                    {
                        break;
                    }

                    PacketFrame? rawFrame = null;
                    if (isChannelPort)
                    {
                        rawFrame = request; // wire bytes before seed decryption
                        if (!gameClientCipher!.TryDecode(request, out var decodedRequest))
                        {
                            packetTrace.LogPacket("RX-ENC", runtimeSession, request);
                            RuntimeLogger.Warn(
                                "Could not recover the channel payload seed for packet type {Type}, protocol {ProtocolId}; {CandidateCount} candidate(s) remain.",
                                request.Type, request.ProtocolId, gameClientCipher!.CandidateCount);
                            continue;
                        }

                        request = decodedRequest;
                        RuntimeLogger.Debug(
                            "Accepted channel payload with {CandidateCount} compatible rolling-seed candidate(s).",
                            gameClientCipher.CandidateCount);
                    }

                    runtime.CountPacket(runtimeSession);
                    runtimeSession.LastProtocolId = request.ProtocolId;
                    packetTrace.LogPacket("RX", runtimeSession, request, rawFrame);

                    if (options.RejectBadCrc32 && !request.HasValidCrc32)
                    {
                        RuntimeLogger.Warn("Rejected frame with bad CRC32 from {SessionId}", runtimeSession.Id);
                        break;
                    }

                    while (session.Events.Reader.TryRead(out var connectionEvent))
                    {
                        switch (connectionEvent.Kind)
                        {
                            case ConnectionEventKind.MailAlarm:
                                if (session.Character.Active is not null)
                                {
                                    await SendNewMailAlarmAsync();
                                }

                                break;
                            case ConnectionEventKind.PremiumServiceUpdate:
                                if (session.Character.Active is not null)
                                {
                                    await SendPremiumServiceInfoAsync(
                                        connectionEvent.ServiceType,
                                        (uint)connectionEvent.Value);
                                }

                                break;
                            case ConnectionEventKind.CeraValueUpdated:
                                if (session.Character.Active is not null)
                                {
                                    session.Currency.Cash = connectionEvent.Value;
                                    var ceraNotification = ShopPacketBuilders.CreateCeraUpdate(
                                        success: true,
                                        value: (uint)connectionEvent.Value);
                                    await ceraNotification.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    packetTrace.LogPacket("TX", runtimeSession, ceraNotification);
                                }

                                break;
                            case ConnectionEventKind.FatigueReset:
                                if (session.Character.Active is not null)
                                {
                                    session.Character.Active = session.Character.Active with
                                    {
                                        UsedFatigue = 0
                                    };
                                    if (session.Dungeon.CurrentDungeonId == 0)
                                    {
                                        await SendTownFatigueStateAsync();
                                    }
                                }

                                break;
                            case ConnectionEventKind.WeaknessRecovered:
                                if (session.Character.Active is not null)
                                {
                                    session.Character.Active = session.Character.Active with
                                    {
                                        WeaknessRecovery = (byte)connectionEvent.Value
                                    };
                                }

                                break;
                        }
                    }

                    if (isChannelPort)
                    {
                        if (await TryDispatchGameCommandAsync(request))
                        {
                            continue;
                        }

                        // Last-resort fallback for commands the registry does not
                        // mount yet; CHECK_CONNECTION is dispatched by the registry
                        // (CheckConnectionHandler) before reaching this point.
                        var gameReply = FallbackCommandReplies.TryCreateReply(
                            request,
                            protocolSession.SessionToken);
                        if (gameReply is null)
                        {
                            RuntimeLogger.Warn(
                                "No channel handler for packet type {Type}, protocol {ProtocolId}; frame retained in logs.",
                                request.Type, request.ProtocolId);
                            continue;
                        }

                        await gameReply.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        packetTrace.LogPacket("TX", runtimeSession, gameReply);
                        continue;
                    }

                    if (await TryDispatchGameCommandAsync(request))
                    {
                        continue;
                    }

                    var reply = EntranceHandshakeReplies.TryCreateReply(request, protocolSession, sessionSenders.LoadContent());
                    if (reply is null)
                    {
                        RuntimeLogger.Warn(
                            "No entrance handler for packet type {Type}, protocol {ProtocolId}; frame retained in logs.",
                            request.Type, request.ProtocolId);
                        continue;
                    }

                    await reply.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    packetTrace.LogPacket("TX", runtimeSession, reply);
                }
            }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                RuntimeLogger.Warn(exception, "Entrance session {SessionId} ended with an error.", runtimeSession.Id);
            }
            finally
            {
                // Complete the outbound channel and drain the pump before the
                // stream is disposed, so no queued frame tears (07 §4.4).
                await connectionQueue.DisposeAsync();
                if (session.Character.Active is not null)
                {
                    try
                    {
                        if (session.Dungeon.CurrentDungeonId != 0
                            && session.Dungeon.CreatureStomachCheckpointSeconds
                                is long finalCheckpoint)
                        {
                            var finalNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var finalElapsedSeconds = checked((uint)Math.Min(
                                uint.MaxValue,
                                Math.Max(0L, finalNow - finalCheckpoint)));
                            if (finalElapsedSeconds != 0
                                && CreatureHungerPlanner.TrySettle(
                                    session.Inventory.Creature,
                                    finalElapsedSeconds,
                                    itemCatalog,
                                    out var finalHungerPlan)
                                && finalHungerPlan.Changed)
                            {
                                await store.SaveCharacterItemSpacesAsync(
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    session.Currency.Gold,
                                    session.Inventory.Main.Values,
                                    session.Inventory.Equipped.Values,
                                    session.Inventory.Warehouse.Values,
                                    CancellationToken.None,
                                    avatarInventory: session.Inventory.Avatar.Values,
                                    creatureInventory:
                                        finalHungerPlan.Inventory.Values);
                            }
                        }

                        var saveTownId = session.Dungeon.CurrentDungeonId == 0
                            ? session.Character.TownId
                            : session.Dungeon.ReturnTownId;
                        var saveAreaId = session.Dungeon.CurrentDungeonId == 0
                            ? session.Character.AreaId
                            : session.Dungeon.ReturnAreaId;
                        var saveX = session.Dungeon.CurrentDungeonId == 0 ? session.Character.X : session.Dungeon.ReturnX;
                        var saveY = session.Dungeon.CurrentDungeonId == 0 ? session.Character.Y : session.Dungeon.ReturnY;
                        var saveDirection = session.Dungeon.CurrentDungeonId == 0
                            ? session.Character.Direction
                            : session.Dungeon.ReturnDirection;
                        await store.SaveCharacterLocationAsync(
                            session.Login.AccountName,
                            session.Character.Active.Id,
                            saveTownId,
                            saveAreaId,
                            saveX,
                            saveY,
                            saveDirection,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        RuntimeLogger.Warn(
                            exception,
                            "Failed to persist the final location for character {CharacterId}.",
                            session.Character.Active.Id);
                    }
                }

                partyCoordinator.OnSessionClosed(runtimeSession.Id);
                characterSessions.Release(session.Character.Lease);
                session.Character.Lease = null;
                characterSessions.DetachPacketChannel(runtimeSession.Id);
                gameplayDatagram.UnregisterChannelSession(runtimeSession.Id);
                runtime.Remove(runtimeSession.Id);
                RuntimeLogger.Info("Entrance client {SessionId} disconnected.", runtimeSession.Id);
            }
        }
    }

}
