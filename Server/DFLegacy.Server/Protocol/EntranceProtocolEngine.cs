using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DFLegacy.Protocol;

public sealed class EntranceSessionState
{
    private static readonly byte[] SessionTokenPrefix = "dfls-local"u8.ToArray();

    public byte[] KeyMaterial { get; } = RandomNumberGenerator.GetBytes(32);
    // This client build later treats the 16-byte field as a C string. A fully
    // random block can contain no terminator and make it read past the field.
    public byte[] SessionToken { get; } = CreateSessionToken();
    public bool KeyExchangeCompleted { get; set; }

    private static byte[] CreateSessionToken()
    {
        var token = new byte[16];
        SessionTokenPrefix.CopyTo(token, 0);
        return token;
    }
}

public sealed record EntranceContent(byte[]? ChannelScript, byte[] Metadata)
{
    public bool HasChannelScript => ChannelScript is { Length: > 0 };
}

/// <summary>
/// Implements the entrance-channel exchange recovered from DNF.exe 1.0.1.9.
/// Protocol pairs are 11/12 (key), 5/6 (cache decision), 9/10 (ChannelScript.pvf),
/// and 1/3 (entrance metadata).
/// </summary>
public static class EntranceProtocolEngine
{
    public static PacketFrame? Handle(
        PacketFrame request,
        EntranceSessionState session,
        EntranceContent content)
    {
        return request.ProtocolId switch
        {
            11 => CreateKeyReply(session),
            5 => CreateCacheReply(session, content.HasChannelScript),
            9 => CreateScriptReply(session, content.ChannelScript ?? []),
            1 => CreateMetadataReply(session, content.Metadata),
            _ => null
        };
    }

    private static PacketFrame CreateKeyReply(EntranceSessionState session)
    {
        var payload = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), 0);
        session.KeyMaterial.CopyTo(payload, 4);
        session.KeyExchangeCompleted = true;
        return PacketFrame.Create(12, payload);
    }

    private static PacketFrame CreateCacheReply(EntranceSessionState session, bool hasChannelScript)
    {
        var plaintext = new byte[32];
        // 0 asks the client to download ChannelScript.pvf with protocol 9.
        // 1 reports a cache hit and skips that transfer.
        BinaryPrimitives.WriteInt32LittleEndian(plaintext.AsSpan(0, 4), hasChannelScript ? 0 : 1);
        session.SessionToken.CopyTo(plaintext, 4);
        return PacketFrame.Create(6, EntranceCrypto.EncryptBlocks(plaintext, session.KeyMaterial));
    }

    private static PacketFrame CreateScriptReply(EntranceSessionState session, byte[] script) =>
        PacketFrame.Create(10, EntranceCrypto.EncryptCompressed(script, session.KeyMaterial));

    private static PacketFrame CreateMetadataReply(EntranceSessionState session, byte[] metadata) =>
        PacketFrame.Create(3, EntranceCrypto.EncryptCompressed(metadata, session.KeyMaterial));
}
