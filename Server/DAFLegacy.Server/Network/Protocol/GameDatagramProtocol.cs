using System.Buffers.Binary;
using System.Net;

namespace DAFLegacy.Protocol;

public sealed record GameNatReport(
    byte NatType,
    IPAddress LocalAddress,
    IPAddress PublicAddress,
    ushort PublicPort,
    uint Mtu);

public sealed record GamePeerInfo(
    ushort UserId,
    IPAddress LocalAddress,
    IPAddress PublicAddress,
    ushort PublicPort,
    uint AccountId,
    byte NatType,
    uint Mtu);

public sealed record GameDatagramApplication(
    byte Mode,
    ushort ProtocolId,
    byte[] Payload);

public sealed record GameMtUdpFrame(
    byte BlockType,
    uint Sequence,
    byte SenderPartyIndex,
    byte Flag,
    byte[] Application);

public static class GameDatagramProtocol
{
    public const int NatReportLength = 17;
    public const byte NatResponseType = 2;
    public const int NatKeepAliveLength = 10;

    /// <summary>CMD 2 第 4 个字段（偏移 13 起 u32）是客户端 ICMP/MTU 探测的结果：
    /// 客户端经 icmp.dll 向探测主机发 1472 字节 ping（register §10 `sub_78C1B0`），
    /// 复位占位为 -1（`sub_78CCD0` 的 this+436）。参考服把它当 MTU 校验
    /// 576..65507（`net/peer_address.h` 的 `PeerAddress::Valid`），本服按同区间
    /// 归一化后进 NOTI 11（见 <c>GameplayDatagramService.NormalizeNatReport</c>）。</summary>
    public const uint MinimumReportedMtu = 576;

    /// <inheritdoc cref="MinimumReportedMtu"/>
    public const uint MaximumReportedMtu = 65507;

    /// <summary>未上报成员与探测失败成员的 NOTI 11 通告 MTU（与现行默认值一致）。</summary>
    public const uint FallbackReportedMtu = 1472;

    /// <summary>
    /// NOTI 11 记录里通告给客户端的 NAT 字节（数据面路由开关，2026-10-08 IDA 定论）：
    /// 客户端 MT-UDP 发送函数按对端记录的该字节二选一——非 0 时把数据报套上
    /// `{u16 type, u16 len, u32 源账号, u32 目标账号}` 交给其 TCP 中继通道（本服未
    /// 实现，配置为惰性值），0 时走直连 UDP，目标即记录里的 IP:端口 = 本服通告的
    /// 监听端点。故本服一律通告 0，使组队数据报落到自己的 UDP 中继上。
    /// </summary>
    public const byte AdvertisedDirectNatType = 0;

    public const byte ReliableBlockType = 1;
    public const byte UnreliableBlockType = 2;
    public const ushort SetActiveObjectHpProtocol = 12;
    public const int ApplicationHeaderLength = 7;
    public const int MtUdpHeaderLength = 9;

    private const uint ApplicationCipherKey = 0x002F1214;
    private const byte ApplicationXorKey = (byte)(ApplicationCipherKey & 0xff);
    private const int ApplicationRotation = (int)((ApplicationCipherKey >> 8) & 7);

