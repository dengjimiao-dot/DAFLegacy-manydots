# 60A1 协议修复日志

记录协议行为修复与新增处理的**最终结果**：现象、根因结论、最终改动与验证状态。

只收录已完成并已验证的条目；未完成、待真机复测、未定论与存在争议的事项，以及历史修订与多轮验证过程，一律登记在 `PENDING.md`，本日志不保留过程性叙述。日期小节与条目均按最后修改时间倒序排列（最新在前）。

---

## 2026-10-26

### FIX-15 全部修理的客户端计价与本地耐久范围改为只覆盖穿戴位（同步自 DFLegacy c1b6a0a）

- **现象**：ADD-37 起服务端对 CMD 25 全部修理（slot=0xFFFF）只收取穿戴位的费用并只修穿戴位，但客户端计价函数仍把主背包内装备一并计入：可负担性判断与确认框金额均包含背包，确认后 REPLY25 的本地分支还会把背包装备耐久刷满，只能等服务端补发的 NOTI14 权威行纠正显示。
- **根因结论**：客户端 `sub_7CDC10` 在 0x007CDC83 处进入主背包扫描，REPLY25 的 0xFFFF 分支在 0x00411C79 处进入主背包耐久填充；两处都位于穿戴位处理之后、窗口清理与 ITEM_REPAIR 通知之前。
- **改动**：`Client/DAFLegacy.Ijl15/src/dnf_patches.h` 新增 `detail::RepairAllScopePatches`（两处地址、原字节与续点）；`dnf_patches.cpp` 将两项按 `RepairAllWornEquipmentOnly` 接入补丁表（表长 38→40）：0x007CDC83 跳至 0x007CDEA9 跳过主背包计价，0x00411C79 跳至 0x00411D6B 跳过主背包耐久填充；`ijl15_config` 新增运行期开关 `[Features] RepairAllWornEquipmentOnly`（默认 1）；`Config.ini` 与 `README.md` 登记开关及回退语义；原生自测新增两处地址、跳转位移与补丁字节执行断言。
- **验证**：补丁地址、原字节与续点依据源项目 DFLegacy c1b6a0a 的 IDA 复核（0x007CDC83 原指令 `8B 35 EC DC B5 00`、0x00411C79 原指令 `8B 0D EC DC B5 00`）；本仓库原生自测 `ijl15_compatibility` 新增断言，需在 Windows 上执行 `Client/DAFLegacy.Ijl15/build-native.ps1 -RunTests` 构建验证（本次会话在 macOS 上未执行）。**真机未复测**：判据并入 `PENDING.md` P-56 的 ADD-37 行。
- **遗留项**：开关置 0 可回退到客户端默认的全量计价与显示，但此时客户端范围大于服务端收费范围，仅用于对照排查。

## 2026-10-11

### ADD-38 商城装扮购买支持客户端技能选项选择；物品删除与分解扩展生物背包（同步自 DFLegacy 0e07c31）

