# 合成/强化包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Craft/CraftPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 本组四条命令成功后统一推 NOTI 14 增量更新(格式见 [packet-detail-item-inventory-60cn.md](packet-detail-item-inventory-60cn.md))。

## CMD 27 `ENUM_CMDPACKET_COMPOUND_ITEM` — 物品合成 ✅

`TryParseCompoundItemCommand`(body 恰 8B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | source | 配方物品 id 或背包槽位 |
| 4 | u8 | isItemId | source 是否为物品 id(≤1) |
| 5 | u8 | craftCount | 制作数量(非 0) |
| 6 | u8 | reserved | 必须 =0 |
| 7 | u8 | fixed | 必须 =1 |

**应答**:
- 成功(`CreateCompoundItemReply`,变长):`[1, consumedCount u8] + m×{listType u8, slot u16, remainingCount u32} + [resultCount u8] + k×{slot u16, itemId u16, countOrValue u32, state u8, durability u16}`
- 失败 2B:`[0, errorCode]`

发包字节(示例):`01 1B 12 00 00 00 AA 3E FB 68 01 00 70 7A 5A 1A 7A 5A`

## CMD 83 `ENUM_CMDPACKET_UPGRADE_ITEM` — 强化 ✅

`TryParseUpgradeItemCommand`(body 8B;要求 targetItem≠0 且 targetSlot≠materialSlot):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | targetSlot | 目标装备槽 |
| 4 | u16 | targetItemId | 目标物品 id |
| 6 | u16 | materialSlot | 材料槽 |

**应答**(`CreateUpgradeItemReply`;客户端解析见 `sub_40F290` case 0x53,处理块 0x416176,登记见 [client-function-register-60cn.md](client-function-register-60cn.md) §16):

| 宽度 | 字段 | 说明 |
|---:|---:|---|
| u8 | result | =1 |
| u16 | materialSlot | |
| u32 | materialRemaining | 材料余量 |
| u8 | resultCode | 结果码:0 成功、1 失败等级不变、2 失败降级、3 毁坏 |
| u8 | newLevel | 强化后等级 |
| u16 | targetSlot | |
| u8 | rewardCount | 仅 resultCode>2 时附加;毁坏补偿条目数 |
| 补偿条目 × rewardCount | | 仅 resultCode>2:每条 `{slot u16, itemId u16, count u32}`;count 为**增量**——客户端对槽内同 id 物品在现有数量上**累加**(物品 vtable+0x38 @0x461AC0:`add 现有值+入参`),槽内无物品则按该数量新建(`sub_51AB60` @0x41638e);服务端须下发本次发放量,不得发槽内最终数量 |

- resultCode=3(毁坏)时**目标物品必须仍在目标槽上**:客户端先查目标槽、查不到即报串 1658(「缺少物品」,0x41624a),再由应答自身移除目标、弹毁坏结果窗(`sub_799EF0(..., 0x44, 0, resultCode, 0)` @0x4162e2;窗构造 `sub_7C2570`、参数处理 `sub_7F20C0`:resultCode=3 发 `EQUIP_BROKEN` 事件并显示串 1672「装备已被破坏」)并写入补偿物品(静默入包,无逐条提示)。故服务端毁坏时应答先于背包快照发送(与 CMD 27 先例一致);非毁坏路径维持 DF2008 实测的「快照→应答」顺序。2026-10-11 真机复测:毁坏弹窗与物品消失符合预期(见 CHANGELOG FIX-14)。
- 毁坏补偿物品由服务端 `etc/upgrade.etc` 可选 `[disjoint]` 段定义(60CN 自带数据无该段→不补偿;段式样见参考工程 `Tool/PvfLib/Models/UpgradeTableFile.cs`,A21 参考值已入库 `Server/DAFLegacy.Server/upgrade.etc`,其补偿物品 3243 强烈之痕迹在 60CN 物品表中不存在,不可照搬);亦可用 `server.json` → `Reinforcement.DestroyBonus` 整节点覆写(`ItemId>0` 时取代 PVF 段,语义与校验见 `docs/design/02-portability-contracts.md` R5)。
- 失败应答 2B:`[0, errorCode]`;错误码→文案映射(0x41645a):4→1658、7→20000、10→1660、13→1654、17→1652、22→1661、99→30005、其余→1662 带码格式化。

发包字节(示例):`01 53 12 00 00 00 27 0E E6 50 01 00 1A 7A 47 9A 5B 7A`

## CMD 84 `ENUM_CMDPACKET_RESET_ITEM_ATTR` — 属性重置 ✅

`TryParseResetItemAttributeCommand`:入参布局与 CMD 83 完全相同(targetSlot/targetItemId/materialSlot)。

**应答**:
- 成功 9B:`[1, materialSlot u16, materialRemaining u32, operationType u16]`
- 失败 2B:`[0, errorCode]`

发包字节(示例):`01 54 12 00 00 00 27 0E E6 50 01 00 1A 7A 47 9A 5B 7A`

## CMD 102 `ENUM_CMDPACKET_COMPOUND_AVATAR` — 时装合成 ✅

`TryParseCompoundAvatarCommand`(body 恰 15B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | materialSlot | 材料槽 |
| 4 | u16 | marker1 | 第一物品列表标记 |
| 6 | u16 | firstItemId | 第一件时装 id |
| 8 | u16 | marker2 | 第二物品列表标记 |
| 10 | u16 | secondItemId | 第二件时装 id |
| 12 | u16 | marker3 | 选项列表标记 |
| 14 | u8 | selectedOption | 选中选项行 |
| 15 | u8 | firstSlot | 第一件槽位 |
| 16 | u8 | secondSlot | 第二件槽位 |

**应答**:
- 成功(变长,`CreateCompoundAvatarReply`):`[1, consumedCount u8] + m×{listType u8, slot u16, remainingCount u32} + resultSlot u16 + resultItemId u16 + remainingSeconds u32 + selectedOption u8`
- 失败 2B:`[0, 4]`(默认错误码 4)

发包字节(示例):`01 66 0C 00 00 00 BE 23 C2 58 01 00` *
