using System.Text;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SystemMailSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(936,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var attachment = new CharacterMailAttachmentRecord(10_000, 1, State: 5, Durability: 1000,
            SealState: 1, EquipmentQualitySeed: 123_456, InstanceId: Guid.NewGuid());
        foreach (var reason in Enum.GetValues<SystemItemMailReason>())
        {
            var mail = SystemMailTemplates.CreateItemReturn(attachment, reason);
            check(mail.Sender == "游戏管理员" && mail.Text.Contains("请注意查收", StringComparison.Ordinal)
                    && !mail.Text.Contains("DAFLegacy", StringComparison.Ordinal)
                    && !mail.Text.Contains("delivered", StringComparison.Ordinal)
                    && ReferenceEquals(mail.Attachment, attachment)
                    && encoding.GetString(encoding.GetBytes(mail.Sender)) == mail.Sender
                    && encoding.GetString(encoding.GetBytes(mail.Text)) == mail.Text,
                $"automatic mail reason {reason} uses the Chinese administrator/template without losing CP936 text or attachment metadata");
        }
        var repair = SystemMailTemplates.CreateItemReturn(attachment, SystemItemMailReason.InventoryRepair);
        check(!repair.Text.Contains("空间不足", StringComparison.Ordinal)
                && repair.Text.Contains("物品栏整理", StringComparison.Ordinal),
            "login inventory repair does not incorrectly claim every recovered item was blocked by a full bag");

        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-system-mail-{Guid.NewGuid():N}");
        try
        {
            var options = new ServerOptions
            {
                DatabasePath = Path.Combine(root, "state.db"),
                ScriptPvfPath = "",
                SkillScriptPath = root
            };
            var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
            var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
            var defaultAdmin = new AdminSendMailRequest(Text: "请查收邮件。", Gold: 1);
            var customAdmin = new AdminSendMailRequest("Custom Sender", "Custom content", Gold: 1);
            check(AdminMailComposer.TryCompose(defaultAdmin, items, out var defaultMail, out _)
                    && defaultMail.Sender == "游戏管理员" && defaultMail.Text == defaultAdmin.Text
                    && AdminMailComposer.TryCompose(customAdmin, items, out var customMail, out _)
                    && customMail.Sender == customAdmin.Sender && customMail.Text == customAdmin.Text,
                "admin mail defaults to the Chinese system sender while preserving explicitly supplied sender and body text");

            TestGameStore.SeedDefaultTestAccount(options);
            var store = TestGameStore.Create(options);
            await store.InitializeAsync();
            var created = await store.CreateCharacterAsync("test", new CreateCharacterRequest("SystemMailTest", Level: 1));
            var character = created.Character!;
            var request = SystemMailTemplates.CreateItemReturn(attachment, SystemItemMailReason.DungeonClearReward);
            var sent = await store.SendCharacterMailAsync(character.Id, request);
            var reloadedStore = TestGameStore.Create(options);
            await reloadedStore.InitializeAsync();
            var reloaded = (await reloadedStore.GetCharactersAsync("test"))
                .Single(candidate => candidate.Id == character.Id);
            var saved = reloaded.Mailbox!.Single(mail => mail.Id == sent.Mail!.Id);
            check(sent.Created && saved.Sender == "游戏管理员" && saved.Text == request.Text
                    && saved.Attachment == attachment && saved.Attachment!.InstanceId == attachment.InstanceId,
                "automatic system mail persists its Chinese sender/body and exact equipment UUID/quality/reinforcement across reload");

            var wire = MailboxPacketBuilders.CreateMailboxList([new GameMailEntry(saved.Id,
                encoding.GetBytes(saved.Sender), encoding.GetBytes(saved.Text), 0,
                new GameMailAttachment(attachment.ItemId, attachment.EquipmentQualitySeed!.Value,
                    attachment.State, attachment.Durability, attachment.SealState),
                saved.SentAt, saved.ExpiresAt)]);
            using var payload = new MemoryStream(wire.Payload);
            using var reader = new BinaryReader(payload);
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadUInt32();
            var packageSender = ReadField(reader, encoding);
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt16();
            _ = reader.ReadByte();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt16();
            _ = reader.ReadByte();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadInt16();
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var bodySender = ReadField(reader, encoding);
            var bodyText = ReadField(reader, encoding);
            check(wire.ProtocolId == 97 && packageSender == "游戏管理员" && bodySender == packageSender
                    && bodyText == request.Text,
                "NOTI97 package and body records carry the same CP936 Chinese sender and ungarbled automatic return text");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string ReadField(BinaryReader reader, Encoding encoding) =>
        encoding.GetString(reader.ReadBytes(checked((int)reader.ReadUInt32())));
}