- **目标**：购买 `[avatar]` 商城商品时客户端首字节为玩家所选技能选项行（ability index）、其后 u16 为时限代号（period code），服务端此前把它误读为「商品类型/属性值」校验；CMD 18（DELETE_ITEM）与 CMD 28（DISJOINT_ITEM）此前不支持生物背包（listType 7）。
- **依据**（源项目经 IDA + 抓包验证，同步入库）：DF2008 购买请求条目 7 字节为 `{u8 ability row, u16 period, u32 commodity}`（sub_867E80）；`[avatar select ability]` 段的非注释 `` `[ `` 行数为可选项数；时限补丁只改 period 不影响所选行。CMD18 生物背包仅允许红色/蓝色/绿色公告牌袋（artifact bag）内数量为 1 的公告牌整体删除。
- **改动**：`ItemDefinition` 新增 `AvatarSelectableAbilityCount`（`ItemCatalog` 解析 `[avatar select ability]`）；`AvatarCompoundCatalog` 改用该缓存值，不再重复读脚本；`CeraShopCatalog` 新增 `CeraShopPurchaseItemPlanner`（装扮按所选行写入 `AvatarAbilityIndex`、`AvatarRemainingSeconds=0`），购买请求字段更名 `AvatarAbilityIndex`/`PeriodCode`，装扮购买日志记录所选行与时限代号；新增 `Game/Inventory/ItemDeletionPlanner`（CMD18 listType 0/7 统一校验与扣除，含逐槽累计与 32 条上限）；`DeleteItemHandler` 改用规划器并支持生物背包，请求体不合法时仍按 DF2008 0x00410CEF 语义回 `CreateDeleteItemError(请求体第 3 字节, 0x11)` 解除客户端挂起操作，保存失败同样回错误；`DisjointItemPlanner` 新增生物背包重载（itemSpace 7 只接受公告牌袋槽位与 `IsArtifact` 物品），`DisjointCatalog` 分解资格对公告牌放行装备类限制，`DisjointItemHandler` 落盘采纳生物背包结果。
- **验证**：服务端与自测构建 0 警告 0 错误；`creature-artifact-disposal`、`full-bag-consumption`、`cera-shop-avatar-purchase` 套件通过（含 DF2008 CMD18 生物背包线格式向量、三色公告牌删除/分解、装扮选项 0-4 全组合经穿戴/JSON 往返/NOTI13 保真、越界选项拒绝）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：无。

### ADD-37 全部修理仅修理穿戴位并回写背包耐久权威行（同步自 DFLegacy de11e8c）

- **目标**：CMD 25「全部修理」（slot=0xFFFF）此前连背包内装备一并计费修理；DF2008 客户端本地会把穿戴位与背包装备全部刷新为满耐久，若服务端只结穿戴位，背包显示会虚增耐久。
- **依据**：DF2008 0x00411C33..0x00411D06 全部修理本地填充穿戴位与全部背包装备；结账只针对穿戴位，故服务端须在 REPLY25 之后以权威 NOTI14 行撤销客户端对背包的本地改写。
- **改动**：新增 `Game/ItemUpgrade/EquipmentRepairPlanner`（错误码枚举 `InvalidTarget=0x11`/`InsufficientGold=0x0A`）；全部修理只收集穿戴位 slot<23 的可修目标并计费，背包不动，`SynchronizationItems` 输出背包装备+穿戴位的权威行供应答回写；单件修理校验 itemSpace 0 走 `IsMainItemSlot`、itemSpace 3 要求 slot<23；`RepairEquipmentHandler` 改用规划器，应答后按 itemSpace 分组补发 `NOTI14` 权威行，日志改报计划价与件数。
- **验证**：服务端与自测构建 0 警告 0 错误；`equipment-repair` 套件通过（全部修理只修穿戴位、背包行原样同步、满耐久零价成功、副本倍率价 1732、余额不足 0x0A、非法目标 0x11、REPLY25 与 NOTI14 字节向量、落盘金币与耐久复核）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：无。

### ADD-36 玩家邮件按客户端规则收取邮费并同步余额（同步自 DFLegacy ff89a4f）

- **目标**：CMD 97 寄信此前只校验「金币余额 ≥ 附带金币」，未收邮费；客户端本地会扣除「附带金币 + 邮费」，成功后服务端如不同步余额，客户端显示会偏差一个邮费。
- **依据**：DF2008 0x00809BB4 / 0x0080AF60：邮费 = 基础 100 + 带附件 900 + floor(金币×5%)（上限 10000），取整除精确成立；余额不足时客户端报错误 10。
- **改动**：新增 `Game/Mail/CharacterMailPostage`（`Calculate`/`TryDebit`，防溢出）；`MailboxSendHandler` 在规划期先做含邮费的余额校验（不足回错误 10），`CharacterMailSendFailure` 新增 `InsufficientGold`；CMD97 成功后的 `NOTI14` 余额同步从「仅附带金币时发送」改为恒发送绝对余额（纯文字信也收 100 基础邮费，且同步不会把邮费变相返还）；存储层 `SendCharacterMailFromCharacterAsync` 以 `TryDebit` 的期望余额做并发对账，不足返回 `InsufficientGold`，成功日志记录邮费与余额。
- **验证**：服务端与自测构建 0 警告 0 错误；`mail-postage` 套件通过（邮费表 8 组边界、TryDebit 溢出与负值拒绝、绕过邮费的余额对账拒绝、附带金币+邮费+堆叠扣减同事务、纯文字信收基础费、收件人校验失败不扣费、系统邮件不收玩家邮费、重载持久）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：无。

### ADD-35 系统邮件统一中文文案与管理员发件人（同步自 DFLegacy e52aab3）

- **目标**：背包满溢出转邮件的发件人此前为 `DAFLegacy`、正文为英文模板，与管理员邮件（`游戏管理员`）不一致。
- **改动**：新增 `Game/Mail/SystemMailTemplates`（发件人常量 `游戏管理员`、八类来源的主题前缀与中文正文模板：通关奖励/物品栏整理/任务奖励/礼包奖励/商城购买/商城礼包奖励/商店购买/任务奖励补发）；`CreateOverflowMailRequests` 参数从字符串改为 `SystemItemMailReason` 枚举，8 处调用点（登录整理、任务奖励、任务批量完成、礼包奖励、商城购买、商城容器奖励、商店购买、副本通关奖励）与存储层遗留任务奖励迁移全部换用模板；线上邮件发件人字段为空时的回退值改为管理员名；`AdminSendMailRequest` 的发件人/正文参数化默认值取模板；后台页面发件人输入框默认值同步为「游戏管理员」。
- **验证**：服务端与自测构建 0 警告 0 错误；`system-mail` 套件通过（八类模板 CP936 编解码无损、附件元数据保真、整理类不误称「空间不足」、管理员默认发件人、NOTI97 包体与正文记录同发件人、重载持久）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：部署副本 `admin/index.html` 需随服务端发布更新（部署副本按约定由用户自行维护）。

### ADD-34 角色名校验与创建失败回复细分（同步自 DFLegacy 548113c）

- **目标**：CMD 5 建角此前在处理器内做重名/名额预检（与落库非同一临界区），保留名（游戏管理员/GM）不拦截，存储失败与请求错误一律回错误码 2。
- **依据**：DF2008 0x0040FB43 失败码映射：2→串 681、4→串 682、0x12→串 683、0x13→串 684。
- **改动**：新增 `Network/Handlers/Characters/CharacterCreationResult`（失败枚举 + `CreateCharacterResult` + `CharacterCreationReply.Create` 按失败原因选错误码：名额满 4、重名/保留名/非法名 0x12、请求错误/账号不存在 0x13、其余 2）；存储层 `CreateCharacterAsync` 返回值改为该结果：保留名（`游戏管理员` 精确匹配、`GM` 忽略大小写）拒绝、重名与名额在存储门内原子判定、落盘失败（IO/未授权）回滚新建条目并返回 `PersistenceFailed`；名称长度与等级校验收敛到 `ValidateCharacterCreation`。`CreateCharacterHandler` 去除预检，直接采纳存储结果应答。
- **验证**：服务端与自测构建 0 警告 0 错误；`character-creation` 套件通过（六种保留名写法双路径拒绝、前缀名不误伤、大小写不敏感重名、并发建名恰好一个成功、名额满错误 4、非法名/等级/账号失败码区分、重载持久）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：源项目的「落盘失败注入」断言依赖 JSON 存储的临时文件锁形态，SQLite 形态下该项未移植（回滚分支保留在生产代码中）。

### ADD-33 复活币兑换道具落地（客户端状态类型 13）（同步自 DFLegacy 476161b）

- **目标**：新年硬币（7180）、硬币书（7181）、通用硬币（42）三类 `[waste]` 兑换券此前无效果定义；DF2008 的 increase_status 类型 11 是公会积分而非硬币，原生硬币券用类型 13。
- **依据**：DF2008 对类型 13 走通用消耗路径、无独立效果消息，余额由 NOTI14 更新；7180/42 记 1 枚、7181 记 5 枚。
- **改动**：`IncreaseStatusType` 新增 `RevivalCoins = 13` 并排除出「按 10 倍下发」与卖出价两类特判；`ItemCatalog` 对三个脚本路径硬编码硬币效果；`IncreaseStatusHandler` 应答后补发主背包 1 号槽（复活币余额槽）的 `NOTI14` 绝对余额行；存储层 `UseIncreaseStatusItemAsync` 在存储门内原子完成「券扣减 + 余额入账」：余额溢出返回新增的 `BalanceOverflow`、余额槽被非硬币占用返回 `ItemStateChanged`、券本体（物品 1）与非法槽位直接拒绝。
- **验证**：服务端与自测构建 0 警告 0 错误；`revival-coin-voucher` 套件通过（三类券效果缓存、逐枚入账不改 SP/经验、末张清槽、空槽重放拒绝、余额上限溢出拒绝、余额槽占用拒绝、CMD32 类型 13 字节向量、NOTI14 余额行布局、重载持久）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：无。

### ADD-32 拾取失败补发回包解除客户端物品操作锁定（同步自 DFLegacy 706f07e）

- **目标**：CMD 46 拾取此前只在超重时回错误 0x15，其余失败路径（无效物品、落库失败、生物/装扮背包放不下等）静默返回；客户端对每次拾取持乐观挂起操作，不收到回包就不解锁，后续 CMD 18/47 全部被阻塞。
- **依据**：DF2008 对 CMD 46 乐观拾取：错误 0x15 保持静默回滚（0x00414198 公共回滚清除物品对象 +0x7A5 挂起标记）；错误 4 在 0x00414170 格式化串 793 后走同一回滚。
- **改动**：`GameProtocolConstants` 新增 `GetItemInventoryFullError = 0x04`；`CraftPacketBuilders` 新增 `CreateGetItemRejection(request, inventoryFull)`（`CreateGetItemSilentRejection` 保留为别名）；`GetItemHandler` 全部失败路径补发回包——背包/快捷栏满用错误 4，其余保持 0x15；请求体不足 2 字节时不回包。
- **验证**：服务端与自测构建 0 警告 0 错误；`full-bag-consumption` 套件通过（满包错误 4 且地面物与背包不变、其余失败保持 0x15、错误 4/0x15 字节向量）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：无。

### ADD-31 副本入场道具校验与原子扣除（同步自 DFLegacy 4693295）

- **目标**：副本 PVF 的 `[required item]`（物品 id 数量 是否扣除）此前不解析，配置了入场票的副本可空手进入；深渊票与普通入场票的分两次落库也可能在失败时留下半扣状态。
- **改动**：`DungeonDefinition` 新增 `RequiredItemId`/`RequiredItemCount`/`ConsumeRequiredItem` 与 `TryConsumeRequiredItem`（非变异规划：按槽位顺序跨堆叠校验总量，可选扣除；id/数量非法时整个副本定义解析失败，不静默降级为免费进入）；`SelectDungeonHandler` 在布局生成前校验入场道具，不足回 `CreateSelectDungeonMissingRequiredItemReply`（CMD 16 错误 17 + 队伍槽位字节，DF2008 0x410A3C 用其格式化串 2100）；普通入场票与深渊票合并为一份入场计划，在迷宫可用后一次性落库，失败整体不动；变更行以 `NOTI14` 统一同步。
- **验证**：服务端与自测构建 0 警告 0 错误；`dungeon-required-item` 套件通过（注释排除、跨行参数、标志 0 保留校验、十二种合法/非法声明、跨堆叠扣除与 uint.MaxValue 溢出边界、与深渊票合成计划、CMD16/NOTI14 字节向量、失败/成功落库与重载）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：部署副本 `Script.pvf` 的对应副本脚本需实际配置 `[required item]` 段才有行为（当前自带 PVF 未登记该项配置）。

### ADD-30 副本 Boss 入场条件与条件怪生成（同步自 DFLegacy a7a3a55，含其前置 78f19c2 基础）

- **目标**：DF2008 支持 `[boss room entrance condition]`（`[hunt monster]` 击杀计数 / `[seeking]` 指定房间全清）与 `[monster condition]` 条件怪行；服务端此前不解析，Boss 房始终直进。
- **依据**：13339 `SetGridPath`（0x082FFB30）把每次出现分配到非起点/非 Boss 的可用格子（可替换）；`check_boss_map` 对未满足条件返回 7；怪物队伍行 `[monster team]` 区分敌我（100 为敌方），房间清场判定只统计存活敌方（0x0830A0E8 `check_grid_clear`）。
- **改动**（前置基础先行入库）：`GameDungeonMonster` 新增服务器侧 `Team`/`IsEnemy`，`DungeonCatalog` 解析 `[monster team]` 并在冠军候选与清场规则中按敌我过滤，`DungeonRunState` 引入 `IsCleared`/`IsBossClearPending` 终局房清场语义（MOVE_MAP 对已清场/待确认状态回错误 0x13）；随后新增 `Game/Dungeon/DungeonBossEntranceCondition`，`DungeonCatalog` 解析 `[boss room entrance condition]` 与 `[monster condition]` 模板行并提供 `GetBossEntranceMonsterCandidates`，`DungeonRunState` 开局分配条件怪（进对应房间时以追加行生成、独立运行时 id、不参与冠军晋升）、按首杀累计击杀、`[seeking]` 按房间全清记账，未满足条件时 `TryMoveRoom` 拒绝进入 Boss 房；`MoveMapHandler` 对被条件阻挡的移动回错误 7、对生命周期拒绝回 0x13；`DieMonsterHandler` 记录条件达成日志。
- **验证**：服务端与自测构建 0 警告 0 错误；`dungeon-boss-entrance` 与 `dungeon-completion` 套件通过（前者：四组猎杀三元组解析、条件怪生成与 NOTI29 九字节记录、首杀去重、未满足条件拒绝进入且房间不变、重进房不重刷、全满解锁、终局房一次性清场、无条件副本不受影响、`[seeking]` 房间目标、无模板时拒绝开局、错误 7 字节向量；后者为 78f19c2 基础的原配套测试：队伍行解析与阵营保真、盟方心脏不阻挡清场且不可晋升冠军、Boss 死亡窗口拒绝 MOVE_MAP、空终局房一次性结算、多 Boss 等末杀、无 Boss 房需全灭、NOTI31/CMD48 字节向量）。**真机未复测**：判据见 `PENDING.md` P-56。
- **遗留项**：①本仓库 Boss 死亡结算有自己的编排（假死判别与多场次重建，见 FIX-11 同期工作），`DungeonRunState.BeginBossClear`/`TryEnableEmptyBossClear` 已随基础入库并由 `dungeon-completion` 套件守护，但处理链尚未接线（DAFLegacy 的 `BossDieCheckHandler` 不调用 `BeginBossClear`、`EnableEmptyBossRoomSettlementAsync` 不调用 `TryEnableEmptyBossClear`），当前死亡窗口语义由既有流程承担；78f19c2 对 DIE_MONSTER/BOSS_DIE_CHECK 的敌我过滤加固（盟方不计击杀、级联只收敌方、存活校验）同样未并入既有流程——三者仅在副本定义含非 100 队伍行时才产生行为差异，如需与源项目完全对齐另行处理；②部署副本 PVF 无 `[boss room entrance condition]` 的副本行为不变。

### ADD-29 IJL15 新增副本秒杀配置开关（复刻客户端 //kill 循环）

- **目标**：用户需求——ijl15 客户端插件增加「秒杀副本房间怪物」能力，配置开启与关闭，且仅在副本内生效。
- **取证结论**（IDA 逐 case 复核，地址登记 `docs/protocol/client-function-register-60cn.md` §9/§17）：客户端自带纯本地的杀怪通路——聊天命令 `//kill`（`sub_846BA0` 0x84824D 分支）在场景类型 ∈{3,6,7}（游戏主对象 `dword_B5D6F0`+0x10 保护整数，城镇=1 不在内）时，遍历 `[[dword_B5DCE0] vtbl+0x80] +0xB0` 对象列表，把 `vtbl+0x2C(0x211)` 判为怪物的对象逐只交给击杀分派器 `sub_843AC0`——分派器按怪物状态路由到本地死亡例程（`vtbl+0x13C(1)`）或直接 CMD 42 击杀上报，死亡/掉落/经验/通关全部走客户端与服务端既有流程。另证：`//allkill` 切换的 killer mode 开关 `dword_B5F418`（§17）经真机验证不产生命中即杀效果（消费方在保护代码区不可见），该机制弃用、证伪结论已登记。
- **改动**：`Client/DAFLegacy.Ijl15/src/dungeon_killer_mode.cpp/.h`——`EnableDungeonKillerMode=1` 时的秒杀为**加载门控 + 进房派发窗口 + 容器内派发**：①挂点——每帧主泵 `sub_433B00` 帧计时调用点 0x433C0D（唯一调用点，原字节 `E8 4E FA FF FF`）在主循环执行扫描；客户端每完成地图加载发送的 CMD 40（发送点 0x89B711/0x8A2EB5）与 NOTI 29 房间查表调用点（0x41F98B → `sub_405750`）挂钩，**只记录/清除加载时刻标志，不发任何包**；②加载门——完成包 +3000ms 或房间推送 +8000ms（兜底）后放行，门未开期间整段挂起（派发进加载中的房间会卡死客户端）；③进房派发窗口——门开时重置跟踪表并打开 1500ms 窗口，窗口内扫描房间容器（`[[dword_B5DCE0] vtbl+0x80] +0xB0`，`sub_4048A0`/`sub_405830` 取对象）：`vtbl+0x2C(0x211)` 低字节判怪物、首见即派发 `sub_843AC0`（不做存活查询门控——该查询对 1079 类怪甚至多数活怪返回 ≤0，真机曾致成片漏杀；误派发尸体仅为重复上报，服务端陈旧守卫吸收）、按 (指针,id) 跟踪防重复；**窗口关闭后新出现的实体只跟踪不派发**（多场次 Boss 的假身/换阶段等战斗编排实体曾被逐个秒杀，服务端按有效击杀结算，造成「Boss 每攻击几次掉落一次」——见 PENDING P-55）；「死→活」只更新状态不派发（复活属换阶段编排）。**派发对象一律取自容器现场，永不使用早先记录的指针**（等待期间被玩家打死的怪对象可能已释放，派发悬空指针会卡死客户端）。临时诊断日志 `Logs\DAFLegacy.Ijl15.killer.log`（客户端目录下，记录门开时刻与每次派发/跟踪决策，收尾时移除）。守卫：开关开启、保护模式门、场景类型 ∈{3,6,7}。开关关闭时模块不做任何事。配置项 `[Features] EnableDungeonKillerMode`（默认关）；钩子安装失败为非致命；构建登记同步（CMakeLists.txt / vcxproj / filters）。
- **验证**：插件 Release Win32 构建 0 警告 0 错误（/W4 /WX），`ijl15_tests` 兼容性测试通过。v1–v8 历次真机判否；v9 真机漏杀（存活查询误判）；v10（首见即派发）真机漏杀修复、但暴露 Boss 假身被逐个秒杀导致每阶段掉落的问题。历次判否的部署核验与抓包取证见 `PENDING.md` P-55。**v11（派发窗口）真机未复测**：判据见 `PENDING.md` P-55。
- **遗留项**：①开启期间进入房间即清空普通怪物与普通 Boss，属该开关预期行为；②51 号多场次 Boss 每次进房被消费一次击杀（一份掉落）、其余场次手动打完，该体验待真机确认；③若真机显示其他场景类型（如 PvP）误触发，收窄 {3,6,7} 集合后复测。

### ADD-28 后台邮件页支持一键清空角色全部邮件

- **目标**：用户需求——后台增加清空指定角色所有邮件的能力（含保管箱与未领取附件）。
- **改动**：存储层新增 `ClearCharacterMailboxAsync`：邮箱整体置空并返回删除数量，不触发过期清扫、不区分邮件状态。管理端点 `DELETE /api/admin/characters/{characterId}/mail`（无请求体，返回 `removedMailCount`；角色不存在 404）。后台邮件页新增「清空邮件」危险操作表单（独立角色选择、提交确认、结果提示），卡片与发送邮件卡片同宽满行、以 18px 行距堆叠（`.view > .surface + .surface` 间距规则，`compact-form` 不再用于该卡片）；在线角色无主动推送——客户端邮箱窗口由打开邮箱（CMD 99）时的服务端快照重建，清空后下次打开即呈现空箱，重建前对已删除邮件的领取请求被服务端拒绝。`ADMIN_API.md` §2 总览、§5.2.6 边界表述与新增 §5.3 登记。自测 `quest-bulk-completion` 套件附带清空断言（清空后邮箱为空、未知角色 404）。
- **验证**：服务端与自测工程构建 0 警告 0 错误；含清空断言的 `quest-bulk-completion` 套件全部断言通过（套件行清理期的既有 SQLite 临时库文件锁 IOException 见 `PENDING.md` P-40 注意，与本项无关）。**真机未复测**：判据见 `PENDING.md` P-54。
- **遗留项**：部署副本 `admin/index.html` 与 `admin.js` 需随服务端发布更新（部署副本按约定由用户自行维护）；清空不可恢复，页面侧以确认弹窗拦截误操作。

### ADD-27 后台「任务权限」：一键完成等级区间覆盖当前等级的任务并结算奖励

