# 邮箱包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Mailbox/MailboxPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。

## CMD 99 `ENUM_CMDPACKET_MAILBOX_OPEN` — 打开邮箱 ✅

**应答**:3B `[1, notLoadedCount i16]`;随后服务端推送 NOTI 97(可能分包)。

发包字节(示例):`01 63 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 97 `ENUM_NOTIPACKET_MAILBOX_MAIL_LIST` — 邮件列表快照 ✅

变长(分包快照,`CreateMailboxList`)。第一段是**奖励包裹列表**(纯文本/存档邮件不在其中),第二段是全部可见邮件的正文记录:

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u8 | packageCount | 包裹数 n |
| u8 | reset | 0=重置列表,1=追加 |
| 记录×n | package | 见下 |
| i16 | notLoadedCount | 未加载邮件数 |
| u16 | bodyCount | 正文记录数 m |
| 记录×m | body | 见下 |

包裹记录(带附件/金币的邮件):

| 宽度 | 字段 | 说明 |
|---:|---:|---|
| u32 | mailId | |
| dstr | sender | 发件人(1..29B) |
| u32 | gold | 附带金币 |
| u16 | itemId | 附件物品(无=0) |
| u8 | seal | 封印态 |
| u32 | countOrValue | 数量/数值 |
| u16 | durability | 耐久 |
| u8 | state | 附件状态 |
| u32 | sentAt | 发送时间 |
| u32 | mailId | 关联字段(连接包裹与正文记录) |

⚠ 此处附件四元组字节序为 seal→value→durability→state,**早于 NOTI 13 的物品布局**,两者不同。

正文记录:

| 宽度 | 字段 | 说明 |
|---:|---:|---|
| u32 | mailId | |
| u32 | expiresAt | 过期时间 |
| dstr | sender | |
| dstr | text | 正文(≤511B) |
| u32 | sentAt | |
| u16 | state | 1/2=收件箱,3=存档(经 mailId 与上方包裹关联) |

分包规则(`CreateMailboxSnapshot`):存档邮件逐封单独成包(空包裹承载包头 id),收件箱邮件合为一包。

回包字节(示例):`00 61 49 00 00 00 D2 D6 D2 D6 D6 D6 DE D6 D6 D6 CB E3 75 DA D6 D6 91 CA D6 D2 D6 D6 D6 FE D6 D6 D6 11 9B 43 D2 D6 D6 D6 D6 D6 D2 D6 D2 D6 D6 D6 D6 9D F3 7B DE D6 D6 D6 CB E3 C2 D6 D6 D6 F7 43 67 67 6B D6 11 9B 43 D2 D6`

## CMD 98 `ENUM_CMDPACKET_MAILBOX_EXTRACT_ITEM` — 取件 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u32 | mailId | 非 0 |

**应答**:5B `[1, mailId u32]`;随后推 NOTI 98。

发包字节(示例):`01 62 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 98 `ENUM_NOTIPACKET_MAILBOX_REMOVE_MAIL` — 移除邮件 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u32 | count | 移除数 n |
| 4 | u32×n | mailId | |

总长 = 4 + 4n。回包字节(示例):`00 62 0E 00 00 00 D2 D6 D6 D6 D2 D6 D6 D6`

## CMD 97 `ENUM_CMDPACKET_MAILBOX_SEND` — 发送邮件 ✅

`TryReadMailboxSend`:

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | dstr | recipient | 收件人(1..29B,名字 ≤20 字符) |
| — | u32 | gold | 附带金币(≤i32.MaxValue) |
| — | u8 | listType | 附件列表类型 |
| — | u16 | slot | 附件槽位 |
| — | u16 | itemId | 附件物品(0=纯文本/金币) |
| — | u32 | countOrValue | 附件数量/数值 |
| — | dstr | text | 正文(≤511B),必须包尾对齐 |

**应答**:无直接回包;收件人在线时收 NOTI 99 提醒。

发包字节(示例):`01 61 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 99 `ENUM_NOTIPACKET_MAILBOX_ALARM` — 新邮件提醒 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | newMailCount | 新邮件数 |

总长 2B。回包字节(示例):`00 63 08 00 00 00 DE D6`

## CMD 144 `ENUM_CMDPACKET_CHANGE_LETTER_STAT` — 邮件状态变更 ✅

`TryReadMailboxState`(body 恰 8B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u32 | mailId | 非 0 |
| 6 | u16 | state | 0/2/3(0=未读恢复,2=已读,3=存档) |

**应答**:7B `[1, mailId u32, state u16]`;状态快照变化重推 NOTI 97。

发包字节(示例):`01 90 0C 00 00 00 BE 23 C2 58 01 00` *
