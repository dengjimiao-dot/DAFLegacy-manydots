using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DFLegacy.Protocol;

/// <summary>
/// The old character-selection worker uses a connected UDP socket in addition
/// to the channel TCP connection. Each header and body is sent as a separate
/// datagram, so callers must preserve the packet boundaries.
/// </summary>
public static class LegacyDatagramProtocol
{
    public const int HeaderLength = 11;
    public const byte Version = 1;

    public static byte[] CreateHeader(byte protocolId, int payloadLength)
    {
        if (payloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
        }

        var header = new byte[HeaderLength];
        header[1] = protocolId;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2, 4), checked(HeaderLength + payloadLength));
        header[10] = Version;
        return header;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> datagram, out LegacyDatagramHeader header)
    {
        header = default;
        if (datagram.Length != HeaderLength)
        {
            return false;
        }

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(datagram.Slice(2, 4));
        if (totalLength < HeaderLength)
        {
            return false;
        }

        header = new LegacyDatagramHeader(
            datagram[0],
            datagram[1],
            totalLength,
            BinaryPrimitives.ReadInt32LittleEndian(datagram.Slice(6, 4)),
            datagram[10]);
        return true;
    }

    public static byte[] EncryptAesEcb(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        if (plaintext.Length == 0 || plaintext.Length % 16 != 0)
        {
            throw new ArgumentException("AES plaintext must contain complete 16-byte blocks.", nameof(plaintext));
        }

        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES key must be 16, 24, or 32 bytes.", nameof(key));
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    }

    public static byte[] DecryptAesEcb(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
        {
            throw new ArgumentException("AES ciphertext must contain complete 16-byte blocks.", nameof(ciphertext));
        }

        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES key must be 16, 24, or 32 bytes.", nameof(key));
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
    }

    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(input);
        }

        return output.ToArray();
    }
}

public readonly record struct LegacyDatagramHeader(
    byte Prefix,
    byte ProtocolId,
    int TotalLength,
    int Reserved,
    byte Version)
{
    public int PayloadLength => TotalLength - LegacyDatagramProtocol.HeaderLength;
}
