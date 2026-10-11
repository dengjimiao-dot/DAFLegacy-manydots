using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

public enum CharacterCreationFailure
{
    None,
    InvalidName,
    InvalidRequest,
    AccountNotFound,
    RosterFull,
    NameUnavailable,
    PersistenceFailed
}

public sealed record CreateCharacterResult(
    bool Created,
    CharacterRecord? Character,
    string? Error = null,
    CharacterCreationFailure Failure = CharacterCreationFailure.None);

public static class CharacterCreationReply
{
    public static GameServerPacket Create(PacketFrame request, CreateCharacterResult result)
    {
        if (request.Type != GameProtocolConstants.CommandPacketType
            || request.ProtocolId != (byte)CmdPacketType.CREATE_CHARACTER)
        {
            throw new ArgumentException("Character creation requires a CMD5 request.", nameof(request));
        }

        if (result.Created)
        {
            return ConnectionPacketBuilders.CreateCommandReply(request, success: true);
        }

        // DF2008 0x0040FB43: success -> 680; failure 2 -> 681,
        // 4 -> 682, 0x12 -> 683, and 0x13 -> the invalid-state message 684.
        var code = result.Failure switch
        {
            CharacterCreationFailure.RosterFull => (byte)0x04,
            CharacterCreationFailure.InvalidName or CharacterCreationFailure.NameUnavailable => (byte)0x12,
            CharacterCreationFailure.InvalidRequest or CharacterCreationFailure.AccountNotFound => (byte)0x13,
            _ => (byte)0x02
        };
        return ConnectionPacketBuilders.CreateCommandError(request, code);
    }
}
