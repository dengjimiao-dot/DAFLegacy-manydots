using System.IO.Compression;
using System.Security.Cryptography;

namespace DFLegacy.Protocol;

public static class EntranceCrypto
{
    public const int BlockSize = 16;

    public static byte[] EncryptBlocks(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> keyMaterial)
    {
        var paddedLength = Math.Max(BlockSize, ((plaintext.Length + BlockSize - 1) / BlockSize) * BlockSize);
        var padded = new byte[paddedLength];
        plaintext.CopyTo(padded);
        return Transform(padded, keyMaterial, encrypt: true);
    }

    public static byte[] DecryptBlocks(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> keyMaterial)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % BlockSize != 0)
        {
            throw new InvalidDataException("Encrypted entrance payload is not block aligned.");
        }

        return Transform(ciphertext, keyMaterial, encrypt: false);
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

    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        using var source = new MemoryStream(input.ToArray());
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] EncryptCompressed(ReadOnlySpan<byte> input, ReadOnlySpan<byte> keyMaterial) =>
        Compress(EncryptBlocks(input, keyMaterial));

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> keyMaterial, bool encrypt)
    {
        if (keyMaterial.Length < BlockSize)
        {
            throw new ArgumentException("At least 16 key bytes are required.", nameof(keyMaterial));
        }

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = keyMaterial[..BlockSize].ToArray();

        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(input.ToArray(), 0, input.Length);
    }
}
