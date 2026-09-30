# -*- coding: utf-8 -*-
import json

res = json.load(open(r'E:\github\DAFLegacy\tools\packet_id_map.json'))

NOTI_TOTAL = max(map(int, res["noti"])) + 1
CMD_TOTAL = max(map(int, res["cmd"])) + 1
NOTI_NAMED = sum(1 for v in res["noti"].values() if "NO_NAME" not in v)
CMD_NAMED = sum(1 for v in res["cmd"].values() if "NO_NAME" not in v)

NOTI_IMPL = {0,1,2,3,4,5,11,12,13,14,19,21,22,23,24,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,
             48,53,66,97,98,99,100,101,102,103,104,105,106,107,120,126,127,131,136}
NOTI_LEN = {
0:"16B(challenge)",1:"变长(名称/hosts/ports)",2:"变长(4种子类型)",3:"3B",4:"1B",5:"2+3n",
11:"1+21n",12:"7+len",13:"3+13n(type2: 5+13n)",14:"3+13n",19:"4+3n",21:"1+2n",
22:"9B",23:"9B",24:"4+7n",26:"1B",27:"2+m",28:"7B",29:"完整12+9n+13k / 缓存7B",
30:"0B",31:"0B",32:"3B",33:"1B",34:"5B",35:"变长(~86+)",36:"6B",37:"15B",38:"6+12n",
39:"金币24B / 物品12B",40:"17B",48:"22B",53:"5B",66:"7B",97:"变长(分包快照)",
98:"4+4n",99:"2B",100:"2B",101:"6+len",102:"5B",103:"8B",104:"2B",105:"1+n*(15+len)",
106:"3B",107:"2B",120:"4B",126:"7+len(含弹窗变体)",127:"2B失败/4B成功",131:"7+len",136:"0B",
}
NOTI_NOTE = {
2:"mode2角色卡/mode0城镇角色/subtype1详情",11:"=服务端 UdpPeerInfo",
27:"此版名 START_GAME(选图资格),=服务端 EnterSelectDungeon",
66:"⚠服务端发 PremiumService 会员通知,此版枚举为 CERA_SPECIALITEM",
105:"此版名 CREATURE_ITEM_LIST,=服务端 CreatureInfo",
126:"服务端另有弹窗变体 PopupNotification",
}

CMD_IMPL = {0,1,2,3,4,5,6,7,8,9,15,16,17,18,19,20,21,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,
            40,41,42,43,44,45,46,47,48,49,50,51,67,68,72,73,74,75,82,83,84,97,98,99,102,103,104,
            105,124,127,130,132,142,144,148,153}
CMD_KIND = {
0:"E",1:"E",3:"E",4:"E/D",5:"E",6:"E/D",7:"E",8:"E/N",82:"E",130:"E",
2:"G",16:"G",17:"G",37:"G",38:"G",40:"G",43:"G",45:"G",48:"G",49:"G",97:"G",124:"G",132:"G",142:"G",148:"G",153:"G",
9:"D",15:"D",18:"D",19:"D",20:"D",21:"D",24:"D",25:"D",26:"D",27:"D",28:"D",29:"D",30:"D",31:"D",
32:"D",33:"D",34:"D",35:"D",36:"D",41:"D",44:"D",46:"D",47:"D",50:"D",51:"D",67:"D",68:"D",72:"D",
73:"D",74:"D",75:"D",83:"D",84:"D",98:"D",99:"D",102:"D",103:"D",105:"D",144:"D",
42:"N",104:"N",127:"N",
}
CMD_LEN = {
0:"NOTI1",1:"5B",3:"1B",4:"变长",5:"1B",6:"2B(result+slot)",7:"1B",
8:"NOTI2",9:"成功5B/错误2B",15:"疲劳不足3B(0x16)",16:"-",17:"-",18:"3+6n/错误3B",19:"11B",
20:"2B/错误3B",21:"25B",24:"10B",25:"8B",26:"5B",27:"变长/失败2B",28:"5+6k",29:"14B",
30:"3B",31:"6B",32:"12B",33:"8+8n",34:"3B",35:"7B",36:"变长",37:"-",38:"-",
40:"-",41:"1B",43:"-",44:"3B",45:"-",46:"静默拒绝2B(0x15)+NOTI39",47:"4B/错误3B",48:"-",49:"-",
50:"8B/错误3B",51:"2B",
67:"25+6n/错误17B",68:"9+len(key)",72:"1B",73:"9B",74:"变长(4列卡片)",75:"3B",
82:"15B(空商店)",83:"11-12B",84:"9B/失败2B",97:"-",98:"5B",99:"3B",
102:"变长",103:"3B/错误2B",104:"NOTI104",105:"1B/错误2B",
124:"-",127:"NOTI127",130:"2B",132:"-",142:"-",144:"7B",148:"-",153:"-",
}
CMD_NOTE = {
2:"SET_UDP_IP_PORT",
46:"错误码0x15静默回滚",
130:"⚠引擎按130应答交换角色信息,此版130=REQUEST_BLACKLIST(错配风险)",
139:"服务端无处理(此版139才是EXCHANGE_SERVER_CHARAC_INFO)",
}

