using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Supplies the loopback-only UDP exchange that the 1.0.1.9 character screen
/// performs before it enables its controls.
/// </summary>
public sealed class CharacterDatagramService(
    ServerOptions options,
    RuntimeState runtime,
    ILogger<CharacterDatagramService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DatagramSession> _sessions = new();
    private UdpClient? _listener;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CharacterDatagram.Enabled)
        {
            logger.LogWarning("Character datagram listener is disabled.");
            return;
        }

        var endpoint = new IPEndPoint(
            IPAddress.Parse(options.CharacterDatagram.Host),
            options.CharacterDatagram.Port);
        _listener = new UdpClient(endpoint);
        logger.LogInformation("Character datagram listener ready at {Endpoint}", endpoint);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var received = await _listener.ReceiveAsync(stoppingToken);
                await HandleDatagramAsync(received.RemoteEndPoint, received.Buffer, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Dispose();
            foreach (var session in _sessions.Values)
            {
                runtime.Remove(session.Runtime.Id);
            }

            _sessions.Clear();
        }
    }

    private async Task HandleDatagramAsync(
        IPEndPoint remote,
        byte[] datagram,
        CancellationToken cancellationToken)
    {
        var key = remote.ToString();
        var session = _sessions.GetOrAdd(key, _ => new DatagramSession
        {
            Runtime = runtime.Add($"character-datagram:{options.CharacterDatagram.Port}", key)
        });

        runtime.CountPacket(session.Runtime);
        runtime.TraceDatagram(
            session.Runtime,
            "RX-UDP",
            datagram,
            options.HexDumpLimit);
        if (options.EnablePacketTracing)
        {
            logger.LogInformation(
                "RX {SessionId} character-datagram remote={Remote} length={Length} body={Body}",
                session.Runtime.Id,
                remote,
                datagram.Length,
                Dump(datagram));
        }

        if (session.PendingHeader is { } pending)
        {
            session.PendingHeader = null;
            if (datagram.Length != pending.PayloadLength)
            {
                logger.LogWarning(
                    "Character datagram {Protocol} expected {Expected} payload bytes but received {Actual}.",
                    pending.ProtocolId,
                    pending.PayloadLength,
                    datagram.Length);
                EndSession(key, session);
                return;
            }

            await HandlePacketAsync(key, session, remote, pending, datagram, cancellationToken);
            return;
        }

        if (!LegacyDatagramProtocol.TryReadHeader(datagram, out var header))
        {
            logger.LogWarning("Ignored character datagram without a valid 11-byte header from {Remote}.", remote);
            return;
        }

        session.Runtime.LastProtocolId = header.ProtocolId;
        logger.LogDebug(
            "Character datagram header protocol={Protocol} total={Total} payload={Payload} version={Version}",
            header.ProtocolId,
            header.TotalLength,
            header.PayloadLength,
            header.Version);

        if (header.PayloadLength == 0)
        {
            await HandlePacketAsync(key, session, remote, header, [], cancellationToken);
        }
        else
        {
            session.PendingHeader = header;
        }
    }

    private async Task HandlePacketAsync(
        string key,
        DatagramSession session,
        IPEndPoint remote,
        LegacyDatagramHeader header,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        switch (header.ProtocolId)
        {
            case 11:
                if (payload.Length != 32)
                {
                    logger.LogWarning("Character key request had unexpected length {Length}.", payload.Length);
                    EndSession(key, session);
                    return;
                }

                if (options.EnablePacketTracing)
                {
                    TryLogInitialPlaintext(payload);
                }
                await SendPacketAsync(session, remote, 12,
                    [new byte[4], session.KeyMaterial], cancellationToken);
                break;

            case 5:
                if (payload.Length % 16 != 0)
                {
                    logger.LogWarning("Character login request was not AES block aligned.");
                    EndSession(key, session);
                    return;
                }

                if (options.EnablePacketTracing)
                {
                    TryLogDecrypted("character login", payload, session.KeyMaterial);
                }
                var loginPlaintext = new byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(loginPlaintext, 1);
                await SendPacketAsync(session, remote, 6,
                    [LegacyDatagramProtocol.EncryptAesEcb(loginPlaintext, session.AesKey)],
                    cancellationToken);
                break;

            case 1:
                // An empty, block-aligned data set is sufficient for an account
                // whose TCP USERINFO response contains no character records.
                var emptyData = LegacyDatagramProtocol.EncryptAesEcb(new byte[16], session.AesKey);
                var compressed = LegacyDatagramProtocol.Compress(emptyData);
                await SendPacketAsync(session, remote, 3, [compressed], cancellationToken);
                EndSession(key, session);
                break;

            case 9:
                // This branch is used only when the login status is zero. The
                // emulator returns status one above, but keep a valid response
                // for diagnostic clients.
                await SendPacketAsync(session, remote, 10, [new byte[16]], cancellationToken);
                break;

            default:
                logger.LogWarning(
                    "No character datagram handler for protocol {Protocol}; payload={Payload}",
                    header.ProtocolId,
                    Dump(payload));
                break;
        }
    }

    private async Task SendPacketAsync(
        DatagramSession session,
        IPEndPoint remote,
        byte protocolId,
        IReadOnlyList<byte[]> payloadDatagrams,
        CancellationToken cancellationToken)
    {
        var listener = _listener ?? throw new InvalidOperationException("UDP listener is not initialized.");
        var payloadLength = payloadDatagrams.Sum(part => part.Length);
        var header = LegacyDatagramProtocol.CreateHeader(protocolId, payloadLength);
        await SendDatagramAsync(listener, session, remote, protocolId, header, cancellationToken);
        foreach (var part in payloadDatagrams)
        {
            await SendDatagramAsync(listener, session, remote, protocolId, part, cancellationToken);
        }
    }

    private async Task SendDatagramAsync(
        UdpClient listener,
        DatagramSession session,
        IPEndPoint remote,
        byte protocolId,
        byte[] datagram,
        CancellationToken cancellationToken)
    {
        await listener.SendAsync(datagram, remote, cancellationToken);
        Interlocked.Increment(ref session.Runtime.SentPackets);
        runtime.TraceDatagram(
            session.Runtime,
            "TX-UDP",
            datagram,
            options.HexDumpLimit,
            protocolId);
        if (options.EnablePacketTracing)
        {
            logger.LogInformation(
                "TX character-datagram remote={Remote} protocol={Protocol} length={Length} body={Body}",
                remote,
                protocolId,
                datagram.Length,
                Dump(datagram));
        }
    }

    private void TryLogInitialPlaintext(byte[] payload)
    {
        try
        {
            var plaintext = LegacyDatagramProtocol.DecryptAesEcb(payload, new byte[16]);
            logger.LogInformation("Character initial AES plaintext={Plaintext}", Dump(plaintext));
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "Could not inspect the initial character datagram AES block.");
        }
    }

    private void TryLogDecrypted(string description, byte[] payload, byte[] keyMaterial)
    {
        try
        {
            var plaintext = LegacyDatagramProtocol.DecryptAesEcb(payload, keyMaterial.AsSpan(0, 16));
            logger.LogInformation("Decrypted {Description} payload={Plaintext}", description, Dump(plaintext));
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "Could not inspect {Description} AES payload.", description);
        }
    }

    private void EndSession(string key, DatagramSession session)
    {
        _sessions.TryRemove(key, out _);
        runtime.Remove(session.Runtime.Id);
    }

    private string Dump(ReadOnlySpan<byte> bytes)
    {
        var length = Math.Min(bytes.Length, Math.Max(0, options.HexDumpLimit));
        return Convert.ToHexString(bytes[..length]) + (bytes.Length > length ? "..." : "");
    }

    private sealed class DatagramSession
    {
        public required RuntimeSession Runtime { get; init; }
        public LegacyDatagramHeader? PendingHeader { get; set; }
        public byte[] KeyMaterial { get; } = new byte[32];
        public ReadOnlySpan<byte> AesKey => KeyMaterial.AsSpan(0, 16);
    }
}
