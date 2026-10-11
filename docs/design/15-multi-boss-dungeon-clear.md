# 15. 多 Boss 房副本与通关结算治理（以 51 号副本为取证样本）

状态：**草案**（待评审，按 D2 确认后实施；实施完成后回写状态并按 D1 登记）。

## 1. 背景与目标

### 1.1 现象与根因

真机反馈：通关结算弹出时房间门处于开启状态、角色可以自由走出房间。根因不在门本身——60cn 协议不存在任何门开关包，门状态由客户端本地模拟（房间内阻挡怪全灭即开门；Boss 门有专门的开启演出与公告）。真正的问题是**服务端过早下发 NOTI 31（允许通关）**：

- 现行 `DungeonRoomClearRules.Evaluate`（Server/DAFLegacy.Server/Game/Dungeon/DungeonRoomClearRules.cs）把「Boss 房内 Boss 类型怪死亡且无存活怪」一律判定为最终通关，`DieMonsterHandler` 随即置 `ClearEnabled` 并推 NOTI 31。
- 51 号副本（`dungeon/Act6/VillageOfTemptation.dgn`，显示名见 `dungeon/dungeon.chn.str` 键 `name_420`）需要与 Boss 战斗三次（1 次真身 + 2 次假身），客户端本地按同一份 PVF 数据判定「尚未通关」并继续演出，而服务端在第一次 Boss 死亡就触发结算，形成「结算窗口中途弹出 + 门开 + 可自由走动」的失配状态。

### 1.2 目标

1. 通关结算（NOTI 31）的时机与客户端本地进度判定一致：多 Boss 房副本必须打满全部场次后才允许结算。
2. 普通副本（单 Boss 房、无假身）结算行为保持现状不变。
3. 不新增、不修改任何包格式；门行为随结算时机修正自然恢复，不做服务端门控。

## 2. 证据基础

### 2.1 PVF 原文（提取工具与样本已入库）

提取工具 `tools/diagnostics/ExtractPvfScript/`（读 PVF 包，路径由命令行传入）；证据文件 `tools/diagnostics/pvf-hamelin-villageoftemptation.dgn.txt` 与 `tools/diagnostics/pvf-dungeon-chn-names.txt`。

51 号副本结构（`dungeon/Act6/VillageOfTemptation.dgn`）：

- `[maze info]` 5×5；`[start map]` (2,4)；`[boss map]` 列出 **3 个 Boss 房位置**：(0,0)、(0,1)、(1,0)。
- `[dummy appear count] 2`（脚本注释原文：假人出现次数 2次）。
- `[pathgate object]` 621..630；`[seal door map index] 60019` + `[hell dungeon] 1`（地狱模式属性，本方案不涉及）。
- **不含任何通关条件标签**（无 A21 时代的 `[clear condition]` 族）。

全量 `.dgn` 扫描结论（用 §2.1 提取工具逐文件核对）：

- **`[dummy appear count]` 仅 51 号副本配置**，其余全部副本（含教程、战争房、迷阵）均无此标签。
- 多个 `[boss map]` 位置在普通副本中普遍存在（如 act1 洛兰 4 个、猛毒雷鸣废墟 5 个、act2 天空之海深/浅海与世帕罗塔上/下层各 3 个等）——多位置本身是 **Boss 房候选池**语义（每次进本从中确定一间为本次 Boss 房），与三场次机制无关。因此**多场次模式的判据只能挂在 `[dummy appear count]` 上**，不得以 Boss 位置数量为条件。

`map/map.lst` 中该副本的 6 张特殊图（路径前缀 `VilligeOfTemptation/`，包内原文即此拼写）：

| 地图 | `[type]` | 怪物行（monsterIndex `[角色]`） |
|---|---|---|
| b6416 | `[boss]` | 1097×2 `[normal]`；**1077 `[boss]`** |
| b6422 | `[boss]` | 1097、1079×2，均 `[normal]` |
| b6423 | `[boss]` | 1097×2、1079，均 `[normal]` |
| d6418 | `[dummy]` | 1079、1097 `[normal]`；**1078 `[boss]`** |
| d6420 | `[dummy]` | 1097、1079×2，均 `[normal]` |
| d6424 | `[dummy]` | 1097×2、1079，均 `[normal]` |