- **目标**：用户需求——后台增加任务操作能力，把等级区间覆盖当前角色等级的任务一次性标记为完成并**带奖励结算**，推进当前等级段任务链并解锁任务关联的隐藏副本（任务侧的"权限"数据面）；按用户后续澄清，不做历史等级段的回溯补记。
- **改动**：新增 `Game/Quest/QuestBulkCompletionPlanner`。入选规则：PVF `[level]` 下限 ≤ 角色等级 ≤ 上限（上限不在区间内的任务保持未完成）；`[job]` 匹配（`QuestCatalog.MatchesJob` 改公开、新增 `Definitions` 只读入口）；成长阶段一致——带 `[grow type]` 限定的普通任务须与角色主成长类型一致，转职/觉醒最终任务（奖励 `grow type`/`awakening type`）与两链中间任务（`[job change quest]`）仅在角色已到达对应转职/觉醒阶段时补记，未到达时整条成长链保留给玩家；新手引导完成任务（1016）不参与。奖励结算 `PlanRewardSettlement` 与启动期遗留奖励迁移（`MigrateLegacyQuestRewardsAsync`）同款语义：任务经验（满级为 0、可重复任务不计）与升级 SP 顺序累计，任务金币直接入账，固定奖励物品先入背包（权重上限放开）、放不下的溢出部分逐件转系统邮件；需自选奖励（`[reward selection int data]`）、物品缺失或金币溢出的任务跳过奖励并回显。存储层 `CompleteCharacterQuestsByLevelAsync`：并入 `CompletedQuestIds` 与 `RewardedQuestIds`（奖励按已结清处理，防止下次启动遗留奖励迁移重复补发），从进行中任务移除，采纳结算后的等级/经验/SP/金币/背包/仓库容量，追加任务关联副本解锁（`HiddenDungeonUnlockCatalog`），溢出邮件在 255 封邮箱上限内逐封追加、超出部分丢弃计数返回。在线会话：`CharacterSessionRegistry` 会话租约新增 `characterProgressReplaced` 回调（`SelectCharacterHandler` 登记时接线），端点落盘后把持久层结果交会话窄合并采纳（任务/背包/金币/SP/技能/邮箱/副本进度；位置、PvP、疲劳等现场字段不动）并推送背包整包、升级时的技能与权威属性同步、可接任务列表（新增 `SendAcceptableQuestListSafeAsync` 跨会话封装）与新增副本权限包，有溢出邮件时推送新邮件提醒；客户端任务日志（EP 页）完整刷新在下次选角。管理端点 `POST /api/admin/characters/{characterId}/quests/complete-by-level`（无请求体，返回新完成任务、跳过奖励任务、溢出邮件去向、新增解锁副本与在线通知结果），后台页「任务权限」页签（提交确认＋结果提示）；`switchView` 对未知页签做文案兜底（旧版 `admin.js` 配新版 `index.html` 时不再抛 `Cannot read properties of undefined`）。`ADMIN_API.md` §2 总览与 §4.7 登记。自测新增 `quest-bulk-completion` 套件（合成 PVF：四种角色形态与等级区间的规划断言、奖励结算断言、落盘合并/解锁/重载断言、邮箱清空断言）。
- **验证**：服务端与自测工程构建 0 警告 0 错误；`quest-bulk-completion` 全部断言通过，`quest-map`、`quest-pvp-rank`、`server-options-validation`、`protocol-golden`、`equipment-reinforcement`、`item-sealing` 套件通过。`quest-bulk-completion` 与既有 `hidden-dungeon-quest` 的套件行在清理期报既有 SQLite 临时库文件锁 IOException（断言 0 失败，未改动 HEAD 工作树复现一致，登记见 `PENDING.md` P-40 注意），与本项无因果关系。**真机未复测**：判据见 `PENDING.md` P-53。
- **遗留项**：部署副本 `admin/index.html` 与 `admin.js` 必须一起更新并强刷浏览器缓存——旧版 `admin.js` 缺少「任务权限」页签的文案与表单绑定，是页面报 `Cannot read properties of undefined (reading '0')` 与"点击完成任务回到邮件页"的直接原因；如需"历史任务全补记"或"可自选奖励指定选项"另行立项。

### ADD-26 强化毁坏补偿支持 server.json 覆写（`Reinforcement.DestroyBonus`）

- **目标**（设计落点 `docs/design/02-portability-contracts.md` R5）：60CN 自带 `upgrade.etc` 无 `[disjoint]` 段，毁坏补偿缺省关闭（见 FIX-14）；提供 `server.json` → `DAFLegacy.Reinforcement.DestroyBonus` 节点，运维不改 PVF 即可启用/调整补偿。
- **改动**：`ServerOptions` 新增 `Reinforcement` 节点——`Enabled` 总开关（默认 `true`；`false` 时毁坏补偿整体关闭，`DestroyBonus` 节点与 PVF `[disjoint]` 段一并失效，节点取值不再校验）与 `DestroyBonus`（`ItemId`/`EquipLevelConst`/`BonusCountLevelBase`/`BonusWeightByRarity`/`CorrectionGradeByRarity`）；**整节点覆写语义**——`ItemId > 0` 时整体取代 PVF `[disjoint]` 段（不逐字段合并），`ItemId = 0` 或节点缺省回落 PVF 段，两者皆无则不补偿。`EquipmentReinforcementCatalog` 注入 `ServerOptions` 并在加载期合并（启动日志打印 `destroyBonus=<id> (source=server.json|etc/upgrade.etc|none)`）。`ServerOptionsValidator` 新增 `ValidateReinforcementDestroyBonus`：`ItemId` 0..65535、两个常量 ≥0、品级键白名单（common/uncommon/rare/unique/epic，键序无关）、权重有限非负、启用即至少一个权重为正——违例与其他配置错误同样启动期拒绝（退出码 2）；组合根在目录初始化后另校验 `ItemId` 存在于物品表（强化可用为前提），指向不存在物品即拒绝启动。`server.json` 模板启用示例配置（3037 无色小晶块、等级基准 10、五品级权重 1：+11 毁坏补 1 个、+12 补 4 个，按（目标等级−基准）²×权重增长）；`README.md` 配置表补行。自测：`server-options-validation` 新增八条路径（缺省节点、启用无权重、完整配置、键序无关、越界物品 id、负常量、未知品级键、负权重/负修正、开关关闭时节点惰性），`equipment-reinforcement` 新增「PVF 段生效 + server.json 整节点覆写」对照断言。
- **验证**：服务端与自测构建 0 警告 0 错误；`server-options-validation`、`equipment-reinforcement`、`protocol-golden` 套件通过（全量 SelfTests 偶发受本机既有 SQLite 临时库文件锁问题影响，登记见 PENDING.md P-40）。**真机未复测**：判据并入 PENDING.md P-52 判据 ③——部署副本 `server.json` 加入模板节点（或沿用其值）重启，+11/+12 毁坏后背包出现对应数量 3037，服务端日志 `destroyRewards=...` 与 `destroyBonus=3037 (source=server.json)`。
- **遗留项**：部署副本 `server.json` 由用户维护，需自行并入该节点；补偿数值（权重/基准/物品）按运营意图调整，改后重启生效。

### FIX-14 强化毁坏（CMD 83 resultCode=3）被客户端弹「缺少物品」：应答时序违背客户端目标槽守卫，毁坏补偿列表未实现

- **现象**：强化 +10→+11 服务端掷中毁坏（`result=3, destroyed=True`，物品 27098@0:13、材料 3171×12、金币 311040 均正确扣减），客户端却弹「缺少物品」，毁坏装备消失但无任何毁坏提示与补偿。
- **根因**（IDA 复核，`sub_40F290` case 0x53，登记见 `docs/protocol/client-function-register-60cn.md` §16）：客户端消费 CMD 83 成功形态应答时，在写入补偿物品**之前**先查目标槽物品（`sub_4048C0` @0x41623d），查不到直接报串 1658「缺少物品」（@0x41624a）并终止。服务端在毁坏路径沿用了非毁坏路径的「背包快照→应答」顺序，快照已把被毁装备从客户端背包移除，应答处理时目标槽为空即触发 1658。毁坏分支的正确次序在客户端自身：移除目标槽物品（`sub_45E210` @0x4162cc）→弹毁坏结果窗（`sub_799EF0(..., 0x44, 0, resultCode, 0)` @0x4162e2）→读补偿条目数 u8 并逐条应用 `{slot u16, itemId u16, count u32}`（@0x416307..0x4163a7；count 为**增量**：物品 vtable+0x38 `sub_461AC0` 在槽内同 id 物品现有数量上累加，槽内无物品则按该数量新建）——此前应答只写补偿条目数=0 且服务端从不发放补偿。
- **改动**：`CreateUpgradeItemReply` 在 resultCode>2 时写补偿条目数与条目列表（空列表与原 12B 逐字节一致）；`EquipmentReinforcementCatalog` 解析 `etc/upgrade.etc` 可选 `[disjoint]` 段（`disjoint bonus item`/`equip level const`/`upgrade failed bonus weight by rarity`/`upgrade const for bonus item count`/`correction grade by rarity`，名称键可带反引号）并按参考工程同式计算毁坏补偿：品级经按稀有度修正后须达 `equip level const` 门槛、目标强化等级须高于计数基准，数量 =（目标等级 − 基准）² × 品级权重（稀有度名 common/uncommon/rare/unique/epic 对应 0..4）；`EquipmentReinforcementPlanner` 毁坏时把补偿物品经 `CharacterInventoryPlanner.TryPlace` 放入主背包（携带重量上限，放不进即整体拒绝并报错误码 21），随计划输出补偿条目——条目 count 为**本次发放增量**（与客户端累加语义及参考工程 `ItemUpgradeAckBuilder` 的 `GrantedCount` 一致；初版误发槽内最终数量，2026-10-11 真机复测发现后按 IDA `sub_461AC0` 增量证据修正）；`UpgradeItemHandler` 毁坏时改为**应答先行、背包快照随后**（快照对账金币/材料/补偿；与 CMD 27「应答后跟权威快照」先例一致），非毁坏路径维持 DF2008 实测的「快照→应答」顺序。A21 参考服 `upgrade.etc` 已入库 `Server/DAFLegacy.Server/upgrade.etc`（与 `channel_info.etc` 同级，作 `[disjoint]` 段式样参考）。
- **验证**：客户端解析面以 IDA 反编逐分支取证（字段序、两道槽守卫、失败码映射 4→1658/7→20000/10→1660/13→1654/17→1652/22→1661/99→30005/其余→1662 带码；毁坏呈现链 `sub_7C2570`/`sub_7F20C0`——resultCode=3 发 `EQUIP_BROKEN` 事件并显示串 1672，地址均登记 §16）；服务端与自测构建 0 警告 0 错误，`equipment-reinforcement` 套件通过（新增含补偿条目的应答字节向量、毁坏补偿放置断言与背包放不下整体拒绝断言；全量 SelfTests 受本机既有 SQLite 临时库文件锁问题阻塞，失败集合与本机基线一致，既有登记见 PENDING.md P-40，与本项无因果关系）。60CN 自带 `upgrade.etc` 无 `[disjoint]` 段，当前部署形态下补偿条目数为 0、行为为「装备消失 + 毁坏提示」。**真机复测（2026-10-11）通过**：+10→+11 毁坏时客户端弹「装备已被破坏」、装备从槽上消失、无「缺少物品」，材料/金币扣减正确（判据 ①②④⑤，见 PENDING.md P-52）；判据 ③（`[disjoint]` 段配置后的补偿物品发放）待配置后复测。
- **遗留项**：①毁坏补偿内容已可经 `server.json` 覆写启用（见同日 ADD-26，模板已含 3037 示例），或部署副本 `Script.pvf` 的 `etc/upgrade.etc` 追加 `[disjoint]` 段；注意 A21 参考值的补偿物品 3243（强烈之痕迹）在 60CN 物品表中不存在，60CN 化配置应改用本版物品（如 3037 无色小晶块，取值式样见入库的 A21 参考 `upgrade.etc` §disjoint）。②毁坏补偿启用后的真机验证并入 PENDING.md P-52 判据 ③。③强化消耗金币在穿戴位目标（listType 3）路径应答后无快照对账，金币显示滞后（既有行为，本次未动），如需处理另行立项。

### ADD-25 后台邮件表单支持为装备附件指定强化等级（0～31）

- **现象**：后台邮件表单只能发放未强化的装备附件，无法直接发放带强化等级的装备。
- **改动**：`AdminSendMailRequest` 新增 `Reinforcement` 字节字段；`AdminMailComposer` 在附件为普通装备（物品栏分类 Equipment）时把该值写入邮件附件 `State` 的低 5 位（再封装高 3 位保持 0，随领取进入物品栏实例，语义同 ADMIN_API.md §5.2.3 的 `state` 复合字节），取值上限复用 `EquipmentReinforcementCatalog.MaximumLevel`（31）；越界取值，以及 Avatar、Creature 物品与堆叠物携带非零强化值，一律报错而非静默忽略。后台页面（`Server/DAFLegacy.Server/wwwroot/admin/`）在附件类型为「普通道具 / 装备」且选中物品确为普通装备时显示「强化等级」0～31 下拉框，其余情形隐藏并按 0 提交。SelfTests 的 admin mail 组装断言扩展装备强化写入、越界拒绝与非装备拒绝三条路径；`ADMIN_API.md` 新增 §5.2.8 登记表单行为。
- **验证**：服务端与自测工程构建 0 警告 0 错误；含新断言的 `server-options-validation` 套件通过。全量 `--selftest-all` 在本机受既有 SQLite 连接池临时库文件锁问题阻塞（失败集合在未改动的 HEAD 工作树上复现一致，既有登记见 `PENDING.md` 本机 SelfTests 临时数据库文件被占用条目），与本项无因果关系。**真机未复测**：判据为后台页面按物品类型显隐「强化等级」、发放后在线邮箱领取的装备在客户端显示对应强化等级。
- **遗留项**：真机复测待做；`admin/index.html` 与 `admin.js` 的部署副本需随服务端发布更新（部署副本按约定由用户自行维护）。

## 2026-10-08