    public static bool TryParseNatReport(
        ReadOnlySpan<byte> commandBody,
        out GameNatReport report)
    {
        report = null!;
        if (commandBody.Length < NatReportLength)
        {
            return false;
        }

        report = new GameNatReport(
            commandBody[2],
            new IPAddress(commandBody.Slice(3, 4)),
            new IPAddress(commandBody.Slice(7, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(commandBody.Slice(11, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(commandBody.Slice(13, 4)));
        return true;
    }

    public static bool TryCreateNatProbeResponse(
        ReadOnlySpan<byte> request,
        IPEndPoint remote,
        out byte[] response)
    {
        response = [];
        // 探测请求恒 1 字节（客户端 sub_78C870 的 sendto 长度为 1，值 1/5；见
        // packet-detail-party-60cn.md「NAT 发现阶段定论」），其余长度一律畸形不回包。
        if (request.Length != 1 || request[0] is not (1 or 5))
        {
            return false;
        }

        var addressBytes = remote.Address.MapToIPv4().GetAddressBytes();
        response = new byte[7];
        response[0] = NatResponseType;
        for (var index = 0; index < addressBytes.Length; index++)
        {
            response[1 + index] = addressBytes[addressBytes.Length - index - 1];
        }

        BinaryPrimitives.WriteUInt16LittleEndian(
            response.AsSpan(5, 2),
            checked((ushort)remote.Port));
        return true;
    }

    public static bool IsNatKeepAlive(ReadOnlySpan<byte> request) =>
        request.Length == NatKeepAliveLength && request[0] == 0;

    public static byte[] CreateSetActiveObjectHpApplication(
        ushort objectType,
        ushort objectId,
        uint hp,
        uint secondaryValue = 0,
        byte mode = 1)
    {
        Span<byte> payload = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, objectType);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[2..], objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], hp);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], secondaryValue);
        return CreateApplication(mode, SetActiveObjectHpProtocol, payload);
    }

    public static byte[] CreateApplication(
        byte mode,
        ushort protocolId,
        ReadOnlySpan<byte> plaintextPayload)
    {
        var output = new byte[ApplicationHeaderLength + plaintextPayload.Length];
        output[0] = mode;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(1, 2), protocolId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(3, 4),
            Crc32.Compute(plaintextPayload));
        EncryptApplicationPayload(
            plaintextPayload,
            output.AsSpan(ApplicationHeaderLength));
        return output;
    }

    public static bool TryParseApplication(
        ReadOnlySpan<byte> application,
        out GameDatagramApplication parsed)
    {
        parsed = null!;
        if (application.Length < ApplicationHeaderLength)
        {
            return false;
        }

        var payload = new byte[application.Length - ApplicationHeaderLength];
        DecryptApplicationPayload(
            application[ApplicationHeaderLength..],
            payload);
        var declaredCrc = BinaryPrimitives.ReadUInt32LittleEndian(application.Slice(3, 4));
        if (Crc32.Compute(payload) != declaredCrc)
        {
            return false;
        }

        parsed = new GameDatagramApplication(
            application[0],
            BinaryPrimitives.ReadUInt16LittleEndian(application.Slice(1, 2)),
            payload);
        return true;
    }

    public static byte[] CreateUnreliableFrame(
        uint sequence,
        byte senderPartyIndex,
        byte flag,
        ReadOnlySpan<byte> application)
    {
        if (senderPartyIndex >= 8)
        {
            throw new ArgumentOutOfRangeException(nameof(senderPartyIndex));
        }

        if (application.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(application));
        }

        var output = new byte[MtUdpHeaderLength + application.Length];
        output[0] = UnreliableBlockType;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(1, 4), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(5, 2),
            checked((ushort)application.Length));
        output[7] = senderPartyIndex;
        output[8] = flag;
        application.CopyTo(output.AsSpan(MtUdpHeaderLength));
        return output;
    }

    public static bool TryReadBlock(
        ReadOnlySpan<byte> datagram,
        out GameMtUdpFrame? frame,
        out int consumed)
    {
        frame = null;
        consumed = 0;
        if (datagram.Length == 0)
        {
            return false;
        }

        if (datagram[0] == 0)
        {
            if (datagram.Length < 8)
            {
                return false;
            }

            var bitCount = datagram[6];
            consumed = 8 + (bitCount >> 3);
            return consumed <= datagram.Length;
        }

        if (datagram[0] is not (ReliableBlockType or UnreliableBlockType)
            || datagram.Length < MtUdpHeaderLength)
        {
            return false;
        }

        var applicationLength = BinaryPrimitives.ReadUInt16LittleEndian(
            datagram.Slice(5, 2));
        consumed = MtUdpHeaderLength + applicationLength;
        if (consumed > datagram.Length || datagram[7] >= 8)
        {
            consumed = 0;
            return false;
        }

        frame = new GameMtUdpFrame(
            datagram[0],
            BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(1, 4)),
            datagram[7],
            datagram[8],
            datagram.Slice(MtUdpHeaderLength, applicationLength).ToArray());
        return true;
    }

    private static void EncryptApplicationPayload(
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext)
    {
        for (var index = 0; index < plaintext.Length; index++)
        {
            ciphertext[index] = RotateRight(
                (byte)(plaintext[index] ^ ApplicationXorKey),
                ApplicationRotation);
        }
    }

    private static void DecryptApplicationPayload(
        ReadOnlySpan<byte> ciphertext,
        Span<byte> plaintext)
    {
        for (var index = 0; index < ciphertext.Length; index++)
        {
            plaintext[index] = (byte)(
                RotateLeft(ciphertext[index], ApplicationRotation)
                ^ ApplicationXorKey);
        }
    }

    private static byte RotateLeft(byte value, int count) =>
        (byte)((value << count) | (value >> (8 - count)));

    private static byte RotateRight(byte value, int count) =>
        (byte)((value >> count) | (value << (8 - count)));
}
