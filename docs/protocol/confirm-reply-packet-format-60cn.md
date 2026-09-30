# 回复方包长度与格式确认方法(IDA 实操, 60A1)

> - **目的**:回答"未实现的包,如何确认服务端应答回包(服务端→客户端)的字段格式与长度"。结论:不靠猜,客户端解析代码本身就是格式的权威定义——**case 内读取顺序 = 线上字段顺序,读取器游标推进量 = 字段宽度,全部推进之和 = 精确载荷长度**(帧总长 = 6 + 载荷)。
> - **验证环境**:客户端主程序 IDA 库(ImageBase 0x400000, Hex-Rays 就绪),经 ida-pro-mcp HTTP 端点 `http://127.0.0.1:13337/mcp` 操作,文中所有地址均已在 IDB 实证。
> - **配套工具**:`tools/extract_noti_readers.py`(批量提取某 NOTI 的读取序列);帧头与加解密实证见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> - **服务端对应物**:回包构建 `Server/DAFLegacy.Server/Network/Core/GameServerPacket.cs`(6B 头自动写长度);明文回读 `Network/Core/PacketPlainText.cs`(当前保留未接入);分发 `Network/EntranceService.cs` + `Network/Dispatch/`(注册表与分发器,未命中回落 `Network/Protocol/FallbackCommandReplies.cs`)。

## 1. 核心原理

客户端收到回包后的处理是"逐字段从解密包体里读":

1. 读到什么类型(u8/u16/u32/块) → 服务端就必须写什么类型;
2. 读的先后顺序 → 服务端写入顺序;
3. 所有读取的游标推进总和 → 载荷长度;存在列表循环时,循环上界就是包内前面的某个计数字段,长度公式 = 固定部分 + 单条 × n。

因此"未实现包的长度和格式"问题完全转化为:**在 IDB 里找到该包 ID 对应的 case 代码,抄下读取序列**。

## 2. 客户端收包链路(全部实证地址)

| 环节 | 地址 | 行为 |
|---|---|---|
| 收包处理 | `sub_40D6D0` | 读 `packet[+2]` 的 totalLength(u32 LE),把偏移 6 起的载荷(长度 totalLength-6)拷入 `this+1118` 缓冲,`sub_776F80` 就地解密(空种子 key=0xB5/rot=6),再 `sub_8F5110(this+1118)` 把全局游标 `dword_BF29E0` 指到解密后包体起点(调用点 `0x40D7DC`) |
| type 分发 | `sub_40D6D0` 尾部 | `packet[0]`=0 → `sub_419420`(NOTI 推送);=1 → `sub_40F290`(CMD 应答)。之后还有一次按连接状态的虚函数转发(`0x40D867-0x40D883`,参数为 protocolId/整包/总长),个别包可能在其他状态另有解析 |
| NOTI 分发 | `sub_419420` | switch(`packet[1]`),边界比较在 `0x41945E`(`cmp 0x97; ja default`,152 case,0..151),间接跳转 `0x419474`,跳转表 `0x42A081`(4 字节/项,表项即 case 代码入口)。**注意:该函数 68KB,Hex-Rays 反编译直接失败(返回 None),只能走跳转表+反汇编** |
| CMD 应答分发 | `sub_40F290` | 同样 switch(`packet[1]`),但先经 `byte_417638[protocolId-1]` 映射到跳转表槽(`0x40F2B5-0x40F2D9`,159 槽,覆盖 protocolId 1..159)。此函数可正常反编译,case 直接在伪代码里找 |

### 2.1 字段读取器(游标家族,均 __cdecl,操作全局游标 `dword_BF29E0`)

| 函数 | 语义 | 游标推进 |
|---|---|---|
| `sub_8F5110(ptr)` | 初始化游标(收包路径传入解密后包体起点) | 重置 |
| `sub_8F5120(&v)` | 读 u8 | +1 |
| `sub_8F5140(&v)` | 读 u16 | +2 |
| `sub_8F5160(&v)` | 读 u32 | +4 |
| `sub_8F5180(dst, n)` | 读 n 字节块(定长字符串/数组) | +n |
| `sub_8B3250(dst, cap, fallback)` | **读 dstr 字符串** = u32 长度 + 内容(内部即 sub_8F5160+sub_8F5180);长度为 0 或 ≥cap 时**游标不推进**,dst 接收 fallback 文案(cap=容量上限) | 合法时 +4+len,非法时 +0 |
| `sub_8F51C0()` / `sub_8F51D0()` | 取当前游标位置(算偏移/长度用) | 0 |

`sub_8AA5A0(id)` 只是查 `dnf.str` 字符串表取文案,**不推进游标、不属于线上布局**;它常作为 `sub_8B3250` 的 fallback 实参出现在 case 里(形式如 `sub_8B3250(buf, 0x100, sub_8AA5A0(639))`),不要误计为字段。除 case 内的固定字段外,聊天/名称类文本一律是 `sub_8B3250` 读取的 dstr(NOTI 12 的 cap=0x100 与服务端 `CreateChatMessageNotification` 的 `< 0x100` 校验互证)。