### CHANGE 端口定义按参考工程对齐：入口 7001、频道 10000+号、数据面 12311/12312，取消遗留频道端口

- **改动**（端口分配表见 `docs/design/02` R6.1，为该表的唯一定名来源；一个数字只承担一个面的语义）：
  - **入口 2311 → 7001**（`Entrance.Port`，与参考工程登录/选角端口同义同号）。启动器同步：`launcher.json` 的 `EntrancePort` 改 `7001`，`LauncherOptions.EntrancePort` 去掉默认值（缺失即在校验期报错，与 Server 侧"端口无默认值"一致）。
  - **角色屏 UDP 2311 → 7001**（`CharacterDatagram.Port`）。与入口同号是客户端约定——客户端用启动器注入的同一端点做角色屏的 UDP 交换，现由启动期校验强制：两值不等即拒绝启动（`CharacterDatagram.Port ... must equal Entrance.Port ...`）。
  - **玩法/NAT UDP 7002/7003 → 12311/12312**（`GameplayDatagram.PrimaryPort`/`SecondaryPort`，对齐参考工程 `InitialUdpPort1/2`；双端口不可合并——客户端按两端口观测到的端点比对判定 NAT 类型）。
  - **取消 `Channel.Port`**（原 7001 遗留频道端口）：空组回退记录与无目录单频道会话改用 `PortBase + 会话频道号`——有目录时即组 0 首频道的监听（与旧遗留端口绑定的身份相同，客户端可见端点仅由 7001 变为该监听端口），无目录时由 `EntranceService` 回退分支单独绑定；`Channel` 节只剩 `Host`/`PortBase`/`MaximumUsers`/`ServerGroup`。`EntranceService` 中针对 `Channel.Port` 的等值跳过分支一并删除（入口与频道端口同名的配置已不可能通过校验）。
  - **兜底应答表 `FallbackCommandReplies` 的 CHECK_CONNECTION 不再硬编码 `127.0.0.1` 与 `2311/2311`**，改为与 `CheckConnectionHandler` 同源读取 `GameplayDatagram.Host` 与主/次端口（该表无线上路径，仅防御性回退；此前它与真实 handler 对同一包各写一份端口）。
  - 同步：`stop.bat` 端口探测模式（TCP `8081`/`7001`/`100xx`、UDP `7001`/`12311`/`12312`）、`tools/diagnostics/netstat_sampler.sh` 采样模式（原 `200xx` 已随 PortBase 改值失效）、`tools/PacketDumper` 的 NOTI 1 样例端口、MCP 抓包事件与过滤器的注释示例；文档 `README.md`、`docs/design/02`（新增 R6.1 端口分配表、段位约定与"绑定地址≠通告地址"约定）、`docs/design/13` §3.1/§11.5、`docs/design/09` 端口取值核对表、`docs/servers4a21/feature-backfill-plan-vs-s4a21.md` §4.3。
- **验证**：构建 0 警告 0 错误；全量 SelfTests 32 套件全绿——`server-options-validation` 改按模板端口表断言并新增"角色屏与入口同号"拒绝路径，`protocol-golden`/`party`/`program-inline`/`entrance-channel-catalog` 的夹具与字节断言按新端口更新（组队端点表记录字节由 `5A 1B`(7002) 改为 `17 30`(12311)）。**真机未复测**：判据见 `PENDING.md` P-38（监听表、启动器链路、回退组进游戏、NOTI 1 探测端口四项）。
- **遗留项**：部署副本 `server.json` 与 `launcher.json` 必须改用新端口并补齐端口键（副本按约定由用户自行维护）；历史抓包记录中的 `channel:7001`/`2311` 标签保持不变（记录当时事实，不改写证据）；登录应答"主机 + 端口1/2"字段的语义差异（参考工程按端口解释，本项目 IDA 按中继解释）登记为 `PENDING.md` P-39 待复核。

### CHANGE 端口配置改为强制显式：移除全部端口代码默认值，新增启动期强校验

- **改动**：`ServerOptions` 的全部端口属性不再带初始值（`Admin.Port`、`Mcp.Port`、`Entrance.Port`、`CharacterDatagram.Port`、`GameplayDatagram.PrimaryPort`/`SecondaryPort`、`Channel.PortBase`，0 即未配置）；新增 `Infrastructure/ServerOptionsValidator.cs`，组合根在读到配置后、注册任何监听前调用：① 必需端口缺失或越界（1..65535）逐条报错（`Enabled=false` 的节不需要端口，`Admin` 无开关恒为必需）；② 主机必须是 IP 字面量（R6 禁主机名，此前主机名会以 `IPAddress.Parse` 抛异常崩在启动路径上）；③ 同一主机同一传输上的重复占用报错——TCP 跨节检查（Admin/MCP/入口及附加端口），UDP 检查角色屏与玩法主次端口，TCP 与 UDP 同号（入口与角色屏的 7001 即此形态）不算冲突；④ `Channel.PortBase` 必须给频道 1 留出端口。任一错误即逐条打印并以退出码 2 拒绝启动，不再有隐式端口或静默丢弃监听。`server.json` 模板本就显式给出全部端口，未改动；`Entrance.AdditionalPorts` 保留为可选项。同步更新 `README.md` 配置表与端口段、`docs/design/02` R6 契约与判定、`docs/design/13` §3.1 与 §11.5、`docs/design/09` 的 MCP 端口表述、`docs/servers4a21/feature-backfill-plan-vs-s4a21.md` 中 PortBase 与 `Channel.Port` 的取值表述（「默认」改为「模板」）。
- **验证**：构建 0 警告 0 错误；全量 SelfTests 32 套件全绿，新增 `server-options-validation` 套件覆盖九条路径（模板形态通过、整份缺省配置逐项报错、启用 MCP 必须给端口、关闭的节不需要端口、入口端口与频道端口重合拒绝、附加端口重复拒绝、玩法主次端口相同拒绝、主机名拒绝、PortBase 无余量拒绝）；`protocol-golden` 与 `party` 两处夹具原依赖被移除的默认端口，改为显式给出模板取值。**真机未复测**：拒绝启动路径与遗留端口恢复的判据见 `PENDING.md` P-38。
- **遗留项**：部署副本的 `server.json` 必须含全部端口键，否则新校验拒绝启动（部署副本按约定由用户自行维护）；`EntranceService` 内部 `boundPorts` 的次序缺陷未改，见 FIX-12 遗留项。

### FIX-12 遗留频道端口 7001 无人监听：入口附加端口与频道端口同名互相抵消

- **现象**：`server.json` 的 `Entrance.AdditionalPorts` 含 `7001`（与 `Channel.Port` 同名）时，启动日志打印「Entrance port 7001 equals the channel port; it is served by the channel listener.」，但该端口实际没有任何监听（`netstat` 无 7001 LISTENING）。影响面：入口元数据把 127.0.0.1:7001 下发给其余八个服务器组的回退记录，PVF 无频道目录时九组记录全部下发该端点——客户端选到这些组连不上。
- **根因结论**（静态复核 `Network/EntranceService.cs:485-525`）：入口端口循环在等值判断之前先 `boundPorts.Add(port)`，命中 `port == Channel.Port` 后仅 `continue`（不建入口监听），该端口却已留在 `boundPorts`；随后 `if (boundPorts.Add(options.Channel.Port))` 返回 false，遗留频道监听一并被跳过。两处占位互相抵消。
- **改动**：`server.json` 模板移除 `Entrance.AdditionalPorts` 整项——`7001` 与频道端口同名，是上述抵消的直接成因；`8080` 为旧代码默认入口端口的兼容项，`Entrance.Port` 取 8080 时被去重、取其他值时无客户端拨入（启动器注入 2311），两种情形下都无实际作用。移除后入口只监听 `Entrance.Port`，`Channel.Port` 恢复为遗留频道监听（绑定目录首条目身份；该键随后由下一条 CHANGE 条目取消，回退端点改为 `PortBase + 会话频道号`）。`stop.bat` 的端口探测模式同步去掉 `:8080` 并补上默认频道段 `:100xx`。该配置组合此后由启动期强校验直接拒绝（入口端口与频道端口重合即报错，见上一条 CHANGE 条目）；`EntranceService` 的 `boundPorts` 次序缺陷本身保留。
- **验证**：静态复核如上；构建 0 警告 0 错误；全量 SelfTests 32 套件全绿（`entrance-channel-catalog` 的遗留端点断言只覆盖元数据字节，不覆盖监听绑定，故本次修复不改变其断言）。**真机复测未做**，判据见 `PENDING.md` P-38。
- **遗留项**：`boundPorts` 次序建议改为「相等判断通过后再占位」或直接报错，现由配置校验兜住，登记于 P-38。

### CHANGE 移除 GameProbe 探测监听（`ProbeService` 与 `server.json` 的 `GameProbe` 节）

- **改动**：删除旁路探测组件及其全部接线——`Network/ProbeService.cs`（默认禁用的被动采集器，自行拼 hex 写日志，不参与真实客户端链路）、`ServerOptions.GameProbe` 选项、`Program.cs` 的 `AddHostedService<ProbeService>()` 注册与 `/api/status` 的 `gameProbe` 字段、`server.json` 模板的 `GameProbe` 节。关联文档同步收敛：`ADMIN_API.md` 字段表、`docs/design/00` 的「网络绑定」实测结论、`docs/design/01` 目标结构树与配置键清单、`docs/design/02` R6 与后台服务/子系统开关审计表（后台服务 6→5 个 `IHostedService`、子系统开关 5→4）、`docs/design/07` 对比表、`docs/design/09` 追踪改造表。`7001` 此后不再兼有探测端口身份，只与登录/选角入口相关（端口表见 `docs/design/02` R6.1）。
- **验证**：解决方案构建 0 警告 0 错误；全量 SelfTests 31 套件全绿（无套件引用该组件）。
- **遗留项**：无。**已部署的 `server.json` 无需改动**：配置绑定为 `GetSection("DAFLegacy").Get<ServerOptions>()`，未匹配的 `GameProbe` 键被忽略，不报错、不生效（部署副本按约定由用户自行维护）。

### ADD-24 数据面拓扑改走"直连通告"（路线 A）+ 登录应答按客户端读取序补全

- **现象**：组队成立后实时链路不通（客户端给出网络/连接类提示）；配合 ADD-23 的现场定位进一步确认失败点在数据面拓扑而非组队协议面。
- **根因结论**（2026-10-08 客户端 IDA 复核，全文见 `docs/design/13-party-dataplane-and-cohort.md` §12）：
  - **通告端点只在"直连"分支被使用**：客户端 MT-UDP 发送函数 `sub_907160` 按对端对象的 `+0x2C` 字节二选一——非 0 时把数据报套上 `{u16 type, u16 len, u32 源账号, u32 目标账号}` 交给其 **TCP 中继通道**（本服未实现），0 时才 `sendto(对端记录 IP:端口)`。本服原先把该字节通告为上报值（1..4）或默认 2，等于把全队推给中继通道。
  - **中继通道配置来自登录应答**：CMD 1 应答 case（0x40F2E0）读 `u8 result + u8×4 + u32 账号 + dstr 中继主机(cap 0x10) + u32 端口1 + u32 端口2 + u8×2` 并交给 `sub_906070` 配置中继传输；本服原应答仅 5 字节，客户端继续读 23+L 字节，账号与中继主机/端口全取越包尾的残留值。
  - 旁证：`+0x2C != 0` 时客户端在成员状态叠加层打印字面量 `"Relay"`（`sub_832F60` @0x8330CA）——即客户端自己把走中继的对端标注出来。
- **改动**：①新增 `GameDatagramProtocol.AdvertisedDirectNatType`(0) 并在**两处通告点**统一使用——`PartyCoordinator.BroadcastPartyEndpoints`（全队端点表，自身与对端记录一致）与 `StartGameHandler`（选图阶段单人表），使客户端把组队数据报发到本服监听端点、由现有 UDP 中继承载；②新增 `ConnectionPacketBuilders.CreateLoginOk(accountUid)`，按客户端读取序给全 `结果 + 4 标志 + 账号 + 中继主机 dstr(空串) + 端口1/2(0) + 2 尾字节`（共 23 字节；本服无中继，中继字段给惰性值，账号取真实账号），`LoginHandler` 与兜底表的 LOGIN 项共用该构建器。**既有合法流量字节不变**（仅在通告里把 NAT 字节定为 0、把登录应答补长）。
- **验证**：构建 0 警告；全量 SelfTests 31 套件绿（`party` 套件改为上报非零 NAT 并断言通告记录 NAT 字节为 0；`program-inline` 的登录应答断言更新为 23 字节读取序形态）；静态验证 = 上述 IDA 复核。**真机复测未做**，判据：组队后队友旁**不再**出现 "Relay" 标注、服务端 UDP 聚合行 `gameplay`/`relayed` 增长、副本门口 P2P 握手可见数据报。
- **遗留项**：①若真机仍走中继（`+0x2C` 实为客户端"直连可行性判定"而非 NAT 直传，见 13 号文档 §12.6），则须转路线 B（补全中继字段 + 实现按账号转发的 TCP 中继）；②跨 NAT 场景的实时链路仍需路线 B 支撑；③组队副本协同（13 号文档 §4 G-1）未动。