结构解读：真身 Boss（monsterIndex 1077）只在 b6416 静态存在；假身（monsterIndex 1078，怪物行标 `[boss]`、所在图 `[type] [dummy]`）只在 d6418 静态存在；其余 boss/dummy 图均无 Boss 类怪物。据此判断**第二、三场次的对像是运行期注入的**（与「假人出现次数 2」一致），静态图数据不完整覆盖演出编排。

### 2.2 客户端 IDA 结论（60cn 主程序）

- NOTI 31 case `0x41FE50..0x4200B1`：客户端收到后立即以 `sub_8B2650` 发送 CMD 49（24 个本地统计值除以队伍人数）、弹系统消息（串表 0x286）、更新状态；**case 内无任何关门/锁动作**。
- NOTI 127 case `0x429250`：`ok=1` 且 `allReported=1` 时执行确认序列（文案串表 986..1003/3500 预取等）；`allReported=0` 无动作。
- `[dummy appear count]` 由迷宫解析器 `sub_725CE0` 读取，存入迷宫对象 `+0x18C`；消费点待实现期回填（见 §7 V4）。
- 门对象本地模拟：开门/关门动画状态机（`sub_57E340`/`sub_57E3D0`，动画名 `DOOR_OPEN`/`DOOR_CLOSE`）；Boss 门开启处理器 `0x578550`（动画 `BOSS_GATE_OPEN` + 公告串 0xE43）。
- 分发层：NOTI 跳转表 `0x42A081`（4B/项，id 0..151）。

### 2.3 参考项目语义（仅作思路，不照搬，按 D3）

ServerS4A21（A21 数据面）无假身/多 Boss 房同名实现，可参考的架构语义：

- 通关判定与房间清理解耦：房间清（阻挡怪死光，开门）是 Room 级事实，副本通关（发 NOTI 31）是 Instance 级决策，二者独立评估。
- 相位机：InProgress → Cleared（已发 NOTI 31，等客户端 CMD 49）→ ResultShown（NOTI 34/35 已发）→ CardsRevealed。
- Boss 房终点语义：Boss 房内全部阻挡型 Boss 类怪死亡才算到达终点；Boss 类怪不在通关时被清除。
- df_game_r 的 `SetGridPath (0x82FFF70)` 语义（本仓库 `DungeonCatalog.TryApplyQuestMap` 注释已登记）：Boss 位置按列表推进，「下一个 Boss 候选」，末位后回退前一位——说明真服对 `[boss map]` 列表维护**当前 Boss 候选**这一运行态。

## 3. 协议面

不新增、不修改任何包。涉及的全部为既有已实现包：CMD 42/NOTI 38（击杀与死亡通知）、NOTI 31（允许通关）、CMD 49/NOTI 34/NOTI 35（结算与翻牌）、CMD 48（房间移动）、CMD 127/NOTI 127（Boss 死亡校验）。

## 4. 服务端设计

按证据充分度分两层：**L1 核心修复**证据充分、无条件实施；**L2 编排对齐**依赖真机抓包，实施前按 §7 完成取证。

### 4.1 L1 核心修复（解决本 bug）

- **R1 通关计数门控**：`DungeonRunState` 新增 Boss 场次计数（含假身场次）。判据为 `DummyAppearCount > 0`（全量扫描确认该标签仅 51 号副本配置；多 `[boss map]` 位置的普通副本是候选池语义，不受影响）：Boss 类怪死亡上报（CMD 42，且 `defeatedMonster.Type == Boss`）每场次计数一次；计数未达 `1 + DummyAppearCount` 前，`DungeonRoomClearRules` 一律返回不结算。计数达标后的最终死亡按现行规则（Boss 房、无存活怪）走 NOTI 31。
- **R2 计数来源与未知怪容忍**：假身实例可能不在当前房间静态怪清单（uniqueId 为运行期新值，待 V1 确认）。多场次模式下，Boss 类死亡上报即使 uniqueId 不在 `AliveMonsters`/房间怪清单内也不丢弃，计入场次并照常回 NOTI 38；普通副本维持现行「未知怪丢弃」逻辑不变。
- **R3 经验掉落基线**：多场次模式下每场次死亡默认按现行路径处理（NOTI 38 + 经验 + 掉落）。真服对假身场次是否给掉落/经验待 V2 取证，结论出来前保持默认行为并在 PENDING 登记。

