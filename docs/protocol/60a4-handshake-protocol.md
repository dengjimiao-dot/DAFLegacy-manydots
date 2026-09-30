# DNF.exe 60A4 (DNF_ACT1_0918) 入口握手协议文档

> 来源：对 `DNF_ACT1_0918/60A4/DNF.exe` 的静态逆向（2026-09-28）。
> 用途：与本项目 `Server/DAFLegacy.Server/Network/`（`PacketFrame` / `EntranceHandshakeReplies` /
> `EntranceCrypto` / `EntranceMetadata`）做一致性核对。仅用于自有程序兼容性研究。
>
> ⚠️ 样本注意：本样本带 VMProtect 壳，`ChannelScript.pvf` 字符串为**脱壳者手动注入的节点**，
> 原版程序只加载 `Script.pvf`。待补充原版 DNF.exe 后需重新核对 §5 与 §7 中的相关条目。
>
> **版本边界**：本文分析的是 60A4 样本（另一版本客户端），与 60A1 帧协议（
> [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)、
> [client-send-crypto-analysis-60cn.md](client-send-crypto-analysis-60cn.md)）互不适用——
> 本文的入口握手为 10B 帧/AES，60A1 为 12B/6B 帧 + LCG/ROR。两样本的数据布局差异
> （如 `.rdata 0xB54170` 包名表在 60A4 样本中不存在，见 §10）属样本差异，非记录矛盾。

## 1. 样本信息

| 项目 | 值 |
|------|----|
| 文件 | `DNF_ACT1_0918/60A4/DNF.exe` |
| MD5 | `e6bdbde1b4ee44caa8290b5f27d0b0e4` |
| 大小 | 17,231,872 字节 |
| 类型 | PE32 GUI, Intel 80386 |
| ImageBase | 0x400000（`.text/.rdata/.data` 及全部节 raw==VA-0x400000） |
| 壳 | VMProtect（`.vmp0` @ VA 0x1460000，另有 13 个 `nsn` 节） |
| 伴生文件 | `DNF_clean.exe`（MD5 `4630b0d0a2813ccda5128df25f0b9f8e`），脱壳重建版：节被改名（nsn→.data1/.text2/.idata/...），去掉 `.vmp0`，`.text/.rdata/.data` 大小与原文件一致 |
| zlib | 1.2.3（deflate/inflate 版权串） |

本文地址若无说明均为 VA（= 文件偏移 + 0x400000）。

## 2. 相关 RTTI 类型（MSVC `.?AV...@@`）

- `CNLoginModule`、`CNEntranceModule`、`CNEntranceChannel`、`CNSelectDungeonModule`、
  `CNGameSocket`、`CNRDUdpModule`、`iRelayPacketReciever`、`UdpPacketHandler`
- 加密类：`ICryptoGraph` 接口 + `CNCryptoRijndael`（另有 Anubis/BlowFish/Cast5/Kasumi/
  Khazad/Multi2/Noekeon/Rc6/Shift/Skipjack/Tea/TwoFish/XTea 等一组实现）
- 虚表：
  - `CNEntranceChannel` @ 0xb08b48：`0x9c1810, 0x9bf9a0, 0x9bc740, ...`
  - `CNEntranceModule` @ 0xb08b78：`0x9c5390, 0x9c2ce0, ...`
  - `CNCryptoRijndael` @ 0xc3ea2c（包装器，内部对象 +8，转发到 0x9f9a00~0x9f9c30）
  - `CNGameSocket` @ 0xc3ddc0

## 3. 传输帧结构（与 `PacketFrame.cs` 一致，已实证）

10 字节头 + body，全部小端：

```
偏移  长度  含义
+0    1    type（入口通道实测均为 0）
+1    1    protocolId
+2    4    totalLength（含 10 字节头）
+6    4    CRC32（可为 0 = 不校验）
+10   1    recordCount（实测恒为 1）
+11   n    载荷
```

实证点（`send`/`recv` 均为 ws2_32 IAT 直调，IAT：send@0xb0653c、recv@0xb06544）：

| VA | 内容 |
|----|------|
| 0x9bca50 | 发送协议 9：`proto=9, totalLen=0x0b`，CRC=0，recordCount=1，无载荷 |
| 0x9bda42 | 发送协议 11：`proto=0xb, totalLen=0x2b`，CRC=0，recordCount=1，随后单独 `send` 32 字节加密载荷 |
| 0x9bdc34 | 发送协议 5：`proto=5, totalLen=0x1b`，CRC=0，recordCount=1，随后单独 `send` 16 字节加密载荷 |
| 0x9bcb40 | 收包循环包装（select + recv，EWOULDBLOCK/WSAEINPROGRESS 等错误码处理） |

