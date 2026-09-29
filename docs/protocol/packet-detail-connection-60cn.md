# 连接/握手包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Connection/ConnectionPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。

## CMD 0 `ENUM_CMDPACKET_CHECK_CONNECTION` — 连接校验 ✅

**方向**:客户端→服务端;**应答**:NOTI 0 + NOTI 1。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | 命令序号(逐包自增) |

正文无附加字段。

发包字节(示例):`01 00 0C 00 00 00 BE 23 C2 58 01 00`

## NOTI 0 `ENUM_NOTIPACKET_CHECK_CONNECTION` — 心跳回执 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | 16B | challenge | 挑战回执(明文,不做载荷加密) |

回包字节(示例):`00 00 16 00 00 00 D6 D2 DE DA C6 C2 CE CA F6 F2 FE FA E6 E2 EE EA`

## NOTI 1 `ENUM_NOTIPACKET_CHANNELINFO` — 频道信息(握手应答) ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | dstr | displayName | 频道显示名(CP936),< 64B |
| 4 | u32 | channelType | 频道类型(服务端固定 1) |
| 8 | u32 | result | 登录结果(1=成功) |
| 12 | u8 | serverId | 服务器编号 |
| 13 | u8 | channelNumber | 频道号;客户端缓存它用于 NOTI 130 频道字段比较(见 packet-detail-chat) |
| 14 | u32 | seed | 载荷解密种子 |
| 18 | u32 | hostCount | host 数量 n |
| 22 | dstr×n | hosts | 连接地址(每个 < 260B) |
| — | u32 | port1 | 游戏端口 |
| — | u32 | port2 | 备用端口 |

回包字节(示例):`00 01 3A 00 00 00 F2 D6 D6 D6 DB 77 53 6F 6F 43 67 56 12 D2 D6 D6 D6 D2 D6 D6 D6 D2 D2 06 9E D6 D6 D2 D6 D6 D6 F2 D6 D6 D6 12 1E 0A 6E 16 6E 16 6E 12 CA F2 D6 D6 CA F2 D6 D6`

## CMD 2 `ENUM_CMDPACKET_SET_UDP_IP_PORT` — UDP 打洞上报 ✅

**方向**:客户端→服务端;**应答**:NOTI 11(对端地址表)。

正文布局未在服务端解析器中确认(服务端仅注册 UDP 端点)。

发包字节(示例):`01 02 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 11 `ENUM_NOTIPACKET_USER_UDP_IP_PORT` — 对端 UDP 地址表 ✅

= 服务端 `CreateUdpPeerInfo`。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | count | 对端数量 n |
| 1 | 记录×n | peer | 每条 21B,见下 |

每条记录(21B):

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u16 | uid | 区域用户 id |
| 4B | localAddress | 内网 IPv4 |
| 4B | publicAddress | 公网 IPv4 |
| u16 | publicPort | 公网端口 |
| u32 | accountId | 账号 id |
| u8 | natType | NAT 类型 |
| u32 | mtu | MTU |

回包字节(示例):`00 0B 1C 00 00 00 D2 D2 D6 D5 74 D2 FE D2 DE DA C6 CA F2 47 D6 D6 D6 D2 37 C2 D6 D6`

## NOTI 26 `ENUM_NOTIPACKET_UDP_HOST` — UDP 房主指定 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | partyIndex | 队内房主序号(组队为 4 槽位制 0..3,见 [packet-detail-party-60cn.md](packet-detail-party-60cn.md);本文档原记 0..7 依据待复核) |

回包字节(示例):`00 1A 07 00 00 00 D6`

## 未实现(仅状态)

- CMD 141 `TIME_CHECK` / NOTI 133 `TIME_CHECK`:时间校验 ❌
- NOTI 135 `CHECK_FORCED_DISCONNECT`:强踢检测 ❌