def n_name(i): return res["noti"].get(str(i), "—")
def c_name(i): return res["cmd"].get(str(i), "—")

BYTES = json.load(open(r'E:\github\DAFLegacy\tools\packet_bytes.json'))
# CMD ids whose example body is a sequence-only placeholder (layout not confirmed by a server parser)
PLACEHOLDER_BODY = {1,2,3,4,5,7,8,9,15,16,17,18,19,20,21,24,25,26,28,29,30,31,32,33,34,35,36,37,38,
                    40,41,43,45,46,47,48,49,51,67,68,72,73,74,75,82,97,98,99,102,103,105,124,132,
                    142,144,148,153}
def hex_spaced(h):
    return " ".join(h[i:i+2] for i in range(0, len(h), 2))

# 分组配对表: (分组名, [ (cmd_id|None, noti_id|None, 配对说明) ])
GROUPS = [
("连接/握手", [
 (0, 0, "心跳回执(明文)"),
 (0, 1, "握手应答=频道信息(端口/hosts)"),
 (141, 133, "时间校验"),
 (None, 135, "强踢检测"),
 (2, 11, "UDP打洞:上报端口↔对端地址表"),
 (None, 26, "UDP 房主指定"),
]),
("登录/角色选择", [
 (1, 2, "登录后推送角色卡列表"),
 (8, 2, "拉取角色列表"),
 (4, 2, "选角→当前角色详情+入场状态"),
 (5, 2, "创角成功→刷新列表"),
 (6, 2, "删角(回包含槽位)→刷新列表"),
 (7, 2, "返回选角界面→刷新列表"),
 (3, None, "退出游戏世界"),
]),
("角色状态", [
 (9, 4, "疲劳度恢复(回包含剩余金币)"),
 (43, 32, "角色死亡/复活状态"),
 (None, 36, "疲劳度同步"),
 (None, 37, "经验/升级/SP"),
 (None, 48, "PVP 战绩"),
 (None, 134, "SP 返还(未实现)"),
]),
("城镇同步", [
 (37, 22, "位置广播"),
 (38, 23, "区域切换广播"),
 (38, 24, "区域内用户列表"),
 (None, 5, "副本进入权限/通关状态"),
]),
("聊天", [
 (17, 12, "普通聊天回显/广播"),
 (124, 126, "宠物聊天(126 兼弹窗)"),
 (132, 131, "宠物脚本消息"),
 (None, 130, "大喇叭(未实现)"),
]),
("物品/背包", [
 (21, 14, "购买→增量更新(回包含四币种钱包)"),
 (24, 14, "出售→增量更新"),
 (25, 14, "修理→增量更新"),
 (18, 14, "删除物品→增量更新"),
 (19, 14, "跨仓库移动→增量更新"),
 (20, 13, "整理→全量列表"),
 (28, 14, "分解→增量更新"),
 (29, 14, "抽奖券→增量更新"),
 (47, 14, "使用消耗品→增量更新"),
 (51, 14, "耐久扣减→增量更新"),
 (46, 39, "拾取→拾取回执(金币/物品两种布局)"),
 (50, 40, "丢弃→地面掉落广播"),
 (26, None, "交易锁定(此版交易NOTI 15-18未实现)"),
 (44, None, "使用硬币(复活币)"),
]),
("商店/货币", [
 (67, 53, "点券商城购买→余额更新"),
 (68, 53, "生成点券票据→余额更新"),
 (66, 66, "⚠此版66=CERA_SPECIALITEM,服务端66发会员服务"),
]),
("合成/强化", [
 (27, 14, "物品合成→增量更新"),
 (83, 14, "强化→增量更新"),
 (84, 14, "属性重置→增量更新"),
 (102, 14, "时装合成→增量更新"),
]),
("技能/任务", [
 (30, 19, "技能栏变更→技能列表"),
 (31, 19, "学习技能→技能列表"),
 (32, None, "属性点分配(回包自带)"),
 (33, 21, "接任务→可接任务列表"),
 (34, 21, "放弃任务→可接任务列表"),
 (35, 21, "任务触发器→可接任务列表"),
 (36, 14, "完成任务→奖励入包"),
]),
("副本流程", [
 (15, 27, "进图资格(疲劳不足回0x16)"),
 (16, 28, "选图→副本信息"),
 (16, 29, "选图→起始地图(怪物/掉落布局)"),
 (48, 29, "过图→新地图快照"),
 (40, 30, "加载完成→加载回执"),
 (40, 136, "入场完成"),
 (42, 38, "杀怪→死亡掉落(含冠军序号)"),
 (42, 31, "清完→允许通关"),
 (49, 34, "提交成绩→结算回执"),
 (42, 35, "通关→翻牌奖励界面(经验九元组)"),
 (73, 35, "翻牌权查询"),
 (74, None, "选牌(回包含金卡奖励)"),
 (45, 33, "放弃游戏→通关失败"),
 (127, 127, "Boss 死亡校验"),
 (148, 38, "全灭怪物→批量死亡"),
 (142, 23, "回城→区域同步"),
 (None, 36, "副本消耗疲劳同步"),
 (72, None, "计分卷状态"),
 (75, None, "EPLP 命令"),
]),
("邮箱", [
 (99, 97, "打开邮箱→邮件列表"),
 (98, 98, "取件→移除邮件"),
 (97, 99, "发信→收件人新邮件提醒"),
 (144, 97, "邮件状态变更"),
]),
("宠物 Creature", [
 (105, 105, "孵化→宠物列表"),
 (103, 101, "改名→改名广播"),
 (104, 104, "召唤/收回→响应"),
 (None, 100, "宠物死亡"),
 (None, 102, "宠物经验"),
 (None, 103, "宠物状态"),
 (None, 106, "宠物进化"),
 (None, 107, "宠物复活"),
]),
("PVP(未实现)", [
 (53, 41, "建房↔房间信息"), (54, 42, "进入↔房间状态"), (55, 43, "座位↔座位状态"),
 (56, 44, "准备↔准备状态"), (58, 46, "死亡↔死亡播报"), (60, 47, "结束↔终局"),
 (61, 49, "段位查询↔排名"), (None, 45, "开局"), (None, 48, "战绩 ✅"),
 (122, 125, "决斗请求"), (120, 124, "心跳↔轮转"), (None, 132, "复活"),
]),
("公会(未实现)", [
 (70, 63, "成员列表↔成员名单"), (77, 59, "退会↔退出播报"),
 (78, 58, "入会申请↔进入播报"), (119, 129, "战争信息↔开战"),
 (None, 60, "徽标变更"), (None, 61, "公会聊天"), (None, 70, "公会信息"),
 (None, 121, "战争结束"), (None, 122, "战争结果"), (None, 139, "成员详情"),
]),
("个人商店(未实现)", [
 (88, 78, "开店↔开店播报"), (89, 79, "关店↔撤店播报"),
 (85, 82, "购买↔成交"), (86, 85, "浏览↔详情"), (None, 80, "强制下架"), (None, 83, "商品列表"),
]),
("战争房间(未实现)", [
 (93, 88, "进入↔房间状态"), (95, 95, "死亡↔死亡状态"), (None, 90, "开局"), (None, 92, "通关奖励"),
]),
("混沌 CHAOS(未实现)", [
 (111, 110, "进入↔房间信息"), (115, 111, "开局↔开局播报"), (116, 118, "通关↔结算奖励"),
 (106, None, "购买FP道具"), (None, 119, "怪物刷新"),
]),
 ("讨伐/反外挂/其他(未实现)", [
 (154, 142, "AI角色死亡↔讨伐死亡"), (155, 141, "讨伐就绪↔开始"),
 (39, 25, "钓鱼"),
 (63, 52, "好友添加↔好友信息"),
 (12, 9, "组队↔队伍信息"), (13, 10, "退队↔离队播报"),
 (158, 147, "血量同步(反外挂,同名对应)"), (161, 152, "反机器人校验(同名对应)"), (162, 153, "反外挂检测(同名对应)"),
 (None, 145, "讨伐图标显示"), (None, 146, "离开讨伐地区"),
 (None, 148, "讨伐结束关闭"), (None, 149, "讨伐区域传送"),
 (None, 150, "AA(讨伐)开始信息"), (None, 151, "AA(讨伐)时间同步"),
 (None, 137, "人证请求"), (None, 138, "GM请求"),
]),
]

