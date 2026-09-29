# 60A1 客户端发包加密实测与逆向分析(CMD 加密、密钥调度、种子存档)

> 本文是对 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md) 发包(CMD)加密一节的实证与修正,
> 结论来源:①抓包 MCP(`Server/DFLegacy.Mcp`,127.0.0.1:12222)回放真实客户端会话 157 个 TCP CMD 帧,
> 逐帧独立复现解密并与服务端 `GameClientCipherState` 产物交叉验证;②IDA(60A1)反编译
> `sub_777310`/`sub_717CF0`/`sub_8B24C0`/`sub_776F80`/`sub_402030`。
> 客户端含 VMProtect 壳(`.vmp0`/`.SCY` 段),但下述函数均未虚拟化,可正常反编译。

## 一、实测结论(TL;DR)

1. **客户端 CMD 正文加密密钥不是会话随机的,而是确定的密钥调度**:
   每次 TCP 会话(连接)开始时 LCG 状态重置为 `1`,之后**每个加密 CMD 包推进一次**(每包连推 3 步),
   与包的命令序号 `sequence` 一一对应。两次独立登录会话中,相同序号的包密钥完全相同(实测证实)。
2. 每包密钥派生:`key = rv & 0xFF`,`rot = (rv >> 8) & 7`,其中 `rv` 是 LCG 连推 3 步的组合值,
   **加密器内部把 rv 截断为 16 位(`__int16`)后再取用,实际只有低 11 位有效**。
3. CRC32 覆盖 `[序号 2B(明文) + 加密前明文正文]`,157/157 帧全部命中。
4. **`CHECK_CONNECTION`(CMD 0)不走加密器**:CRC 按全零明文计算,序号恒为 0 且不推进密钥状态,
   wire 上的 8 字节正文是固定填充 `0x75`×8(填充发生在 CRC 计算之后)。
5. 服务端 `GameClientCipherState` 的"15 位种子盲试 + CRC 锁定"在真实流量上仍然可用,但并非必要——
   密钥调度是确定的,可直接预生成密钥表(见 §5 服务端建议)。

## 二、实测方法与数据

### 2.1 抓包流程

- 服务端 `server.json` 中 `DFLegacy.Mcp.Enabled = true`,端点 `http://127.0.0.1:12222/mcp`(Streamable HTTP,JSON-RPC)。
- 客户端登录、选角、进城、聊天产生完整会话流量;`packet_watch`(afterSeq=-1 回放)取全部留存事件。
- 每个 RX TCP CMD 事件带 `wireHex`(线路原始帧,含帧头与密文)与 `bodyHex`(服务端 `TryDecode` 产物,前 2B 序号 + 明文正文)。

### 2.2 会话组成(ring 内 157 个 RX TCP CMD)

| 包号 | 报文名 | 次数 |
|---|---|---|
| 0 | CHECK_CONNECTION(明文直发) | 4 |
| 1 | LOGIN | 4(两次会话 × 2) |
| 2/4 | SET_UDP_IP_PORT / SELECT_CHARACTER | 各 4 |
| 8/82/130 | GET_USERINFO / CALL_MEMER_LIST / REQUEST_BLACKLIST | 各 4 |
| 37/38 | SET_USER_POSITION / SET_USER_AREA | 162/4 |
| 17 | SEND_MESSAGE | 4 |
| 161 | ANTIBOT | 82 |
| 其余 | 组队/黑名单/生物脚本等 | 少量 |

### 2.3 判别实验:密钥只随序号变化

对每帧用服务端明文反推 `(rot, key)`(对 8 个 rot 枚举,要求全部字节自洽):

- `seqno=1 LOGIN` → `(rot=3, key=0x81)`;`seqno=2 ANTIBOT` → `(5, 0xFB)`;`seqno=3` → `(7, 0x54)`;
  `seqno=4` → `(5, 0xDF)`;`seqno=7` → `(1, 0x56)`;`seqno=9` → `(2, 0x87)`;`seqno=10` → `(0, 0x88)`;
  `seqno=11` → `(6, 0xEA)`;`seqno=12` → `(6, 0x7B)`。
- **两次独立会话(两次 LOGIN,序号各自从 1 重计)的相同 seqno 密钥完全相同** ⇒ 与任何会话随机量无关。

### 2.4 密钥调度求解

对种子空间做约束搜索(`bit16..26` 被首包 `a=0` 约束清零,候选 = 低 16 位 ∪ 高 5 位),以
"每加密包推进一次(3 步 LCG)、序号即推进序号"为模型,匹配全部 9 个观测签名的解:

```
0x1, 0x8000001, 0x10000001, ..., 0xF8000001   (共 32 个,全部是 0x1 | hi<<27)
```

由 LCG 乘法进位性质,输出的低 11 位只依赖状态低 11 位,上述解等价——**初始状态 = 0x1**。

### 2.5 全量验证

以初始状态 1 预生成密钥表,独立解密全部 157 帧:

- **155/157 加密 CMD 与服务端 `TryDecode` 产物逐字节一致**;
- 其余 2 帧是 `CHECK_CONNECTION`(见 §4),不属于加密路径。

