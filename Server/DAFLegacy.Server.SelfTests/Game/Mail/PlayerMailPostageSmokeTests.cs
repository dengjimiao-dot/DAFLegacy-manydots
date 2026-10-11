using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PlayerMailPostageSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(CharacterMailPostage.Calculate(0, false) == 100
                && CharacterMailPostage.Calculate(0, true) == 1000
                && CharacterMailPostage.Calculate(19, false) == 100
                && CharacterMailPostage.Calculate(20, false) == 101
                && CharacterMailPostage.Calculate(99_999, true) == 5999
                && CharacterMailPostage.Calculate(199_999, true) == 10_999
                && CharacterMailPostage.Calculate(200_000, true) == 11_000
                && CharacterMailPostage.Calculate(int.MaxValue, false) == 10_100,
            "DF2008 player postage uses base 100, attachment 900, floored five-percent gold tax and its 10000 cap");
        check(CharacterMailPostage.TryDebit(105_998, 99_999, true, out var exact) && exact == 0
                && !CharacterMailPostage.TryDebit(105_997, 99_999, true, out _)
                && !CharacterMailPostage.TryDebit(int.MaxValue, int.MaxValue, true, out _)
                && !CharacterMailPostage.TryDebit(1000, -1, false, out _),
            "mail gold checks include postage, accept exact funds and cannot overflow or turn an invalid transfer into a credit");

        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-mail-postage-{Guid.NewGuid():N}");
        try
        {
            var options = new ServerOptions { DatabasePath = Path.Combine(root, "state.db") };
            TestGameStore.SeedDefaultTestAccount(options);
            var store = TestGameStore.Create(options);
            await store.InitializeAsync();
            var sender = (await store.CreateCharacterAsync("test", new CreateCharacterRequest("PostageSender", Level: 1))).Character!;
            var recipient = (await store.CreateCharacterAsync("test", new CreateCharacterRequest("PostageTarget", Level: 1))).Character!;
            var source = new[] { new CharacterItemRecord(73, 3037, 10) };
            var remaining = new[] { new CharacterItemRecord(73, 3037, 7) };
            sender = (await store.SaveCharacterInventoryAsync("test", sender.Id, 200_000, source, []))!;
            var request = new CreateCharacterMailRequest(sender.Name, "player mail", Gold: 99_999,
                Attachment: new CharacterMailAttachmentRecord(3037, 3));

            var withoutFee = await store.SendCharacterMailFromCharacterAsync("test", sender.Id, recipient.Name,
                request, 100_001, remaining, []);
            check(!withoutFee.Sent && withoutFee.Failure == CharacterMailSendFailure.SenderStateChanged
                    && (await store.GetCharacterMailsAsync(recipient.Id)).Length == 0,
                "a caller cannot bypass authoritative postage by submitting a gold-only debit plan");
            _ = CharacterMailPostage.TryDebit(sender.Gold, request.Gold, true, out var balance);
            var delivered = await store.SendCharacterMailFromCharacterAsync("test", sender.Id, recipient.Name,
                request, balance, remaining, []);
            var clientBalanceAfterSuccess = 200_000 - 99_999 - 5999;
            check(delivered.Sent && delivered.Sender!.Gold == 94_002
                    && delivered.Sender.Gold == clientBalanceAfterSuccess
                    && delivered.Sender.Inventory!.Single().CountOrValue == 7
                    && delivered.Mail!.Gold == 99_999 && delivered.Mail.Attachment!.CountOrValue == 3,
                "gold, postage, stack deduction and mail creation commit together; the absolute wallet update matches CMD97's local debit and produces no refund");

            var textOnly = await store.SendCharacterMailFromCharacterAsync("test", sender.Id, recipient.Name,
                new(sender.Name, "text only"), 93_902, remaining, []);
            check(textOnly.Sent && textOnly.Sender!.Gold == 93_902 && textOnly.Mail!.Gold == 0
                    && textOnly.Mail.Attachment is null,
                "a text-only player mail also charges the basic 100-gold postage");
            var beforeFailure = textOnly.Sender!;
            var missingRecipient = await store.SendCharacterMailFromCharacterAsync("test", sender.Id, "MissingRecipient",
                new(sender.Name, "missing"), 93_802, remaining, []);
            check(!missingRecipient.Sent && missingRecipient.Failure == CharacterMailSendFailure.RecipientNotFound
                    && (await store.GetCharactersAsync("test")).Single(character => character.Id == sender.Id).Gold == beforeFailure.Gold,
                "recipient validation failure does not charge postage or consume attachments");

            _ = await store.SaveCharacterInventoryAsync("test", sender.Id, 99, remaining, []);
            var poor = await store.SendCharacterMailFromCharacterAsync("test", sender.Id, recipient.Name,
                new(sender.Name, "not enough"), 0, remaining, []);
            check(!poor.Sent && poor.Failure == CharacterMailSendFailure.InsufficientGold
                    && (await store.GetCharactersAsync("test")).Single(character => character.Id == sender.Id).Gold == 99
                    && (await store.GetCharacterMailsAsync(recipient.Id)).Length == 2,
                "insufficient postage funds fail without delivering a mail or altering the sender balance");
            var system = await store.SendCharacterMailAsync(recipient.Id,
                new(SystemMailTemplates.SenderName, "system delivery", Gold: 1));
            var reload = TestGameStore.Create(options);
            await reload.InitializeAsync();
            check(system.Created
                    && (await reload.GetCharactersAsync("test")).Single(character => character.Id == sender.Id).Gold == 99
                    && (await reload.GetCharacterMailsAsync(recipient.Id)).Length == 3,
                "player postage and deliveries persist across reload without applying player fees to admin/system mail");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