### ADD-23 组队"加入"失败：CMD 11 成功应答形态不足（IDA 复核）+ CMD 2 的 ICMP/MTU 字段归一化

- **现象**：真机双客户端同区域组队，邀请弹窗正常到达，点"加入"后失败（客户端不落实）；客户端另出现网络/防火墙类提示。
- **根因结论**（2026-10-08 客户端主程序 IDA 逐点复核，ImageBase 0x400000）：
  - **CMD 11 成功应答形态不足（首选根因）**：应答 case `0x4104FF` 在结果字节后**无条件**读 `u16 + u8`（`0x41050A..0x410533`：`sub_8F5140` 读 u16、`sub_8F5120` 读 u8），且该 type 字节为 1 时再读一个 u8（`0x41054D`，交易完成分支）。本服成功只回 `[1]`，客户端读越包尾（读到接收缓冲残留；残留恰为 1 时还会带垃圾 uid 走进交易完成分支）。
  - **同意帧语义定案**：同意写入点 `sub_4FEBC0` 写 `{u16 邀请人 uid, u8 0, u32 0}`（调用点 `0x821E08` 传 `[esi+38h]`=弹窗对端 uid，发送前用 `sub_4FDDB0` 校验该 uid 的用户记录）；拒绝路径（`0x821E46`）写 `{u16 uid, u8 弹窗结果码 3, u32}`。既有登记「接受=7B 全零」（`client-function-register-60cn.md` §6）不成立，组队分册的「u16 邀请人 uid」正确。
  - **CMD 2 第 4 字段语义**：为客户端经 icmp.dll 的 **MTU 二分探测结果**（`sub_78C1B0`：1472 字节起、失败下调/成功上调，结果按桶 `1472/1280/1024/768/750/740` 写 `this+436`；探测未完成时为复位占位 `-1`），经 `sub_78C4F0` 随 CMD 2 上报。参考服按 MTU `576..65507` 校验该字段；越界值原样进 NOTI 11 会让队友按荒谬 MTU 组包。
- **改动**：①新增 `PartyPacketBuilders.CreatePeerResponseAck`（成功应答 = `[1][u16 peerUid][u8 type]`，回显请求字段，与参考服同命令形态一致）并接入 `PartyHandlerCore` 的 RESPONSE_PEER 成功分支；②`GameplayDatagramService.NormalizeNatReport`：CMD 2 第 4 字段越界回落 `GameDatagramProtocol.FallbackReportedMtu`(1472，即客户端自身的最大探测桶)并 Warn 附环境提示，常量 `MinimumReportedMtu`/`MaximumReportedMtu`/`FallbackReportedMtu` 集中登记，`PartyCoordinator` 未上报默认值同源；③RESPONSE_PEER 的分类/撤销/拒绝/同意分支补 Info 日志；④撤销上一轮基于错误登记的「pending 邀请兜底」同意路径（帧内 uid 即邀请人 uid，兜底会掩盖真实失败）。**既有合法流量字节不变**（仅补齐应答缺失字段）。
- **验证**：构建 0 警告；全量 SelfTests 31 套件绿（`party` 套件新增 CMD 11 成功应答字节断言与同意帧严格性断言，`endpoint-claim` 新增归一化三组断言）；静态验证 = 上述 IDA 逐点复核（case `0x4104FF`、`0x4FEBC0`、`0x821E46`、`0x78C1B0`、`0x78C4F0`）；**真机复测未做**，判据与复现步骤见 `PENDING.md` P-37 与 `docs/design/13-party-dataplane-and-cohort.md` §11。
- **遗留项**：RESPONSE_PEER 失败应答的类码映射（case 链 3/4/0x11/0x12/0x13 → 串 `0x2C3..0x2CC`）结构已定，但各串文案未取证，暂维持现行 `[0,0,code,0]`（类码 0 → 不弹提示）；组队副本协同（13 号文档 §4 G-1）与数据面形态（G-2/G-3）未动。

## 2026-10-05

### CHANGE 配置变更

- `Channel.PortBase` 默认值 20000 → 10000：`ServerOptions.ChannelOptions.PortBase` 默认值与仓库 `server.json` 模板同步，频道 N 监听 `PortBase + N`（默认即 10001 起）；README 配置表、`docs/servers4a21/feature-backfill-plan-vs-s4a21.md` M2 节、`PENDING.md` P-31 判据与防火墙端口段备注同步改为随 `PortBase` 描述。SelfTests 的逐频道端口断言显式传 `channelPortBase: 20000`，不受默认值影响。**已部署副本 `dist/server-osx-arm64/server.json` 显式写有 20000，需手动改为 10000（或换用新模板）并重启服务端后才生效**；部署副本按约定由用户自行维护。

## 2026-10-03

### ADD-22 UDP 中继薄弱点加固：转发前置结构门、槽位伪造观测、中继发送可观测、名册零分配（设计 `docs/design/10-udp-nat-dataplane.md` §4.5）

- **现象**：①中继转发先于块结构解析执行，任何来自已归属端点的字节（含畸形帧）都原样转发给同队成员，CRC 失败仅产生日志；②帧内偏移 7 的发送槽位不与归属会话的名册槽位比对，队友间可伪造他人槽位（客户端按该字节归属对端，party 分册「数据面归属」第 1 条）；③逐对端转发 fire-and-forget，`SendAsync` 异常不可观测，`relayed` 计数实为「尝试转发」；④解析器逐包做队伍深拷贝快照（`CreateSnapshot`）并构建清单，每数据报多次分配。
- **根因结论**：转发路径与解析路径各自独立遍历且转发在前；UDP 面归属只有端点反查、无内容侧交叉校验；发送侧无错误观察点；名册消费走快照接口而非为热路径设计的只读形态。
- **改动**：玩法数据报先经 `TryReadBlockSequence` 整包块结构门（类型 0/1/2 之外、声明应用长度越包、槽位 ≥8、尾部垃圾判畸形）——畸形计入 `malformed`、首见 Warn、**不再转发**；应用层 CRC/密码学失败不设门（接收端职责），块物化一次供日志与槽位观测复用。槽位观测：新增 `SetSenderSlotLookup`（组合根接 `PartyCoordinator.GetSenderPartySlot` → `PartyManager.GetMemberSlotByUser`，会话须与成员记录一致），不一致计入 `slotMismatch`、首见 Warn、**照常转发**——拦截以真机计数为零为前提另行实施。中继发送改为**非阻塞派发**：接收循环同步完成归属反查、槽位观测与对端端点解析，逐对端触发独立发送任务（发送不阻塞下一包接收），任务内 `SocketException` 计入 `relayFailed`、停机竞态（取消/监听释放）静默终止，`relayed` 口径为成功转发数；同一数据报内逐对端保序，跨数据报顺序由客户端帧序列号承接。名册零分配：`Party.MemberSessionIds` replace-on-write 数组（增删/克隆时重建、内容不就地变更）+ `PartyManager.GetRelaySessionIdsByUser` 锁内只读引用，解析器取消逐包快照。聚合日志行扩为 11 计数器。**线上字节零变更**。
- **验证**：构建 0 警告，SelfTests 全量 31 套件绿——新增 `gameplay-block-gate`（合法帧/多块序列通过、声明长度越包/槽位越界/尾部垃圾/未知块类型拒绝、空包放行、短首零包不误判保活），`party` 套件扩展中结名册断言（无队伍无槽位、replace-on-write 旧引用不变、槽位会话校验、退队清空）；真机项（双开副本数据报互通即门不误伤、`slotMismatch` 恒零、聚合行形态）归 `PENDING.md` P-36。

### ADD-18 网络数据面整体优化三批次落地（设计 `docs/design/10-udp-nat-dataplane.md`，状态已实现）

- **现象**：①同 IP 多会话（同机双开/同一 NAT 出口）下 UDP 端点学习按 TCP 源 IP 整体赋予，后到探测覆盖先到端点，中继转发错位或落「无法归属」（party 分册数据面第 3 条登记缺陷）；②NAT 探测入口无长度校验、无限流、逐包日志（洪水即日志洪水）；③城镇在场表挂在组队域、端点状态散在三处，无唯一归属。
- **改动**（三批次，全部经 SelfTests 新套件守护）：批次 1——`NatProbeRateLimiter`（按源 IP 固定 1 秒窗口、默认 32 次/秒、双端口监听共用实例、`TimeProvider` 注入、`NatProbeRateLimit` 可配 `<=0` 旁路）+ `TryCreateNatProbeResponse` 长度==1 校验（协议锚：客户端 sendto 恒 1 字节）+ 9 计数器与每 5 秒聚合汇总、逐包日志降 Debug；批次 2——端点学习按会话认领（自报公网端点精确唯一命中→只认领该会话；无命中且单会话→沿用整体学习；多会话无法区分→不学习），解析总序四级（认领>非共享观测>自报>null），单会话行为与现行严格等价；批次 3——新增 `TownPresenceService` 城镇广播中枢（在场注册/三级广播/查找服务/状态翻转钩子/解析端点反查/频道人口统计），`PartyCoordinator` 瘦身为纯组队消费方，`EntranceMetadata.CurrentUsers` 改取中枢连接注册数（原 registry 计数职责移除）。
- **验证**：SelfTests 全量绿（新套件 `nat-probe-hardening`/`endpoint-claim`/`town-presence-hub`：认领三支路径、混合场景、解析总序单会话等价性、限流边界与窗口翻转、人口计数、反查归属）；探测回包/NOTI 11/中继帧**线上字节零变更**；真机项（双开进副本端点各归其位、抓包逐字节比对）归 `PENDING.md` P-36。

### ADD-19 好友与黑名单（`social.friends` 组，docs/design/11 §P2，已实现真机待 P-36）

- **协议取证**（P-35）：应答分发器 `sub_40F290` 跳表键值 = `dec eax`（var=协议号−1）后按现存 var 线性排列，**IDA case 标注即协议号**——据此定位 CMD 63 应答=0x414986、64=0x414BA7、129=0x414D34、130=0x414D97；NOTI 56 记录=42B 固定（名字内联@[3..]、uid u16@[36]）；NOTI 54/55={u8,dstr(31),u8}；CMD 130 应答={u8 count;count×{dstr 名字,u8 日,u8 月−1,u8 年−1900}}；CMD 63 请求=单 dstr 名字。**J1 裁决**：客户端黑名单管理器键为名字（角色名），角色级锚定与客户端兼容。
- **改动**：五 handler（63/64 增删好友、128/129 黑名单增删、130 黑名单查询——替换原 canned 应答并删除 `ExchangeServerCharacterInfoHandler`）；存储切片（`character_friends`/`character_blacklist` 启用，CharacterNo 整型键，独立小表紧随主状态写落盘，跨重启持久化有断言）；`SocialPacketBuilders`（NOTI 56/54/55 与五应答，dstr 遵守 1..30 游标约束）；`SocialPresenceNotifier`（城镇翻转钩子：进城推 NOTI 56 初始列表+反向边扇出 NOTI 54，离城扇出 NOTI 55）；增删成功即重发 NOTI 56 全量（用户定案：列表实时刷新，不依赖重选角色）。密语拦截与联动规则未实现（60A1 无证据，见 11 号 §3.9 T3/T4）。
- **验证**：`social-store`（CRUD/幂等/反向边/跨重启）与 `social-packet-builders`（逐偏移）套件绿；值域对拍归 P-36。

### ADD-20 组队补完：CMD 131 队长转让 + NOTI 2 mode 3 成员互发（docs/design/11 §P0）

- **取证**：CMD 131 二进制权威名 = `ENUM_CMDPACKET_CHANGE_HOST`，**无应答 case**（fire-and-forget）；载荷 = 1 字节目标座位号（发送点 0x852326）。mode 3 与 mode 1 共享读序分支（0x41A764→0x41A780，构建器尾段 0x41AEE0 已实现）——mode 3 字节即 `CreateCurrentCharacterDetails(mode:3)`，无需逐字段新取证。
- **改动**：`PartyManager.TransferLeader`（现任队长+会话一致+目标槽位占用校验）→ `PartyCoordinator` 广播 NOTI 26（1B 新队长槽位）+ NOTI 9 花名册，handler 层对 131 抑制 Type-1 应答；`CharacterSessionRegistry` 详情现场构建通道（`SetDetailsProvider`/`TryBuildDetails`）+ `ChannelSessionSenders.BuildMode3Details`（实时技能/投影属性/穿戴快照）+ `PartyCoordinator.ExchangePartyDetails`（组队成立时成员互发 mode 3，插在端点表之前——对端连接对象 user+0x110 的唯一创建源，P-12 前置补齐）。
- **验证**：`party` 套件扩展（转让四分支断言；互发帧与相位顺序 mode3→endpoints→ack→roster→endpoints→host 断言）绿；双客户端进副本端到端归 P-36。

### ADD-21 玩家交易协议落地第一步 + 个人商店骨架 + PVP 房间骨架（docs/design/11 §P3/P4/P6）

