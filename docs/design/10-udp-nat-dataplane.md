# 网络数据面整体优化:城镇广播中枢、UDP 中继与 NAT 探测

> **状态**:已实现(2026-10-01,三批次全部落地,全量自测绿;真机复测统一归 P-36)。2026-10-03 追加转发门、槽位观测与中继发送非阻塞化加固(§4.5,自测绿,真机项并入 P-36)。
> **范围**:城镇 presence 中枢独立化、`GameplayDatagramService` 数据面、`GameplayDatagramOptions`;**线上字节零变更**。
> **调整记录**:2026-10-01 由「UDP/NAT 数据面优化」扩编为整体方案——城镇广播中枢由 11 号文档 P1 的「条件触发剥离」调整为**一步到位独立化**(用户决策),与 UDP 端点认领、NAT 加固合并为一个基建包;同日审核后调整批次顺序(认领先于中枢独立化,见 §5)。2026-10-03 依据薄弱点评审追加 §4.5(转发前结构门、槽位伪造观测、中继发送可观测、名册零分配),不改变三批次边界与线上字节。修订项所依赖的流程断言已按 IDA 逐条核验,记录与结论见 `PENDING.md` P-34。
> **依据**:2026-10-01 NAT 探测协议 IDA 复核(结论登记 `docs/protocol/packet-detail-party-60cn.md`「NAT 发现阶段探测字节与回包校验定论」与 `client-function-register-60cn.md` §10),该分册「数据面归属与前置缺口」,以及参考项目 ACT12 的 `TownPresence`/`NatProbeServer`/relay 形态对照。

## 1. 背景与已验证事实

服务端网络数据面当前由三块组成:`PartyCoordinator` 内的城镇存在表(账号在城状态、位置镜像、同区域投递)、`GameplayDatagramService`(双端口 UDP:NAT 探测回显、保活观测、队伍 MT-UDP 中继)、以及入口会话的出站通道。三者现状与参考形态的差距:

| 块 | 本项目现状 | 参考形态(ACT12) | 差距 |
| --- | --- | --- | --- |
| 城镇广播 | presence 表挂在 `PartyCoordinator`(`_presenceBySession` + 位置镜像 + 同区域投递 + 喇叭全服广播) | `TownPresence` 独立中枢:actorId 分配、进/离可见性、身份包重建、三级广播、好友/工会观察者脏标记、查找服务、待发队列背压、**网络端点状态(`UpdateNetwork`)** | 缺观察者/查找/背压;presence 与组队域耦合;端点状态分散在 datagram 服务 |
| UDP 中继 | 端点学习按 TCP 源 IP 整体赋予(`ObserveEndpoint`),同 IP 多会话互相覆盖 | 单口中继 + 按会话网络地址(`PeerAddress`)分发 | 端点归属粒度(下文 G1) |
| NAT 探测 | 接受字节 1/5 回 7B `{2,公网IP,端口}`,无长度校验、无限流、逐包日志 | 精确 1 字节校验 + 按 IP 限流(32/s/IP)+ 聚合流量报告 | 入口加固与遥测(下文 G2/G3) |

与本次优化相关的已验证事实(60A1 客户端,ImageBase 0x400000):

| 事实 | 依据 |
| --- | --- |
| 探测请求精确 1 字节,值 1(state1/2)或 5(state3) | `sub_78C870` 0x78C8DD/0x78C98E;初值 `sub_78CCD0` 0x78CD0B |
| keepalive 精确 10 字节,首字节 0,每 10s 发往端口 B | `sub_78C870` 0x78C8DA..0x78C8DD |
| 回包只校验首字节==2,源地址不参与任何比对 | `sub_78C9E0` 0x78CABF 起 |
| 客户端探测节奏 ≤ 2 次/秒 | `sub_78C870` 0x78C969/0x78C91A |
| 服务端中继是唯一数据面(队员记录统一通告服务端端点) | `packet-detail-party-60cn.md`「服务器中继」 |

