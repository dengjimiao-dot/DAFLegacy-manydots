# 未分组已实现包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `GameProtocolEngine.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 本组收录未归入功能分组的已实现协议。

## NOTI 3 `ENUM_NOTIPACKET_USER_STATE` — 用户状态 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |
| 2 | u8 | state | 状态值 |

总长 3B。

## NOTI 120 `ENUM_NOTIPACKET_EVENT_INFO` — 活动信息 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u32 | featureFlags | 活动功能开关位图 |

总长 4B。

## CMD 41 `ENUM_CMDPACKET_USE_SKILL` — 使用技能 ✅

副本内使用技能;正文布局未单独确认。

**应答**:1B `[1]`(通用成功)。方块(_cube)消耗品事务由随后的 NOTI 14 增量更新完成。

## CMD 82 `ENUM_CMDPACKET_CALL_MEMER_LIST` — 个人商店轮询 ✅

⚠ 此版枚举名 `CALL_MEMER_LIST`,服务端将其作为个人商店状态轮询应答。

**应答**(`CreatePrivateStorePollReply`,15B):

| 宽度 | 字段 | 说明 |
|---:|---:|---|
| u8 | result | =1 |
| u16 | localStoreState | 本地商店状态(空=0) |
| 4B | storeName | 商店名(空) |
| u16 | storeFlags | 标志 |
| u32 | firstValue | 第一个商店值 |
| u32 | secondValue | 第二个商店值 |
| u8 | extraCount | 附加商店数(0) |

## CMD 130 `ENUM_CMDPACKET_REQUEST_BLACKLIST` — 黑名单查询(枚举错配) ✅

⚠ 此版 130 = `REQUEST_BLACKLIST`,但服务端引擎按 `ExchangeServerCharacterInfoCommand`(跨服角色信息交换)应答 —— 存在错配风险,见主索引"版本差异与备注"。

**应答**(`CreateExchangeServerCharacterInfoReply`):2B `[1, characterCount u16=0]`。

## CMD 153 `ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG` — 新手引导完成上报 ✅

正文含引导模块号 u32(=31 时判定完成,`IsTutorialCompletionModule`);引导副本 id=10000、地图 id=61000。

**应答**:通用成功;角色引导位图经 NOTI 2 subtype 1 的 completedTutorialFlags 字段同步。