- **交易**（协议定界 P-35 全记录）：NOTI 15={u16,u16,u32,u8,u16}/16={u16 count;count×{u16,u8,u16}}/17={u16,u8}/18={u16×3}；**CMD 26 状态域实证 {1/0}确认、{2}取消、{3}双锁**（与 A12 状态机同构、编号+1 错位）——`ItemTradeStateHandler` 改双形态（body 3 字节→`TradeSessionRegistry` 状态机并向对方广播 NOTI 17；锁定体→既有 NPC 路径），断线拆对；`TradePacketBuilders` 四构建器。**待办**：交易发起流（CMD 10 无 type-1 发送点，真机抓包定案）、CMD 19 交易格分支、值域 0/4（P-36/T6-T8）。
- **个人商店**：请求侧全族取证（88={u8 标志}、90 上架={dstr 标题≤40}、86={u32 店主 uid}、89/87=空；发送点聚簇 0x7B2690..0x7B28F8）——`PrivateStoreRegistry`（每角色一店）+五 handler（88/90/86/87/89）+ NOTI 78 建摊广播构建器；CMD 82 维持社交轮询裁决。**待办**：85 购买（应答八字段值域）、上架通道、79-85 接线、两表落库（T9-T11）。
- **PVP**：`PvpRoomRegistry` 房间状态机骨架（8 座两队/准备/开战校验/击杀计数/断线清理与最低座继任）+ NOTI 44/46 全量与 45 头部构建器；CMD 53={u8,u32 条件,dstr 标题}、56={u8,u8}。**待办**：handler 接线、41/43/47 复合布局、结算公式 J7（T14-T16）。
- **验证**：`trade-protocol`/`pvp-room` 套件绿（状态机转移矩阵、房间生命周期、构建器线序）；房间挪位残留 SessionId 缺陷在自测中暴露并修复。

### CHANGE 配置与存储变更

- `Channel.ServerGroup` 配置化（默认 1，此前写死）：PVF `etc/channel_info.etc` 只收配置组号的 `[server]` 行，元数据 GroupIndex=ServerGroup−1；README 运行配置节同步。
- 存档更名 `game.db` → **`game.sqlite`**：`ServerOptions.DatabasePath` 默认值、`server.json`（仓库+dist）、README、`docs/design/08`、`docs/sql/data_schema.sql` 用法、P-26/P-33/P-36 同步；既有部署手动改名旧档即可沿用。
- `act1_schema.sql` 基线调整（P-36 重建窗口内，无迁移需求）：`character_blacklist.created_at` TEXT→INTEGER（unix 秒，CMD 130 按 tm 消费）；`private_store_items` 增 `count` 列（可堆叠上架）；四组表注释按实现状态刷新。
- 种子库重建：部署库 `dist/server-osx-arm64/Data/game.sqlite` 与仓库 `Server/DAFLegacy.Server/Data/game.sqlite` 均按基线 100 + 种子重建（test 账号/4 角色/341 物品/7 邮件/125 技能，integrity_check 通过）；运行期规范化由服务端首次启动自动执行。

---

## 2026-09-30

### FIX-11 SQLite 种子把物品可空列压成 0：装备耐久恒下发 0（修理不生效的表象）、点击宠物崩溃

- **现象**：以 `act1_schema.sql` + `data_schema.sql` 重建 game.db 后：装备耐久显示恒 0，副本磨损（CMD 51）与修理（CMD 25）在库中真实写入、重登后仍显示 0，表现为「耐久不保存」；背包宠物页点击宠物客户端必崩。
- **根因结论**：种子导出工具 `tools/export_state_sql.py` 把 `CharacterItemRecord` 的九个可空标量经 `num()/b()/or ""` 压成 0/空串落库，破坏「null 原样存取」约定（`act1_schema.sql` character_items 列注释、`SqliteStateMapper` 头注均明示按 0 落库会改变下发字节）。两条下发链路受害：①线路耐久 = `AvatarAbilityIndex ?? Durability`（`CharacterItemWireProjection.Project`），种子把 `avatar_ability_index` 写成 0 后全部装备耐久恒下发 0，修理/磨损的库值被 0 覆盖，表象即「不保存」；②条目 `add_info` = `AvatarRemainingSeconds ?? CountOrValue`，宠物容器条目的实例 uid 被覆盖为 0，客户端按 uid 查宠物状态对象落空即解引用崩溃（与 `CharacterCreatureInventoryPlanner.NormalizeCreatureInstance` 已登记的客户端空指针机理一致）。库内 `durability` 数值本身一直正确，`SqliteStateMapper.BindItem/ReadItem` 读写链路无误，服务端代码零改动。
- **改动**（仅种子工具与产物）：九个可空物品列与邮件附件三个可空列改为 null 透传（JSON null → SQL NULL，bool → 0/1）；回放校验从 6 列扩为全部 16 列（含九个可空列与 instance_id），同类回归无法再通过校验；重新生成 `docs/sql/data_schema.sql`（穿戴时装的合法 0 保留，普通装备/宠物饲料/宠物蛋九列恢复 NULL）；产物头部用法指向现行基线路径。
- **验证**：工具回放逐字段校验通过（341 物品 16 列与 state.json 一致）；以新种子回放内存库模拟 `CharacterItemWireProjection` 投影，装备线路耐久恢复为库值（34/38/30/…）、宠物条目 uid 恢复（宠物蛋 2–5、已装备 1）。**由旧种子重建的存量 game.db 含同样坏数据，需删库后按新种子重建**；真机复测判据见 `PENDING.md` P-28。真机复测（2026-10-01，抓包 + 部署库对账）：判据②③④通过——修理耐久带值且金币扣减与库值一致、宠物列表正常下发且点击不崩、堆叠数量经 `add_info` 正常；①的耐久掉落往返因两轮全程未出现 CMD 51 待补，剩余动作收敛登记于 `PENDING.md` P-28。

### ADD-14 M2 按频道监听：会话频道随所选频道绑定（切频道后右上角/喇叭频道号正确）

- **现象**：M1 单监听形态下，客户端切换到任意频道后右上角恒显示「01 新手-洛兰」，喇叭横幅恒渲染「1频道」。取证确认客户端不在任何包里上报所选频道（三次不同频道状态下 LOGIN 解码正文逐字节一致，尾部仅启动器机器标识；NOTI 130 报文本身无频道字段，「频道N」由客户端以 NOTI 1 给定的频道号渲染）——真服以「每频道独立端口」让服务端凭连接端口识别频道，M1 把全部频道端点写成同一监听后服务端无从区分，NOTI 1 恒下发目录首条目（频道 1）。
- **改动**（设计见 `docs/servers4a21/feature-backfill-plan-vs-s4a21.md` §4.3 M2，用户确认后实施）：`ServerOptions.ChannelOptions` 新增 `PortBase`（默认 20000），频道 N 监听 `PortBase + N`；`Network/EntranceService.cs` 为目录每条频道起独立 TcpListener，accept 时携带绑定频道，`Channel.Port`（7001）恒保留为遗留频道端口（绑定目录首条目身份，承载其余八组回退记录端点与旧配置直连），入口端口表排除该端口避免双绑；`ChannelSessionState` 新增 `SessionChannel` 落位连接绑定频道；NOTI 1（`CheckConnectionHandler`）、喇叭 NOTI 130 flag（`SendMessageHandler`，跨频道收件人按客户端原生分流走仅横幅渲染）、频道通关经验加成（`DungeonExperienceCatalog` 改为按 etc「额外经验比例」列逐频道懒加载，`SetPlayResultHandler` 传会话频道号）全部改读会话绑定频道；`EntranceMetadata.BuildChannels` 组 0 目录频道记录写各自端点与各自在线人数（`CharacterSessionRegistry` 逐频道连接计数：频道监听 accept 时 +1、读循环收尾 -1，元数据 CurrentUsers 改按频道取值——解决「进游戏后全部频道显示良好」的总数误报），其余八组回退记录保持遗留端口与其绑定频道计数；PVF 无目录时回退单频道监听，行为字节不变。server.json 模板 `Channel` 节新增 `PortBase`；README、`docs/design/08-sqlite-storage.md`、`packet-detail-connection-60cn.md` 同步。
- **验证**：服务端与 SelfTests 构建 0 警告，`entrance-channel-catalog`、`dungeon-clear-experience`、`megaphone` 套件通过（含逐频道端口字节断言 20001/20006/20010/20011/20200、逐频道在线人数差异化断言（0/4/回退值）、遗留端点 7001 保留断言、频道 15/16 差异化经验加成断言；5 项存量 SQLite 临时库文件锁失败与本次无关，干净树复现一致）；真机复测见 `PENDING.md` P-31（2026-10-01 补：双客户端会话均落 `channel:20001`，按频道监听真机生效；同频道双开的到位/摆摊定位/退出跨会话广播正常，UI 判据待目视）。

### FIX-10 切频道 ~10.7 秒停顿的「NOTI 1 重置 NAT 模块」补丁：两轮真机证伪，已整体撤销

- **最终状态**：路线 A 补丁（重定向 NOTI 1 处理分支 0x419647 的 `call sub_78C3B0`，在探测列表/端口填充后清除模块跨会话残留字段）经两轮真机实验全部证伪并整体撤销，代码已还原：第一版桩漏转发 `port1/port2` 参数且未清参（该调用为 `__thiscall(this, port1, port2)`、被调方 `ret 8` 自清），探测列表端口错乱，`ResetChannelNatDiscoveryOnChannelInfo=1` 时**首次选频挂死**（0/1 对照实验抓出）；修正转发后，**客户端在 NOTI 1 处理中即静默挂死**（AppHangB1，收到 NOTI 1 后连 LOGIN 都不发出），静态层看钩子仅四个字段写+参数透传、行为应良性——运行态交互超出静态可证范围。Config.ini 键与 README 键名一并移除。
- **取证存档**（对后续有效，全部地址登记 `client-function-register-60cn.md` §10）：①模块 `dword_B5DCE4` 的每次会话再初始化其实大部分已由 NOTI 1 处理内建——`sub_78CE00` 清探测列表/使用位图并置 `this+436=-1`、`sub_78CEB0` 重填主机、`sub_78C3B0`（仅两次赋值 +496/+500）重设端口对；**跨会话残留只有状态 +0x40、上报类型 +0x44、重试计数 +0x1AC、等待标志 +0x1B0 四字段**；②清除这四字段的时机/方式仍不对——挂死点在 NOTI 1 处理内部，提示状态机存在静态未见的隐蔽不变量（或 VMP 运行态差异）；③挂起形态为 AppHangB1 主线程停止响应（事件日志 22:34），非内存违例。
- **后续路线**（按优先级）：a. 补齐 `sub_78C730` 尾部、`this+504`/`this+436` 生命周期与 state 0 路径的完整契约后再定新钩点；b. 构建带逐 tick 模块状态日志的诊断版 DLL，一次真机挂起复现即可拿到状态轨迹；c. 有真服环境时抓「切频道」服务端字节做对照（最快定谳是否服务端缺信号）。

### FIX-09 跨频道悄悄话不显示发送人：投递前补送发送方城镇外观

- **现象**：M2 后跨频道悄悄话可达但对端聊天行**不显示发送人**（只有文本）；同频道正常。
- **根因结论**：悄悄话下行 = NOTI 12 type 1（`msgType + 发送者区域 uid + 文本`），报文无名字字段，接收端 case 12（sub_849280 mode 1/7 分支）按 uid 在**本机城镇用户表**解析说话人名字与气泡。FIX-08 名册按频道隔离后，跨频道收发双方不再交换外观，接收方城镇用户表（NOTI 2 mode 0 的 find-or-create @0x4FDF60 建立）没有发送方记录，名字解析落空。与既存登记「跨区域/跨城镇队伍行只显示文本」同源（packet-detail-chat-60cn.md type 2/11 遗留项）。
- **改动**：`PartyCoordinator.TrySendToName` 增加发送方上下文前置——解析目标后，若发送方与目标不同城/同区域/频道三者任一不满足，先向目标投递一份发送方的 mode-0 城镇外观（`BuildTownAppearancePacket`，仅外观包、无进城副作用序列），为城镇用户表建立可解析记录后再投聊天行；同城同频道维持原单包路径。首登流 CMD 8 列表与既有外观交换不受影响。
- **验证**：构建 0 警告，`party`/`megaphone` 套件通过；真机复测判据并入 `PENDING.md` P-31。遗留观察项：mode-0 记录是否在接收端渲染静止城镇角色（登记于 P-31；若出现，改走 mode 3 或收缩为用户表注入）。

### FIX-08 城镇可见性跨频道串人：名册按频道隔离（M2 补全）

- **现象**：M2 后不同频道的玩家在同一城镇同区域互相可见（12 频道能看到 14 频道的人），外观/位置/离开通知跨频道串发。
- **根因结论**：`PartyCoordinator` 城镇名册只按「城镇 + 区域」匹配（`SameAreaSnapshotLocked` 无频道维度）。M1 单频道时代等价于按频道隔离；M2 多频道后同城区跨频道的居民互相进入对方的同区域快照，到达/移动/离开三类广播全部串频道。
- **改动**：`PartyPresenceUpdate` 与 `PresenceEntry` 携带频道号（取会话绑定频道，`ChannelSessionState.SessionChannel`）；`SameAreaSnapshotLocked` 增加频道相等过滤，到达/位置/离开/区域聊天（`SendToSameArea`）四条路径一并隔离。私聊（按名字定向 `TrySendToName`）保持跨频道可用；组队名册暂未隔离频道（见 `PENDING.md` P-31 备注）。
- **验证**：构建 0 警告，`party` 套件通过；真机复测判据并入 `PENDING.md` P-31（双客户端不同频道同城应互不可见，同频道互见不变）。

