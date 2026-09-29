using System.Net;
using System.Text;
using System.Text.Json;
using DFLegacy.Protocol;
using DFLegacy.Server;

// 组合根：配置与数据锚点（R5）。优先级 --home <path> > DFLEGACY_HOME > BaseDirectory。
var homeAnchor = PathResolver.ResolveHome(args);
// wwwroot 锚定到 home：内容根默认跟随进程工作目录，换目录启动会丢静态
// 文件（管理后台、client_pay 等）。主机级设置只能在 CreateBuilder 时传入，
// 事后改 WebHost 会抛 NotSupportedException。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(homeAnchor, "wwwroot")
});
// The default Windows Event Log provider can require an administrator-created
// event source. This emulator must be runnable as a normal desktop user.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Configuration.AddJsonFile(
    Path.Combine(homeAnchor, "server.json"),
    optional: false,
    reloadOnChange: true);

var options = builder.Configuration.GetSection("DFLegacy").Get<ServerOptions>() ?? new ServerOptions();
options.HomeDirectory = homeAnchor;

// DAF-MCP 模块运行期加载（docs/design/09-mcp-packet-tap.md §5.3）：默认关闭即
// 短路；失败只降级 MCP。组合根的 app.Logger 要到 Build 后才有，装配期日志走
// 同格式的引导记录器。
var moduleLogger = LoggerFactory.Create(logging => logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
})).CreateLogger("DFLegacy.Server.McpModuleLoader");
McpModuleLoader.TryAttach(builder.Services, options, moduleLogger);

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var clientTextEncoding = PvfEncodings.Cp936Strict();
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Listen(IPAddress.Parse(options.Admin.Host), options.Admin.Port));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ScriptFileSystem>();
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<JsonGameStore>();
builder.Services.AddSingleton<CharacterSessionRegistry>();
builder.Services.AddSingleton<PartyCoordinator>();
builder.Services.AddSingleton<SkillCatalog>();
builder.Services.AddSingleton<QuestCatalog>();
builder.Services.AddSingleton<CharacterExperienceCatalog>();
builder.Services.AddSingleton<DungeonExperienceCatalog>();
builder.Services.AddSingleton<PvpExperienceCatalog>();
builder.Services.AddSingleton<CharacterSpCatalog>();
builder.Services.AddSingleton<CharacterStatCatalog>();
builder.Services.AddSingleton<CreatureExperienceCatalog>();
builder.Services.AddSingleton<StaminaRecoveryCatalog>();
builder.Services.AddSingleton<ItemCatalog>();
builder.Services.AddSingleton<CompoundCatalog>();
builder.Services.AddSingleton<AvatarCompoundCatalog>();
builder.Services.AddSingleton<EquipmentReinforcementCatalog>();
builder.Services.AddSingleton<ResealCatalog>();
builder.Services.AddSingleton<DisjointCatalog>();
builder.Services.AddSingleton<CeraShopCatalog>();
builder.Services.AddSingleton<PremiumBenefitCatalog>();
builder.Services.AddSingleton<DungeonCatalog>();
builder.Services.AddSingleton<WorldMapCatalog>();
builder.Services.AddSingleton<WorldDropCatalog>();
builder.Services.AddSingleton<GoldDropCatalog>();
builder.Services.AddSingleton<MonsterDropCatalog>();
builder.Services.AddSingleton<MonsterChampionDropCatalog>();
builder.Services.AddSingleton<DungeonDropGenerator>();
builder.Services.AddSingleton<HellDropCatalog>();
builder.Services.AddSingleton<ObjectDropCatalog>();
builder.Services.AddSingleton<ObjectDropGenerator>();
builder.Services.AddSingleton<ClearRewardCatalog>();
builder.Services.AddSingleton<ClearRewardGenerator>();
builder.Services.AddSingleton<GameplayDatagramService>();
builder.Services.AddHostedService<EntranceService>();
builder.Services.AddHostedService<CharacterDatagramService>();
builder.Services.AddHostedService<FatigueResetService>();
builder.Services.AddHostedService<WeaknessRecoveryService>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<GameplayDatagramService>());
builder.Services.AddHostedService<ProbeService>();

var app = builder.Build();