lines = []
A = lines.append
A("# 客户端协议枚举全表(60A1, IDA 提取)")
A("")
A("> 来源: `D:/IDA/60A1/DNF.exe.i64`(对应已脱壳 `DNF.exe`, ImageBase 0x400000)。")
A("> 枚举名表位于 .rdata: NOTI 表 `0xB54170`(%d 项,0-%d 命名,其余为 `NOTI_NO_NAME` 占位,槽 %d NULL 结尾), CMD 表 `0xB54430`(%d 项,0-%d 命名,其余为 `CMD_NO_NAME` 占位,表后紧接 RTTI)。表索引即枚举数值,已用 DAFLegacy 已知 ID 逐项验证。" % (NOTI_TOTAL, NOTI_NAMED-1, NOTI_TOTAL, CMD_TOTAL, CMD_NAMED-1))
A("> 提取脚本: `tools/extract_packet_tables.py` / `tools/dump_packet_tables.py`;原始数据 `tools/packet_id_map.json`。")
A(">")
A("> 图例: ✅=DAFLegacy 服务端已实现(有应答/发送) ❌=未实现(客户端会收到忽略/无回包)")
A("> 汇总: NOTI %d/%d 已实现; CMD %d/%d 已处理(分母含 NO_NAME 占位:NOTI %d 项、CMD %d 项,均未实现)。" % (len(NOTI_IMPL), NOTI_TOTAL, len(CMD_IMPL), CMD_TOTAL, NOTI_TOTAL-NOTI_NAMED, CMD_TOTAL-CMD_NAMED))
A("> CMD 处理方式: `E`=引擎内联 `D`=专用应答 `G`=通用成功 `N`=异步回NOTI")
A(">")
A("> 字节示例说明:回包字节 = 服务端 `GameServerPacket.Encode()` 的真实输出(密文);发包字节 = 客户端帧格式,示例统一用种子 `0x1234`、序号 `0x0001`(真实会话中种子逐包推进、序号自增)。带 `*` 的发包正文仅含 2 字节序号占位,实际字段布局未在服务端解析器中确认。")
A("")