由此确立两条边界:**跨 socket 回包不采纳**(客户端不校验回包来源,无功能后果);**同 socket 回包形态维持不变**(7B `{2, 公网IP, 端口}`)。

## 2. 目标与非目标

### 目标

- **G1 端点学习按会话认领**:修复同 IP 多会话(同机双开/同一 NAT 出口)下端点互相覆盖与中继转发错位;端点状态的权威存放点随 G4 迁入中枢连接态。
- **G2 NAT 探测入口加固**:探测请求长度校验 + 按源 IP 限流;合法客户端无感。
- **G3 中继与广播可观测性**:UDP 侧逐包 Info 收敛为周期聚合报告 + 计数器;中枢广播补待发队列上限与丢弃计数。
- **G4 城镇广播中枢独立化**:把 presence 从 `PartyCoordinator` 剥离为独立 `TownPresenceService`,一次补齐参考中枢的职责面(状态翻转钩子[原观察者,按 11 号 P2 收敛]、查找、三级广播、身份包、背压、**网络端点状态**),作为社交系统各阶段(P0/P2/P5/P6)的共同地基。

### 非目标(均有既有证据结论,不重开)

- P2P 优先拓扑、TURN 式 per-ordered-pair 中继(party 分册「TURN 迁移结论:不迁移」)。
- 探测字节 5 跨 socket 回包(上文边界)。
- NOTI 2 mode 3 队友快照与多人副本协同本体(11 号文档 P0 承接)。
- keepalive 限流(10B/10s/客户端,不构成向量)。
- 广播内容/身份包的字节变更(身份包布局以 IDA 验证结论为准,属 11 号文档 P0/P1 验证项,本方案只收口调用点)。

## 3. 协议面声明

**客户端可见行为零变更**。探测回包、CMD 2 解析(`TryParseNatReport`)、NOTI 11 端点表、MT-UDP 中继帧、身份/区域广播包的全部字节不动;协议核心层(`Network/Protocol/`)除 `TryCreateNatProbeResponse` 增加长度校验外无改动,不触及字节冻结约束。限流超限时对探测请求**静默丢弃**(不回包),客户端按既有节奏(500ms 步进、10 次重试、换目标)自然重试,阈值设计保证合法流量永不触发。

## 4. 设计面

### 4.1 G4 城镇广播中枢(`TownPresenceService`,独立服务)

**职责面**(参考 ACT12 `TownPresence`,按本项目已验证需求裁剪):

- **在城注册与 actorId**:会话进/离城登记、actor 分配与回收;进副本/PVP 撤出可见集合(保留组队域的既有语义)。**频道人口统计随注册一并继承**:中枢在频道会话建立时即登记(acquire)、断开时回收,**每频道注册数 = 人口唯一真源**(与现行 `CharacterSessionRegistry` 会话计数同口径——含副本中与未进城的已连接会话,不以城镇可见性为准),经 `GetChannelPopulation` 暴露;入口元数据(`EntranceMetadata` 的 `CurrentUsers` 字段,客户端以在线/容量>95% 判拥挤)的组合根注入切换到中枢,原 registry 计数职责随之移除。
- **位置与身份**:CMD 37/38 的位置记录与区域切换;身份(外观)包缓存的重建与刷新收口(现散在 presence 更新路径)。
- **三级广播**:区域/频道/全服语义显式化,收口现散落的同区域投递、喇叭全服广播等调用点。
- **观察者/状态翻转钩子**:好友与工会的上下线通知按 11 号 P2 收敛为**状态翻转钩子**形态(翻转时按反向边/成员表现查,不做常驻订阅登记);连接级脏标记(`FriendsChanged`/`GuildChanged` 类)保留为批量刷新的辅助形态。P2(好友)直接使用,P5(工会)复用。
- **查找服务**:按角色 ID/名字的在城查找统一收敛,替代散落的 `FindPresence` 调用(密语、组队邀请共用)。
- **背压**:每连接出站已有界 `ConnectionQueue`(07 架构)承担排队上限——**实施偏离记录**:中枢不另设第二级队列(与出站泵重复),以广播投递计数(`BroadcastRecipientCount`)与丢弃 Warn 兜底可观测性;慢消费者由既有有界通道隔离。
- **网络端点状态(与 G1 的接缝)**:连接态持有自报 `GameNatReport` 端点与已认领的观测端点;`SET_UDP_IP_PORT`(CMD 2)处理链改为「中枢登记自报 → 中枢重解析端点 → `PartyCoordinator` 刷新 NOTI 11」——对齐参考项目 `SET_IP → UpdateNetwork → RefreshNetwork` 的形态。