## 4. 密钥交换（协议对 11/12，与 `EntranceHandshakeReplies.TryCreateReply` 的协议 11 分支一致）

- **客户端请求（11）**：帧总长 0x2b = 10 头 + 1 recordCount + 32 字节载荷。载荷为
  一个 C 字符串（先 `strlen`）经会话加密对象（见 §6）加密成 32 字节。
- **回包校验（0x9bda90）**：
  - `cmp byte [ebp-0xb], 0xc` → protocolId 必须为 12，否则错误码 0x4020；
  - `(totalLength - 11) == 0x24` → body 为 1 + 36 字节，36 = **4 字节状态字 + 32 字节密钥**；
  - 状态字 ∈ {0, 1}，读入会话 +0x11c10，否则错误码 0x14000；
  - 再读 32 字节密钥，`rep movsd` 拷贝到会话 +0x11c14；
  - 状态为 0 时调用密钥扩展 0xa0c410：密钥长度 0x20（32 字节）但 **keySize=0x10、blockSize=0x10**，
    即 AES-128 只取前 16 字节。

与项目实现完全一致：`KeyMaterial` 32 字节、回包 4+32、AES-128 用前 16 字节。

## 5. 缓存判定（协议对 5/6）

- 请求（5）：16 字节加密载荷（§3 表中 0x9bdc34）。
- 回包（0x9bdc80 接收）：校验 `proto==6`、recordCount==1、`11 < totalLength ≤ 11+0x100`，
  然后按会话加密对象块大小做整除校验并解密。

## 6. 会话加密对象（与 `EntranceCrypto` 一致）

- 会话内偏移：`+0x1178c` ICryptoGraph 包装器（内部 +8 指向 Rijndael 实现），
  `+0x11c0c` 已连接标志，`+0x11c10` 状态字，`+0x11c14` 32 字节密钥材料。
- 算法：AES-128（S-box @0xc4aa68、逆 S-box @0xc4ab68），ECB 单块变换，无填充；
  输入长度不按块对齐返回错误码 `0x70000006`（对应错误串 "ERROR!! encrypt - 4byte align required"）。
- 变长包（pvf/元数据）流水线：**先 zlib 解压、再块对齐校验、后 AES 解密**
  （0x9bdf60 起的处理器；载荷上限 0x100000=1MB）。与项目
  `EncryptCompressed = Compress(EncryptBlocks(...))` 互为镜像，顺序正确。
- 密钥交换前，会话加密对象已用**固定初始密钥**初始化（密钥表 0xc4dce8 常量，
  见 0x9bc9ce / 0x9bdb87 两处 0xa0c410 调用）——协议 11 请求体就是用它加密的。
- 另发现一对 dword 级混淆函数（0x9bc7a0 / 0x9bc7e0，`ror 0x13` / `xor 0x02a886ae`，
  互为逆运算），不属于入口握手链路，疑为其他通道的数据混淆，待查。

## 7. 大块内容回包（协议 9/10 及元数据）

- 0x9bca50 发送协议 9 请求后，变长大包回包由 §6 的通用流水线处理。
- **⚠️ 样本注意**：`ChannelScript.pvf` 字符串（@0x83cc80）是脱壳者手动注入的节点，
  原版程序只加载 `Script.pvf`（@0xc1c5f8 处还有 `Script.pvf` 引用，xref @0x7d47fb）。
  项目当前 `EntranceHandshakeReplies` 以协议 5 回包引导客户端走协议 9 下载
  `ChannelScript.pvf` 的路径需在拿到原版 DNF.exe 后重新核对（原版可能直接读本地
  `Script.pvf`，或协议 9 的语义/文件名不同）。
- 接收端还实测到一个项目未实现的协议号 **18 (0x12)**（0x9bdfa8 校验），同样走
  "解压→解密"变长流水线。遇到未知回包协议号时先对照该函数。

## 8. 入口元数据素材（对照 `EntranceMetadata`）

- 0x83cc34 起九个大区名（内存升序）：`anton, seria, ruke, hilder, casillas, prey, siroco, diregie, cain`
  —— 与项目 `EntranceMetadata.ServerGroups` 同一组名字，但**项目数组为 cain→anton，顺序相反**。
  不影响握手；若客户端按下标渲染大区列表会反序，待实测确认。
- 相邻字符串：`ChannelScript.pvf`(注入)、`dnf_game`、`Neople`、`Etc/pvpChannelList.etc`、
  `ChannelInfo.txt`、调试串 `192.168.8.20` / `test pvp channel` 等。
