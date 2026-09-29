using System.Buffers.Binary;

namespace DFLegacy.Protocol;

/// <summary>
/// DNF.exe 1.0.1.9 uses a packed ten-byte transport header:
/// type(1), protocol id(1), total length(4), CRC32(4).
/// The first byte after the header is normally a record count.
/// </summary>
public sealed record PacketFrame(byte Type, byte ProtocolId, uint DeclaredCrc32, byte[] Body)
{
    public const int HeaderLength = 10;
    public const int DefaultMaximumLength = 4 * 1024 * 1024;

    public int TotalLength => checked(HeaderLength + Body.Length);
    public byte RecordCount => Body.Length == 0 ? (byte)0 : Body[0];
    public uint ComputedCrc32 => Crc32.Compute(Body);
    public bool HasValidCrc32 => DeclaredCrc32 == 0 || DeclaredCrc32 == ComputedCrc32;

    public byte[] Encode()
    {
        var output = new byte[TotalLength];
        output[0] = Type;
        output[1] = ProtocolId;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2, 4), (uint)TotalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(6, 4), DeclaredCrc32);
        Body.CopyTo(output, HeaderLength);
        return output;
    }

    public static PacketFrame Create(byte protocolId, ReadOnlySpan<byte> payload, byte type = 0,
        byte recordCount = 1, bool includeCrc32 = false)
    {
        var body = new byte[payload.Length + 1];
        body[0] = recordCount;
        payload.CopyTo(body.AsSpan(1));
        var crc = includeCrc32 ? Crc32.Compute(body) : 0;
        return new PacketFrame(type, protocolId, crc, body);
    }

    public static async ValueTask<PacketFrame?> ReadAsync(
        Stream stream,
        int maximumLength = DefaultMaximumLength,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        var hasHeader = await ReadExactOrEofAsync(stream, header, cancellationToken);
        if (!hasHeader)
        {
            return null;
        }

        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        if (totalLength < HeaderLength || totalLength > maximumLength)
        {
            throw new InvalidDataException($"Invalid frame length {totalLength}.");
        }

        var body = new byte[checked((int)totalLength - HeaderLength)];
        if (body.Length > 0 && !await ReadExactOrEofAsync(stream, body, cancellationToken))
        {
            throw new EndOfStreamException("The peer closed in the middle of a frame.");
        }

        return new PacketFrame(
            header[0],
            header[1],
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6, 4)),
            body);
    }

    public async ValueTask WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(Encode(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<bool> ReadExactOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (count == 0)
            {
                return false;
            }

            offset += count;
        }

        return true;
    }
}
