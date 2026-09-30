# 客户端协议枚举状态索引(60A1, IDA 提取)

> 来源: `/60A1/DNF.exe.i64`(对应已脱壳 `DNF.exe`, ImageBase 0x400000)。
> 枚举名表位于 .rdata: NOTI 表 `0xB54170`(175 项,0-153 命名,其余为 `NOTI_NO_NAME` 占位,槽 175 NULL 结尾), CMD 表 `0xB54430`(184 项,0-162 命名,其余为 `CMD_NO_NAME` 占位,表后紧接 RTTI)。表索引即枚举数值,已用 DAFLegacy 已知 ID 逐项验证。
> 提取脚本: `tools/extract_packet_tables.py` / `tools/dump_packet_tables.py`;原始数据 `tools/packet_id_map.json`。
>
> **本文档只跟踪协议实现状态**(✅/❌)。具体收发包格式、字节示例与逐字段参数说明在各自独立文档中:
>
> - 帧头/加解密约定: [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)
> - 各功能组格式: 分组标题所链 `packet-detail-*-60cn.md`
>
> 图例: ✅=DAFLegacy 服务端已实现(有应答/发送;指存在实现代码,真机复测状态以各分册与 [CHANGELOG](../../CHANGELOG.md) 为准) ❌=未实现(客户端会收到忽略/无回包)
> **文档约定**:凡逆向实现的封包,对应 `packet-detail-*.md` 必须附"客户端调用链"表(格式与交付清单见 [ida-call-chain-convention.md](ida-call-chain-convention.md);范例见 [packet-detail-party-60cn.md](packet-detail-party-60cn.md) 附录),并把新增函数回填 [client-function-register-60cn.md](client-function-register-60cn.md) 登记表。
> 汇总: NOTI 55/175 已实现; CMD 73/184 已处理(分母含 NO_NAME 占位:NOTI 21 项、CMD 21 项,均未实现)。

## 分组索引

| 分组 | 详细格式文档 |
|---|---|
| 连接/握手 | [packet-detail-connection-60cn.md](packet-detail-connection-60cn.md) |
| 登录/角色选择 | [packet-detail-login-character-60cn.md](packet-detail-login-character-60cn.md) |
| 角色状态 | [packet-detail-character-state-60cn.md](packet-detail-character-state-60cn.md) |
| 城镇同步 | [packet-detail-town-sync-60cn.md](packet-detail-town-sync-60cn.md) |
| 聊天 | [packet-detail-chat-60cn.md](packet-detail-chat-60cn.md) |
| 物品/背包 | [packet-detail-item-inventory-60cn.md](packet-detail-item-inventory-60cn.md) |
| 商店/货币 | [packet-detail-shop-currency-60cn.md](packet-detail-shop-currency-60cn.md) |
| 合成/强化 | [packet-detail-compound-upgrade-60cn.md](packet-detail-compound-upgrade-60cn.md) |
| 技能/任务 | [packet-detail-skill-quest-60cn.md](packet-detail-skill-quest-60cn.md) |
| 副本流程 | [packet-detail-dungeon-60cn.md](packet-detail-dungeon-60cn.md) |
| 邮箱 | [packet-detail-mailbox-60cn.md](packet-detail-mailbox-60cn.md) |
| 宠物 Creature | [packet-detail-creature-60cn.md](packet-detail-creature-60cn.md) |
| 组队 | [packet-detail-party-60cn.md](packet-detail-party-60cn.md) |
| 未分组已实现包 | [packet-detail-misc-60cn.md](packet-detail-misc-60cn.md) |

PVP、公会、个人商店、战争房间、混沌、讨伐/反外挂未实现,无详细格式文档。

## CMD ↔ NOTI 对应分组表

### 连接/握手 → [格式详情](packet-detail-connection-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 0 | `ENUM_CMDPACKET_CHECK_CONNECTION` | ✅ | 0 | `ENUM_NOTIPACKET_CHECK_CONNECTION` | ✅ | 心跳回执(明文) |
| 0 | `ENUM_CMDPACKET_CHECK_CONNECTION` | ✅ | 1 | `ENUM_NOTIPACKET_CHANNELINFO` | ✅ | 握手应答=频道信息(端口/hosts) |
| 141 | `ENUM_CMDPACKET_TIME_CHECK` | ❌ | 133 | `ENUM_NOTIPACKET_TIME_CHECK` | ❌ | 时间校验 |
| — | — | — | 135 | `ENUM_NOTIPACKET_CHECK_FORCED_DISCONNECT` | ❌ | 强踢检测 |
| 2 | `ENUM_CMDPACKET_SET_UDP_IP_PORT` | ✅ | 11 | `ENUM_NOTIPACKET_USER_UDP_IP_PORT` | ✅ | UDP打洞:上报端口↔对端地址表 |
| — | — | — | 26 | `ENUM_NOTIPACKET_UDP_HOST` | ✅ | UDP 房主指定 |

### 登录/角色选择 → [格式详情](packet-detail-login-character-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 1 | `ENUM_CMDPACKET_LOGIN` | ✅ | 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ | 登录后推送角色卡列表 |
| 8 | `ENUM_CMDPACKET_GET_USERINFO` | ✅ | 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ | 拉取角色列表 |
| 4 | `ENUM_CMDPACKET_SELECT_CHARACTER` | ✅ | 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ | 选角→当前角色详情+入场状态 |
| 5 | `ENUM_CMDPACKET_CREATE_CHARACTER` | ✅ | 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ | 创角成功→刷新列表 |
| 6 | `ENUM_CMDPACKET_DELETE_CHARACTER` | ✅ | 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ | 删角(回包含槽位)→刷新列表 |
| 7 | `ENUM_CMDPACKET_RETURN_SELECT_CHARACTER` | ✅ | 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ | 返回选角界面→刷新列表 |
| 3 | `ENUM_CMDPACKET_EXIT` | ✅ | — | — | — | 退出游戏世界 |

### 角色状态 → [格式详情](packet-detail-character-state-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 9 | `ENUM_CMDPACKET_RECOVER_STAMINA` | ✅ | 4 | `ENUM_NOTIPACKET_STAMINA` | ✅ | 疲劳度恢复(回包含剩余金币) |
| 43 | `ENUM_CMDPACKET_DIE_CHARACTER` | ✅ | 32 | `ENUM_NOTIPACKET_DIE_STATE` | ✅ | 角色死亡/复活状态 |
| — | — | — | 36 | `ENUM_NOTIPACKET_FATIGUE` | ✅ | 疲劳度同步 |
| — | — | — | 37 | `ENUM_NOTIPACKET_EXP` | ✅ | 经验/升级/SP |
| — | — | — | 48 | `ENUM_NOTIPACKET_PVP_RECORD` | ✅ | PVP 战绩 |
| — | — | — | 134 | `ENUM_NOTIPACKET_SP_RETURN_INFO` | ❌ | SP 返还(未实现) |

### 城镇同步 → [格式详情](packet-detail-town-sync-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 37 | `ENUM_CMDPACKET_SET_USER_POSITION` | ✅ | 22 | `ENUM_NOTIPACKET_USER_POSITION` | ✅ | 位置广播 |
| 38 | `ENUM_CMDPACKET_SET_USER_AREA` | ✅ | 23 | `ENUM_NOTIPACKET_USER_AREA` | ✅ | 区域切换广播 |
| 38 | `ENUM_CMDPACKET_SET_USER_AREA` | ✅ | 24 | `ENUM_NOTIPACKET_AREA_USERS` | ✅ | 区域内用户列表 |
| — | — | — | 5 | `ENUM_NOTIPACKET_DUNGEON_PERMISSION` | ✅ | 副本进入权限/通关状态 |

### 聊天 → [格式详情](packet-detail-chat-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 17 | `ENUM_CMDPACKET_SEND_MESSAGE` | ✅ | 12 | `ENUM_NOTIPACKET_MESSAGE` | ✅ | 普通聊天回显/广播;type 2/11=队伍聊天(城镇/副本,按名册投递),13/14/15=喇叭变体(见 [packet-detail-chat-60cn.md](packet-detail-chat-60cn.md)) |
| 124 | `ENUM_CMDPACKET_CREATURE_SEND_MESSAGE` | ✅ | 126 | `ENUM_NOTIPACKET_CREATURE_MESSAGE` | ✅ | 宠物聊天(126 兼弹窗) |
| 132 | `ENUM_CMDPACKET_CREATURE_SCRIPT_MESSAGE` | ✅ | 131 | `ENUM_NOTIPACKET_CREATURE_SCRIPT_MESSAGE` | ✅ | 宠物脚本消息 |
| — | — | — | 130 | `ENUM_NOTIPACKET_MEGAPHONE_MSG` | ✅ | 大喇叭广播(物品 36/37;格式见 [packet-detail-chat-60cn.md](packet-detail-chat-60cn.md)) |

### 物品/背包 → [格式详情](packet-detail-item-inventory-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 21 | `ENUM_CMDPACKET_BUY_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 购买→增量更新(回包含四币种钱包) |
| 24 | `ENUM_CMDPACKET_SELL_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 出售→增量更新 |
| 25 | `ENUM_CMDPACKET_REPAIR_EQUIPMENT` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 修理→增量更新 |
| 18 | `ENUM_CMDPACKET_DELETE_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 删除物品→增量更新 |
| 19 | `ENUM_CMDPACKET_MOVE_ITEMSPACE` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 跨仓库移动→增量更新 |
| 20 | `ENUM_CMDPACKET_SORT_ITEM` | ✅ | 13 | `ENUM_NOTIPACKET_ITEM_LIST` | ✅ | 整理→全量列表(增量通道无法表达搬迁,见根目录 [CHANGELOG.md](../../CHANGELOG.md)) |
| 28 | `ENUM_CMDPACKET_DISJOINT_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 分解→增量更新 |
| 29 | `ENUM_CMDPACKET_USE_LOTTERY_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 抽奖券→增量更新 |
| 47 | `ENUM_CMDPACKET_USE_STACKABLE` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 使用消耗品→增量更新 |
| 51 | `ENUM_CMDPACKET_DECREASE_DURABILITY` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 耐久扣减→增量更新 |
| 46 | `ENUM_CMDPACKET_GET_ITEM` | ✅ | 39 | `ENUM_NOTIPACKET_GET_ITEM` | ✅ | 拾取→拾取回执(金币/物品两种布局) |
| 50 | `ENUM_CMDPACKET_DROP_ITEM` | ✅ | 40 | `ENUM_NOTIPACKET_DROP_ITEM` | ✅ | 丢弃→地面掉落广播 |
| 26 | `ENUM_CMDPACKET_SET_ITEMTRADE_STATE` | ✅ | — | — | — | 交易锁定(此版交易NOTI 15-18未实现) |
| 44 | `ENUM_CMDPACKET_USE_COIN` | ✅ | — | — | — | 使用硬币(复活币) |