**边界**:`PartyCoordinator` 保留组队域(队伍状态、邀请、名册、端点表编排),通过中枢接口消费在城状态;`PrivateStorePollHandler` 等城镇面 handler 后续按需切换消费方,本方案不改其行为。

**落位**:`Network/TownPresenceService.cs`(与 `PartyCoordinator.cs` 同级平铺,遵循现目录形态);命名登记进 07 文档基建目录条目。

### 4.2 G1 端点学习按会话认领

**现行问题**:`ObserveEndpoint` 维护 IP→端点映射,任一探测/保活到达即把该 IP 下**所有**会话的端点覆盖为同一观测值;同 IP 多会话时后到者覆盖先到者,反查索引随之错位。

**认领规则**(纯函数,便于单测;观测路径=探测与保活共同入口):

1. **精确认领**:观测端点(`ip:port`)与某会话自报 `GameNatReport` 公网端点精确匹配,且**唯一命中** → 仅更新该会话的解析端点,并登记认领;
2. **单会话回落**:无自报命中,且该 IP 下活跃会话数 == 1 → 沿用现行整体学习;
3. **多会话不学习**:无自报命中,且该 IP 下活跃会话数 > 1 → 不学习、不覆盖,计数并首次 Warn。

多会话自报同一公网端点(无法区分)按第 3 支处理,不认领。

**端点解析总序**(单会话场景与现行严格等价,这是回退底线):

1. 该会话已被认领的观测端点;
2. 该会话 IP 无共享时的最新观测端点(现行语义);
3. 自报公网端点(`PublicPort != 0`,现行回落语义);
4. 无(返回 null,不参与中继)。

相对现行的唯一收紧:**共享 IP 且无认领时,不得采用按 IP 共享的观测值**。`SetNatReport` 不再触发按 IP 整体赋值,只更新自报值并按总序重新解析自身端点。归属反查(`RelayDatagram` 定位发送者)只按**已解析端点**维护反查索引,写入点单一。

**存储归属与实施次序**:认领规则先在现行 `GameplayDatagramService` 的会话结构(`GameplaySession`)上落地(批次 2);G4 中枢独立化时,自报值/认领值/解析值三个字段随连接态整体迁入中枢,算法与测试随迁(批次 3)。认领不等待中枢——已登记缺陷的修复与最大重构解耦。

### 4.3 G2 NAT 探测入口加固

- **长度校验**:`TryCreateNatProbeResponse` 增加 `request.Length == 1` 条件(协议锚:客户端 `sendto` 恒 1 字节);keepalive 已有 `Length == 10` 校验,不动。
- **按 IP 限流**:新增 `NatProbeRateLimiter`(独立文件),固定 1 秒窗口计数,键为源 IP;**双端口监听共用同一限流器实例**(客户端以同一 socket 交替探测两端口,按 IP 聚合计数才是本意);默认阈值 32 次/秒/IP(可容 16 个同 IP 客户端全速探测),`GameplayDatagramOptions.NatProbeRateLimit` 可配,`<= 0` 表示不限流。超限静默丢弃 + 计数。
- **未知数据报**:不回包、计数(现行走 Info 日志 + 逐块解析,由 G3 收敛)。