## 3. 实操五步(经 MCP 全程验证)

MCP 端点是标准 MCP 协议(`initialize`/`tools/list`/`tools/call`),不是裸 JSON-RPC;常用工具:`server_health`、`decompile`(参数 `addr`)、`disasm`(参数 `addr`/`max_instructions`)、`xrefs_to`(参数 `addrs`)、`py_eval`(参数 `code`)。

1. **确认 IDB**:`server_health` → 核对 `idb_path`/`imagebase`。
2. **定位 case 入口**:用 `py_eval` 扫 `sub_419420` 内的 switch(`idaapi.get_switch_info`),从跳转表 `0x42A081 + id×4` 读出入口地址;再把 152 个入口按地址排序,**下一个入口地址即本 case 代码的结束边界**(case 体按表序物理相邻排布)。
3. **提取读取序列**:反汇编 case 区间,按出现顺序记录读取器调用(脚本已自动化,见 §6)。
4. **识别循环与计数**:平铺序列里"位置靠后且成对重复"的读取通常是循环体;循环上界 = 前面某个 u8/u16 计数字段(在反汇编里找 `movzx eax,[var]; cmp [i], eax; jge` 可确认)。长度公式 = 固定字节 + 循环体字节 × n。
5. **交叉核对**:已实现包可与文档中的"回包字节/长度"列对账(如 NOTI 13 = `3+13n`);新推导的布局照 §5 落地后,用包追踪日志核对真实字节。

```python
# 第 2 步的可复用片段(经 py_eval 执行;注意 py_eval 是 exec 环境,
# 函数/推导式访问不到模块级变量,脚本必须写扁平)
import idaapi, ida_bytes
ea = 0x419420
end = 0x42A081
while ea < end:                       # 找到函数内全部 switch
    swi = idaapi.get_switch_info(ea)
    if swi and swi.ncases >= 8:
        print(hex(ea), swi.ncases, hex(swi.jumps), swi.get_jtable_element_size())
    ea = ida_bytes.next_head(ea, end)
```

## 4. CMD 应答(type=1)的特殊性

- 应答分发器 `sub_40F290` 的 case 经 `byte_417638` 二次映射,大量 case **只弹 UI 不读包体**(如 CMD 70 公会信息应答仅 `sub_8AA5A0(149)` + 发 UI 消息)——真正的数据随后的 NOTI 推送送达。确认"某 CMD 的应答格式"前先分清:应答帧本身(常为 `[1]`/`[0,errorCode]` 通用应答,对应服务端 `CreateCommandReply`/`CreateCommandError`)与配套 NOTI。
- NOTI 分发器 `sub_419420` 的 default(本状态忽略)含 25,52,62,63,67,108-119,129,133,135,146,147;62/63(公会聊天/成员名)落在此处,说明其解析若存在也在其他状态路径,回填前需单独确认。

## 5. 实测产出:公会组布局(服务端当前完全未实现,可直接作回填依据)

> dstr = u32 长度 + 内容字节;下表长度公式中 a/b 为各字符串内容长度。

| NOTI | 名称 | 读取序列 | 载荷长度 |
|---:|---|---|---|
| 58 | GUILD_ENTER | u32, u32, dstr, dstr | 16+a+b |
| 59 | GUILD_SECEDE | u32, u32, u8, dstr, dstr | 17+a+b |
| 60 | GUILD_MARK_CHANGE | u32 | 4B |
| 61 | GUILD_DISMISS | 无读取 | 空包(总长 6B) |
| 62/63 | GUILD_CHAT_MSG / GUILD_CHAR_NAMES | default 忽略 | 待另行确认 |
| 64 | GUILD_MEMBER_LOGIN | u8, u8, dstr | 6+a |
| 70 | GUILD_INFO | dstr(公会名) + u8,u8,u8,u16,u8,u32,u8,u32,u16,u8 + 循环(u32,u8) | **22+len+5n**(n=第 11 个字段 u8 成员数) |
| 71 | GUILD_CREATE | u32, dstr | 8+a |
| 130 | MEGAPHONE_MSG | u8(类型) + u8(标志) + u16 + dstr(发送者名) + dstr(消息) | 12+a+b |

NOTI 70 逐字段(case `0x42609C..0x426314`,"目标"为公会对象 `dword_B5DCD4` 的偏移,语义名待回填时对照 UI):