// 快照/日志通道开关（= EnablePacketTracing）；Tap 通道由 MCP 模块宿主自行接入。
app.Services.GetRequiredService<RuntimeState>().TracingEnabled = options.EnablePacketTracing;

// Party real-time datagrams relay through the gameplay UDP listener: the
// endpoint table advertises this server for every peer, and this resolver
// tells the listener where to forward each sender's datagrams.
var partyCoordinator = app.Services.GetRequiredService<PartyCoordinator>();
app.Services.GetRequiredService<GameplayDatagramService>()
    .SetRelayResolver(partyCoordinator.GetPartyPeerSessionIds);

var randomStatus = GameRandomSource.Shared.GetStatus();
app.Logger.LogInformation(
    "Game random source initialized: provider={Provider}.",
    randomStatus.Provider);
app.Logger.LogInformation(
    "Configuration anchor: {HomeDirectory}", homeAnchor);
var itemCatalog = app.Services.GetRequiredService<ItemCatalog>();
itemCatalog.Initialize();
var gameStore = app.Services.GetRequiredService<JsonGameStore>();
await gameStore.InitializeAsync();
var compoundCatalog = app.Services.GetRequiredService<CompoundCatalog>();
compoundCatalog.Initialize();
var avatarCompoundCatalog =
    app.Services.GetRequiredService<AvatarCompoundCatalog>();
avatarCompoundCatalog.Initialize();
var reinforcementCatalog =
    app.Services.GetRequiredService<EquipmentReinforcementCatalog>();
reinforcementCatalog.Initialize();
var resealCatalog = app.Services.GetRequiredService<ResealCatalog>();
resealCatalog.Initialize();
var characterSpCatalog = app.Services.GetRequiredService<CharacterSpCatalog>();
characterSpCatalog.Initialize();
var pvpExperienceCatalog = app.Services.GetRequiredService<PvpExperienceCatalog>();
pvpExperienceCatalog.Initialize();
var normalizedPvpGrades = await gameStore.NormalizePvpGradesAsync(
    pvpExperienceCatalog);
if (normalizedPvpGrades != 0)
{
    app.Logger.LogInformation(
        "Normalized {CharacterCount} persisted PvP grade(s) from cumulative points.",
        normalizedPvpGrades);
}
var characterStatCatalog = app.Services.GetRequiredService<CharacterStatCatalog>();
characterStatCatalog.Initialize();
var creatureExperienceCatalog =
    app.Services.GetRequiredService<CreatureExperienceCatalog>();
creatureExperienceCatalog.Initialize();
var staminaRecoveryCatalog =
    app.Services.GetRequiredService<StaminaRecoveryCatalog>();
staminaRecoveryCatalog.Initialize();
var disjointCatalog = app.Services.GetRequiredService<DisjointCatalog>();
disjointCatalog.Initialize();
var premiumBenefitCatalog = app.Services.GetRequiredService<PremiumBenefitCatalog>();
premiumBenefitCatalog.Initialize();
var monsterDropCatalog = app.Services.GetRequiredService<MonsterDropCatalog>();
monsterDropCatalog.Initialize();
var monsterChampionDropCatalog =
    app.Services.GetRequiredService<MonsterChampionDropCatalog>();
monsterChampionDropCatalog.Initialize();
var hellDropCatalog = app.Services.GetRequiredService<HellDropCatalog>();
hellDropCatalog.Initialize();

// 启动完成前汇总缺失数据（R9）：降级不静默——每项缺失在上游各自有
// Error/Warning 日志，这里再给出一页式清单，避免掩盖配置错误。
var scripts = app.Services.GetRequiredService<ScriptFileSystem>();
var missingData = new List<string>();
if (!scripts.IsPvf)
{
    missingData.Add("script source (Script.pvf not found; directory fallback or none)");
}
if (itemCatalog.Count == 0)
{
    missingData.Add("item definitions (item production disabled)");
}
if (compoundCatalog.Count == 0)
{
    missingData.Add("compound recipes (item production disabled)");
}
if (avatarCompoundCatalog.Count == 0)
{
    missingData.Add("avatar compound definitions (avatar compound disabled)");
}
if (characterStatCatalog.Count == 0)
{
    missingData.Add("stat tables (built-in base attributes in use)");
}
if (creatureExperienceCatalog.ThresholdCount == 0)
{
    missingData.Add("creature experience table (creature experience disabled)");
}
if (missingData.Count > 0)
{
    app.Logger.LogWarning(
        "Missing or empty script data ({Count}): {MissingData}",
        missingData.Count,
        string.Join("; ", missingData));
}
else
{
    app.Logger.LogInformation("Script data check passed: no missing core data sets.");
}