### 4.4 G3 可观测性

- **UDP 计数器**(`Interlocked`,服务级):`rxNatProbe`、`rxKeepalive`、`rxGameplay`、`rxRelayed`(**成功**转发数,§4.5)、`txRelayFailures`(转发发送失败,§4.5)、`rxNoPeerEndpoint`、`rxUnattributed`、`rxRateLimited`、`rxMalformed`(判畸形且**不转发**,§4.5)、`rxSlotMismatch`(槽位伪造观测,§4.5),及认领跳过计数 `rxClaimSkipped`(§4.2 第 3 支,首见 Warn,累计进聚合行)。
- **周期聚合**:5 秒窗口,有活动才输出一条 Info 汇总(形态对齐参考实现 relay 周期 `ReportTraffic`:计数行,无逐包内容)。
- **降噪**:逐包 `RuntimeLogger.Info`(RX-UDP 头部 dump、转发行、逐块解析行)降为 `Debug`;malformed 首见 Warn + 计数。`TraceDatagram`(tap 捕获通道)与 `EnablePacketTracing` 行为不变。
- **中枢计数**:广播丢弃、队列上限触达、观察者投递计数,并入同一聚合日志。
- **不引入发送队列**:UDP 无 TCP 式排队背压,`UdpClient.SendAsync` 直写 OS 缓冲;参考实现的队列满丢弃遥测属其中继的 TCP 专有形态,不适用,以计数器等价覆盖。

### 4.5 转发门与槽位观测(2026-10-03 加固)

- **转发前置结构门**:玩法数据报先经 `TryReadBlockSequence` 整包块结构遍历(类型 0/1/2 之外的块、声明应用长度越包、发送槽位 ≥8、尾部垃圾均判畸形),失败即计入 `rxMalformed`、首见 Warn、**不再转发**。判畸形的权威与客户端一致:接收端对越界/无法归属的块整包丢弃(party 分册「数据面归属」第 1 条),转发畸形包只有跨成员放大效果;合法客户端与解析共用协议核心层的块布局,门不触及合法流量。**应用层 CRC/密码学失败不设门**——CRC 校验是接收端职责,转发门只看块结构。解析遍历与逐块日志、槽位观测共用一次物化,不二次解析。
- **槽位伪造观测(先计数不拦截)**:归属会话的名册槽位(`PartyManager.GetMemberSlotByUser`,会话须与成员记录一致)与帧内偏移 7 的发送槽位不一致时计入 `rxSlotMismatch` 并首见 Warn,数据报照常转发。客户端接收端按该字节选对端对象,mismatch 即冒名证据;但「合法帧恒写自身槽位」尚无发送点锚定,拦截收紧以真机计数为零为前提,另行实施。
- **中继发送可观测(非阻塞派发)**:接收循环同步完成归属反查、槽位观测与对端端点解析后,**逐对端触发独立发送任务**——发送不阻塞下一包接收(同一监听 socket 上的并发 `SendToAsync` 由 `Socket` 线程安全保证)。逐任务观测结果:`SocketException` 计入 `txRelayFailures` 且永不计入 `relayed`;停机竞态(取消、监听已释放)静默终止,不泄漏未观察异常。`relayed` 口径为成功转发数。同一数据报内逐对端保持发出顺序;跨数据报的逐对端顺序不再有保证(UDP 常规语义,客户端按帧内序列号处理;参考实现按对独立 socket 的形态同样无跨包序保证)。
- **中继名册零分配**:`Party` 维护 replace-on-write 的成员会话数组(`MemberSessionIds`,增删与快照克隆时重建、内容不就地变更),`PartyManager.GetRelaySessionIdsByUser` 锁内返回只读引用(数组含发送者,中继侧跳过自身),解析器不再逐包做队伍快照与清单分配;逐监听接收循环复用块清单,稳态零清单分配。逐对端发送任务引入每次发送的小型闭包/状态机分配(≤3 次/数据报),相对被移除的整队快照深拷贝可忽略。
- **威胁边界登记**:UDP 数据面无认证,归属 = 端点反查 + 帧内槽位;盲源伪造(猜中受害者 NAT 外映射)可注入帧,槽位观测提供取证面,端点级认证需改客户端、不在范围。NAT 重绑窗口(端点变更后依赖探测/keepalive ≤10s 重学,期间该会话帧 unattributed 丢弃)为端点学习机制的既知行为,与原服一致,不另设机制。