### FIX-07 切频道 ~10.7 秒停顿的「补推角色列表」假设：真机证伪，已撤销

- **最终状态**：该修复（LOGIN 应答后 500ms 未见 CMD 8 则补推角色列表）经真机复测**无效并撤销**——补推确实送达（抓包确认切换会话 LOGIN 后 ~0.5s 出现服务端 NOTI 2），但客户端仍在 LOGIN 后静默 10.7/10.9s 才同毫秒爆发 CMD 2 + CMD 4。由此定谳：**停顿与角色列表无关**，客户端在重连回归流（EXIT + 重连新频道端口）里等待的是其他未知门控。代码已完整回滚（`LoginHandler`/`ChannelSessionState`/`GetUserInfoHandler`），未定论事项与全部取证见 `PENDING.md` P-32。
- **取证存档**（对后续排查有效）：①停顿时长高度一致（10703/10800/10714ms），是客户端固定超时；②停顿窗口内客户端无任何网络动作（无 CMD 8、无 UDP NAT 探测、无 keepalive、无其他 TCP 连接尝试，netstat 300ms 采样证实）；③新旧连接均正常关闭（netstat 逐连接时间线：旧连接在新连接建立后 ~1s 内消失，无泄漏）；④该停顿先于 M2 存在——M2 前的 EXIT 重连流即有 9.8s/10.8s 静默（旧抓包），M2 使每次切频道都走该路径而全面暴露；⑤同连接的 CMD 7 回归流（不重连）无停顿且客户端正常发 CMD 8。

### FIX-06 多频道改造后选频列表为空：入口元数据记录名未携带频道号

- **现象**：入口频道目录化（回填方案 §4.3 M1）后，客户端登录后选频界面频道列表为空；多频道改造前的单频道回退形态正常。
- **根因结论**：60A1 客户端应用入口元数据时，从每条记录的显示名提取全部 ASCII 数字字符拼接后 `atol` 作为频道号（`sub_8929C0`，0x892A24 逐字节判定 0x30–0x39），再以（组下标+1, 频道号）查客户端频道表（`sub_71FE80`），未命中则该记录静默跳过不注册（`sub_894B10` 0x894D36–0x894D93，`if (buf[23])` 分支），不抛错也不触发重试。`EntranceMetadata.BuildChannels` 此前把 etc.chn.str 解析出的显示名原样写入记录名，而现网频道名（「格兰之森 A」「新手 - 洛兰」等）不含任何数字——凯恩组 38 条记录全部推出频道号 0、全部脱靶，列表为空。单频道回退名 `Local Channel 1` 自带号数，故旧形态正常。抓包互证：解密本次登录的协议 3 元数据（密钥取自同会话协议 12 明文回包）确认下发记录名确无数字，且协议 1 仅一次交互无重试，与静默跳过吻合。
- **改动**：`EntranceMetadata.BuildChannels` 记录名组合为「显示名 + 空格 + 频道号」后 CP936 写入，并拒绝含数字的显示名（构造期防御）；`ChannelCatalog.TryParseChannelInfoEtc` 将显示名含 ASCII 数字的行按坏行丢弃（回填方案 §4.3 风险④要求的目录校验落地）；SelfTests `entrance-channel-catalog` 同步组合名断言，新增数字名行丢弃与构造拒绝用例。
- **验证**：服务端与 SelfTests 全量构建 0 警告，`entrance-channel-catalog` 套件通过（5 项存量 SQLite 临时库文件锁失败与本次无关，干净树复现一致）；静态 IDA 与抓包解密互证如上；真机复测见 `PENDING.md` P-29。

### FIX-05 自定义队名下行通道定谳：设置段 kind==0 原生读 dstr，撤销 ADD-11 扩展块方案

- **现象**：「设置段 + type 4 扩展块」组合在真机上自定义队名始终不可见。
- **根因结论**：NOTI 9 设置段读取端（0x41B8F9）在 kind==0 分支调用的 `sub_8B3250` 无条件从游标消费 `u32 长度`：长度在 1..0x3F 时按长度读入 CP936 文本直接写入队伍名，0/≥0x40 时回退本地串且不消费载荷——**原生协议本就承载自定义队名文本，且与上行 CMD 12 的 dstr 形态对称**。ADD-11 的设计前提「下行不承载队名文本」系把该 dstr 原语误读为固定拷贝本地串，扩展块方案在任何客户端上都不可能工作。字段级结论见 [packet-detail-party-60cn.md](docs/protocol/packet-detail-party-60cn.md)；由本事件固化的六条强制核对项见 [ida-call-chain-convention.md](docs/protocol/ida-call-chain-convention.md) §6。
- **改动**：方案经用户确认按「原生 dstr 形态，撤销扩展」执行。服务端 `PartyPacketBuilders.WritePartySettings` 改为：kind≠0 发 `kind + userMax`，kind==0 发 `kind + u32 长度 + 文本 + userMax`（空自定义名发零长度，客户端显示『无队伍名』；长度 ≥0x40 抛 `ArgumentOutOfRangeException`，与上行解析一致）；删除 `CustomTitleBlockType`/`HasCustomTitle`/`WriteCustomTitle` 与追加循环，`MaximumCustomTitleBytes` 更名 `CustomTitleLengthCap`（互斥上限语义），`PartyInfoWireBlock.TitleBytes` 保留；SelfTests `party` 套件断言重写（kind==0 三形态字节断言 + 端到端改名设置块断言）。客户端 `Client/DAFLegacy.Ijl15` 移除 `UsePartyCustomTitle` 全部实现与配置——类型分派接管钩子、五处诊断钩子、日志辅助、`ijl15_config` 开关及 Config.ini 键、`ijl15_tests` 对应断言；自定义队名自此完全由原生协议承载，与补丁无关。
- **验证**：服务端与原生全量重建 0 警告，SelfTests 全绿、CTest 通过；真机复测通过（部署新服务端后，任意客户端建队/改自定义队名均直接显示所输文本）。

### ADD-13 客户端镜头拉近（命中画面放大）逆向登记：纯客户端实现，无协议面

- **结论**：副本内攻击命中（含暴击）时的整画面放大为**纯客户端实现、无协议面**。开关是客户端选项下标 3（INI 段 `DNF COMMON` 键 `ZOOM EFFECT`，默认 1=开）；由屏幕缩放控制器逐帧推进三段状态机（放大 → 保持 → 还原），主路径参数 2.0 倍 / 300 ms / 保持 1000 ms，锚点取命中目标包围盒中心，关闭开关会立即复位在播效果。对全部 152 个 NOTI case 按「调用缩放入口 / 访问控制器字段 / 引用输出全局量」三类特征逐 case 扫描，命中 0 处——没有任何包驱动该缩放，日志中的击杀包只是缩放时刻的伴生记录，服务端不参与。
- **改动**：仅新增与回填文档，无代码改动。选项系统、控制器字段、状态机与时间线、触发点与调用链见 [client-camera-zoom-60cn.md](docs/protocol/client-camera-zoom-60cn.md)；函数登记回填 `client-function-register-60cn.md` §9。
- **遗留项**：`PENDING.md` P-09（真机画面复测）、P-15（启动条件语义）、P-16（0x5E37CE 处 1.5 倍调用是否由网络侧驱动）、P-17（命中计数器与档位语音语义）。

### ADD-11 自定义队名可见：NOTI 9 type 4 扩展块 + 客户端补丁（已由 FIX-05 撤销）

- **最终状态**：该方案以「NOTI 9 追加块类型 4 扩展块（服务端）+ `UsePartyCustomTitle` 接管补丁（客户端）」实现，经真机多轮插桩复测证伪并撤销——其设计前提「下行不承载队名文本」系对设置段读取端的误读，扩展块在任何客户端上都不可能工作。自定义队名最终由 FIX-05 的原生 dstr 通道承载；客户端接管钩子、诊断钩子与日志辅助已全部退役，`ijl15_config` 的 Config.ini 运行时配置框架（ADD-12）保留。撤销定谳与最终形态见 FIX-05。

### ADD-12 IJL15 功能开关改为 Config.ini 运行时配置

- **内容**：`dnf_patches.h` 的 11 个编译期功能开关（反调试探测、多开、启动不最小化、装扮期数合并、[etc] 堆叠自定义使用、弹窗公告去标题、外部串表、邮件归档刷新、点券门控总开关、自定义队名、日志明文）与日志路径重定向、type-16 提示音屏蔽开关全部迁入 `ijl15_config` 运行时配置：`Config.ini`（客户端可执行文件旁）的 `[Features]` 节逐键开关，`[CeraPurchaseGate]` 节逐门控开关（8 项，随总开关生效）；缺失的键回落编译期默认值，既有客户端配置无需修改即行为不变。
- **结构性改动**：`dnf_patches.cpp` 的补丁表从 `constexpr` 改为配置加载后的 `BuildPatches()` 运行时构建，逐项范围校验收口 `ValidatePatchRanges`；`ShouldBypassCeraPurchaseGate` 改为读配置。
- **验证**：原生 Release 编译 0 警告、CTest 通过；配置模板 `Config.ini` 全键默认值化并附中文说明。README「启动器」与 `packet-detail-party-60cn.md` 相应键名标注同步。

---

## 2026-09-29

### FIX-04 改队伍名被拒：CMD 12 容量字节越界（自定义队名不显示一项后由 FIX-05 定谳）

- **现象**：单人队伍改队名不生效；部分改名请求被服务端直接拒绝，客户端弹『FAIL : 您不是房主，不能要求变更房间环境！』。
- **根因结论**：改名请求的 userMax 字节越界——创建窗口对已存在队伍重开时从 `*(队员记录 + 0xFC) + 0x3C` 读到 `0x30`（48），超出服务端 `IsSupportedCapacity(2..4)` 校验，请求整体拒绝（错误码 8），同一请求内的队名改动一并失效。该容量字节是客户端回读自存的队伍记录值，非队名内容。
- **改动**：`Game/Party/PartyState.cs` 新增 `PartyConstants.DefaultCapacity = 4`，`IsSupportedCapacity` 改为「修正判据」语义；`Game/Party/PartyManager.cs` 的 `SetPartyInfo` 不再因 userMax 越界拒绝——建队取默认容量、改设置保留队伍现有容量，修正经 `PartyOpResult.RepairedUserMax` 回报；有效容量但小于当前人数仍拒绝；`Network/PartyCoordinator.cs` 对修正记信息日志；SelfTests `party` 套件新增越界修正断言。
- **验证**：全量重建 0 错误 0 告警，SelfTests 全绿；真机复测通过（携带越界容量 `0x30` 的请求按原容量处理并照常下发设置块，无错误应答；预设与自定义改名链路均被接受）。
- **遗留项**：客户端开窗处为何读到 48 未定论（按登记偏移应为同一字段，实测读数不一致），见 `PENDING.md` P-14。本条目同批登记的「自定义队名在客户端无下行显示通道」结论后经 FIX-05 证伪更正，队名显示的最终结论以 FIX-05 与 `packet-detail-party-60cn.md` 为准。

### ADD-10 队伍聊天：CMD 17 type 2/11 按队伍名册投递

- **现象/结论**：队伍内发聊天消息本机无显示，原因是服务端 `SendMessageHandler` 只认 type 3/1/7/18/13/14/15，type 2（城镇）/11（副本）被丢弃。客户端取值与显示语义：聊天窗频道下拉选择项 1（队伍）在城镇取 type 2、副本取 11，两值属同一频道（同色、同页签开关）；下行经 NOTI 12（`mode u8 + uid u16 + dstr 文本`）按发送者 uid 渲染聊天行并走气泡路径；发送者本人的行只能靠下行副本回显（本地自渲染块仅存在于悄悄话分支）。
- **改动**：`Network/PartyCoordinator.cs` 新增 `SendToParty`（按队伍名册投递、含发送者本人，返回收件会话数）；`Network/Handlers/Chat/SendMessageHandler.cs` 新增 type 2/11 分支，以 `ChatPacketBuilders.CreateMessageNotification` 投递全体队员，type 原样透传；无队伍时告警丢弃；SelfTests 新增名册投递与实测报文解析断言。
- **验证**：全量重建 0 错误 0 告警，SelfTests 21 套件全绿；真机复测见 `PENDING.md` P-06。
- **遗留项**：跨区域队友聊天行缺名字前缀（需 NOTI 2 mode 3 快照）见 P-12；公会聊天（type 6）与其余场景频道未支持见 P-13；`LocalUserId = CharacterNo & 0xFFFF` 的跨账号 uid 撞号风险见 P-20。
- **文档回写**：`packet-detail-chat-60cn.md` CMD 17 表与「type 2 / type 11 队伍聊天」节；`client-function-register-60cn.md` §7；`client-packet-enums-60cn.md` 聊天表。