### 商店/货币 → [格式详情](packet-detail-shop-currency-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 67 | `ENUM_CMDPACKET_BUY_CERASHOP_ITEM` | ✅ | 53 | `ENUM_NOTIPACKET_CERA` | ✅ | 点券商城购买→余额更新 |
| 68 | `ENUM_CMDPACKET_GEN_CERATICKET` | ✅ | 53 | `ENUM_NOTIPACKET_CERA` | ✅ | 生成点券票据→余额更新 |
| 66 | `ENUM_CMDPACKET_CERA` | ✅ | 53 | `ENUM_NOTIPACKET_CERA` | ✅ | 点券商城「刷新点券」按钮(请求体仅 seq)→ NOTI 53 余额回推;此版 66 的 NOTI 位被 CERA_SPECIALITEM 占用,服务端用 66 发会员服务通知 |

### 合成/强化 → [格式详情](packet-detail-compound-upgrade-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 27 | `ENUM_CMDPACKET_COMPOUND_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 物品合成→增量更新 |
| 83 | `ENUM_CMDPACKET_UPGRADE_ITEM` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 强化→增量更新 |
| 84 | `ENUM_CMDPACKET_RESET_ITEM_ATTR` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 属性重置→增量更新 |
| 102 | `ENUM_CMDPACKET_COMPOUND_AVATAR` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 时装合成→增量更新 |

### 技能/任务 → [格式详情](packet-detail-skill-quest-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 30 | `ENUM_CMDPACKET_CHANGE_SKILLSLOT` | ✅ | 19 | `ENUM_NOTIPACKET_SKILLINFO` | ✅ | 技能栏变更→技能列表 |
| 31 | `ENUM_CMDPACKET_BUY_SKILL` | ✅ | 19 | `ENUM_NOTIPACKET_SKILLINFO` | ✅ | 学习技能→技能列表 |
| 32 | `ENUM_CMDPACKET_INCREASE_STATUS` | ✅ | — | — | — | 属性点分配(回包自带) |
| 33 | `ENUM_CMDPACKET_ACCEPT_QUEST` | ✅ | 21 | `ENUM_NOTIPACKET_ACCEPTABLE_QUEST_LIST` | ✅ | 接任务→可接任务列表 |
| 34 | `ENUM_CMDPACKET_GIVEUP_QUEST` | ✅ | 21 | `ENUM_NOTIPACKET_ACCEPTABLE_QUEST_LIST` | ✅ | 放弃任务→可接任务列表 |
| 35 | `ENUM_CMDPACKET_SET_QUEST_TRIGGER` | ✅ | 21 | `ENUM_NOTIPACKET_ACCEPTABLE_QUEST_LIST` | ✅ | 任务触发器→可接任务列表 |
| 36 | `ENUM_CMDPACKET_FINISH_QUEST` | ✅ | 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ | 完成任务→奖励入包 |

### 副本流程 → [格式详情](packet-detail-dungeon-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 15 | `ENUM_CMDPACKET_START_GAME` | ✅ | 27 | `ENUM_NOTIPACKET_START_GAME` | ✅ | 进图资格(疲劳不足回0x16) |
| 16 | `ENUM_CMDPACKET_SELECT_DUNGEON` | ✅ | 28 | `ENUM_NOTIPACKET_DUNGEON_INFO` | ✅ | 选图→副本信息 |
| 16 | `ENUM_CMDPACKET_SELECT_DUNGEON` | ✅ | 29 | `ENUM_NOTIPACKET_START_MAP` | ✅ | 选图→起始地图(怪物/掉落布局) |
| 48 | `ENUM_CMDPACKET_MOVE_MAP` | ✅ | 29 | `ENUM_NOTIPACKET_START_MAP` | ✅ | 过图→新地图快照 |
| 40 | `ENUM_CMDPACKET_FINISH_LOADING` | ✅ | 30 | `ENUM_NOTIPACKET_FINISH_LOADING` | ✅ | 加载完成→加载回执 |
| 40 | `ENUM_CMDPACKET_FINISH_LOADING` | ✅ | 136 | `ENUM_NOTIPACKET_ENTER_GAMEWORLD_COMPLETE` | ✅ | 入场完成 |
| 42 | `ENUM_CMDPACKET_DIE_MONSTER` | ✅ | 38 | `ENUM_NOTIPACKET_DIE_MONSTER` | ✅ | 杀怪→死亡掉落(含冠军序号) |
| 42 | `ENUM_CMDPACKET_DIE_MONSTER` | ✅ | 31 | `ENUM_NOTIPACKET_ENABLE_CLEAR_DUNGEON` | ✅ | 清完→允许通关 |
| 49 | `ENUM_CMDPACKET_SET_PLAY_RESULT` | ✅ | 34 | `ENUM_NOTIPACKET_PLAY_RESULT` | ✅ | 提交成绩→结算回执 |
| 42 | `ENUM_CMDPACKET_DIE_MONSTER` | ✅ | 35 | `ENUM_NOTIPACKET_CLEAR_DUNGEON_REWARD` | ✅ | 通关→翻牌奖励界面(经验九元组) |
| 73 | `ENUM_CMDPACKET_CARD_SELECT_RIGHT_STATE` | ✅ | 35 | `ENUM_NOTIPACKET_CLEAR_DUNGEON_REWARD` | ✅ | 翻牌权查询 |
| 74 | `ENUM_CMDPACKET_SELECT_CARD` | ✅ | — | — | — | 选牌(回包含金卡奖励) |
| 45 | `ENUM_CMDPACKET_GIVEUP_GAME` | ✅ | 33 | `ENUM_NOTIPACKET_FAIL_CLEAR_DUNGEON` | ✅ | 放弃游戏→通关失败 |
| 127 | `ENUM_CMDPACKET_BOSS_DIE_CHECK` | ✅ | 127 | `ENUM_NOTIPACKET_BOSS_DIE_CHECK` | ✅ | Boss 死亡校验 |
| 148 | `ENUM_CMDPACKET_ALLDIE_MONSTER` | ✅ | 38 | `ENUM_NOTIPACKET_DIE_MONSTER` | ✅ | 全灭怪物→批量死亡 |
| 142 | `ENUM_CMDPACKET_BACK_2_VILLAGE` | ✅ | 23 | `ENUM_NOTIPACKET_USER_AREA` | ✅ | 回城→区域同步 |
| — | — | — | 36 | `ENUM_NOTIPACKET_FATIGUE` | ✅ | 副本消耗疲劳同步 |
| 72 | `ENUM_CMDPACKET_SCORE_SCROLL_STATE` | ✅ | — | — | — | 计分卷状态 |
| 75 | `ENUM_CMDPACKET_EPLP_COMMAND` | ✅ | — | — | — | EPLP 命令 |

### 邮箱 → [格式详情](packet-detail-mailbox-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 99 | `ENUM_CMDPACKET_MAILBOX_OPEN` | ✅ | 97 | `ENUM_NOTIPACKET_MAILBOX_MAIL_LIST` | ✅ | 打开邮箱→邮件列表 |
| 98 | `ENUM_CMDPACKET_MAILBOX_EXTRACT_ITEM` | ✅ | 98 | `ENUM_NOTIPACKET_MAILBOX_REMOVE_MAIL` | ✅ | 取件→移除邮件 |
| 97 | `ENUM_CMDPACKET_MAILBOX_SEND` | ✅ | 99 | `ENUM_NOTIPACKET_MAILBOX_ALARM` | ✅ | 发信→收件人新邮件提醒 |
| 144 | `ENUM_CMDPACKET_CHANGE_LETTER_STAT` | ✅ | 97 | `ENUM_NOTIPACKET_MAILBOX_MAIL_LIST` | ✅ | 邮件状态变更 |

### 宠物 Creature → [格式详情](packet-detail-creature-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 105 | `ENUM_CMDPACKET_HATCH_CREATURE` | ✅ | 105 | `ENUM_NOTIPACKET_CREATURE_ITEM_LIST` | ✅ | 孵化→宠物列表 |
| 103 | `ENUM_CMDPACKET_RENAME_CREATURE` | ✅ | 101 | `ENUM_NOTIPACKET_RENAME_CREATURE` | ✅ | 改名→改名广播 |
| 104 | `ENUM_CMDPACKET_RESPONSE_CREATURE` | ✅ | 104 | `ENUM_NOTIPACKET_RESPONSE_CREATURE` | ✅ | 召唤/收回→响应 |
| — | — | — | 100 | `ENUM_NOTIPACKET_DIED_CREATURE` | ✅ | 宠物死亡 |
| — | — | — | 102 | `ENUM_NOTIPACKET_GAIN_EXP_CREATURE` | ✅ | 宠物经验 |
| — | — | — | 103 | `ENUM_NOTIPACKET_CREATURE_STATE` | ✅ | 宠物状态 |
| — | — | — | 106 | `ENUM_NOTIPACKET_EVOLUTE_CREATURE` | ✅ | 宠物进化 |
| — | — | — | 107 | `ENUM_NOTIPACKET_REVIVAL_CREATURE` | ✅ | 宠物复活 |

### PVP(未实现)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 53 | `ENUM_CMDPACKET_MAKE_PVP_ROOM` | ❌ | 41 | `ENUM_NOTIPACKET_PVP_ROOM_INFO` | ❌ | 建房↔房间信息 |
| 54 | `ENUM_CMDPACKET_ENTER_PVP_ROOM` | ❌ | 42 | `ENUM_NOTIPACKET_PVP_ROOM_STATE` | ❌ | 进入↔房间状态 |
| 55 | `ENUM_CMDPACKET_SET_PVP_SEAT_STATE` | ❌ | 43 | `ENUM_NOTIPACKET_PVP_SEAT_STATE` | ❌ | 座位↔座位状态 |
| 56 | `ENUM_CMDPACKET_SET_PVP_READY_STATE` | ❌ | 44 | `ENUM_NOTIPACKET_PVP_READY_STATE` | ❌ | 准备↔准备状态 |
| 58 | `ENUM_CMDPACKET_DIE_PVP_CHARACTER` | ❌ | 46 | `ENUM_NOTIPACKET_DIE_PVP_CHARACTER` | ❌ | 死亡↔死亡播报 |
| 60 | `ENUM_CMDPACKET_END_PVP_RESULT` | ❌ | 47 | `ENUM_NOTIPACKET_END_PVP` | ❌ | 结束↔终局 |
| 61 | `ENUM_CMDPACKET_RES_PVP_RANK` | ❌ | 49 | `ENUM_NOTIPACKET_REQ_PVP_RANK` | ❌ | 段位查询↔排名 |
| — | — | — | 45 | `ENUM_NOTIPACKET_START_PVP` | ❌ | 开局 |
| — | — | — | 48 | `ENUM_NOTIPACKET_PVP_RECORD` | ✅ | 战绩 ✅ |
| 122 | `ENUM_CMDPACKET_PVP_REQUEST_FIGHT` | ❌ | 125 | `ENUM_NOTIPACKET_PVP_REQUEST_FIGHT` | ❌ | 决斗请求 |
| 120 | `ENUM_CMDPACKET_PVP_HEART_BEAT` | ❌ | 124 | `ENUM_NOTIPACKET_PVP_TURN_PLAYER` | ❌ | 心跳↔轮转 |
| — | — | — | 132 | `ENUM_NOTIPACKET_PVP_REVIVE` | ❌ | 复活 |

