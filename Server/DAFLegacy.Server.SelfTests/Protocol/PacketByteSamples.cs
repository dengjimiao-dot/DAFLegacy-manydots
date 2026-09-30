using System.Buffers.Binary;
using System.Text.Json;
using DAFLegacy.Protocol;

internal static class PacketByteSamples
{
    /// <summary>
    /// PacketDumper 黄金字节库(tools/packet_bytes.json):键为 "cmd:&lt;id&gt;" /
    /// "noti:&lt;id&gt;",值为完整线上帧 hex(10B 头 + body)。golden 测试直接消费,
    /// 保证 R0 起新链路的字节断言与真机样本同源(07 §7)。
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadAll()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "packet_bytes.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidDataException($"packet_bytes.json at {path} parsed to null.");
    }

    /// <summary>Decodes a full wire frame hex sample into its header and body.</summary>
    public static PacketFrame DecodeFrame(string hex)
    {
        var wire = Convert.FromHexString(hex);
        if (wire.Length < PacketFrame.HeaderLength)
        {
            throw new InvalidDataException("A packet_bytes sample is shorter than the 10-byte header.");
        }

        var body = new byte[wire.Length - PacketFrame.HeaderLength];
        Array.Copy(wire, PacketFrame.HeaderLength, body, 0, body.Length);
        return new PacketFrame(
            wire[0],
            wire[1],
            BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(6, 4)),
            body);
    }

    /// <summary>
    /// Decodes an outbound wire packet (6-byte type/id/length header plus the
    /// in-place encrypted payload) back into a GameServerPacket.
    /// </summary>
    public static GameServerPacket DecodeServerPacket(byte[] wire)
    {
        if (wire.Length < GameServerPacket.HeaderLength)
        {
            throw new InvalidDataException("Outbound wire bytes are shorter than the 6-byte header.");
        }

        var payload = new byte[wire.Length - GameServerPacket.HeaderLength];
        Array.Copy(wire, GameServerPacket.HeaderLength, payload, 0, payload.Length);
        GamePayloadCipher.DecryptServerPayload(payload);
        return new GameServerPacket(wire[0], wire[1], payload);
    }
}