- 客户端 PVF 包格式侧（`Pack.cpp`, `RDAR/Library/PackSVN`）：`Pack::_encrypt`、
  "4byte align required"、"CRC failed" 等——为 PVF 文件加密，与网络协议无关。

## 9. 与项目实现的一致性结论

| 条目 | 结论 |
|------|------|
| 帧头 10 字节 {type, proto, totalLen(含头), CRC(可0)}, recordCount | ✅ 逐字节一致 |
| 协议对 11/12：请求 32 字节加密体；回包 4+32 密钥 | ✅ 一致 |
| AES-128，密钥取 32 字节材料前 16 字节，ECB 无填充、块对齐 | ✅ 一致 |
| 变长包：compress(encrypt(...))，客户端解压→解密 | ✅ 顺序一致 |
| 协议对 5/6、9 请求 | ✅ 一致 |
| 协议 10 / 1/3 接收端 | ⚠️ 未逐条定位（VMP 控制流分散），待原版样本 |
| `ChannelScript.pvf` 下载路径（协议 9/10） | ⚠️ 脱壳注入节点，原版只加载 `Script.pvf`，待核对 |
| 九大区名 | ✅ 集合一致；⚠️ 顺序与项目相反 |
| 额外协议号 18 | ➕ 项目未实现，见 §7 |

## 10. 后续工作

1. 取得原版 DNF.exe 后：核对协议 9/10 的文件名语义（`Script.pvf` vs 注入的
   `ChannelScript.pvf`）、协议 1/3 与 10 接收端协议号、协议 18 的用途。
2. 核对大区名下标顺序对选区界面渲染的影响。
3. 追查 0x9bc7a0/0x9bc7e0 的 `ror 0x13 / xor 0x02a886ae` 混淆对所属通道。
4. 项目 `Network/Core/PacketTypes.cs` 头注释引用的 `.rdata 0xB54170/0xB54430` 包名表在本样本中不存在
   （`ENUM_NOTIPACKET_*` 字符串未找到）；该地址来源（应为另一构建/dump）待回溯，
   属游戏服包枚举，不影响握手层。

## 11. 60A1 原版交叉核对（2026-09-30）

> 样本：60A1 IDA 库（输入本体 `60CNA1/DNF.exe`，PE32，ImageBase 0x400000，VMProtect+SCY 段，
> 22,541 函数中 748 具名，Hex-Rays 9.1）。本节为 §10 待办提供**跨版本参照**：60A1 与 60A4 同属
> 入口握手协议族，但号位语义与样本细节不同，**不据此判定 §10 各条完成**。函数/地址登记见
> [client-function-register-60cn.md](client-function-register-60cn.md) §10。

### 11.1 号位对照（跨版本）

| 语义族 | 60A4（本文 §3–§7） | 60A1 |
|---|---|---|
| 密钥交换 | 11/12 | 11/12（与本项目 `EntranceHandshakeReplies` 一致，真机登录路径已验证） |
| 缓存/版本判定 | 5/6（缓存判定） | 5/6（缓存判定，同上） |
| 脚本下载 | 9/10（`ChannelScript.pvf`，注入疑点见 §5/§7） | 9/10（`ChannelScript.pvf`，本地加载链见 11.2） |
| 入口元数据 | 1/3（接收端未逐条定位） | 1/3（消费侧解析器已定位，见 11.3） |
| 额外协议号 | 18（§7） | 未核验 |

号位本身引自本项目已真机验证的 `EntranceHandshakeReplies` 实现注释；本次 IDA 核验的是
**内容消费侧**（脚本加载链与元数据解析器），分派侧（协议号→处理器映射）位于未分析区域
（见 11.5 第 3 条），未重复定位。

### 11.2 `ChannelScript.pvf` 本地加载链（原版实证）

- 路径拼装（`sub_8916D0`，反编）：`GetModuleFileNameA` → `_splitpath` → `<exe 目录>\ChannelScript.pvf`
  （文件名 17 字符含 NUL），即客户端从**自身所在目录**读取该文件。
- 容器布局（保存侧 `sub_892060` 与加载侧 `sub_891D90` 双向一致，反编）：

  ```
  32B 密钥
  16B IV
  u32 明文长度（校验 0 < len < 0x100000，且为块大小整数倍；块大小经 sub_8C0930 取自加密上下文）
  密文（长度字节）
  ```

- 加载侧：读 32B 密钥 → `sub_8911D0` 原地变换 → `sub_8C1460(上下文, 密钥, 32, unk_A8DB00, 16, 16, 0, 0)`
  初始化，IV 为 `.rdata` 16 字节常量 `unk_A8DB00`；保存侧 IV 由加密上下文虚调用 `vtbl+8` 取出后随密钥落盘。