前 24 项密钥表(可离线任意扩展):

```
seqno: key    rot          seqno: key    rot
   1 : 0x81   3             13 : 0xA1   5
   2 : 0xFB   5             14 : 0x54   3
   3 : 0x54   7             15 : 0x37   7
   4 : 0xDF   5             16 : 0x5B   4
   5 : 0xE1   1             17 : 0x79   2
   6 : 0xBF   6             18 : 0xE3   0
   7 : 0x56   1             19 : 0x8E   1
   8 : 0x47   4             20 : 0x99   7
   9 : 0x87   2             21 : 0x3F   5
  10 : 0x88   0             22 : 0x43   0
  11 : 0xEA   6             23 : 0x0D   5
  12 : 0x7B   6             24 : 0xA6   2
```

## 三、IDA 逆向(60A1)

### 3.1 发包加密器 `sub_777310`(0x777310)

```c
int __cdecl sub_777310(int a1, int a2, int a3)   // a1=data, a2=len, a3=种子结构指针
{
  v3 = a3;
  n16053 = 16053;                     // 0x3EB5:无种子时的固定 rv(见 3.5)
  if ( a3 )
  {
    a3 = sub_402030(a3);              // 从混淆存档恢复当前种子(含完整性校验)
    n16053 = sub_717CF0(&a3);         // rv = LCG 连推 3 步;状态就地更新(a3 = 第 3 步值)
    v5 = a3;
    // 新种子异或混淆后写回存档数组(对象 dword_CFD458 的 slot17/slot18 双份),
    // 索引 = *(DWORD*)(v3+4) 即种子结构里的槽号
    *(DWORD *)(*(DWORD *)dword_CFD458 + 17*4 + 4*slot) = a3 ^ dword_CFD45C ^ (arr17 + 4*slot);
    *(DWORD *)(*(DWORD *)dword_CFD458 + 18*4 + 4*slot) = v5 ^ dword_CFD45C ^ (arr18 + 4*slot);
  }
  v6 = HIBYTE(n16053) & 7;            // rot = (rv16 >> 8) & 7 —— 注意 n16053 是 __int16!
  ::n16053 = n16053;                  // rv 存入全局 0xBDC8BC
  for ( dword_BDC8C0 = v6; v8 < a2; ++v8 )
  {
    v9 = ::n16053 ^ data[v8];         // (rv & 0xFF) ^ 明文字节
    data[v8] = __ROR1__(v9, dword_BDC8C0);   // 密文 = ROR(明文 ^ key, rot)
  }
}
```

要点:

- **`n16053` 声明为 `__int16`**:LCG 返回的 32 位 rv 被截断为 16 位,`key` 取低 8 位、
  `rot` 取 bit8..10——**rv 只有低 11 位参与加密**。rv 的 32 位组合恰好是
  `c | b<<10 | a<<20` 无进位拼接(`a/b/c` 分别为第 1/2/3 步状态高位的掩码段),
  因此实际生效的是 `c`(第 3 步高 10 位)与 `b` 的最低位。
- 加密体:`密文 = ROR(明文 ^ key, rot)`;解密即 `明文 = ROL(密文, rot) ^ key`。
- 种子以**双份异或混淆数组**持久保存(见 3.4),跨包推进、跨调用持久。

### 3.2 LCG/RNG `sub_717CF0`(0x717CF0)

```c
int __cdecl sub_717CF0(int *a1)
{
  if ( !a1 ) { dword_B165AC += timeGetTime(); v1 = &dword_B165AC; }   // 空指针=以时间为种
  v2 = 1103515245 * *v1 + 12345;      // 0x41C64E6D, 0x3039
  v3 = 1103515245 * v2 + 12345;
  v4 = 1103515245 * v3 + 12345;
  *v1 = v4;                           // 状态就地为第 3 步值
  return ((((HIWORD(v2) & 0x7FF) << 10) ^ HIWORD(v3) & 0x3FF) << 10) ^ HIWORD(v4) & 0x3FF;
}
```

- 常量 `0x41C64E6D/0x3039` 与既有记录一致;`HIWORD` = `(x >> 16) & 0xFFFF`。
- 这是全游戏的通用 RNG(调用者遍布游戏逻辑);发包加密只是它的一个使用者。
- 返回值 32 位,但**加密器只消费其低 11 位**(3.1)。

### 3.3 发包打包器 `sub_8B24C0`(0x8B24C0,this = 网络套接字对象)

```c
*(WORD *)(buf + 10) = *(WORD *)(this + 1434064);              // sequence(自增计数)
*(DWORD *)(buf + 6) = sub_8B23B0(buf + 10, payloadLen + 2);   // CRC32 覆盖 [seq 2B + 明文正文]
sub_777310(buf + 12, payloadLen, this + 1434068);             // 加密正文;种子结构在 socket+1434068
*(DWORD *)(buf + 2) = payloadLen + 12;                        // totalLength
...
++*(WORD *)(this + 1434064);                                  // 发送后序号自增
```