### 4.2 L2 编排对齐（依赖取证后实施）

- **R4 Boss 房建模**：多场次模式（`DummyAppearCount > 0`）下，`TryCreateRoom` 的 Boss 房判定从单一 `layout.BossX/Y` 扩展为「位置 ∈ BossPositions 集合」（三个位置皆为战斗场地）；普通副本的候选池语义维持现状不动。`ResolveMapId` 的 Boss 角色池匹配随之扩展。
- **R5 假身场次的房间内容**：运行期注入的假身/Boss 怪如何进入 START_MAP 怪物清单（服务端注入 vs 客户端本地注入 + 服务端仅计数），依 V1/V3 取证结论定案；`[type] [dummy]` 图（`DungeonMapType.Dummy` 解析已存在，无消费方）是否作为假身场次房间图由取证结论决定。
- **R6 Boss 候选推进**：多场次模式下按 SetGridPath 语义维护「当前 Boss 候选位置」运行态（推进/回退规则依 V3）。

### 4.3 不变式

- 普通副本（无 `[dummy appear count]` 标签）全链路行为与现状逐字节一致（含多 `[boss map]` 位置的候选池语义维持现状、空 Boss 房结算兜底 `EnableEmptyBossRoomSettlementAsync`）。
- 教程副本路径不受影响。

## 5. 改动落点

| 落点 | 内容 |
|---|---|
| Server/DAFLegacy.Server/GameWorld/DungeonCatalog.cs | Boss 房集合判定、（L2）假身图选择 |
| Server/DAFLegacy.Server/Game/Dungeon/DungeonRunState.cs | Boss 场次计数运行态 |
| Server/DAFLegacy.Server/Game/Dungeon/DungeonRoomClearRules.cs | 多场次门控 |
| Server/DAFLegacy.Server/Network/Handlers/Dungeon/DieMonsterHandler.cs | Boss 场次计数、未知 Boss 死亡容忍（L1）；（L2）注入怪处理 |
| Server/DAFLegacy.Server.SelfTests | 51 号副本三场次门控与普通副本回归的冒烟用例 |
| docs/protocol/packet-detail-dungeon-60cn.md | NOTI 31 语义补注（多场次副本的触发时机） |
| docs/protocol/client-function-register-60cn.md | 回填 `0x41FE50`（NOTI 31 case）、`0x429250`（NOTI 127 case）、`sub_725CE0`（迷宫解析）等增量 |
| CHANGELOG.md / PENDING.md | 完成后按 C1/D1 登记 |

## 6. 验收标准

- AC1（51 号副本真机）：b6416 击杀真身 → 不弹结算、门开、可移动至后续场次房间；两次假身击杀均不弹结算；第三次击杀触发结算，翻牌与回城流程完整。
- AC2（普通副本回归）：洛兰等单 Boss 房副本结算时机与现状一致；全链路抓包无行为漂移。
- AC3（计数一致性）：服务端收到的 Boss 类 CMD 42 次数、NOTI 31 下发时机与客户端本地进度一致（抓包核对）。
- AC4（自测）：新增冒烟用例覆盖三场次门控、未知 Boss 死亡计数、普通副本不受影响三条路径。

## 7. 待验证项（D1，真机抓包 + IDA 互证）

- V1 客户端对假身场次是否每次都上报 CMD 42；假身实例的 uniqueId 形态（复用静态 id 还是新分配）。
- V2 假身场次的死亡是否伴随 NOTI 38 掉落与经验（真服基线）；trailer 冠军序号取值。
- V3 Boss 房访问顺序与「下一个 Boss 候选」推进的实际行为（自由探索还是定序）。
- V4 客户端迷宫对象 `+0x18C`（假人出现次数）的消费点——编排主逻辑定位与回填。

## 8. 边界与风险

- 本方案不处理地狱模式（`[hell dungeon]`）与 `[pathgate object]` 的独立语义，51 号副本按普通流程进场。
- 若 V1 取证发现假身场次不上报 CMD 42，则 R1 的计数来源需改挂 CMD 127/NOTI 127 序列，届时按取证结论修订本文档后再实施 L2。