### 公会(未实现)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 70 | `ENUM_CMDPACKET_GUILD_MEMER_LIST` | ❌ | 63 | `ENUM_NOTIPACKET_GUILD_CHAR_NAMES` | ❌ | 成员列表↔成员名单 |
| 77 | `ENUM_CMDPACKET_GUILD_INFO` | ❌ | 59 | `ENUM_NOTIPACKET_GUILD_SECEDE` | ❌ | 退会↔退出播报 |
| 78 | `ENUM_CMDPACKET_REQUEST_GUILD_ENTER` | ❌ | 58 | `ENUM_NOTIPACKET_GUILD_ENTER` | ❌ | 入会申请↔进入播报 |
| 119 | `ENUM_CMDPACKET_GUILD_WAR_INFO` | ❌ | 129 | `ENUM_NOTIPACKET_GUILD_WAR_START` | ❌ | 战争信息↔开战 |
| — | — | — | 60 | `ENUM_NOTIPACKET_GUILD_MARK_CHANGE` | ❌ | 徽标变更 |
| — | — | — | 61 | `ENUM_NOTIPACKET_GUILD_DISMISS` | ❌ | 公会解散(原注"公会聊天"系误标,公会聊天为 NOTI 62) |
| — | — | — | 70 | `ENUM_NOTIPACKET_GUILD_INFO` | ❌ | 公会信息 |
| — | — | — | 121 | `ENUM_NOTIPACKET_GUILD_WAR_END` | ❌ | 战争结束 |
| — | — | — | 122 | `ENUM_NOTIPACKET_GUILD_WAR_RESULT` | ❌ | 战争结果 |
| — | — | — | 139 | `ENUM_NOTIPACKET_GUILD_MEMBER_INFO` | ❌ | 成员详情 |

### 个人商店(未实现)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 88 | `ENUM_CMDPACKET_CREATE_PRIVATE_STORE` | ❌ | 78 | `ENUM_NOTIPACKET_CREATE_PRIVATE_STORE` | ❌ | 开店↔开店播报 |
| 89 | `ENUM_CMDPACKET_REMOVE_PRIVATE_STORE` | ❌ | 79 | `ENUM_NOTIPACKET_REMOVE_PRIVATE_STORE` | ❌ | 关店↔撤店播报 |
| 85 | `ENUM_CMDPACKET_BUY_PRIVATE_STORE_ITEM` | ❌ | 82 | `ENUM_NOTIPACKET_BUY_PRIVATE_STORE_ITEM` | ❌ | 购买↔成交 |
| 86 | `ENUM_CMDPACKET_ENTER_PRIVATE_STORE` | ❌ | 85 | `ENUM_NOTIPACKET_PRIVATE_STORE_INFO` | ❌ | 浏览↔详情 |
| — | — | — | 80 | `ENUM_NOTIPACKET_FROCE_REMOVE_PRIVATE_STORE` | ❌ | 强制下架 |
| — | — | — | 83 | `ENUM_NOTIPACKET_PRIVATE_STORE_ITEM` | ❌ | 商品列表 |

### 战争房间(未实现)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 93 | `ENUM_CMDPACKET_ENTER_WARROOM` | ❌ | 88 | `ENUM_NOTIPACKET_WARROOM_STATE` | ❌ | 进入↔房间状态 |
| 95 | `ENUM_CMDPACKET_DIE_WARROOM_CHARACTER` | ❌ | 95 | `ENUM_NOTIPACKET_WARROOM_DIE_STATE` | ❌ | 死亡↔死亡状态 |
| — | — | — | 90 | `ENUM_NOTIPACKET_START_WARROOM` | ❌ | 开局 |
| — | — | — | 92 | `ENUM_NOTIPACKET_CLEAR_WARAREA_REWARD` | ❌ | 通关奖励 |

### 混沌 CHAOS(未实现)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 111 | `ENUM_CMDPACKET_ENTER_CHAOSROOM` | ❌ | 110 | `<<enum_NOTIPACKET_CHAOSROOM_INFO>>` | ❌ | 进入↔房间信息 |
| 115 | `ENUM_CMDPACKET_START_CHAOSROOM` | ❌ | 111 | `<<enum_NOTIPACKET_CHAOSROOM_START>>` | ❌ | 开局↔开局播报 |
| 116 | `ENUM_CMDPACKET_CHAOS_GAME_CLEAR` | ❌ | 118 | `ENUM_NOTIPACKET_CHAOSROOM_END_REWARD` | ❌ | 通关↔结算奖励 |
| 106 | `ENUM_CMDPACKET_CHAOS_BUY_FP_ITEM` | ❌ | — | — | — | 购买FP道具 |
| — | — | — | 119 | `ENUM_NOTIPACKET_CHAOSROOM_MONSTER_SPAWN` | ❌ | 怪物刷新 |

### 组队 → [格式详情](packet-detail-party-60cn.md)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 12 | `ENUM_CMDPACKET_SET_PARTY_INFO` | ✅ | 9 | `ENUM_NOTIPACKET_PARTY_INFO` | ✅ | 建队/改设置↔队伍信息；队名 6 条预设本地渲染，自定义名经设置段 kind==0 的 dstr 原生下行（所有客户端直接显示）；改名请求的容量字节可越界（服务端按原容量修正，见分册） |
| 13 | `ENUM_CMDPACKET_LEAVE_PARTY` | ✅ | 10 | `ENUM_NOTIPACKET_WALKOUT` | ✅ | 退队↔离队播报 |
| 10 | `ENUM_CMDPACKET_REQUEST_PEER` | ✅ | 7 | `ENUM_NOTIPACKET_REQUEST_PEER` | ✅ | 邀请/申请↔邀请弹窗 |
| 11 | `ENUM_CMDPACKET_RESPONSE_PEER` | ✅ | 8 | `ENUM_NOTIPACKET_RESPONSE_PEER` | ✅ | 接受/拒绝↔回执 |
| 14 | `ENUM_CMDPACKET_WALKOUT_PARTY_MEMBER` | ✅ | 9 | `ENUM_NOTIPACKET_PARTY_INFO` | ✅ | 踢人↔队伍信息 |
| — | — | — | 26 | `ENUM_NOTIPACKET_UDP_HOST` | ✅ | 房主槽位指定(格式登记于连接分册) |

### 讨伐/反外挂/其他(未实现)

| CMD ID | 发包(客户端→服务端) | 状态 | NOTI ID | 回包(服务端→客户端) | 状态 | 配对说明 |
|---:|---|---|---:|---|---|---|
| 154 | `ENUM_CMDPACKET_DIE_AI_CHARACTER` | ❌ | 142 | `ENUM_NOTIPACKET_DIE_ASSAULT_PLAYER` | ❌ | AI角色死亡↔讨伐死亡 |
| 155 | `ENUM_CMDPACKET_COMPLETE_ASSAULT_READY` | ❌ | 141 | `ENUM_NOTIPACKET_START_ASSAULT` | ❌ | 讨伐就绪↔开始 |
| 39 | `ENUM_CMDPACKET_FISHING` | ❌ | 25 | `ENUM_NOTIPACKET_FISHING` | ❌ | 钓鱼 |
| 63 | `ENUM_CMDPACKET_ADD_FRIEND` | ❌ | 52 | `ENUM_NOTIPACKET_FRIEND_INFO` | ❌ | 好友添加↔好友信息 |
| 158 | `ENUM_CMDPACKET_CHANGE_HP` | ❌ | 147 | `ENUM_NOTIPACKET_CHANGE_HP` | ❌ | 血量同步(反外挂,同名对应) |
| 161 | `ENUM_CMDPACKET_ANTIBOT` | ❌ | 152 | `ENUM_NOTIPACKET_ANTIBOT` | ❌ | 反机器人校验(同名对应) |
| 162 | `ENUM_CMDPACKET_DPROTO` | ❌ | 153 | `ENUM_NOTIPACKET_DPROTO` | ❌ | 反外挂检测(同名对应) |
| — | — | — | 145 | `ENUM_NOTIPACKET_VIEW_ASSAULT_ICON` | ❌ | 讨伐图标显示 |
| — | — | — | 146 | `ENUM_NOTIPACKET_LEAVE_ASSAULT_PLACE` | ❌ | 离开讨伐地区 |
| — | — | — | 148 | `ENUM_NOTIPACKET_CLOSE_ASSAULT` | ❌ | 讨伐结束关闭 |
| — | — | — | 149 | `ENUM_NOTIPACKET_TELEPORT` | ❌ | 讨伐区域传送 |
| — | — | — | 150 | `ENUM_NOTIPACKET_AA_STARTINFO` | ❌ | AA(讨伐)开始信息 |
| — | — | — | 151 | `ENUM_NOTIPACKET_AA_TIME` | ❌ | AA(讨伐)时间同步 |
| — | — | — | 137 | `ENUM_NOTIPACKET_REQ_HUMAN_CERTIFY` | ❌ | 人证请求 |
| — | — | — | 138 | `ENUM_NOTIPACKET_GM_REQUEST_MID` | ❌ | GM请求 |

### 其余未入组包

未入组但已实现的协议格式见 [packet-detail-misc-60cn.md](packet-detail-misc-60cn.md)。

