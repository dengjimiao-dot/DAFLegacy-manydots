# 包头结构与加解密(60A1 客户端反编译实证)

> 本文是全部收发包共用的帧层约定;各协议的逐包字段布局见 [packet-detail-*-60cn.md](client-packet-enums-60cn.md) 分组索引所列详细文档。
> 实现状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md)。
> 发包加密的实测数据、密钥调度推导与函数逐行分析见 [client-send-crypto-analysis-60cn.md](client-send-crypto-analysis-60cn.md)(2026-09 实测修正:初始种子为确定值,非会话随机)。

## 发包(客户端→服务端, CMD) — 总长 = 12 + 正文长度,打包函数 `sub_8B24C0`

```
偏移 0   : type(1B)          0=NOTI 1=CMD
偏移 1   : protocolId(1B)    CMD 枚举值
偏移 2   : totalLength(4B)   = 12 + 正文长度(含序号2B)
偏移 6   : CRC32(4B)         加密前对 [序号2B+明文正文] 计算 (sub_8B23B0→sub_8B31C0, 标准查表CRC32)
偏移 10  : sequence(2B)      每发包自增
偏移 12  : 正文(密文)        仅正文加密,序号为明文
```

正文加密 `sub_777310`:每包推进 LCG `sub_717CF0`(`x=x*0x41C64E6D+0x3039` 连推3步,
rv=第3步高10位^((第2步高10位^第1步高11位<<10)<<10)),`key=rv低8位,rot=(rv bit8..10)&7`
(rv 在加密器内被截断为 16 位,实际只有低 11 位参与),逐字节 `密文=ROR(明文^key,rot)`。
**密钥调度是确定的**(2026-09 抓包实测修正):每次连接 LCG 状态重置为 `1`,每个加密 CMD 推进一次,
与序号一一对应;两次会话相同序号密钥完全相同。旧记录"初始种子15位随机、CRC即密钥协商"不成立——
CRC 只是校验;服务端 `GameClientCipherState` 的盲试因"能产生相同签名的种子均通过 CRC 筛选"而仍可用,
但可替换为确定性密钥表(实测 155/157 加密帧逐字节复现,见 client-send-crypto-analysis-60cn.md)。
例外:`CHECK_CONNECTION`(CMD 0)走直发路径不加密不推进,CRC 按全零明文计算,wire 正文为固定 `0x75`×8 填充。

## 回包(服务端→客户端, NOTI) — 总长 = 6 + 载荷长度

```
偏移 0   : type(1B)          = 0 (NOTI)
偏移 1   : protocolId(1B)    NOTI 枚举值
偏移 2   : totalLength(4B)   = 6 + 载荷长度
偏移 6   : 载荷(密文)        整体加密,无序号无CRC
```

客户端解密器 `sub_776F80` 空种子时随机值取常量 0x3EB5:`key=0xB5,rot=6`,
逐字节 `明文=ROL(密文,6)^0xB5`,与服务端 `GamePayloadCipher` 的 `ROR(明文^0xB5,6)` 严格互逆(Python 回环实测一致)。
反编译复核:rv 同样以 `__int16` 截断后取用(与发包加密器一致),空种子走 `n16053=0x3EB5` 常量路径。

## 字节示例约定

各 packet-detail 文档引用的字节示例:

- **回包字节** = 服务端 `GameServerPacket.Encode()` 的真实输出(密文)。
- **发包字节** = 客户端帧格式,示例统一用种子 `0x1234`、序号 `0x0001`(真实会话中种子逐包推进、序号自增)。
- 带 `*` 的发包示例正文仅含 2 字节序号占位(帧长 0x0C),不代表实际正文字段;实际布局以各文档字段表(来自服务端解析器)为准。

## 服务端实现对照

| 客户端函数 | 服务端对应 | 说明 |
|---|---|---|
| `sub_8B24C0` 发包打包器 | `PacketFrame` / `GameServerPacket.Encode()` | 填 12B/6B 头 |
| `sub_777310` 发包加密器 | `GameClientPayloadCipher` | `ROR(x^key,rot)`;实测为确定性密钥调度,盲试可换密钥表 |
| `sub_402030` 种子存档恢复 | —(无需对应) | 异或混淆存档 + 双份校验,种子链实测等效初始 1 |
| `sub_776F80` 收包解密器 | `GamePayloadCipher` | `ROL(x,rot)^key`,空种子=0xB5/6 |
| `sub_8B23B0`→`sub_8B31C0` CRC32 | `Crc32` | 标准查表 CRC32 |
| `sub_8F5110` 包体游标初始化 | — | 收包路径把全局游标指到解密后载荷起点 |
| `sub_8F5120/140/160` 包体读取器 | — | 依次读 u8/u16/u32,游标推进 1/2/4 |
| `sub_8F5180` 块读取器 | — | 按给定 n 读字节块(变长字符串=u32 长度+块读,dstr) |

> 逐包载荷布局与长度的确认步骤(NOTI/CMD 分发器定位、case 读取序列提取、长度公式推导、MCP 工具流)见 [confirm-reply-packet-format-60cn.md](confirm-reply-packet-format-60cn.md)。

## 关键函数地址(IDA 60A1)

| 功能 | 地址 | 说明 |
|---|---|---|
| LCG推进/通用RNG | `sub_717CF0` | 空种子时用 `dword_B165AC+=timeGetTime()` 作种 |
| 发包加密器 | `sub_777310` | `ROR(x^key,rot)`,种子历史异或存档,rv 截断16位仅低11位生效 |
| 种子存档恢复/校验 | `sub_402030` | `种子 ^ dword_CFD45C ^ (数组基址+4*槽号)` 双份互校 |
| 收包解密器 | `sub_776F80` | `ROL(x,rot)^key`,空种子=0xB5/6 |
| 发包打包器 | `sub_8B24C0` | 填12B头,序号自增;种子结构位于 socket+1434068 |
| 发包加密第二调用者 | `sub_78D800` | UDP/多播发送路径,密文可选混淆存档(`sub_901150` 开关) |
| CRC32 | `sub_8B23B0`→`sub_8B31C0` | 标准查表CRC32 |
| 包体游标初始化 | `sub_8F5110` | 收包路径把全局游标 `dword_BF29E0` 指到解密后载荷起点 |
| 包体读取器 | `sub_8F5120`/`sub_8F5140`/`sub_8F5160` | 依次读 u8/u16/u32,游标推进 1/2/4 |
| 块读取器 | `sub_8F5180` | 按给定 n 读字节块(变长字符串=u32 长度+块读) |
