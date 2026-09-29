# 商店/货币包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Shop/ShopPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> NPC 金币商店(CMD 21)在 [packet-detail-item-inventory-60cn.md](packet-detail-item-inventory-60cn.md)。

## CMD 67 `ENUM_CMDPACKET_BUY_CERASHOP_ITEM` — 点券商城购买 ✅

`TryReadCeraShopPurchase`:

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | giftMode | 0=自购,1=赠送 |
| 3 | u32 + n | recipient | 仅赠送:dstr 收件人名(1..30B) |
| — | u8 | count | 商品数(1..32) |
| — | 记录×count | product | 每条 7B:{productType u8, attributeValue u16, commodityNo u32(非 0)} |

**应答**:
- 成功 `CreateCeraShopBuyReply`:`[1, 0, category u8(0..3), commodityNo u32 ×3(相同值)] + FFFF u16 + count u16 + n×{itemId u16, count u32} + FFFF u16 + 0 u32` = 25+6n;三个 commodityNo 相同 selects 正常购买完成路径。
- 错误 17B:`[0, errorCode, category u8, commodityNo u32, 0 u32, 0 u32, 0 u16]`

随后推 NOTI 53 余额更新。

发包字节(示例):`01 43 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 68 `ENUM_CMDPACKET_GEN_CERATICKET` — 生成点券票据 ✅

**应答**(`CreateCeraTicketReply`):`[1, keyLen u32, billingKey(1..63 ASCII), ticketNumber u32]` = 9+len(key)。

发包字节(示例):`01 44 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 53 `ENUM_NOTIPACKET_CERA` — 点券余额更新 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | ok | 成功标志 |
| 1 | u32 | cera | 最新点券余额 |

回包字节(示例):`00 35 0B 00 00 00 D2 F4 9A D6 D6`

## NOTI 66 `ENUM_NOTIPACKET_CERA_SPECIALITEM` — 会员服务通知 ✅

⚠ 此版 66 号枚举名为 `CERA_SPECIALITEM`,服务端实际发送会员服务(`PremiumService`)通知,需复核。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | action | 更新动作(服务端用 2) |
| 2 | u8 | serviceType | 服务类型(16=黑钻,22=霸王契约,27=大师契约) |
| 3 | u32 | remainSeconds | 剩余秒数 |

回包字节(示例):`00 42 0D 00 00 00 DE D6 96 D4 93 D2 D6`

## CMD 66 `ENUM_CMDPACKET_CERA` — 点券刷新查询 ✅

点券商城界面的「刷新点券」按钮。**请求无字段**,仅帧头与 seq 前缀(抓包 body 恒 2 字节)。

**应答不产生命令型回包**:服务端按当前余额推送 **NOTI 53**(`ShopPacketBuilders.CreateCeraUpdate(success: true, value)`),与余额变更时的推送是同一报文;客户端 NOTI 53 case(0x4255BA)按 `[u8 flag][u32 cera]` 读取并刷新点券显示,`flag != 0` 不触发提示行。

- 未选角时不应答(客户端只在游戏内商城界面发送)。
- 客户端发送器 `sub_85E1E0`(`BeginPacket(66)` → `EndPacket`,无载荷),按钮调用点 `sub_864E10`。

发包字节(抓包示例):`01 42 0C 00 00 00 D8 38 53 FC EB 00`(`EB 00` 为 seq)

## 未实现(仅状态)

- 本分册余下未实现条目见状态索引 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);CMD 66 已于 2026-09-29 实现(见上节)。