| NOTI ID | 枚举 | 状态 |
|---:|---|---|
| 3 | `ENUM_NOTIPACKET_USER_STATE` | ✅ |
| 6 | `ENUM_NOTIPACKET_USER_LEAVE` | ✅ |
| 15 | `ENUM_NOTIPACKET_CHANGE_ITEMTRADE_ITEM` | ❌ |
| 16 | `ENUM_NOTIPACKET_CANCEL_ITEMTRADE` | ❌ |
| 17 | `ENUM_NOTIPACKET_STATE_ITEMTRADE` | ❌ |
| 18 | `ENUM_NOTIPACKET_FINISH_ITEMTRADE` | ❌ |
| 20 | `ENUM_NOTIPACKET_CHANGE_BONUSSP` | ❌ |
| 50 | `ENUM_NOTIPACKET_KEY_COUNT` | ❌ |
| 51 | `ENUM_NOTIPACKET_MONEY_FULL` | ❌ |
| 54 | `ENUM_NOTIPACKET_BUDDY_IN` | ❌ |
| 55 | `ENUM_NOTIPACKET_BUDDY_OUT` | ❌ |
| 56 | `ENUM_NOTIPACKET_BUDDY_CONN_LIST` | ❌ |
| 57 | `ENUM_NOTIPACKET_PVPEXP_OF_WEEK` | ❌ |
| 62 | `ENUM_NOTIPACKET_GUILD_CHAT_MSG` | ❌ |
| 64 | `ENUM_NOTIPACKET_GUILD_MEMBER_LOGIN` | ❌ |
| 65 | `ENUM_NOTIPACKET_MESSAGE_OTHER_CHANNEL` | ❌ |
| 67 | `ENUM_NOTIPACKET_CARD_SELECT_COMPLETE_STATE` | ❌ |
| 68 | `ENUM_NOTIPACKET_CERA_GROUP_HISTORY` | ❌ |
| 69 | `ENUM_NOTIPACKET_SERVEREVENT` | ❌ |
| 71 | `ENUM_NOTIPACKET_GUILD_CREATE` | ❌ |
| 72 | `ENUM_NOTIPACKET_MEMBER_MEMBER_LOGIN_OUT` | ❌ |
| 73 | `ENUM_NOTIPACKET_REQUEST_MEMBER_ENTER_TO_RESPONSER` | ❌ |
| 74 | `ENUM_NOTIPACKET_MEMBER_ENTER_OK` | ❌ |
| 75 | `ENUM_NOTIPACKET_DELETE_MEMBER_ID` | ❌ |
| 76 | `ENUM_NOTIPACKET_PAY_TAX_TO_UPPER` | ❌ |
| 77 | `ENUM_NOTIPACKET_MEMBER_SECEDE` | ❌ |
| 81 | `ENUM_NOTIPACKET_COMPLETE_DISPLAY` | ❌ |
| 84 | `ENUM_NOTIPACKET_PRIVATE_STORE_LIST` | ❌ |
| 86 | `ENUM_NOTIPACKET_SERVER_BROADCAST` | ❌ |
| 87 | `ENUM_NOTIPACKET_WARROOM_INFO` | ❌ |
| 89 | `ENUM_NOTIPACKET_WARROOM_SEAT_STATE` | ❌ |
| 91 | `ENUM_NOTIPACKET_WARROOM_MONSTER_SPAWN` | ❌ |
| 93 | `ENUM_NOTIPACKET_WARROOM_GET_ITEM` | ❌ |
| 94 | `ENUM_NOTIPACKET_ADD_KP` | ❌ |
| 96 | `ENUM_NOTIPACKET_CLEAR_WARAREA_FP` | ❌ |
| 108 | `<<enum_NOTIPACKET_CHAOS_BUY_FP_ITEM>>` | ❌ |
| 109 | `<<enum_NOTIPACKET_CHAOS_ZONE_OWNERSHIP>>` | ❌ |
| 112 | `<<enum_NOTIPACKET_DIE_CHAOSROOM_CHARACTER>>` | ❌ |
| 113 | `<<enum_NOTIPACKET_CHAOSROOM_DIE_STATE>>` | ❌ |
| 114 | `<<enum_NOTIPACKET_CHAOS_GAME_CLEAR >>` | ❌ |
| 115 | `<<enum_NOTIPACKET_CHAOSROOM_SEAT_STATE>>` | ❌ |
| 116 | `<<enum_NOTIPACKET_CHAOSROOM_READY_STATE>>` | ❌ |
| 117 | `<<enum_NOTIPACKET_CHAOSROOM_STATE>>` | ❌ |
| 120 | `ENUM_NOTIPACKET_EVENT_INFO` | ✅ |
| 123 | `ENUM_NOTIPACKET_CODE_CHECKSUM_RANGE` | ❌ |
| 128 | `ENUM_NOTIPACKET_SEAL_DOOR_ITEMS` | ❌ |
| 140 | `ENUM_NOTIPACKET_MOVE_ASSAULT_PLACE` | ❌ |
| 143 | `ENUM_NOTIPACKET_END_ASSAULT` | ❌ |
| 144 | `ENUM_NOTIPACKET_REVIVAL_ASSAULT_PLAYER` | ❌ |
| 154 | `<<NOTI_NO_NAME>>` | ❌ |
| 155 | `<<NOTI_NO_NAME>>` | ❌ |
| 156 | `<<NOTI_NO_NAME>>` | ❌ |
| 157 | `<<NOTI_NO_NAME>>` | ❌ |
| 158 | `<<NOTI_NO_NAME>>` | ❌ |
| 159 | `<<NOTI_NO_NAME>>` | ❌ |
| 160 | `<<NOTI_NO_NAME>>` | ❌ |
| 161 | `<<NOTI_NO_NAME>>` | ❌ |
| 162 | `<<NOTI_NO_NAME>>` | ❌ |
| 163 | `<<NOTI_NO_NAME>>` | ❌ |
| 164 | `<<NOTI_NO_NAME>>` | ❌ |
| 165 | `<<NOTI_NO_NAME>>` | ❌ |
| 166 | `<<NOTI_NO_NAME>>` | ❌ |
| 167 | `<<NOTI_NO_NAME>>` | ❌ |
| 168 | `<<NOTI_NO_NAME>>` | ❌ |
| 169 | `<<NOTI_NO_NAME>>` | ❌ |
| 170 | `<<NOTI_NO_NAME>>` | ❌ |
| 171 | `<<NOTI_NO_NAME>>` | ❌ |
| 172 | `<<NOTI_NO_NAME>>` | ❌ |
| 173 | `<<NOTI_NO_NAME>>` | ❌ |
| 174 | `<<NOTI_NO_NAME>>` | ❌ |

| CMD ID | 枚举 | 状态 |
|---:|---|---|
| 10 | `ENUM_CMDPACKET_REQUEST_PEER` | ✅ |
| 11 | `ENUM_CMDPACKET_RESPONSE_PEER` | ✅ |
| 14 | `ENUM_CMDPACKET_WALKOUT_PARTY_MEMBER` | ✅ |
| 22 | `ENUM_CMDPACKET_RENT_AVATAR` | ❌ |
| 23 | `ENUM_CMDPACKET_EXTEND_AVATAR` | ❌ |
| 41 | `ENUM_CMDPACKET_USE_SKILL` | ✅ |
| 52 | `ENUM_CMDPACKET_REPORT_BAD_P2P_USER` | ❌ |
| 57 | `ENUM_CMDPACKET_SET_PVP_TEAM_MODE` | ❌ |
| 59 | `ENUM_CMDPACKET_PVP_TIME_OUT` | ❌ |
| 62 | `ENUM_CMDPACKET_SET_PVP_MAP_INDEX` | ❌ |
| 64 | `ENUM_CMDPACKET_REMOVE_FRIEND` | ❌ |
| 65 | `ENUM_CMDPACKET_DEBUG_COMMAND` | ❌ |
| 69 | `ENUM_CMDPACKET_REQUEST_PVPEXP_OF_WEEK` | ❌ |
| 71 | `ENUM_CMDPACKET_CALL_GUILD_CREATE_RIGHT` | ❌ |
| 76 | `ENUM_CMDPACKET_CALL_GUILD_LEVEL_UP` | ❌ |
| 79 | `ENUM_CMDPACKET_REQUEST_MEMBER_ENTER` | ❌ |
| 80 | `ENUM_CMDPACKET_MEMBER_ENTER_REPLY` | ❌ |
| 81 | `ENUM_CMDPACKET_MEMBER_SECEDE` | ❌ |
| 82 | `ENUM_CMDPACKET_CALL_MEMER_LIST` | ✅ |
| 87 | `ENUM_CMDPACKET_EXIT_PRIVATE_STORE` | ❌ |
| 90 | `ENUM_CMDPACKET_COMPLETE_DISPLAY` | ❌ |
| 91 | `ENUM_CMDPACKET_MOVE_TO_GATE` | ❌ |
| 92 | `ENUM_CMDPACKET_MAKE_WARROOM_TEMP` | ❌ |
| 94 | `ENUM_CMDPACKET_SET_WARROOM_SEAT_STATE` | ❌ |
| 96 | `ENUM_CMDPACKET_START_WARROOM_TEMP` | ❌ |
| 100 | `ENUM_CMDPACKET_PEER_CONNECT_RESULT` | ❌ |
| 101 | `ENUM_CMDPACKET_QUICK_JOIN_ROOM` | ❌ |
| 107 | `ENUM_CMDPACKET_CHAOS_ZONE_OWNERSHIP` | ❌ |
| 108 | `ENUM_CMDPACKET_GATHERING_PARTY_STATUS` | ❌ |
| 109 | `ENUM_CMDPACKET_WORLD_CUP_HIT_COUNT` | ❌ |
| 110 | `ENUM_CMDPACKET_MAKE_CHAOSROOM` | ❌ |
| 112 | `ENUM_CMDPACKET_SET_CHAOSROOM_SEAT_STATE` | ❌ |
| 113 | `ENUM_CMDPACKET_SET_CHAOSROOM_READY_STATE` | ❌ |
| 114 | `ENUM_CMDPACKET_DIE_CHAOSROOM_CHARACTER` | ❌ |
| 117 | `ENUM_CMDPACKET_GM_COMMAND` | ❌ |
| 118 | `ENUM_CMDPACKET_RESTART_CHAOSROOM` | ❌ |
| 121 | `ENUM_CMDPACKET_CODE_CHECKSUM` | ❌ |
| 123 | `ENUM_CMDPACKET_MOUSEREGISTER` | ❌ |
| 125 | `ENUM_CMDPACKET_TRACE_ERROR` | ❌ |
| 126 | `ENUM_CMDPACKET_OTHER_USER_INFO` | ❌ |
| 128 | `ENUM_CMDPACKET_REGISITER_TO_BLACKLIST` | ❌ |
| 129 | `ENUM_CMDPACKET_DELETE_TO_BLACKLIST` | ❌ |
| 130 | `ENUM_CMDPACKET_REQUEST_BLACKLIST` | ✅ |
| 131 | `ENUM_CMDPACKET_CHANGE_HOST` | ❌ |
| 133 | `ENUM_CMDPACKET_CHARACTER_STATISTIC` | ❌ |
| 134 | `ENUM_CMDPACKET_REPORT_CLIENT_SPEC` | ❌ |
| 135 | `ENUM_CMDPACKET_GUILDMEMBER_NAMING` | ❌ |
| 136 | `ENUM_CMDPACKET_SET_SUB_GUILD_MASTER` | ❌ |
| 137 | `ENUM_CMDPACKET_EXCHANGE_SERVER_INFO` | ❌ |
| 138 | `ENUM_CMDPACKET_EXCHANGE_SERVER_INFO_RET` | ❌ |
| 139 | `ENUM_CMDPACKET_EXCHANGE_SERVER_CHARAC_INFO` | ❌ |
| 140 | `ENUM_CMDPACKET_EXCHANGE_SERVER_CHARAC_INFO_RET` | ❌ |
| 143 | `ENUM_CMDPACKET_DNF_RADIO_LISTEN` | ❌ |
| 145 | `ENUM_CMDPACKET_CHANGE_CHARAC_NAME` | ❌ |
| 146 | `ENUM_CMDPACKET_QUERY_CHARAC_INFO` | ❌ |
| 147 | `ENUM_CMDPACKET_REPORT_MANNERLESS_USER` | ❌ |
| 149 | `ENUM_CMDPACKET_GUILD_MEMER_LIST_NEXT` | ❌ |
| 150 | `ENUM_CMDPACKET_GUILD_ALL_MEMBER_LIST` | ❌ |
| 151 | `ENUM_CMDPACKET_GUILD_ALL_MEMBER_LIST_NEXT` | ❌ |
| 152 | `ENUM_CMDPACKET_RPY_HUMAN_CERTIFY` | ❌ |
| 153 | `ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG` | ✅ |
| 156 | `ENUM_CMDPACKET_DIE_ASSAULT_PLAYER` | ❌ |
| 157 | `ENUM_CMDPACKET_REVIVAL_ASSAULT_PLAYER` | ❌ |
| 159 | `ENUM_CMDPACKET_BIO_VISUAL` | ❌ |
| 160 | `ENUM_CMDPACKET_SDC_DAMAGE_CHECK` | ❌ |
| 163 | `<<CMD_NO_NAME>>` | ❌ |
| 164 | `<<CMD_NO_NAME>>` | ❌ |
| 165 | `<<CMD_NO_NAME>>` | ❌ |
| 166 | `<<CMD_NO_NAME>>` | ❌ |
| 167 | `<<CMD_NO_NAME>>` | ❌ |
| 168 | `<<CMD_NO_NAME>>` | ❌ |
| 169 | `<<CMD_NO_NAME>>` | ❌ |
| 170 | `<<CMD_NO_NAME>>` | ❌ |
| 171 | `<<CMD_NO_NAME>>` | ❌ |
| 172 | `<<CMD_NO_NAME>>` | ❌ |
| 173 | `<<CMD_NO_NAME>>` | ❌ |
| 174 | `<<CMD_NO_NAME>>` | ❌ |
| 175 | `<<CMD_NO_NAME>>` | ❌ |
| 176 | `<<CMD_NO_NAME>>` | ❌ |
| 177 | `<<CMD_NO_NAME>>` | ❌ |
| 178 | `<<CMD_NO_NAME>>` | ❌ |
| 179 | `<<CMD_NO_NAME>>` | ❌ |
| 180 | `<<CMD_NO_NAME>>` | ❌ |
| 181 | `<<CMD_NO_NAME>>` | ❌ |
| 182 | `<<CMD_NO_NAME>>` | ❌ |
| 183 | `<<CMD_NO_NAME>>` | ❌ |