## 5. 实施批次(每批 SelfTests 全绿 + 回归清单通过后合入下一批;2026-10-01 审核后调整认领与中枢的先后)

| 批次 | 内容 | 依赖 |
| --- | --- | --- |
| 批次 1 ✅ 已实施(2026-10-01,自测绿) | G2 + G3:限流器(双端口共用)、长度校验、计数器与聚合日志(全部在 `GameplayDatagramService` 内,不动状态归属) | 无 |
| 批次 2 ✅ 已实施(2026-10-01,自测绿) | G1:认领规则与解析总序在现行 `GameplaySession` 结构上落地,反查索引改按解析端点维护 | 批次 1(计数器) |
| 批次 3 ✅ 已实施(2026-10-01,自测绿) | G4:中枢独立化,端点三字段(自报/认领/解析)随连接态迁入并随迁测试;`PartyCoordinator` 改为消费方;CMD 2 链路改接中枢;翻转钩子/查找/三级广播/背压就位 | 批次 2(认领语义已定型并测试覆盖,中枢迁移的等价基线干净) |

调整理由:认领修复的是已登记缺陷,不应被风险最大的中枢重构阻塞;P0(组队多人副本)只依赖批次 2,可在批次 3 进行中并行开工。

回归底线(每批同一清单):单会话端点解析与现行等价;聊天(同区域/队伍/喇叭)、位置镜像(CMD 37/38)、组队链路(CMD 10-14、NOTI 11/26)、探测与保活字节不变。

## 6. 改动落点

| 文件 | 改动 |
| --- | --- |
| `Server/DAFLegacy.Server/Network/TownPresenceService.cs` | 新增:城镇广播中枢(G4) |
| `Server/DAFLegacy.Server/Network/PartyCoordinator.cs` | presence 剥离,改消费中枢;保留组队域 |
| `Server/DAFLegacy.Server/Network/GameplayDatagramService.cs` | 认领规则与解析总序、端点态改挂中枢、计数器与聚合日志 |
| `Server/DAFLegacy.Server/Network/NatProbeRateLimiter.cs` | 新增:固定窗口按 IP 限流器(时钟可注入) |
| `Server/DAFLegacy.Server/Infrastructure/ServerOptions.cs` | `GameplayDatagramOptions.NatProbeRateLimit`(默认 32) |
| `Server/DAFLegacy.Server/Network/Protocol/GameDatagramProtocol.cs` | `TryCreateNatProbeResponse` 长度==1 校验 |
| `Server/DAFLegacy.Server/Network/Handlers/Login/SetUdpEndpointHandler.cs` | CMD 2 链路改接中枢(登记→解析→刷新) |
| `Server/DAFLegacy.Server/Network/Handlers/Town/`、`Network/Sessions/ChannelSessionSenders.*` | 广播/查找调用点切换消费方 |
| `Server/DAFLegacy.Server/Network/Protocol/EntranceMetadata.cs`、组合根注入 | `CurrentUsers` 来源切换为中枢 `GetChannelPopulation`(批次 3) |
| `Server/DAFLegacy.Server/Game/Party/PartyState.cs` | `Party.MemberSessionIds` replace-on-write 会话数组(2026-10-03 §4.5) |
| `Server/DAFLegacy.Server/Game/Party/PartyManager.cs` | `GetRelaySessionIdsByUser`/`GetMemberSlotByUser` 锁内查询(§4.5) |
| `Server/DAFLegacy.Server/Network/PartyCoordinator.cs` | 中继解析器零分配化、`GetSenderPartySlot`(§4.5) |
| `Server/DAFLegacy.Server.SelfTests/Network/` | 新增:认领规则、解析总序、限流器、观察者/查找/背压单测;块结构门(`gameplay-block-gate`)与中继名册断言(`party` 套件)(§4.5) |