| # | 宽度 | 读入栈变量 | 去向/备注 |
|---:|---|---|---|
| 0 | dstr | var_2AD8 | **公会名**,cap=0x30,fallback=字符串表 672(长度非法时显示该文案) |
| 1 | u8 | var_2AA4 | 对象+0x18 |
| 2 | u8 | var_2A9A | 对象+0x38,存储时 +1 |
| 3 | u8 | var_2AA2 | 读出后 case 内未使用 |
| 4 | u16 | var_2AE4 | 对象+0x44,仅低字节参与运算(**线上仍须写满 2 字节**,否则后续字段错位) |
| 5 | u8 | var_2AA1 | 对象+0x1C |
| 6 | u32 | var_2AA0 | 对象+0x20 |
| 7 | u8 | var_2A9B | 对象+0x28 |
| 8 | u32 | var_2AE0 | 对象+0x4C |
| 9 | u16 | var_2AE8 | 对象+0x54,仅低字节;同上须写满 |
| 10 | u8 | var_2AA3 | 对象+0x5C,成员数 n(0..255) |
| 11+ | u32+u8 ×n | var_2ADC, var_2A9C | 成员条目;case 内未见存储(疑似仅推进游标),布局以读取为准 |

服务端构建示意(顺序即客户端读取顺序,长度由 `GameServerPacket.Encode` 自动写为 6+载荷):

```csharp
// NOTI 70 GUILD_INFO:载荷 = 4+len(名) + 18B 固定 + 5B × count(示意,语义名待对照)
WriteDstr(w, guildName, maxLen: 0x30);  // 公会名 u32+bytes
w.Write((byte)field1);          // u8
w.Write((byte)field2);          // u8
w.Write((byte)field3);          // u8 (客户端未用,占位)
w.Write((ushort)field4);        // u16
w.Write((byte)field5);          // u8
w.Write(field6);                // u32
w.Write((byte)field7);          // u8
w.Write(field8);                // u32
w.Write((ushort)field9);        // u16
w.Write((byte)members.Count);   // u8
foreach (var m in members) {    // 5B/条
    w.Write(m.Id);              // u32
    w.Write((byte)m.State);     // u8
}
```

## 6. 工具与产物

- `tools/extract_noti_readers.py`:对指定 NOTI 批量提取读取序列(`python tools/extract_noti_readers.py 58 59 60 61 62 63 64 70 71`;`--all` 全表)。输出为平铺序列,含一次循环体(标 `fixed>=`),循环结构与计数字段需按其打印的 case 区间人工核对。
- 提取结果按仓库惯例回填两处:`docs/protocol/client-packet-enums-60cn.md` 对应行的"回包字节/长度"列;`Network/Core/PacketPlainText.cs` 的 `DecodeNoti` 加 case(该解码器当前保留未接入,重新接入后包追踪日志即可输出明文字段而非 hex 兜底)。
- 实现顺序遵循 `docs/servers4a21/feature-backfill-plan-vs-s4a21.md` 的"文档先行"约定:布局进文档 → 评审 → 动代码。

## 7. 坑与注意事项

1. **`sub_419420` 不可反编译**(Hex-Rays 返回 None),一切 NOTI 分析走"跳转表 → case 区间反汇编";不要试图重建函数。
2. **case 边界靠排序推断**,尾部 `0x42A069` 是共享 default;仅被 default 引用的 id(见 §4)没有本路径解析。
3. **py_eval 是 exec 环境**:模块级 import/变量在函数与推导式里不可见,脚本写扁平;异常会原样回传 stderr。
4. **u16 字段可能只取低字节**,但线上宽度仍是 2 字节——偷工减料会让后续字段全部错位(表现为黑屏/字段漂移,与分组构建器(`Network/Builders/`)中 NOTI 126 等注释的教训一致)。
5. **type=1 与 type=0 分发器是两套 case 空间**(且 type=1 有 LUT 二次映射),不要把 CMD 号直接当 NOTI 号查。
6. **次要路径**:解密器 `sub_776F80` 另有一个调用者 `sub_78EE00`(未调查);`sub_40D6D0` 尾部还有按状态的虚函数转发。个别包若在主路径为 default,先查这两处再下"客户端忽略"的结论。
7. **发包(客户端→服务端)方向的格式确认是同一方法的镜像**:从打包器 `sub_8B24C0` 的调用点(xrefs)进入发送侧构造函数,读写入序列即可;帧头与加密实证见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
8. **读取器集合必须完整,否则整段布局错位**:漏掉 `sub_8B3250`(dstr 原语)曾导致公会组布局整体前移——NOTI 70 被误推为 `18+5n`,实为 `22+len+5n`(开头漏了公会名 dstr)。遇到不认识的、指针指向栈缓冲的调用,先反编译确认它是否推进游标,再决定是否计入布局;抓包对账(哪怕一条真实报文)能立刻暴露这类错位。
9. **命令语义不要想当然**:喇叭(消耗品)"使用"并不走 USE_STACKABLE(CMD 47),而走 SEND_MESSAGE(CMD 17)携带槽位+文本;判断实际入口的最可靠手段是抓包(服务端 `EnablePacketTracing` 日志已带明文与原始字节)。