# ---------- 分组配对表 ----------
A("## CMD ↔ NOTI 对应分组表")
A("")
used_n, used_c = set(), set()
for gname, rows in GROUPS:
    A("### " + gname)
    A("")
    A("| CMD ID | 发包(客户端→服务端) | 状态 | 发包字节(示例) | NOTI ID | 回包(服务端→客户端) | 状态 | 回包字节(示例) | 长度 | 配对说明 |")
    A("|---:|---|---|---|---:|---|---|---|---|---|")
    for cid, nid, note in rows:
        if cid is not None: used_c.add(cid)
        if nid is not None: used_n.add(nid)
        ccell = "—" if cid is None else "`%s`" % c_name(cid)
        ncell = "—" if nid is None else "`%s`" % n_name(nid)
        cst = ("—" if cid is None else ("✅" if cid in CMD_IMPL else "❌"))
        nst = ("—" if nid is None else ("✅" if nid in NOTI_IMPL else "❌"))
        txh = BYTES.get("cmd:%d" % cid, "") if cid is not None else ""
        rxh = BYTES.get("noti:%d" % nid, "") if nid is not None else ""
        txcell = ("`" + hex_spaced(txh) + ("` *" if cid in PLACEHOLDER_BODY else "`")) if txh else "—"
        rxcell = ("`" + hex_spaced(rxh) + "`") if rxh else "—"
        parts = []
        if cid is not None and cid in CMD_IMPL and CMD_LEN.get(cid) and CMD_LEN[cid] != "-" and not CMD_LEN[cid].startswith("NOTI"):
            parts.append("CMD " + CMD_LEN[cid])
        if nid is not None and nid in NOTI_IMPL and NOTI_LEN.get(nid): parts.append("NOTI " + NOTI_LEN[nid])
        extra = " / ".join(parts)
        cid_s = str(cid) if cid is not None else "—"
        nid_s = str(nid) if nid is not None else "—"
        A("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (cid_s, ccell, cst, txcell, nid_s, ncell, nst, rxcell, extra, note))
    A("")

rest_n = [i for i in range(NOTI_TOTAL) if i not in used_n]
rest_c = [i for i in range(CMD_TOTAL) if i not in used_c and res["cmd"].get(str(i))]
A("### 其余未入组包")
A("")
A("| NOTI ID | 枚举 | 状态 |")
A("|---:|---|---|")
for i in rest_n:
    raw = n_name(i)
    if raw == "—":
        A("| %d | —(无名) | ❌ |" % i)
    else:
        A("| %d | `%s` | %s |" % (i, raw, "✅" if i in NOTI_IMPL else "❌"))
A("")
A("| CMD ID | 枚举 | 状态 |")
A("|---:|---|---|")
for i in rest_c:
    A("| %d | `%s` | %s |" % (i, c_name(i), "✅" if i in CMD_IMPL else "❌"))
A("")

# ---------- flat appendix ----------
A("## 附录A:NOTI 全表(ENUM_NOTIPACKET_*,%d 项:0-%d 命名 + %d-%d 占位)" % (NOTI_TOTAL, NOTI_NAMED-1, NOTI_NAMED, NOTI_TOTAL-1))
A("")
A("| NOTI ID | 客户端枚举(全称) | 状态 | 回包长度 | 备注 |")
A("|---:|---|---|---|---|")
for i in range(NOTI_TOTAL):
    raw = res["noti"].get(str(i))
    ok = i in NOTI_IMPL
    if raw is None:
        A("| %d | —(无名) | ❌ 未实现 | | |" % i)
        continue
    ln = NOTI_LEN.get(i, "") if ok else ""
    note = NOTI_NOTE.get(i, "") or ("NO_NAME 占位保留位" if "NO_NAME" in raw else "")
    extra = " *(小写风格,后期追加)*" if raw.startswith("enum_") else ""
    A("| %d | `%s`%s | %s | %s | %s |" % (i, raw, extra, "✅ 已实现" if ok else "❌ 未实现", ln, note))
A("")
A("## 附录B:CMD 全表(ENUM_CMDPACKET_*,%d 项:0-%d 命名 + %d-%d 占位)" % (CMD_TOTAL, CMD_NAMED-1, CMD_NAMED, CMD_TOTAL-1))
A("")
A("| CMD ID | 客户端枚举(全称) | 状态 | 处理方式 | 应答长度 | 备注 |")
A("|---:|---|---|---|---|---|")
for i in range(CMD_TOTAL):
    raw = res["cmd"].get(str(i))
    ok = i in CMD_IMPL
    if raw is None:
        A("| %d | —(无名) | ❌ 未实现 | | | |" % i)
        continue
    kind = CMD_KIND.get(i, "") if ok else ""
    ln = CMD_LEN.get(i, "") if ok else ""
    note = CMD_NOTE.get(i, "") or ("NO_NAME 占位保留位" if "NO_NAME" in raw else "")
    extra = " *(小写风格,后期追加)*" if raw.startswith("enum_") else ""
    A("| %d | `%s`%s | %s | %s | %s | %s |" % (i, raw, extra, "✅ 已实现" if ok else "❌ 未实现", kind, ln, note))
A("")

# ---------- crypto ----------
A("## 包头结构与加解密(60A1 客户端反编译实证)")
A("")
A("### 发包(客户端→服务端, CMD) — 总长 = 12 + 正文长度,打包函数 `sub_8B24C0`")
A("")
A("```")
A("偏移 0   : type(1B)          0=NOTI 1=CMD")
A("偏移 1   : protocolId(1B)    CMD 枚举值")
A("偏移 2   : totalLength(4B)   = 12 + 正文长度(含序号2B)")
A("偏移 6   : CRC32(4B)         加密前对 [序号2B+明文正文] 计算 (sub_8B23B0→sub_8B31C0, 标准查表CRC32)")
A("偏移 10  : sequence(2B)      每发包自增")
A("偏移 12  : 正文(密文)        仅正文加密,序号为明文")
A("```")
A("")
A("正文加密 `sub_777310`:每包推进 LCG `sub_717CF0`(`x=x*0x41C64E6D+0x3039` 连推3步,")
A("rv=第3步高10位^((第2步高10位^第1步高11位<<10)<<10)),`key=rv低8位,rot=(rv高8位)&7`,")
A("逐字节 `密文=ROR(明文^key,rot)`。初始种子15位,CRC即密钥协商:服务端盲试候选种子集,解密后CRC命中即锁定。")
A("")
A("### 回包(服务端→客户端, NOTI) — 总长 = 6 + 载荷长度")
A("")
A("```")
A("偏移 0   : type(1B)          = 0 (NOTI)")
A("偏移 1   : protocolId(1B)    NOTI 枚举值")
A("偏移 2   : totalLength(4B)   = 6 + 载荷长度")
A("偏移 6   : 载荷(密文)        整体加密,无序号无CRC")
A("```")
A("")
A("客户端解密器 `sub_776F80` 空种子时随机值取常量 0x3EB5:`key=0xB5,rot=6`,")
A("逐字节 `明文=ROL(密文,6)^0xB5`,与服务端 `GamePayloadCipher` 的 `ROR(明文^0xB5,6)` 严格互逆(Python 回环实测一致)。")
A("")
A("### 关键函数地址(IDA 60A1)")
A("")
A("| 功能 | 地址 | 说明 |")
A("|---|---|---|")
A("| LCG推进/通用RNG | `sub_717CF0` | 空种子时用 `dword_B165AC+=timeGetTime()` 作种 |")
A("| 发包加密器 | `sub_777310` | `ROR(x^key,rot)`,种子历史异或存档 |")
A("| 收包解密器 | `sub_776F80` | `ROL(x,rot)^key`,空种子=0xB5/6 |")
A("| 发包打包器 | `sub_8B24C0` | 填12B头,序号自增 |")
A("| CRC32 | `sub_8B23B0`→`sub_8B31C0` | 标准查表CRC32 |")
A("")

# ---------- drift notes ----------
A("## 版本差异与备注")
A("")
for d in [
 "**NOTI 66**: 此版为 `CERA_SPECIALITEM`,服务端发 `PremiumService` 会员通知 —— 需复核。",
 "**CMD 130/139**: 此版 `EXCHANGE_SERVER_CHARAC_INFO`=139、`REQUEST_BLACKLIST`=130;引擎 `ExchangeServerCharacterInfoCommand` 固定 130(存在错配风险)。",
 "**NOTI 27**: 此版名 `START_GAME`(选图资格),=服务端 `EnterSelectDungeonNotification`。",
 "**NOTI 105**: 此版名 `CREATURE_ITEM_LIST`,=服务端 `CreatureInfoNotification`。",
 "NOTI 108-117(CHAOS)用小写 `enum_*` 前缀,CMD 106-107 仍为标准大写;NOTI 154-174、CMD 163-183 为 `*_NO_NAME` 占位保留位。",
 "表尾按 IDB 实测动态扫描(NOTI 实际 %d 项、CMD 实际 %d 项,早期版本误按 145/165 截断),已补齐 NOTI 145-153 与全部占位项。" % (NOTI_TOTAL, CMD_TOTAL),
 "载荷级约定(消息颜色 `GameMessageType`、NOTI 38 冠军序号 1-11、NOTI 35 经验九元组等)详见 `GameProtocolEngine.cs` 注释。",
]:
    A("- " + d)
A("")

out = r"E:\github\DAFLegacy\docs\protocol\client-packet-enums-60cn.md"
open(out, "w", encoding="utf-8").write("\n".join(lines))
print("written:", out, len(lines), "lines")
