# 角色状态包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `GameProtocolEngine.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。

## CMD 9 `ENUM_CMDPACKET_RECOVER_STAMINA` — 疲劳度恢复(购疲劳) ✅

正文布局未在服务端解析器中单独确认。

**应答**:
- 成功 5B:`[1, gold u32]`(回包含剩余金币)
- 错误 2B:`[0, errorCode]`

发包字节(示例):`01 09 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 4 `ENUM_NOTIPACKET_STAMINA` — 疲劳度 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | stamina | 当前疲劳值 |

回包字节(示例):`00 04 07 00 00 00 47`

## CMD 43 `ENUM_CMDPACKET_DIE_CHARACTER` — 角色死亡/复活 ✅

**应答**:通用成功(复活流程由 NOTI 32 呈现)。

发包字节(示例):`01 2B 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 32 `ENUM_NOTIPACKET_DIE_STATE` — 死亡/复活状态 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |
| 2 | u8 | alive | 1=复活,0=死亡 |

回包字节(示例):`00 20 09 00 00 00 D2 D6 D6`

## NOTI 36 `ENUM_NOTIPACKET_FATIGUE` — 疲劳度同步 ✅

城镇与副本共用(进图消耗后同步)。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | used | 已用疲劳 |
| 2 | u16 | max | 上限(默认 156;黑钻 188) |
| 4 | u16 | premium | 付费疲劳 |

回包字节(示例):`00 24 0C 00 00 00 FE D6 A4 D6 D6 D6`

## NOTI 37 `ENUM_NOTIPACKET_EXP` — 经验/升级/SP ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | level | 等级 |
| 1 | u32 | exp | 累计经验 |
| 5 | u32 | extraA | 附加经验 A |
| 9 | u32 | extraB | 附加经验 B |
| 13 | u16 | sp | 技能点 |

回包字节(示例):`00 25 15 00 00 00 FA 32 16 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 DE D6`

## NOTI 48 `ENUM_NOTIPACKET_PVP_RECORD` — PVP 战绩 ✅

13338 客户端 case 48:五个 i32 + 两个字节。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | i32 | win | 胜场 |
| 4 | i32 | lose | 负场 |
| 8 | i32 | points | PVP 点数 |
| 12 | i32 | rank | 当前段位分 |
| 16 | i32 | nextRank | 下一档段位分 |
| 20 | u8 | grade | 段位 |
| 21 | u8 | gradeExt | 段位扩展 |

回包字节(示例):`00 30 1C 00 00 00 FE D6 D6 D6 C2 D6 D6 D6 47 D6 D6 D6 14 C6 D6 D6 A5 C2 D6 D6 DA D6`

## 未实现(仅状态)

- NOTI 134 `SP_RETURN_INFO`:SP 返还 ❌