证实帧布局(12B 头)与 CRC 覆盖范围;**种子结构内嵌于套接字对象**,每会话一份,
会话(连接)重建即重置——与 §2.4 实测"每次登录密钥表从头开始"吻合。

### 3.4 种子存档与完整性校验 `sub_402030`(0x402030)

```c
int __thiscall sub_402030(_DWORD *this)   // this = 种子结构: [+0]=混淆对象指针, [+4]=槽号
{
  if ( (dword_CFD45C ^ *this) - this != this[1] )   // 对象指针自校验
    sub_901470(this, 63);                           // 篡改则进入异常处理
  v4 = dword_CFD45C ^ (arr17 + 4*slot) ^ *(arr17 + 4*slot);   // 解混淆恢复种子
  if ( v4 != dword_CFD45C ^ (arr18 + 4*slot) ^ *(arr18 + 4*slot) )
    sub_901470(this, 64);                           // 双份不一致则异常
  return v4;
}
```

- 种子从不以明文落内存:存档值 = `种子 ^ dword_CFD45C ^ (数组基址 + 4*槽号)`,
  `sub_777310` 写回、`sub_402030` 恢复,slot17/slot18 双份互为校验。
- 该机制属反内存篡改设计,不影响算法本身(恢复出的种子链实测等效于初始 1)。
- 初始化写入点(种子=1 的来源)位于运行时路径,受 VMProtect 影响未在静态 IDB 中定位;
  §2.4 的实测结果已完全确定其对外行为。

### 3.5 收包解密器 `sub_776F80`(0x776F80)——服务端→客户端方向

```c
int __cdecl sub_776F80(int a1, int a2, int *a3)   // a1=data, a2=len, a3=种子指针(可空)
{
  n16053 = 16053;                       // 0x3EB5
  if ( a3 ) n16053 = sub_717CF0(a3);    // 非空则推进种子
  rot = HIBYTE(n16053) & 7;
  for (...) data[i] = ROL1(data[i], rot) ^ (::n16053 & 0xFF);
}
```

- 空种子(`a3=0`)时 rv 固定 `0x3EB5` → `key=0xB5, rot=6`,即既有文档记录的
  `明文 = ROL(密文, 6) ^ 0xB5`;与抓包中服务端 TX 密文使用的常量变换一致。
- 调用者:`sub_40D6D0`、`sub_78EE00`(NOTI 分发前置路径)。

## 四、CHECK_CONNECTION(CMD 0)例外

实测两帧 wire 帧头声明 CRC = `0xE38A6876` = `CRC32(00 00 + 00×8)`:

- CRC 按序号 `0x0000` + **全零明文** 计算;
- wire 上的 8 字节正文实为固定填充 `0x75`×8(在 CRC 计算之后写入);
- 序号恒为 0、不递增,也不推进密钥状态(若推进,后续 `seqno=1` 的密钥将错位一位,实测未发生);
- 与服务端既有结论一致:该包经 `CNGameSocket` 直发路径,不经过 `sub_8B24C0` 打包器。

## 五、对服务端实现的建议

`GameClientCipherState` 目前用"15 位候选种子盲试 + CRC 命中收窄"解密。由于真实密钥调度确定
(初始状态 1、每包推进一次),可以:

1. 预生成(或运行时生成)密钥表 `key[seqno], rot[seqno]`,O(1) 解密,不再盲试 32768 个候选;
2. CRC 从"筛选器"变为纯校验器(命中失败即报异常),`RejectBadCrc32` 语义可以启用;
3. 保留 CHECK_CONNECTION 的明文旁路语义(序号 0、不推进)。
4. 注意:加密器每包推进与"序号自增"一一对应的前提是**该包确实经过 `sub_8B24C0`**;
   任何走直发路径的 CMD(如 CHECK_CONNECTION)都不推进。

> 兼容性提醒:以上适用于 60A1(DNF.exe 1.0.1.9)。其他版本若复测发现密钥随会话变化,
> 再回到盲试模型。

## 六、验证脚本要点(Python 等价实现)

```python
MUL, ADD, M32 = 0x41C64E6D, 0x3039, 0xFFFFFFFF
f = lambda x: (x * MUL + ADD) & M32

def key_schedule(n):          # 第 n 个加密 CMD 的 (key, rot);初始状态 1
    s, out = 1, []
    for _ in range(n):
        x1, x2, x3 = f(s), f(f(s)), f(f(f(s))); s = x3
        rv = ((((x1 >> 16) & 0x7FF) << 10) ^ ((x2 >> 16) & 0x3FF)) << 10 ^ ((x3 >> 16) & 0x3FF)
        out.append((rv & 0xFF, (rv >> 8) & 7))
    return out

def decrypt_body(ct, key, rot):
    rol = lambda v, n: ((v << n) | (v >> (8 - n))) & 0xFF if n else v
    return bytes(rol(c, rot) ^ key for c in ct)
```

帧解析:`type=w[0], protocolId=w[1], totalLength=u32le(w[2:6]), crc32=u32le(w[6:10]),
sequence=u16le(w[10:12]), 密文正文=w[12:totalLength]`;
校验 `crc32(sequence 2B + 明文正文) == crc32`。