## 附录A:NOTI 全表(ENUM_NOTIPACKET_*,175 项:0-153 命名 + 154-174 占位)

| NOTI ID | 客户端枚举(全称) | 状态 | 备注 |
|---:|---|---|---|
| 0 | `ENUM_NOTIPACKET_CHECK_CONNECTION` | ✅ 已实现 |  |
| 1 | `ENUM_NOTIPACKET_CHANNELINFO` | ✅ 已实现 |  |
| 2 | `ENUM_NOTIPACKET_USERINFO` | ✅ 已实现 | mode2角色卡/mode0城镇角色/subtype1详情 |
| 3 | `ENUM_NOTIPACKET_USER_STATE` | ✅ 已实现 |  |
| 4 | `ENUM_NOTIPACKET_STAMINA` | ✅ 已实现 |  |
| 5 | `ENUM_NOTIPACKET_DUNGEON_PERMISSION` | ✅ 已实现 |  |
| 6 | `ENUM_NOTIPACKET_USER_LEAVE` | ✅ | [组队(存在交换)](packet-detail-party-60cn.md) |
| 7 | `ENUM_NOTIPACKET_REQUEST_PEER` | ✅ | [组队](packet-detail-party-60cn.md) |
| 8 | `ENUM_NOTIPACKET_RESPONSE_PEER` | ✅ | [组队](packet-detail-party-60cn.md) |
| 9 | `ENUM_NOTIPACKET_PARTY_INFO` | ✅ | [组队](packet-detail-party-60cn.md) |
| 10 | `ENUM_NOTIPACKET_WALKOUT` | ✅ | [组队](packet-detail-party-60cn.md) |
| 11 | `ENUM_NOTIPACKET_USER_UDP_IP_PORT` | ✅ 已实现 | =服务端 UdpPeerInfo |
| 12 | `ENUM_NOTIPACKET_MESSAGE` | ✅ 已实现 |  |
| 13 | `ENUM_NOTIPACKET_ITEM_LIST` | ✅ 已实现 |  |
| 14 | `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` | ✅ 已实现 |  |
| 15 | `ENUM_NOTIPACKET_CHANGE_ITEMTRADE_ITEM` | ❌ 未实现 |  |
| 16 | `ENUM_NOTIPACKET_CANCEL_ITEMTRADE` | ❌ 未实现 |  |
| 17 | `ENUM_NOTIPACKET_STATE_ITEMTRADE` | ❌ 未实现 |  |
| 18 | `ENUM_NOTIPACKET_FINISH_ITEMTRADE` | ❌ 未实现 |  |
| 19 | `ENUM_NOTIPACKET_SKILLINFO` | ✅ 已实现 |  |
| 20 | `ENUM_NOTIPACKET_CHANGE_BONUSSP` | ❌ 未实现 |  |
| 21 | `ENUM_NOTIPACKET_ACCEPTABLE_QUEST_LIST` | ✅ 已实现 |  |
| 22 | `ENUM_NOTIPACKET_USER_POSITION` | ✅ 已实现 |  |
| 23 | `ENUM_NOTIPACKET_USER_AREA` | ✅ 已实现 |  |
| 24 | `ENUM_NOTIPACKET_AREA_USERS` | ✅ 已实现 |  |
| 25 | `ENUM_NOTIPACKET_FISHING` | ❌ 未实现 |  |
| 26 | `ENUM_NOTIPACKET_UDP_HOST` | ✅ 已实现 |  |
| 27 | `ENUM_NOTIPACKET_START_GAME` | ✅ 已实现 | 此版名 START_GAME(选图资格),=服务端 EnterSelectDungeon |
| 28 | `ENUM_NOTIPACKET_DUNGEON_INFO` | ✅ 已实现 |  |
| 29 | `ENUM_NOTIPACKET_START_MAP` | ✅ 已实现 |  |
| 30 | `ENUM_NOTIPACKET_FINISH_LOADING` | ✅ 已实现 |  |
| 31 | `ENUM_NOTIPACKET_ENABLE_CLEAR_DUNGEON` | ✅ 已实现 |  |
| 32 | `ENUM_NOTIPACKET_DIE_STATE` | ✅ 已实现 |  |
| 33 | `ENUM_NOTIPACKET_FAIL_CLEAR_DUNGEON` | ✅ 已实现 |  |
| 34 | `ENUM_NOTIPACKET_PLAY_RESULT` | ✅ 已实现 |  |
| 35 | `ENUM_NOTIPACKET_CLEAR_DUNGEON_REWARD` | ✅ 已实现 |  |
| 36 | `ENUM_NOTIPACKET_FATIGUE` | ✅ 已实现 |  |
| 37 | `ENUM_NOTIPACKET_EXP` | ✅ 已实现 |  |
| 38 | `ENUM_NOTIPACKET_DIE_MONSTER` | ✅ 已实现 |  |
| 39 | `ENUM_NOTIPACKET_GET_ITEM` | ✅ 已实现 | 金币/物品两种布局 |
| 40 | `ENUM_NOTIPACKET_DROP_ITEM` | ✅ 已实现 |  |
| 41 | `ENUM_NOTIPACKET_PVP_ROOM_INFO` | ❌ 未实现 |  |
| 42 | `ENUM_NOTIPACKET_PVP_ROOM_STATE` | ❌ 未实现 |  |
| 43 | `ENUM_NOTIPACKET_PVP_SEAT_STATE` | ❌ 未实现 |  |
| 44 | `ENUM_NOTIPACKET_PVP_READY_STATE` | ❌ 未实现 |  |
| 45 | `ENUM_NOTIPACKET_START_PVP` | ❌ 未实现 |  |
| 46 | `ENUM_NOTIPACKET_DIE_PVP_CHARACTER` | ❌ 未实现 |  |
| 47 | `ENUM_NOTIPACKET_END_PVP` | ❌ 未实现 |  |
| 48 | `ENUM_NOTIPACKET_PVP_RECORD` | ✅ 已实现 |  |
| 49 | `ENUM_NOTIPACKET_REQ_PVP_RANK` | ❌ 未实现 |  |
| 50 | `ENUM_NOTIPACKET_KEY_COUNT` | ❌ 未实现 |  |
| 51 | `ENUM_NOTIPACKET_MONEY_FULL` | ❌ 未实现 |  |
| 52 | `ENUM_NOTIPACKET_FRIEND_INFO` | ❌ 未实现 |  |
| 53 | `ENUM_NOTIPACKET_CERA` | ✅ 已实现 |  |
| 54 | `ENUM_NOTIPACKET_BUDDY_IN` | ❌ 未实现 |  |
| 55 | `ENUM_NOTIPACKET_BUDDY_OUT` | ❌ 未实现 |  |
| 56 | `ENUM_NOTIPACKET_BUDDY_CONN_LIST` | ❌ 未实现 |  |
| 57 | `ENUM_NOTIPACKET_PVPEXP_OF_WEEK` | ❌ 未实现 |  |
| 58 | `ENUM_NOTIPACKET_GUILD_ENTER` | ❌ 未实现 |  |
| 59 | `ENUM_NOTIPACKET_GUILD_SECEDE` | ❌ 未实现 |  |
| 60 | `ENUM_NOTIPACKET_GUILD_MARK_CHANGE` | ❌ 未实现 |  |
| 61 | `ENUM_NOTIPACKET_GUILD_DISMISS` | ❌ 未实现 |  |
| 62 | `ENUM_NOTIPACKET_GUILD_CHAT_MSG` | ❌ 未实现 |  |
| 63 | `ENUM_NOTIPACKET_GUILD_CHAR_NAMES` | ❌ 未实现 |  |
| 64 | `ENUM_NOTIPACKET_GUILD_MEMBER_LOGIN` | ❌ 未实现 |  |
| 65 | `ENUM_NOTIPACKET_MESSAGE_OTHER_CHANNEL` | ❌ 未实现 |  |
| 66 | `ENUM_NOTIPACKET_CERA_SPECIALITEM` | ✅ 已实现 | ⚠服务端发 PremiumService 会员通知,此版枚举为 CERA_SPECIALITEM |
| 67 | `ENUM_NOTIPACKET_CARD_SELECT_COMPLETE_STATE` | ❌ 未实现 |  |
| 68 | `ENUM_NOTIPACKET_CERA_GROUP_HISTORY` | ❌ 未实现 |  |
| 69 | `ENUM_NOTIPACKET_SERVEREVENT` | ❌ 未实现 |  |
| 70 | `ENUM_NOTIPACKET_GUILD_INFO` | ❌ 未实现 |  |
| 71 | `ENUM_NOTIPACKET_GUILD_CREATE` | ❌ 未实现 |  |
| 72 | `ENUM_NOTIPACKET_MEMBER_MEMBER_LOGIN_OUT` | ❌ 未实现 |  |
| 73 | `ENUM_NOTIPACKET_REQUEST_MEMBER_ENTER_TO_RESPONSER` | ❌ 未实现 |  |
| 74 | `ENUM_NOTIPACKET_MEMBER_ENTER_OK` | ❌ 未实现 |  |
| 75 | `ENUM_NOTIPACKET_DELETE_MEMBER_ID` | ❌ 未实现 |  |
| 76 | `ENUM_NOTIPACKET_PAY_TAX_TO_UPPER` | ❌ 未实现 |  |
| 77 | `ENUM_NOTIPACKET_MEMBER_SECEDE` | ❌ 未实现 |  |
| 78 | `ENUM_NOTIPACKET_CREATE_PRIVATE_STORE` | ❌ 未实现 |  |
| 79 | `ENUM_NOTIPACKET_REMOVE_PRIVATE_STORE` | ❌ 未实现 |  |
| 80 | `ENUM_NOTIPACKET_FROCE_REMOVE_PRIVATE_STORE` | ❌ 未实现 |  |
| 81 | `ENUM_NOTIPACKET_COMPLETE_DISPLAY` | ❌ 未实现 |  |
| 82 | `ENUM_NOTIPACKET_BUY_PRIVATE_STORE_ITEM` | ❌ 未实现 |  |
| 83 | `ENUM_NOTIPACKET_PRIVATE_STORE_ITEM` | ❌ 未实现 |  |
| 84 | `ENUM_NOTIPACKET_PRIVATE_STORE_LIST` | ❌ 未实现 |  |
| 85 | `ENUM_NOTIPACKET_PRIVATE_STORE_INFO` | ❌ 未实现 |  |
| 86 | `ENUM_NOTIPACKET_SERVER_BROADCAST` | ❌ 未实现 |  |
| 87 | `ENUM_NOTIPACKET_WARROOM_INFO` | ❌ 未实现 |  |
| 88 | `ENUM_NOTIPACKET_WARROOM_STATE` | ❌ 未实现 |  |
| 89 | `ENUM_NOTIPACKET_WARROOM_SEAT_STATE` | ❌ 未实现 |  |
| 90 | `ENUM_NOTIPACKET_START_WARROOM` | ❌ 未实现 |  |
| 91 | `ENUM_NOTIPACKET_WARROOM_MONSTER_SPAWN` | ❌ 未实现 |  |
| 92 | `ENUM_NOTIPACKET_CLEAR_WARAREA_REWARD` | ❌ 未实现 |  |
| 93 | `ENUM_NOTIPACKET_WARROOM_GET_ITEM` | ❌ 未实现 |  |
| 94 | `ENUM_NOTIPACKET_ADD_KP` | ❌ 未实现 |  |
| 95 | `ENUM_NOTIPACKET_WARROOM_DIE_STATE` | ❌ 未实现 |  |
| 96 | `ENUM_NOTIPACKET_CLEAR_WARAREA_FP` | ❌ 未实现 |  |
| 97 | `ENUM_NOTIPACKET_MAILBOX_MAIL_LIST` | ✅ 已实现 |  |
| 98 | `ENUM_NOTIPACKET_MAILBOX_REMOVE_MAIL` | ✅ 已实现 |  |
| 99 | `ENUM_NOTIPACKET_MAILBOX_ALARM` | ✅ 已实现 |  |
| 100 | `ENUM_NOTIPACKET_DIED_CREATURE` | ✅ 已实现 |  |
| 101 | `ENUM_NOTIPACKET_RENAME_CREATURE` | ✅ 已实现 |  |
| 102 | `ENUM_NOTIPACKET_GAIN_EXP_CREATURE` | ✅ 已实现 |  |
| 103 | `ENUM_NOTIPACKET_CREATURE_STATE` | ✅ 已实现 |  |
| 104 | `ENUM_NOTIPACKET_RESPONSE_CREATURE` | ✅ 已实现 |  |
| 105 | `ENUM_NOTIPACKET_CREATURE_ITEM_LIST` | ✅ 已实现 | 此版名 CREATURE_ITEM_LIST,=服务端 CreatureInfoNotification |
| 106 | `ENUM_NOTIPACKET_EVOLUTE_CREATURE` | ✅ 已实现 |  |
| 107 | `ENUM_NOTIPACKET_REVIVAL_CREATURE` | ✅ 已实现 |  |
| 108 | `<<enum_NOTIPACKET_CHAOS_BUY_FP_ITEM>>` | ❌ 未实现 |  |
| 109 | `<<enum_NOTIPACKET_CHAOS_ZONE_OWNERSHIP>>` | ❌ 未实现 |  |
| 110 | `<<enum_NOTIPACKET_CHAOSROOM_INFO>>` | ❌ 未实现 |  |
| 111 | `<<enum_NOTIPACKET_CHAOSROOM_START>>` | ❌ 未实现 |  |
| 112 | `<<enum_NOTIPACKET_DIE_CHAOSROOM_CHARACTER>>` | ❌ 未实现 |  |
| 113 | `<<enum_NOTIPACKET_CHAOSROOM_DIE_STATE>>` | ❌ 未实现 |  |
| 114 | `<<enum_NOTIPACKET_CHAOS_GAME_CLEAR >>` | ❌ 未实现 |  |
| 115 | `<<enum_NOTIPACKET_CHAOSROOM_SEAT_STATE>>` | ❌ 未实现 |  |
| 116 | `<<enum_NOTIPACKET_CHAOSROOM_READY_STATE>>` | ❌ 未实现 |  |
| 117 | `<<enum_NOTIPACKET_CHAOSROOM_STATE>>` | ❌ 未实现 |  |
| 118 | `ENUM_NOTIPACKET_CHAOSROOM_END_REWARD` | ❌ 未实现 |  |
| 119 | `ENUM_NOTIPACKET_CHAOSROOM_MONSTER_SPAWN` | ❌ 未实现 |  |
| 120 | `ENUM_NOTIPACKET_EVENT_INFO` | ✅ 已实现 |  |
| 121 | `ENUM_NOTIPACKET_GUILD_WAR_END` | ❌ 未实现 |  |
| 122 | `ENUM_NOTIPACKET_GUILD_WAR_RESULT` | ❌ 未实现 |  |
| 123 | `ENUM_NOTIPACKET_CODE_CHECKSUM_RANGE` | ❌ 未实现 |  |
| 124 | `ENUM_NOTIPACKET_PVP_TURN_PLAYER` | ❌ 未实现 |  |
| 125 | `ENUM_NOTIPACKET_PVP_REQUEST_FIGHT` | ❌ 未实现 |  |
| 126 | `ENUM_NOTIPACKET_CREATURE_MESSAGE` | ✅ 已实现 | 服务端另有弹窗变体 PopupNotification |
| 127 | `ENUM_NOTIPACKET_BOSS_DIE_CHECK` | ✅ 已实现 |  |
| 128 | `ENUM_NOTIPACKET_SEAL_DOOR_ITEMS` | ❌ 未实现 |  |
| 129 | `ENUM_NOTIPACKET_GUILD_WAR_START` | ❌ 未实现 |  |
| 130 | `ENUM_NOTIPACKET_MEGAPHONE_MSG` | ✅ 已实现 | 大喇叭广播;格式见 packet-detail-chat-60cn.md(原暂存于 new-packet-formats-60cn.md,已收敛) |
| 131 | `ENUM_NOTIPACKET_CREATURE_SCRIPT_MESSAGE` | ✅ 已实现 |  |
| 132 | `ENUM_NOTIPACKET_PVP_REVIVE` | ❌ 未实现 |  |
| 133 | `ENUM_NOTIPACKET_TIME_CHECK` | ❌ 未实现 |  |
| 134 | `ENUM_NOTIPACKET_SP_RETURN_INFO` | ❌ 未实现 |  |
| 135 | `ENUM_NOTIPACKET_CHECK_FORCED_DISCONNECT` | ❌ 未实现 |  |
| 136 | `ENUM_NOTIPACKET_ENTER_GAMEWORLD_COMPLETE` | ✅ 已实现 |  |
| 137 | `ENUM_NOTIPACKET_REQ_HUMAN_CERTIFY` | ❌ 未实现 |  |
| 138 | `ENUM_NOTIPACKET_GM_REQUEST_MID` | ❌ 未实现 |  |
| 139 | `ENUM_NOTIPACKET_GUILD_MEMBER_INFO` | ❌ 未实现 |  |
| 140 | `ENUM_NOTIPACKET_MOVE_ASSAULT_PLACE` | ❌ 未实现 |  |
| 141 | `ENUM_NOTIPACKET_START_ASSAULT` | ❌ 未实现 |  |
| 142 | `ENUM_NOTIPACKET_DIE_ASSAULT_PLAYER` | ❌ 未实现 |  |
| 143 | `ENUM_NOTIPACKET_END_ASSAULT` | ❌ 未实现 |  |
| 144 | `ENUM_NOTIPACKET_REVIVAL_ASSAULT_PLAYER` | ❌ 未实现 |  |
| 145 | `ENUM_NOTIPACKET_VIEW_ASSAULT_ICON` | ❌ 未实现 |  |
| 146 | `ENUM_NOTIPACKET_LEAVE_ASSAULT_PLACE` | ❌ 未实现 |  |
| 147 | `ENUM_NOTIPACKET_CHANGE_HP` | ❌ 未实现 |  |
| 148 | `ENUM_NOTIPACKET_CLOSE_ASSAULT` | ❌ 未实现 |  |
| 149 | `ENUM_NOTIPACKET_TELEPORT` | ❌ 未实现 |  |
| 150 | `ENUM_NOTIPACKET_AA_STARTINFO` | ❌ 未实现 |  |
| 151 | `ENUM_NOTIPACKET_AA_TIME` | ❌ 未实现 |  |
| 152 | `ENUM_NOTIPACKET_ANTIBOT` | ❌ 未实现 | id `0x98` 在客户端**类型分派之前**被传输层拦截(收包入口 `sub_40D6D0` → `sub_8C7300`,`cmp id,98h` 分支把 `[6..]` 交给 `dword_BF1634+4` 对象的 `vtable+8`),不经游戏层;NOTI 分派 switch 仅覆盖 id 0~151。故 ANTIBOT 应答无游戏层消费者,无真服样本前不实现(见 ADD-09/ADD-03) |
| 153 | `ENUM_NOTIPACKET_DPROTO` | ❌ 未实现 | id `0x99` 同被传输层拦截,作**压缩信封**:`vtable+48` 判型后取 `vtable+12` 的解压缓冲替换整包(同上入口) |
| 154 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 155 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 156 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 157 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 158 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 159 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 160 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 161 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 162 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 163 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 164 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 165 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 166 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 167 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 168 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 169 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 170 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 171 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 172 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 173 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 174 | `<<NOTI_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |

## 附录B:CMD 全表(ENUM_CMDPACKET_*,184 项:0-162 命名 + 163-183 占位)

| CMD ID | 客户端枚举(全称) | 状态 | 备注 |
|---:|---|---|---|
| 0 | `ENUM_CMDPACKET_CHECK_CONNECTION` | ✅ 已实现 |  |
| 1 | `ENUM_CMDPACKET_LOGIN` | ✅ 已实现 |  |
| 2 | `ENUM_CMDPACKET_SET_UDP_IP_PORT` | ✅ 已实现 | SET_UDP_IP_PORT |
| 3 | `ENUM_CMDPACKET_EXIT` | ✅ 已实现 |  |
| 4 | `ENUM_CMDPACKET_SELECT_CHARACTER` | ✅ 已实现 |  |
| 5 | `ENUM_CMDPACKET_CREATE_CHARACTER` | ✅ 已实现 |  |
| 6 | `ENUM_CMDPACKET_DELETE_CHARACTER` | ✅ 已实现 |  |
| 7 | `ENUM_CMDPACKET_RETURN_SELECT_CHARACTER` | ✅ 已实现 |  |
| 8 | `ENUM_CMDPACKET_GET_USERINFO` | ✅ 已实现 |  |
| 9 | `ENUM_CMDPACKET_RECOVER_STAMINA` | ✅ 已实现 |  |
| 10 | `ENUM_CMDPACKET_REQUEST_PEER` | ✅ | [组队](packet-detail-party-60cn.md) |
| 11 | `ENUM_CMDPACKET_RESPONSE_PEER` | ✅ | [组队](packet-detail-party-60cn.md) |
| 12 | `ENUM_CMDPACKET_SET_PARTY_INFO` | ✅ | [组队](packet-detail-party-60cn.md) |
| 13 | `ENUM_CMDPACKET_LEAVE_PARTY` | ✅ | [组队](packet-detail-party-60cn.md) |
| 14 | `ENUM_CMDPACKET_WALKOUT_PARTY_MEMBER` | ✅ | [组队](packet-detail-party-60cn.md) |
| 15 | `ENUM_CMDPACKET_START_GAME` | ✅ 已实现 | 疲劳不足错误 0x16 |
| 16 | `ENUM_CMDPACKET_SELECT_DUNGEON` | ✅ 已实现 |  |
| 17 | `ENUM_CMDPACKET_SEND_MESSAGE` | ✅ 已实现 |  |
| 18 | `ENUM_CMDPACKET_DELETE_ITEM` | ✅ 已实现 |  |
| 19 | `ENUM_CMDPACKET_MOVE_ITEMSPACE` | ✅ 已实现 |  |
| 20 | `ENUM_CMDPACKET_SORT_ITEM` | ✅ 已实现 |  |
| 21 | `ENUM_CMDPACKET_BUY_ITEM` | ✅ 已实现 |  |
| 22 | `ENUM_CMDPACKET_RENT_AVATAR` | ❌ 未实现 |  |
| 23 | `ENUM_CMDPACKET_EXTEND_AVATAR` | ❌ 未实现 |  |
| 24 | `ENUM_CMDPACKET_SELL_ITEM` | ✅ 已实现 |  |
| 25 | `ENUM_CMDPACKET_REPAIR_EQUIPMENT` | ✅ 已实现 |  |
| 26 | `ENUM_CMDPACKET_SET_ITEMTRADE_STATE` | ✅ 已实现 |  |
| 27 | `ENUM_CMDPACKET_COMPOUND_ITEM` | ✅ 已实现 |  |
| 28 | `ENUM_CMDPACKET_DISJOINT_ITEM` | ✅ 已实现 |  |
| 29 | `ENUM_CMDPACKET_USE_LOTTERY_ITEM` | ✅ 已实现 |  |
| 30 | `ENUM_CMDPACKET_CHANGE_SKILLSLOT` | ✅ 已实现 |  |
| 31 | `ENUM_CMDPACKET_BUY_SKILL` | ✅ 已实现 |  |
| 32 | `ENUM_CMDPACKET_INCREASE_STATUS` | ✅ 已实现 |  |
| 33 | `ENUM_CMDPACKET_ACCEPT_QUEST` | ✅ 已实现 |  |
| 34 | `ENUM_CMDPACKET_GIVEUP_QUEST` | ✅ 已实现 |  |
| 35 | `ENUM_CMDPACKET_SET_QUEST_TRIGGER` | ✅ 已实现 |  |
| 36 | `ENUM_CMDPACKET_FINISH_QUEST` | ✅ 已实现 |  |
| 37 | `ENUM_CMDPACKET_SET_USER_POSITION` | ✅ 已实现 |  |
| 38 | `ENUM_CMDPACKET_SET_USER_AREA` | ✅ 已实现 |  |
| 39 | `ENUM_CMDPACKET_FISHING` | ❌ 未实现 |  |
| 40 | `ENUM_CMDPACKET_FINISH_LOADING` | ✅ 已实现 |  |
| 41 | `ENUM_CMDPACKET_USE_SKILL` | ✅ 已实现 |  |
| 42 | `ENUM_CMDPACKET_DIE_MONSTER` | ✅ 已实现 |  |
| 43 | `ENUM_CMDPACKET_DIE_CHARACTER` | ✅ 已实现 |  |
| 44 | `ENUM_CMDPACKET_USE_COIN` | ✅ 已实现 |  |
| 45 | `ENUM_CMDPACKET_GIVEUP_GAME` | ✅ 已实现 |  |
| 46 | `ENUM_CMDPACKET_GET_ITEM` | ✅ 已实现 | 错误码0x15静默回滚 |
| 47 | `ENUM_CMDPACKET_USE_STACKABLE` | ✅ 已实现 |  |
| 48 | `ENUM_CMDPACKET_MOVE_MAP` | ✅ 已实现 |  |
| 49 | `ENUM_CMDPACKET_SET_PLAY_RESULT` | ✅ 已实现 |  |
| 50 | `ENUM_CMDPACKET_DROP_ITEM` | ✅ 已实现 |  |
| 51 | `ENUM_CMDPACKET_DECREASE_DURABILITY` | ✅ 已实现 |  |
| 52 | `ENUM_CMDPACKET_REPORT_BAD_P2P_USER` | ❌ 未实现 |  |
| 53 | `ENUM_CMDPACKET_MAKE_PVP_ROOM` | ❌ 未实现 |  |
| 54 | `ENUM_CMDPACKET_ENTER_PVP_ROOM` | ❌ 未实现 |  |
| 55 | `ENUM_CMDPACKET_SET_PVP_SEAT_STATE` | ❌ 未实现 |  |
| 56 | `ENUM_CMDPACKET_SET_PVP_READY_STATE` | ❌ 未实现 |  |
| 57 | `ENUM_CMDPACKET_SET_PVP_TEAM_MODE` | ❌ 未实现 |  |
| 58 | `ENUM_CMDPACKET_DIE_PVP_CHARACTER` | ❌ 未实现 |  |
| 59 | `ENUM_CMDPACKET_PVP_TIME_OUT` | ❌ 未实现 |  |
| 60 | `ENUM_CMDPACKET_END_PVP_RESULT` | ❌ 未实现 |  |
| 61 | `ENUM_CMDPACKET_RES_PVP_RANK` | ❌ 未实现 |  |
| 62 | `ENUM_CMDPACKET_SET_PVP_MAP_INDEX` | ❌ 未实现 |  |
| 63 | `ENUM_CMDPACKET_ADD_FRIEND` | ❌ 未实现 |  |
| 64 | `ENUM_CMDPACKET_REMOVE_FRIEND` | ❌ 未实现 |  |
| 65 | `ENUM_CMDPACKET_DEBUG_COMMAND` | ❌ 未实现 |  |
| 66 | `ENUM_CMDPACKET_CERA` | ✅ 已实现 | 点券商城刷新按钮 → NOTI 53 余额回推 |
| 67 | `ENUM_CMDPACKET_BUY_CERASHOP_ITEM` | ✅ 已实现 |  |
| 68 | `ENUM_CMDPACKET_GEN_CERATICKET` | ✅ 已实现 |  |
| 69 | `ENUM_CMDPACKET_REQUEST_PVPEXP_OF_WEEK` | ❌ 未实现 |  |
| 70 | `ENUM_CMDPACKET_GUILD_MEMER_LIST` | ❌ 未实现 |  |
| 71 | `ENUM_CMDPACKET_CALL_GUILD_CREATE_RIGHT` | ❌ 未实现 |  |
| 72 | `ENUM_CMDPACKET_SCORE_SCROLL_STATE` | ✅ 已实现 |  |
| 73 | `ENUM_CMDPACKET_CARD_SELECT_RIGHT_STATE` | ✅ 已实现 |  |
| 74 | `ENUM_CMDPACKET_SELECT_CARD` | ✅ 已实现 |  |
| 75 | `ENUM_CMDPACKET_EPLP_COMMAND` | ✅ 已实现 |  |
| 76 | `ENUM_CMDPACKET_CALL_GUILD_LEVEL_UP` | ❌ 未实现 |  |
| 77 | `ENUM_CMDPACKET_GUILD_INFO` | ❌ 未实现 |  |
| 78 | `ENUM_CMDPACKET_REQUEST_GUILD_ENTER` | ❌ 未实现 |  |
| 79 | `ENUM_CMDPACKET_REQUEST_MEMBER_ENTER` | ❌ 未实现 |  |
| 80 | `ENUM_CMDPACKET_MEMBER_ENTER_REPLY` | ❌ 未实现 |  |
| 81 | `ENUM_CMDPACKET_MEMBER_SECEDE` | ❌ 未实现 |  |
| 82 | `ENUM_CMDPACKET_CALL_MEMER_LIST` | ✅ 已实现 | 个人商店轮询应答 |
| 83 | `ENUM_CMDPACKET_UPGRADE_ITEM` | ✅ 已实现 |  |
| 84 | `ENUM_CMDPACKET_RESET_ITEM_ATTR` | ✅ 已实现 |  |
| 85 | `ENUM_CMDPACKET_BUY_PRIVATE_STORE_ITEM` | ❌ 未实现 |  |
| 86 | `ENUM_CMDPACKET_ENTER_PRIVATE_STORE` | ❌ 未实现 |  |
| 87 | `ENUM_CMDPACKET_EXIT_PRIVATE_STORE` | ❌ 未实现 |  |
| 88 | `ENUM_CMDPACKET_CREATE_PRIVATE_STORE` | ❌ 未实现 |  |
| 89 | `ENUM_CMDPACKET_REMOVE_PRIVATE_STORE` | ❌ 未实现 |  |
| 90 | `ENUM_CMDPACKET_COMPLETE_DISPLAY` | ❌ 未实现 |  |
| 91 | `ENUM_CMDPACKET_MOVE_TO_GATE` | ❌ 未实现 |  |
| 92 | `ENUM_CMDPACKET_MAKE_WARROOM_TEMP` | ❌ 未实现 |  |
| 93 | `ENUM_CMDPACKET_ENTER_WARROOM` | ❌ 未实现 |  |
| 94 | `ENUM_CMDPACKET_SET_WARROOM_SEAT_STATE` | ❌ 未实现 |  |
| 95 | `ENUM_CMDPACKET_DIE_WARROOM_CHARACTER` | ❌ 未实现 |  |
| 96 | `ENUM_CMDPACKET_START_WARROOM_TEMP` | ❌ 未实现 |  |
| 97 | `ENUM_CMDPACKET_MAILBOX_SEND` | ✅ 已实现 |  |
| 98 | `ENUM_CMDPACKET_MAILBOX_EXTRACT_ITEM` | ✅ 已实现 |  |
| 99 | `ENUM_CMDPACKET_MAILBOX_OPEN` | ✅ 已实现 |  |
| 100 | `ENUM_CMDPACKET_PEER_CONNECT_RESULT` | ❌ 未实现 |  |
| 101 | `ENUM_CMDPACKET_QUICK_JOIN_ROOM` | ❌ 未实现 |  |
| 102 | `ENUM_CMDPACKET_COMPOUND_AVATAR` | ✅ 已实现 |  |
| 103 | `ENUM_CMDPACKET_RENAME_CREATURE` | ✅ 已实现 |  |
| 104 | `ENUM_CMDPACKET_RESPONSE_CREATURE` | ✅ 已实现 |  |
| 105 | `ENUM_CMDPACKET_HATCH_CREATURE` | ✅ 已实现 |  |
| 106 | `ENUM_CMDPACKET_CHAOS_BUY_FP_ITEM` | ❌ 未实现 |  |
| 107 | `ENUM_CMDPACKET_CHAOS_ZONE_OWNERSHIP` | ❌ 未实现 |  |
| 108 | `ENUM_CMDPACKET_GATHERING_PARTY_STATUS` | ❌ 未实现 |  |
| 109 | `ENUM_CMDPACKET_WORLD_CUP_HIT_COUNT` | ❌ 未实现 |  |
| 110 | `ENUM_CMDPACKET_MAKE_CHAOSROOM` | ❌ 未实现 |  |
| 111 | `ENUM_CMDPACKET_ENTER_CHAOSROOM` | ❌ 未实现 |  |
| 112 | `ENUM_CMDPACKET_SET_CHAOSROOM_SEAT_STATE` | ❌ 未实现 |  |
| 113 | `ENUM_CMDPACKET_SET_CHAOSROOM_READY_STATE` | ❌ 未实现 |  |
| 114 | `ENUM_CMDPACKET_DIE_CHAOSROOM_CHARACTER` | ❌ 未实现 |  |
| 115 | `ENUM_CMDPACKET_START_CHAOSROOM` | ❌ 未实现 |  |
| 116 | `ENUM_CMDPACKET_CHAOS_GAME_CLEAR` | ❌ 未实现 |  |
| 117 | `ENUM_CMDPACKET_GM_COMMAND` | ❌ 未实现 |  |
| 118 | `ENUM_CMDPACKET_RESTART_CHAOSROOM` | ❌ 未实现 |  |
| 119 | `ENUM_CMDPACKET_GUILD_WAR_INFO` | ❌ 未实现 |  |
| 120 | `ENUM_CMDPACKET_PVP_HEART_BEAT` | ❌ 未实现 |  |
| 121 | `ENUM_CMDPACKET_CODE_CHECKSUM` | ❌ 未实现 |  |
| 122 | `ENUM_CMDPACKET_PVP_REQUEST_FIGHT` | ❌ 未实现 |  |
| 123 | `ENUM_CMDPACKET_MOUSEREGISTER` | ❌ 未实现 |  |
| 124 | `ENUM_CMDPACKET_CREATURE_SEND_MESSAGE` | ✅ 已实现 |  |
| 125 | `ENUM_CMDPACKET_TRACE_ERROR` | ❌ 未实现 |  |
| 126 | `ENUM_CMDPACKET_OTHER_USER_INFO` | ❌ 未实现 |  |
| 127 | `ENUM_CMDPACKET_BOSS_DIE_CHECK` | ✅ 已实现 |  |
| 128 | `ENUM_CMDPACKET_REGISITER_TO_BLACKLIST` | ❌ 未实现 |  |
| 129 | `ENUM_CMDPACKET_DELETE_TO_BLACKLIST` | ❌ 未实现 |  |
| 130 | `ENUM_CMDPACKET_REQUEST_BLACKLIST` | ✅ 已实现 | ⚠引擎按130应答交换角色信息,此版130=REQUEST_BLACKLIST(错配风险) |
| 131 | `ENUM_CMDPACKET_CHANGE_HOST` | ❌ 未实现 |  |
| 132 | `ENUM_CMDPACKET_CREATURE_SCRIPT_MESSAGE` | ✅ 已实现 |  |
| 133 | `ENUM_CMDPACKET_CHARACTER_STATISTIC` | ❌ 未实现 |  |
| 134 | `ENUM_CMDPACKET_REPORT_CLIENT_SPEC` | ❌ 未实现 |  |
| 135 | `ENUM_CMDPACKET_GUILDMEMBER_NAMING` | ❌ 未实现 |  |
| 136 | `ENUM_CMDPACKET_SET_SUB_GUILD_MASTER` | ❌ 未实现 |  |
| 137 | `ENUM_CMDPACKET_EXCHANGE_SERVER_INFO` | ❌ 未实现 |  |
| 138 | `ENUM_CMDPACKET_EXCHANGE_SERVER_INFO_RET` | ❌ 未实现 |  |
| 139 | `ENUM_CMDPACKET_EXCHANGE_SERVER_CHARAC_INFO` | ❌ 未实现 | 服务端无处理(此版139才是EXCHANGE_SERVER_CHARAC_INFO) |
| 140 | `ENUM_CMDPACKET_EXCHANGE_SERVER_CHARAC_INFO_RET` | ❌ 未实现 |  |
| 141 | `ENUM_CMDPACKET_TIME_CHECK` | ❌ 未实现 |  |
| 142 | `ENUM_CMDPACKET_BACK_2_VILLAGE` | ✅ 已实现 |  |
| 143 | `ENUM_CMDPACKET_DNF_RADIO_LISTEN` | ❌ 未实现 |  |
| 144 | `ENUM_CMDPACKET_CHANGE_LETTER_STAT` | ✅ 已实现 |  |
| 145 | `ENUM_CMDPACKET_CHANGE_CHARAC_NAME` | ❌ 未实现 |  |
| 146 | `ENUM_CMDPACKET_QUERY_CHARAC_INFO` | ❌ 未实现 |  |
| 147 | `ENUM_CMDPACKET_REPORT_MANNERLESS_USER` | ❌ 未实现 |  |
| 148 | `ENUM_CMDPACKET_ALLDIE_MONSTER` | ✅ 已实现 |  |
| 149 | `ENUM_CMDPACKET_GUILD_MEMER_LIST_NEXT` | ❌ 未实现 |  |
| 150 | `ENUM_CMDPACKET_GUILD_ALL_MEMBER_LIST` | ❌ 未实现 |  |
| 151 | `ENUM_CMDPACKET_GUILD_ALL_MEMBER_LIST_NEXT` | ❌ 未实现 |  |
| 152 | `ENUM_CMDPACKET_RPY_HUMAN_CERTIFY` | ❌ 未实现 |  |
| 153 | `ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG` | ✅ 已实现 |  |
| 154 | `ENUM_CMDPACKET_DIE_AI_CHARACTER` | ❌ 未实现 |  |
| 155 | `ENUM_CMDPACKET_COMPLETE_ASSAULT_READY` | ❌ 未实现 |  |
| 156 | `ENUM_CMDPACKET_DIE_ASSAULT_PLAYER` | ❌ 未实现 |  |
| 157 | `ENUM_CMDPACKET_REVIVAL_ASSAULT_PLAYER` | ❌ 未实现 |  |
| 158 | `ENUM_CMDPACKET_CHANGE_HP` | ❌ 未实现 |  |
| 159 | `ENUM_CMDPACKET_BIO_VISUAL` | ❌ 未实现 |  |
| 160 | `ENUM_CMDPACKET_SDC_DAMAGE_CHECK` | ❌ 未实现 |  |
| 161 | `ENUM_CMDPACKET_ANTIBOT` | ❌ 未实现 |  |
| 162 | `ENUM_CMDPACKET_DPROTO` | ❌ 未实现 |  |
| 163 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 164 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 165 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 166 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 167 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 168 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 169 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 170 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 171 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 172 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 173 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 174 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 175 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 176 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 177 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 178 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 179 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 180 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 181 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 182 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |
| 183 | `<<CMD_NO_NAME>>` | ❌ 未实现 | NO_NAME 占位保留位 |

## 版本差异与备注

- **NOTI 66**: 此版为 `CERA_SPECIALITEM`,服务端发 `PremiumService` 会员通知 —— 需复核。
- **CMD 130/139**: 此版 `EXCHANGE_SERVER_CHARAC_INFO`=139、`REQUEST_BLACKLIST`=130;引擎 `ExchangeServerCharacterInfoCommand` 固定 130(存在错配风险)。
- **NOTI 27**: 此版名 `START_GAME`(选图资格),=服务端 `EnterSelectDungeonNotification`。
- **NOTI 105**: 此版名 `CREATURE_ITEM_LIST`,=服务端 `CreatureInfoNotification`。
- NOTI 108-117(CHAOS)用小写 `enum_*` 前缀,CMD 106-107 仍为标准大写;NOTI 154-174、CMD 163-183 为 `*_NO_NAME` 占位保留位。
- 表尾按 IDB 实测动态扫描(NOTI 实际 175 项、CMD 实际 184 项,早期版本误按 145/165 截断),已补齐 NOTI 145-153 与全部占位项。
- 载荷级约定(消息颜色 `GameMessageType`、NOTI 38 冠军序号 1-11、NOTI 35 经验九元组等)已收录于对应 `packet-detail-*-60cn.md`。
