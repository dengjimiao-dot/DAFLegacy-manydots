namespace DFLegacy.Protocol;

/// <summary>
/// 组队命令面（CMD 10-14 请求体、NOTI 7-10 块布局）的 TCP 报文模型。原与
/// <see cref="GameDatagramProtocol"/> 同文件；这些类型不含 UDP 数据报结构，
/// 属频道命令面，故独立成册。
/// </summary>

/// <summary>One PARTY_INFO roster slot. UserId 0xFFFF marks an empty slot;
/// the state byte is stored on the client member record at +0x1A.</summary>
public sealed record PartyWireSlot(ushort UserId, byte State);

/// <summary>One PARTY_INFO block. Block types: 0 full formation, 1 settings
/// only, 2 roster only, 3 clear (no payload beyond id and type). The 60A1
/// reader (0x41B8F9) reads kind 0/1 settings as the bare pair u8 title kind +
/// u8 user max and renders kind-0 titles from its local string 0x272, so no
/// title text ever crosses the wire downstream.</summary>
public sealed record PartyInfoWireBlock(
    int PartyId,
    byte BlockType,
    byte TitleKind,
    byte UserMax,
    IReadOnlyList<PartyWireSlot> Members,
    byte LeaderSlot);

/// <summary>SET_PARTY_INFO body (after the two-byte sequence number):
/// u8 title kind, an optional u32-prefixed custom title, u8 user max.</summary>
public sealed record PartySettingsRequest(byte TitleKind, byte[] TitleBytes, byte UserMax);

/// <summary>REQUEST_PEER body: u16 target uid, u8 request type, u32 peer id.</summary>
public sealed record PeerRequestFrame(ushort TargetUserId, byte RequestType, uint PeerId);

/// <summary>RESPONSE_PEER body: u16 peer uid, u8 response type, u32 value.
/// A type-0 response with a zero value is the party accept.</summary>
public sealed record PeerResponseFrame(ushort PeerUserId, byte ResponseType, uint Value);