关联文档:实施合入后回写本文件状态(草案 → 已实现)、`docs/design/README.md` 索引、`packet-detail-party-60cn.md` 数据面第 3 条(修复方向 → 已实现,真机复测标记)、11 号文档 P1 阶段状态、`CHANGELOG.md`(按 D1)。

## 7. 验收标准

- **单测**:认领规则三支路径(自报精确唯一命中 / 单会话回落 / 多会话不学习)与多会话互不覆盖;混合场景(同 IP 两会话,其一已自报、其一未报——已报者认领、未报者不学习);解析总序四级的单会话等价性;限流器(窗口翻转、阈值边界、多 IP 隔离、双端口共用实例、`<=0` 旁路);`TryCreateNatProbeResponse` 拒绝非 1 字节探测;观察者打标/消费、查找服务、队列上限与丢弃计数;块结构门(合法帧与多块序列通过、声明长度越包/槽位越界/尾部垃圾/未知块类型拒绝、空包放行)与中继名册(replace-on-write 旧引用不变、槽位会话校验、退队清空)。SelfTests 全绿。
- **行为(单测层面)**:两个会话同 IP、各自探测到达后端点各归其位;第 33 个探测被丢弃且计数;慢消费者不阻塞广播路径。
- **真机**(登记 `PENDING.md`,复测窗口与 mode 3 事项合并):同机双开进入副本,中继转发端点归属不错位(日志可见两会话各自认领);抓包比对探测回包与改动前逐字节一致;聊天/组队/位置真机回归。
- **日志**:默认配置下无逐包 Info 洪水;持续流量下每 5s 至多一条汇总行。

## 8. 风险与回退

- **中枢拆分回归**(G4 触及组队/聊天/位置三条既有路径):按实施批次分批合入,每批以「单会话行为等价 + 回归底线清单」托底;任一批次真机回归失败,按批次粒度整体回退,不设运行期开关(避免双路径长存)。
- **限流误伤**:阈值远高于合法节奏(2 次/秒/客户端),极端部署经 `NatProbeRateLimit` 上调,无需改码。
- **认领规则对自报数据的依赖**:客户端 CMD 2 缺省(公网端口 0)时自动落解析总序第 2/3/4 级,与现行回落一致,无新增失败模式。
- **转发门误伤合法帧**(§4.5):服务端块遍历若比客户端解析更严(某种合法块形态被门拒绝),组队数据报会静默丢包;以 P-36 真机回归托底(双开副本内数据报互通 + 聚合行 malformed 计数),出现即回退该门。
- **槽位观测的判定前提**(§4.5):拦截收紧以「合法帧恒写发送者自身槽位」为前提,该前提现仅由接收端归属语义反推;真机窗口内 `rxSlotMismatch` 必须为零才允许实施拦截。
- **非阻塞派发的顺序与背压**(§4.5):跨数据报的逐对端顺序不再保证,依赖客户端帧序列号承接(与参考实现按对独立 socket 的形态同级);归属会话的高速组包没有接收侧背压,发送任务由线程池吸收,`txRelayFailures` 上升即发送缓冲压力的观测信号。
- **耦合低估**:presence 消费方盘点以注册表与调用点扫描为准,发现未登记消费方时先并入回归清单再切流,不静默替换。