### ADD-09 新增 CMD 66 点券刷新查询（点券商城「刷新点券」按钮）

- **现象/结论**：点券商城点「刷新点券」后余额不更新：客户端以约 150ms 间隔连发数条 CMD 66（body 仅 seq、无载荷）即放弃，服务端无处理器。应答复用既有 `CreateCeraUpdate`（NOTI 53 `[u8 flag][u32 cera]`），与余额变更推送同报文；客户端的 NOTI 66 号位已被 `ENUM_NOTIPACKET_CERA_SPECIALITEM`（会员服务通知）占用，故本查询应答只能走 NOTI 53。发送器 `sub_85E1E0` 由按钮处理 `sub_864E10` 调用，NOTI 53 case 于 `0x4255BA`（登记见 `client-function-register-60cn.md` §1/§5）。
- **改动**：新增 `Network/Handlers/Shop/CeraQueryHandler.cs`（`economy.shop`）：请求体仅 seq，选角后推 NOTI 53 回推 `Currency.Cash`，未选角静默忽略；注册表 74 → 75 项；`protocol-golden` 新增三条断言。
- **验证**：全量重建 0 错误 0 告警，SelfTests 21 套件全绿；真机复测见 `PENDING.md` P-05。
- **关联**：ANTIBOT（CMD 161 → NOTI 152）不随实现——其 id 在客户端类型分派前被传输层拦截、无真服样本，按 D1 维持未实现，见 `PENDING.md` P-13 与 `client-packet-enums-60cn.md` NOTI 152/153 行。

### ADD-08 统一文件日志改为开关控制（默认关闭）

- **背景/改动**：`BoundedQueueFileLoggerProvider` 此前在组合根无条件注册，任何部署都会落盘 `logs/server.log`。现 `LoggingOptions.Enabled` 默认 `false`（`Infrastructure/ServerOptions.cs`）：关闭时不注册提供器、不创建日志目录、不建队列与后台消费任务；控制台日志不受影响。
- **验证**：三项目全量重建 0 错误 0 告警，SelfTests 全绿（`file-logger` 套件含默认值断言）。README「常用配置」补 `Logging.*` 四行。

### FIX-03 选角后无法进入城镇：装备归位循环 `continue` 被误写为 `return`（迁移期控制流缺陷）

- **症状**：真机选择角色后客户端停在载入画面，无法进入城镇；服务端无任何下行。
- **根因结论**：R1/R3 协议迁移把旧实现迁入 `SelectCharacterHandler` 与 `CeraShopBuyHandler` 时，持久化装备归位与购买落位循环内合计 11 处「本件处理完毕、继续下一件」的 `continue;` 被误写为 `return;`——处理到第一件即整条 handler 提前返回，后续应答与初始化包族全部未发出。
- **改动**：`Network/Handlers/Characters/SelectCharacterHandler.cs` 4 处、`Network/Handlers/Shop/CeraShopBuyHandler.cs` 7 处 `return;` → `continue;`，恢复旧实现语义；两文件与旧实现整段规范化对拍复核，无其它语义偏差。
- **验证**：全量重建 0 错误 0 告警，SelfTests 21 套件全绿；真机复测通过——选角 → 进城及进城后移动、聊天与喇叭、组队、点券购买、删物品、商店买卖、体力、技能、副本通关与放弃、邮件打开与删除等路径全部正常，全环无 CRC 失效、无缺失应答；已覆盖与未覆盖清单见 `PENDING.md` P-01、P-02。
- **遗留项**：迁移期其余片段是否还有同类失真，随真机全流程冒烟（`PENDING.md` P-01）逐协议复核。

### ADD-07 后台邮件表单附带点券发放（账号级即时到账，非邮件附件）

- **背景/功能**：邮件协议三个环节（发送 CMD 97、记录 NOTI 97、领取 CMD 98）均无点券字段，邮件本身无法携带点券。`/admin/index.html` 邮件视图新增「点券（发放到账号）」字段：填写大于 0 时先调用 `POST /api/client-pay/cera` 发放点券、再调用邮件接口落信；点券为账号级数据、即时到账，与邮件是否被领取无关。
- **校验放宽**：`JsonGameStore.GrantCeraToAccountAsync` 的校验由「正整数且为 100 的倍数」放宽为「正整数」——点券在 NOTI 53 中为 u32 原值直传，协议侧无取整要求；`ADMIN_API.md` §5.2.7 与 §6.1 同步。
- **非原子语义**：两次调用不构成事务；点券已发放而邮件发送失败时页面明确提示，点券保留、邮件不落库。
- **验证**：全量重建 0 错误 0 告警，SelfTests 全绿（含非 100 倍数发放与 0/负数拒绝断言）；页面组合动作经桩后端实测（先发放后发信、失败文案区分、0 值不发起调用、负值被原生约束拦截）。真实服务端的页面验证由用户手动执行，判据见 `PENDING.md` P-08。

### ADD-06 协议层重构管线落地（R0–R7 收官：71/71 协议挂载新链路）

- **内容**：按 `docs/design/07-protocol-layer-refactor.md` 整体落地（分支 `refactor/protocol-layer`）：`GameCommandRegistry` 字典注册表 + `GameCommandDispatcher` 通用管线（端口/阶段门槛统一校验）；`ChannelSessionState` 十二域会话状态对象化；`ConnectionQueue` 有界出站队列 + 每连接单写者泵（背压阻塞、不丢包不乱序、发送计账单点）；`ConnectionTimerScheduler` 单调一次性定时器基建；71/71 协议全部挂载新链路，注册表 75 项（含 CMD 66 与组队族薄壳）；Parser/Handlers/Parsers/Builders 按 `Network/{Core,Protocol}` 两极与三层镜像归位，引擎按域拆分为 14 个分组构建器；协议号枚举 `CmdPacketType`/`NotiPacketType` 方向分表（163/154 项，源 `tools/packet_id_map.json`）。诊断工具随枚举化收敛：`PacketNames.cs` 删除（反查并入 `PacketTypeNames`）、`PacketPlainText.cs` 保留但断开接线（`protocol_decode` 工具随之下线）、`tools/gen_packet_names.py` 删除。最终目录形态、登记偏差与工具处置见 07 §11；跨会话推送统一经目标会话出站泵入队，fire-and-forget 直写他连流的裂缝在已迁移协议面上机制化封口。
- **验证**：SelfTests 全量通过（含 `protocol-golden` 71 协议字节等价守护，样本库 `tools/packet_bytes.json`；`HandleClientAsync` 全链路字节行为经内联检查与 golden 回归为零变化）；真机冒烟状态见 `PENDING.md` P-01。
- **遗留项**：真机全流程冒烟见 P-01；R6 定时器收编（唯一未完成的机制项）见 P-10；PacketDumper 重写与枚举重生成脚本见 P-11；多人副本数据面前置缺口与端点归属歧义见 P-12、P-19；uid 唯一性见 P-20。

### ADD-05 新增 DAF-MCP 抓包网关（<http://127.0.0.1:12222/mcp>）

- **功能**：服务端内置标准 MCP 服务器（默认关闭），供智能体实时抓包分析协议——报文名富化的收发包流、单包完整字节、包号↔报文名检索。
- **开启步骤**：
  1. 服务端发布目录需存在 `DAFLegacy.Mcp.dll`：由 `publish-mcp.bat` / `publish-mcp.sh` 发布到 `dist\DAFLegacy.Mcp` 后手动拷入；未拷贝时服务端照常运行，仅不加载 MCP。
  2. `server.json` 的 `DAFLegacy.Mcp.Enabled` 改为 `true` 并重启服务端（`Mcp.Host` / `Mcp.Port` 默认 `127.0.0.1:12222`）。
  3. 在 MCP 客户端配置中加入：

  ```json
  {
    "mcpServers": {
      "daflegacy": {
        "type": "http",
        "url": "http://127.0.0.1:12222/mcp"
      }
    }
  }
  ```

- **工具清单（均为只读）**：
  - `packet_watch`：实时跟踪收发包（`tail -f` 语义），`afterSeq` 游标续读，可按方向/协议号/报文名/会话过滤；
  - `packet_get`：按 seq 取单包完整详情（含 body 十六进制与 RX 密文 wireHex），供逆向拷字节；
  - `packet_wait`：挂起等待第一条匹配事件，用于"做了操作等回包"式往返验证；
  - `packet_names_search`：在 60CN 报文名注册表中按名字片段或包号检索；
  - `session_list`：列出当前网络会话，定位要盯的 sessionId；
  - `tap_status`：抓包通道状态自检（当前 seq、环形存档占用）。
- **典型用法**：`session_list` 找到目标会话 → `packet_names_search` 查包号定过滤 → `packet_watch` 续读跟踪；需要等特定回包时用 `packet_wait`，深挖字段用 `packet_get`。
- **安全**：默认仅回环监听；改为非回环等同主动暴露协议流与游戏数据。
- **关联**：`docs/design/09-mcp-packet-tap.md`（设计全档）；README「常用配置 → 服务端 → MCP」。

---

## 2026-09-28

### ADD-04 新增喇叭功能（CMD 17 type 13/14 + NOTI 130 全服广播）

- **功能/结论**：使用服务器喇叭（PVF 物品 36，type 13）/ 频道喇叭（PVF 物品 37，type 14）发言：喇叭不走物品使用命令，走聊天命令 CMD 17，请求体在普通聊天布局外以 slot 字段指明喇叭所在背包槽（服务端凭此校验并扣减道具）；随后向全部在线会话广播 NOTI 130 大横幅消息（`u8 msgType + u8 channelNo + u16 speakerUid + dstr 发送者名(cap 0x1E) + dstr 消息(cap 0x200)`），构包器对两个 dstr 长度做硬性上限校验，type 原样透传。客户端按 channelNo/speakerUid 与本机值的比对结果区分显示形态，字段语义与显示分支详见 [packet-detail-chat-60cn.md](docs/protocol/packet-detail-chat-60cn.md) NOTI 130 节。
- **实现**：`Protocol/`（现 `Network/Core/PacketTypes.cs`）常量 `MegaphoneMessageNotification = 130` 与构包器 `CreateMegaphoneNotification`；`Network/EntranceService.cs` CMD 17 分支放行 type 13/14（槽位权威校验、扣减持久化、回 NOTI 14 单条目刷新）；`CharacterSessionRegistry` 新增 `NotifyMegaphoneAll` 全服遍历。
- **验证**：冒烟全绿（真实抓包帧解析、NOTI 130 字节逐字节断言、容量与空消息拒绝路径）；真客户端复测通过（发言者与他人显示形态符合分册语义）。
- **待确认**：见 `PENDING.md` P-18（type 14 ↔ 物品 37 映射、flag 字节语义细节、type 15 喇叭变体未处理）。
- **关联**：[confirm-reply-packet-format-60cn.md](docs/protocol/confirm-reply-packet-format-60cn.md) §5（NOTI 130 布局登记）；[new-packet-formats-60cn.md](docs/protocol/new-packet-formats-60cn.md) §1（已收敛至聊天分册）；[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) NOTI 130 行。

### ADD-03 新增 ANTIBOT 上报消费（CMD 161）

- **现象/结论**：客户端登录时发一条 92 字节、此后每 60 秒一条 18 字节的 CMD 161，服务端原每分钟告警。线上格式：`body = seq u16 + u32 声明长度 + 记录`，记录以大端 u16 kind 开头（已观测登录环境上报 kind 0x0003 与周期心跳 kind 0x000A）。契约特性：**fire-and-forget**——无应答时客户端心跳计数器持续递增，不重试、不断线、无惩罚路径，服务端不应答即正确行为（发送器 `sub_8C74F0` 属 ANTIBOT 模块；客户端内嵌 SNMP 栈同含立即数 161，与游戏协议无关）。
- **改动**：协议号常量 `AntibotCommand = 161`；`Game/Antibot/AntibotReportParser.cs` 解析 seq、声明长度校验与记录 kind；`Network/EntranceService.cs` 显式分支静默消费并以 Debug 级日志记录；SelfTests 以真实抓包帧断言解析与拒绝路径。
- **验证**：冒烟全绿；真机复测见 `PENDING.md` P-04。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 161 行；配对 NOTI 152 无真服样本，保持未实现，见 `PENDING.md` P-13。

### FIX-01 返回选角后公告弹窗需要确认两次

- **现象**：首次登录选角，活动公告弹窗确认 1 次；进入城镇后返回选角，同一弹窗需确认 2 次。
- **根因结论**：客户端返回选角（CMD 7）后自行发 GET_USERINFO（CMD 8）拉取角色列表，而服务端在 CMD 7 应答后又主动补发一份 NOTI 2（mode=2），同一帧列表到达两次；客户端每次收到列表都重建选角界面并重放活动公告，弹窗次数与列表帧数 1:1 对应。
- **修复**：删除 CMD 7 处理器中应答后的主动补发，以 CMD 8 应答为列表唯一来源（`Network/EntranceService.cs`）。
- **验证**：冒烟全绿；真机复测见 `PENDING.md` P-03（返回选角后弹窗应仅 1 次）。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 7/8 行。