- 算法标注：密钥 32B、块 16B、带 IV，属 AES 族（60A4 §6 同族为 AES-128/ECB；此处密钥 32B 且带 IV，
  具体模式待逐 case）。Hex-Rays 视图两函数均以异常码 0..7 作控制流收尾，解密续段未逐 case 跟完。
- 明文按文本处理（`sub_891430`，反编）：`strlen` 后以 `ChannelScript.pvf` 名登记（`sub_77BBF0`）。
- **对 §5/§7 疑点的修正**：60A1 原版客户端存在本地 `ChannelScript.pvf` 加载链，与 `Script.pvf`
  游戏包加载链（`sub_7183B0`）并存。「脱壳者注入」判定仍限于 60A4 样本自身；「原版只加载
  `Script.pvf`」的推测按 60A1 证据修正。

### 11.3 入口元数据解析器（`sub_894B10`，反编）

布局（与本项目 `EntranceMetadata.BuildSingleLocalChannel` 逐字段一致）：

```
u32 组数 n
n × {
  20B 组名（固定宽；strncmp 0x13 命中内置表，见下）
  u32 频道数 m
  m × {
    20B 显示名（19 字符 + NUL，strncpy 0x13）
    u32 容量
    u32 在线
    16B 主机（15 字符 + NUL，strncpy 0xF）
    u32 端口
  }                        // 单频道记录 48B
}
```

- 组数为 0 → 抛 4096。
- 组名必须命中客户端内置 109 项名表 `Str1`（@0xb53d78，`strncmp` 19B），未命中抛 4096。
  表内容：前 9 项 `cain, diregie, siroco, prey, casillas, hilder, ruke, seria, anton`
  （与本项目 `EntranceMetadata.ServerGroups` 同序同集），其后 100 项 `china_1..china_100` 占位。
- 显示名 → 频道号（`sub_8929C0`，反编）：提取显示名中**全部**数字字符（0x30–0x39）拼接后 `atol`。
  即本项目「显示名前缀不得含数字」约束与 `"{Name} {ChannelNumber}"` 格式的客户端依据。
- (组下标+1, 频道号) 经 `sub_71FE80` 查客户端频道表，未命中返回 0，该频道不注册。
- 拥挤标志 = `100 × 在线 / 容量 > 95`（容量为 0 时恒拥挤）。
- 解析完成 → `sub_72A270("Etc/pvpChannelList.etc", 组名表, 109)` 应用 PvP 频道表（确切语义待逐 case），
  随后 `sub_891A20` 收尾。
- 收尾编排（`sub_894E50`，反编）：成功路径 = `sub_891430`（脚本登记）→ `sub_894B10`（元数据应用）→
  `sub_892060`（脚本落盘，条件 `this[29872]!=1 || this[29908]!=1`）；失败路径重试计数 `this[29976]`，
  不足 10 次错误码 89、否则 128（`sub_799EF0` 界面提示）。
- **调试通道**：客户端目录存在 `_ChannelTest` 文件时，把收到的元数据（`this+29928` 指针 /
  `this+29932` 长度）原样写入 `ChannelInfo.txt`——真机抓元数据字节的最短路径，可直接用于
  服务端元数据构造的对照校准。

### 11.4 RTTI 与字符串登记

- RTTI：`.?AVCNEntranceChannel@@` @0xafdb8c、`.?AVCNEntranceModule@@` @0xafdbac（与 §2 的 60A4 清单同族）。
- 字符串（均在本体文件内，代码引用已核）：`ChannelScript.pvf` @0xa85f08（`sub_891430`/`sub_8916D0`）、
  `Etc/channel_info.etc` @0xa6bfe4（引用点 0x720823 位于未定义代码区，消费函数待定位）、
  `Script.pvf` @0xa6b7f4（`sub_7183B0`）、`_ChannelTest` @0xa85ff0、`ChannelInfo.txt` @0xa85fe0。

### 11.5 遗留与待复核

1. `Etc/channel_info.etc` 的客户端消费函数未定位（引用点 0x720823 所在区域未分析）；本项目现以
   PVF 内该文件作为协议 9 脚本真源（`Network/Sessions/ChannelSessionSenders.cs` LoadContent），
   客户端解析行为待逆向后确认。
2. `sub_891D90`/`sub_892060` 的加解密续段与具体 AES 模式待逐 case；加载侧常量 IV 与保存侧
   上下文取 IV 的关系待澄清。
3. 入口协议分派侧位于未分析区域（`sub_894E50` 唯一调用点 0x894fbc 无所属函数）；本项目现行号位
   以真机登录路径背书，静态侧待补。
4. `Etc/pvpChannelList.etc` 应用的确切语义待逐 case。
