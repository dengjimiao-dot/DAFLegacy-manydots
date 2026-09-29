using System.Buffers.Binary;

namespace DFLegacy.Protocol;

/// <summary>
/// A game packet sent from the channel server to this client build.
/// Unlike client commands, server packets only have the six-byte
/// type/id/length header. The complete payload is encrypted in place.
/// </summary>
public sealed record GameServerPacket(byte Type, byte ProtocolId, byte[] Payload)
{
    public const int HeaderLength = 6;

    public int TotalLength => checked(HeaderLength + Payload.Length);

    public byte[] Encode()
    {
        var output = new byte[TotalLength];
        output[0] = Type;
        output[1] = ProtocolId;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2, 4), (uint)TotalLength);
        Payload.CopyTo(output, HeaderLength);
        GamePayloadCipher.EncryptServerPayload(output.AsSpan(HeaderLength));
        return output;
    }

    public async ValueTask WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(Encode(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

/// <summary>
/// Implements the fixed server-to-client byte transform used by DNF.exe 1.0.1.9.
/// Passing a null seed to the native client decryptor selects 0x04453EB5:
/// rotate left six bits, then XOR B5. Server encryption applies its inverse.
/// </summary>
public static class GamePayloadCipher
{
    private const byte XorKey = 0xB5;
    private const int Rotation = 6;

    public static void EncryptServerPayload(Span<byte> payload)
    {
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = RotateRight((byte)(payload[index] ^ XorKey), Rotation);
        }
    }

    public static void DecryptServerPayload(Span<byte> payload)
    {
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(RotateLeft(payload[index], Rotation) ^ XorKey);
        }
    }

    private static byte RotateLeft(byte value, int count) =>
        (byte)((value << count) | (value >> (8 - count)));

    private static byte RotateRight(byte value, int count) =>
        (byte)((value >> count) | (value << (8 - count)));
}