var addCashArgumentIndex = Array.FindIndex(
    args,
    argument => string.Equals(
        argument,
        "--add-cash-all",
        StringComparison.OrdinalIgnoreCase));
if (addCashArgumentIndex >= 0)
{
    if (addCashArgumentIndex + 1 >= args.Length
        || !int.TryParse(args[addCashArgumentIndex + 1], out var addCashAmount)
        || addCashAmount <= 0)
    {
        app.Logger.LogError("--add-cash-all requires a positive integer amount.");
        Environment.ExitCode = 2;
        return;
    }

    var updatedCharacters = await gameStore.AddCashToAllCharactersAsync(addCashAmount);
    app.Logger.LogInformation(
        "Added {Amount} Cera to {CharacterCount} characters.",
        addCashAmount,
        updatedCharacters);
    return;
}

if (args.Contains("--reset-maximum-transfer-roster", StringComparer.OrdinalIgnoreCase))
{
    var replacement = await gameStore.ReplaceAllCharactersAsync(
        "test",
        MaximumTransferRoster.Create());
    if (!replacement.Replaced)
    {
        app.Logger.LogError(
            "Could not replace the character roster: {Error}",
            replacement.Error);
        Environment.ExitCode = 2;
    }
    else
    {
        app.Logger.LogInformation(
            "Created {Count} maximum-level transfer characters on account test.",
            replacement.CharacterCount);
    }

    return;
}

await gameStore.MigrateLegacyQuestRewardsAsync(
    app.Services.GetRequiredService<QuestCatalog>(),
    app.Services.GetRequiredService<CharacterExperienceCatalog>(),
    characterSpCatalog,
    itemCatalog,
    skillCatalog: app.Services.GetRequiredService<SkillCatalog>());

app.UseStaticFiles();
app.MapGet("/client", () => Results.Redirect("/client/client_pay.htm"));
app.MapGet("/client/", () => Results.Redirect("/client/client_pay.htm"));
app.MapGet("/client/client_pay", () => Results.Redirect("/client/client_pay.htm"));
app.MapGet("/client/client_diamond", () => Results.Redirect("/client/client_diamond.htm"));
app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "dflegacy-emulator" }));

app.MapGet("/api/status", (RuntimeState runtime, ScriptFileSystem scripts, ItemCatalog items) => Results.Ok(new
{
    runtime.StartedAt,
    runtime.TotalConnections,
    runtime.TotalPackets,
    ActiveSessions = runtime.Sessions().Length,
    Entrance = new[] { options.Entrance.Port }
        .Concat(options.Entrance.AdditionalPorts)
        .Distinct()
        .Select(port => $"{options.Entrance.Host}:{port}"),
    CharacterDatagram = options.CharacterDatagram.Enabled
        ? $"{options.CharacterDatagram.Host}:{options.CharacterDatagram.Port}/udp"
        : "disabled",
    GameplayDatagram = options.GameplayDatagram.Enabled
        ? new[]
        {
            $"{options.GameplayDatagram.Host}:{options.GameplayDatagram.PrimaryPort}/udp",
            $"{options.GameplayDatagram.Host}:{options.GameplayDatagram.SecondaryPort}/udp"
        }
        : [],
    GameProbe = options.GameProbe.Enabled ? $"{options.GameProbe.Host}:{options.GameProbe.Port}" : "disabled",
    Random = GameRandomSource.Shared.GetStatus(),
    ChannelScriptConfigured = scripts.FileExists("etc/channel_info.etc"),
    CachedItems = items.Count
}));

app.MapGet("/api/sessions", (RuntimeState runtime) => Results.Ok(runtime.Sessions()));
app.MapGet("/api/packets", (RuntimeState runtime) => Results.Ok(runtime.RecentPackets()));
app.MapGet("/api/accounts", async (JsonGameStore store, CancellationToken token) =>
    Results.Ok(await store.GetAccountsAsync(token)));

