# 城镇同步包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Town/TownPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。

## CMD 37 `ENUM_CMDPACKET_SET_USER_POSITION` — 位置上报 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | i16 | x | 坐标 X |
| 4 | i16 | y | 坐标 Y |
| 6 | u8 | dir | 朝向 |
| 7 | u16 | move | 移动值(默认 100) |

**应答**:无直接回包;服务端向同区域广播 NOTI 22。

发包字节(示例):`01 25 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 22 `ENUM_NOTIPACKET_USER_POSITION` — 位置广播 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |
| 2 | i16 | x | |
| 4 | i16 | y | |
| 6 | u8 | dir | |
| 7 | u16 | move | 移动值 |

回包字节(示例):`00 16 0F 00 00 00 D2 D6 47 D6 F5 D6 D2 47 D6`

## CMD 38 `ENUM_CMDPACKET_SET_USER_AREA` — 区域切换 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | town | 城镇 id |
| 3 | u8 | area | 区域 id |
| 4 | i16 | x | |
| 6 | i16 | y | |
| 8 | u8 | dir | |

**应答**:无直接回包;广播 NOTI 23 并向切入者发 NOTI 24。

发包字节(示例):`01 26 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 23 `ENUM_NOTIPACKET_USER_AREA` — 区域切换广播 ✅

回城(BACK_2_VILLAGE)也复用本包同步区域。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |
| 2 | u8 | town | |
| 3 | u8 | area | |
| 4 | i16 | x | |
| 6 | i16 | y | |
| 8 | u8 | dir | |
| 9 | u8 | active | 1=活跃城镇实体 |

回包字节(示例):`00 17 10 00 00 00 D2 D6 D2 D2 47 D6 F5 D6 D2 D2`

## NOTI 24 `ENUM_NOTIPACKET_AREA_USERS` — 区域内用户列表 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | town | |
| 1 | u8 | area | |
| 2 | u16 | count | 用户数 n |
| 4 | 记录×n | user | 每条 8B:{uid u16, x i16, y i16, dir u8, active u8} |

总长 = 4 + 8n。

回包字节(示例):`00 18 12 00 00 00 D2 D2 D2 D6 D2 D6 47 D6 F5 D6 D2 D2`

## NOTI 5 `ENUM_NOTIPACKET_DUNGEON_PERMISSION` — 副本进入权限/通关状态 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | count | 条目数 n |
| 2 | 记录×n | permission | 每条 3B:{dungeonId u16, clearState u8(上限 3)} |

总长 = 2 + 3n。

回包字节(示例):`00 05 0B 00 00 00 D2 D6 71 DA DA`