app.MapGet("/api/admin/characters", async (
    JsonGameStore store,
    CharacterSessionRegistry sessions,
    CancellationToken token) =>
{
    var accounts = await store.GetAccountsAsync(token);
    return Results.Ok(accounts
        .SelectMany(account => account.Characters.Select(character => new
        {
            character.Id,
            character.CharacterNo,
            character.Name,
            character.Job,
            character.GrowType,
            character.Level,
            account.UserName,
            account.AccountUid,
            Online = sessions.IsOnline(character.Id)
        }))
        .OrderByDescending(character => character.Online)
        .ThenBy(character => character.UserName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(character => character.CharacterNo));
});

app.MapGet("/api/admin/items", (
    string? query,
    AdminMailAttachmentKind? kind,
    int? limit,
    ItemCatalog items) =>
{
    if (kind.HasValue
        && (kind == AdminMailAttachmentKind.None || !Enum.IsDefined(kind.Value)))
    {
        return Results.BadRequest(new { error = "kind must be Item, Avatar or CreatureEgg." });
    }

    var normalizedQuery = query?.Trim() ?? string.Empty;
    var maximum = Math.Clamp(limit ?? 40, 1, 100);
    var result = items.Definitions.Values
        .Where(AdminMailComposer.IsSupportedAttachment)
        .Where(definition => !kind.HasValue
            || AdminMailComposer.GetAttachmentKind(definition) == kind.Value)
        .Where(definition => normalizedQuery.Length == 0
            || definition.Id.ToString().Contains(
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase)
            || definition.Name.Contains(
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase)
            || definition.ScriptPath.Contains(
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(definition =>
            definition.Id.ToString() == normalizedQuery)
        .ThenBy(definition => definition.Id)
        .Take(maximum)
        .Select(definition => new
        {
            definition.Id,
            Name = string.IsNullOrWhiteSpace(definition.Name)
                ? $"Item {definition.Id}"
                : definition.Name,
            AttachmentKind = AdminMailComposer.GetAttachmentKind(definition),
            ScriptKind = definition.ScriptKind.ToString(),
            InventoryCategory = definition.InventoryCategory.ToString(),
            definition.TypeTag,
            AttachType = definition.AttachType.ToString(),
            definition.StackLimit,
            Durability = items.GetInitialDurability(definition.Id),
            definition.ScriptPath
        });
    return Results.Ok(result);
});

app.MapGet("/api/admin/dungeons", (DungeonCatalog dungeons) => Results.Ok(
    dungeons.DungeonIds.Select(dungeonId =>
    {
        dungeons.TryGetDefinition(dungeonId, out var definition);
        return new
        {
            DungeonId = dungeonId,
            definition.MinimumLevel,
            definition.BasisLevel,
            definition.IsHellDungeon,
            definition.GoldCardUse
        };
    })));

app.MapGet("/api/admin/message-types", () => Results.Ok(
    Enum.GetValues<GameMessageType>().Select(messageType => new
    {
        Value = (byte)messageType,
        Name = messageType.ToString()
    })));

app.MapPost("/api/admin/characters/{characterId:guid}/mail", async (
    Guid characterId,
    AdminSendMailRequest request,
    JsonGameStore store,
    ItemCatalog items,
    CharacterSessionRegistry sessions,
    CancellationToken token) =>
{
    if (!AdminMailComposer.TryCompose(request, items, out var mailRequest, out var error))
    {
        return Results.BadRequest(new { error });
    }

    try
    {
        _ = clientTextEncoding.GetBytes(mailRequest.Sender);
        _ = clientTextEncoding.GetBytes(mailRequest.Text ?? string.Empty);
    }
    catch (EncoderFallbackException)
    {
        return Results.BadRequest(new
        {
            error = "Mail sender or text contains characters that cannot be encoded as CP936."
        });
    }

    var result = await store.SendCharacterMailAsync(characterId, mailRequest, token);
    if (!result.Created)
    {
        return Results.BadRequest(new { error = result.Error });
    }

    var notifiedOnlineCharacter = sessions.NotifyMailReceived(characterId);
    return Results.Created(
        $"/api/characters/{characterId}/mail/{result.Mail!.Id}",
        new { result.Mail, NotifiedOnlineCharacter = notifiedOnlineCharacter });
});

app.MapPost("/api/admin/broadcast/message", (
    AdminBroadcastMessageRequest request,
    CharacterSessionRegistry sessions) =>
{
    if (string.IsNullOrEmpty(request.Message))
    {
        return Results.BadRequest(new { error = "Message must not be empty." });
    }

    if (!Enum.IsDefined(request.MessageType))
    {
        return Results.BadRequest(new { error = "MessageType is not supported." });
    }

    byte[] messageBytes;
    try
    {
        messageBytes = clientTextEncoding.GetBytes(request.Message);
    }
    catch (EncoderFallbackException)
    {
        return Results.BadRequest(new
        {
            error = "Message contains characters that cannot be encoded as CP936."
        });
    }

    if (messageBytes.Length >= 0x100)
    {
        return Results.BadRequest(new
        {
            error = "CP936-encoded message must be shorter than 256 bytes."
        });
    }

    var recipients = sessions.NotifyMessageAll(
        request.MessageType,
        request.TargetAreaUserId,
        messageBytes);
    return Results.Ok(new
    {
        queued = true,
        recipients,
        request.MessageType,
        request.TargetAreaUserId,
        encodedLength = messageBytes.Length
    });
});

app.MapPost("/api/admin/broadcast/popup", (
    AdminBroadcastPopupRequest request,
    CharacterSessionRegistry sessions) =>
{
    if (string.IsNullOrEmpty(request.Message))
    {
        return Results.BadRequest(new { error = "Message must not be empty." });
    }

    byte[] messageBytes;
    try
    {
        messageBytes = clientTextEncoding.GetBytes(request.Message);
    }
    catch (EncoderFallbackException)
    {
        return Results.BadRequest(new
        {
            error = "Message contains characters that cannot be encoded as CP936."
        });
    }

    if (messageBytes.Length >= 0x100)
    {
        return Results.BadRequest(new
        {
            error = "CP936-encoded message must be shorter than 256 bytes."
        });
    }

    var recipients = sessions.NotifyPopupAll(messageBytes);
    return Results.Ok(new
    {
        queued = true,
        recipients,
        encodedLength = messageBytes.Length
    });
});

app.MapPut("/api/admin/characters/{characterId:guid}/dungeons/{dungeonId:int}", async (
    Guid characterId,
    int dungeonId,
    AdminSetDungeonDifficultyRequest request,
    JsonGameStore store,
    DungeonCatalog dungeons,
    CharacterSessionRegistry sessions,
    CancellationToken token) =>
{
    if (dungeonId is <= 0 or > ushort.MaxValue
        || !dungeons.TryGetDefinition((ushort)dungeonId, out _))
    {
        return Results.BadRequest(new { error = "Dungeon id is not present in the loaded PVF." });
    }

    if (request.MaximumDifficulty > DungeonDifficultyProgression.HighestDifficulty)
    {
        return Results.BadRequest(new { error = "MaximumDifficulty must be between 0 and 3." });
    }

    var updated = await store.SetCharacterDungeonDifficultyAsync(
        characterId,
        (ushort)dungeonId,
        request.MaximumDifficulty,
        token);
    if (updated is null)
    {
        return Results.NotFound(new { error = "Character does not exist." });
    }

    var notifiedOnlineCharacter = sessions.NotifyDungeonPermission(
        characterId,
        (ushort)dungeonId,
        request.MaximumDifficulty);
    return Results.Ok(new
    {
        characterId,
        DungeonId = (ushort)dungeonId,
        request.MaximumDifficulty,
        NotifiedOnlineCharacter = notifiedOnlineCharacter
    });
});

// These client-pay mutations are intentionally on the loopback admin listener;
// the embedded IE page uses the same origin and does not carry a separate login.
app.MapPost("/api/client-pay/cera", async (
    ClientCeraTopUpRequest request,
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    CancellationToken token) =>
{
    var result = await store.GrantCeraToAccountAsync(
        request.Account,
        request.Amount,
        token);
    if (!result.Success)
    {
        return Results.BadRequest(result);
    }

    var characters = await store.GetCharactersAsync(result.Account, token);
    foreach (var character in characters)
    {
        characterSessions.NotifyCera(
            character.Id,
            checked((uint)Math.Max(0, character.Cash)));
    }

    return Results.Ok(result);
});

app.MapPost("/api/client-pay/diamond", async (
    ClientDiamondGrantRequest request,
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    CancellationToken token) =>
{
    var result = await store.GrantDiamondToAccountAsync(
        request.Account,
        request.Days,
        token);
    if (!result.Success)
    {
        return Results.BadRequest(result);
    }

    var characters = await store.GetCharactersAsync(result.Account, token);
    foreach (var character in characters)
    {
        characterSessions.NotifyPremiumInfo(
            character.Id,
            GameProtocolEngine.BlackDiamondServiceType,
            result.RemainingSeconds);
    }
    foreach (var characterId in result.WeaknessRecoveredCharacterIds ?? [])
    {
        characterSessions.NotifyWeaknessRecovery(
            characterId,
            DungeonWeaknessPolicy.FullStamina);
    }

    return Results.Ok(result);
});

app.MapPost("/api/client-pay/premium", async (
    ClientPremiumGrantRequest request,
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    CancellationToken token) =>
{
    var result = await store.GrantPremiumServiceToAccountAsync(
        request.Account,
        request.ServiceType,
        request.Days,
        token);
    if (!result.Success)
    {
        return Results.BadRequest(result);
    }

    var characters = await store.GetCharactersAsync(result.Account, token);
    foreach (var character in characters)
    {
        characterSessions.NotifyPremiumInfo(
            character.Id,
            result.ServiceType,
            result.RemainingSeconds);
    }
    foreach (var characterId in result.WeaknessRecoveredCharacterIds ?? [])
    {
        characterSessions.NotifyWeaknessRecovery(
            characterId,
            DungeonWeaknessPolicy.FullStamina);
    }

    return Results.Ok(result);
});

app.MapPost("/api/accounts", async (CreateAccountRequest request, JsonGameStore store, CancellationToken token) =>
{
    var result = await store.CreateAccountAsync(request, token);
    if (result.Created)
    {
        return Results.Created($"/api/accounts/{result.Account!.UserName}", result.Account);
    }

    return string.Equals(result.Error, "账号已存在。", StringComparison.Ordinal)
        ? Results.Conflict(new { error = result.Error })
        : Results.BadRequest(new { error = result.Error });
});

app.MapPut("/api/accounts/{userName}/password", async (
    string userName,
    ChangeAccountPasswordRequest request,
    JsonGameStore store,
    CancellationToken token) =>
{
    var result = await store.ChangePasswordAsync(userName, request, token);
    return result switch
    {
        { Success: true } => Results.Ok(result),
        { Failure: AccountPasswordUpdateFailure.InvalidCredentials } => Results.Unauthorized(),
        _ => Results.BadRequest(result)
    };
});

app.MapPost("/api/accounts/{userName}/password/reset", async (
    string userName,
    ResetAccountPasswordRequest request,
    JsonGameStore store,
    CancellationToken token) =>
{
    var result = await store.ResetPasswordAsync(userName, request, token);
    return result switch
    {
        { Success: true } => Results.Ok(result),
        { Failure: AccountPasswordUpdateFailure.InvalidRecoveryInformation } => Results.Unauthorized(),
        _ => Results.BadRequest(result)
    };
});

app.MapPost("/api/accounts/{userName}/characters", async (
    string userName,
    CreateCharacterRequest request,
    JsonGameStore store,
    CancellationToken token) =>
{
    var result = await store.CreateCharacterAsync(userName, request, token);
    return result.Created
        ? Results.Created($"/api/accounts/{userName}/characters/{result.Character!.Id}", result.Character)
        : Results.BadRequest(new { error = result.Error });
});

app.MapPut("/api/characters/{characterId:guid}/fatigue", async (
    Guid characterId,
    SetCharacterFatigueRequest request,
    JsonGameStore store,
    CancellationToken token) =>
{
    if (request.UsedFatigue > GameProtocolEngine.BlackDiamondMaximumFatigue)
    {
        return Results.BadRequest(new
        {
            error = $"Used fatigue must be between 0 and {GameProtocolEngine.BlackDiamondMaximumFatigue}."
        });
    }

    var character = await store.SetCharacterUsedFatigueAsync(
        characterId,
        request.UsedFatigue,
        token);
    return character is null
        ? Results.NotFound()
        : Results.Ok(character);
});

app.MapGet("/api/characters/{characterId:guid}/mail", async (
    Guid characterId,
    JsonGameStore store,
    CancellationToken token) =>
    Results.Ok(await store.GetCharacterMailsAsync(characterId, token)));

app.MapPost("/api/characters/{characterId:guid}/mail", async (
    Guid characterId,
    CreateCharacterMailRequest request,
    JsonGameStore store,
    CharacterSessionRegistry sessions,
    CancellationToken token) =>
{
    var result = await store.SendCharacterMailAsync(characterId, request, token);
    if (!result.Created)
    {
        return Results.BadRequest(new { error = result.Error });
    }

    sessions.NotifyMailReceived(characterId);
    return Results.Created(
        $"/api/characters/{characterId}/mail/{result.Mail!.Id}",
        result.Mail);
});

app.MapPost("/api/characters/{characterId:guid}/message", (
    Guid characterId,
    SendCharacterMessageRequest request,
    CharacterSessionRegistry sessions) =>
{
    if (string.IsNullOrEmpty(request.Message))
    {
        return Results.BadRequest(new { error = "Message must not be empty." });
    }

    if (!Enum.IsDefined(request.MessageType))
    {
        return Results.BadRequest(new
        {
            error = "MessageType must be a defined DFLegacy client message type."
        });
    }

    byte[] messageBytes;
    try
    {
        messageBytes = clientTextEncoding.GetBytes(request.Message);
    }
    catch (EncoderFallbackException)
    {
        return Results.BadRequest(new
        {
            error = "Message contains characters that cannot be encoded as CP936."
        });
    }

    if (messageBytes.Length >= 0x100)
    {
        return Results.BadRequest(new
        {
            error = "CP936-encoded message must be shorter than 256 bytes."
        });
    }

    if (!sessions.NotifyMessage(
            characterId,
            request.MessageType,
            request.TargetAreaUserId,
            messageBytes))
    {
        return Results.Conflict(new { error = "Character is not online." });
    }

    return Results.Ok(new
    {
        queued = true,
        characterId,
        request.MessageType,
        request.TargetAreaUserId,
        encodedLength = messageBytes.Length
    });
});

app.MapPost("/api/characters/{characterId:guid}/popup", (
    Guid characterId,
    SendCharacterPopupRequest request,
    CharacterSessionRegistry sessions) =>
{
    if (string.IsNullOrEmpty(request.Message))
    {
        return Results.BadRequest(new { error = "Message must not be empty." });
    }

    byte[] messageBytes;
    try
    {
        messageBytes = clientTextEncoding.GetBytes(request.Message);
    }
    catch (EncoderFallbackException)
    {
        return Results.BadRequest(new
        {
            error = "Message contains characters that cannot be encoded as CP936."
        });
    }

    if (messageBytes.Length >= 0x100)
    {
        return Results.BadRequest(new
        {
            error = "CP936-encoded message must be shorter than 256 bytes."
        });
    }

    if (!sessions.NotifyPopup(characterId, messageBytes))
    {
        return Results.Conflict(new { error = "Character is not online." });
    }

    return Results.Ok(new
    {
        queued = true,
        characterId,
        encodedLength = messageBytes.Length
    });
});

app.MapGet("/", () => Results.Text(
    "DFLegacy emulator is running. GET /api/status, /api/sessions, /api/packets, /api/accounts\n",
    "text/plain; charset=utf-8"));

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Admin API ready at http://{Host}:{Port}", options.Admin.Host, options.Admin.Port);
    var scripts = app.Services.GetRequiredService<ScriptFileSystem>();
    if (!scripts.FileExists("etc/channel_info.etc"))
    {
        app.Logger.LogWarning(
            "etc/channel_info.etc is missing from Script.pvf. Entrance handshake works, but protocol 9 download is disabled.");
    }
});

await app.RunAsync();
