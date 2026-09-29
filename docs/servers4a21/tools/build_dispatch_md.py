import io, re

LOOKUP = __import__("os").path.dirname(__file__)

def load_lookup(name):
    m = {}
    with io.open(LOOKUP + "\\" + name, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            n, v = line.split("=")
            m[n.strip()] = v.strip()
    return m

cmd = load_lookup("cmd-a21.txt")
rev = {}
for n, v in cmd.items():
    rev.setdefault(v.lower(), n)

rows = []
with io.open(LOOKUP + r"\dispatch-table.txt", encoding="utf-8") as f:
    for line in f:
        parts = line.rstrip("\n").split("\t")
        if len(parts) >= 4:
            rows.append(parts[:4])

name_doc_override = {
    "USE_LOTTERY_ITEM": "09", "INCREASE_CHANCE_LOTTERY_RESET": "09",
    "CRANE_START_USE": "09", "CRANE_PICKUP": "09",
    "GET_PCROOM_TIME_POINT_ITEM": "09", "AT_DAILY_ATTENDANCE": "09",
    "EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK": "09",
    "ACHIEVEMENT_TRIGGER": "03",
}

group_doc = {
    "RegisterLoginHandlers": "01", "RegisterCharacterHandlers": "01",
    "RegisterSettingsHandlers": "01", "RegisterInventoryHandlers": "02/03",
    "RegisterSortItemLockHandlers": "02/03", "RegisterEquipmentItemLockHandlers": "02/03",
    "RegisterEquipmentSocketHandlers": "02/03", "RegisterEquipmentEmblemHandlers": "02/03",
    "RegisterAvatarSocketHandlers": "02/03", "RegisterAvatarEmblemHandlers": "02/03",
    "RegisterPetHandlers": "10", "RegisterDungeonHandlers": "04/05",
    "RegisterSkillHandlers": "06", "RegisterTownHandlers": "06",
    "RegisterQuestHandlers": "06", "RegisterPartyHandlers": "07",
    "RegisterRaidHandlers": "08", "RegisterMailboxHandlers": "09",
    "RegisterCollectionBoxHandlers": "09", "RegisterEventJoustHandlers": "09",
    "RegisterMercenaryHandlers": "10", "RegisterExpertJobHandlers": "10",
    "ctor:udp-endpoint": "07", "ctor:shop-coin-event": "09",
    "ctor:guild-members": "07", "ctor:guild-join": "07",
    "ctor:guild-creation": "07", "ctor:guild-management": "07",
    "ext:chat": "07", "ext:friend": "07", "ext:blacklist": "07",
    "ext:userchannel": "07", "ext:pvp": "08", "ext:rental": "09",
    "ext:knightshield": "02/03",
}

def doc_for(grp, name, val):
    if name and name in name_doc_override:
        return name_doc_override[name]
    if grp == "RegisterMiscHandlers":
        if name and re.match(r"^(PVP|MAKE_PVP|ENTER_PVP|SET_PVP|DIE_PVP|END_PVP|RES_PVP|COMPLETE_LOAD_PVP|CONNECT_P2P_PVP|CHECK_PVP)", name):
            return "08"
        if val == "0x0040" or name in ("GEN_CERATICKET", "CHARGE_RENTPOINT", "GET_EXPAND_EXP_GAGE_REWARD", "UPGRADE_CARRY_GOLD"):
            return "09"
        if val == "0x01de":
            return "04"
        return "01"
    return group_doc.get(grp, "??")

def clean_handler(h):
    h = h.strip()
    h = re.sub(r"^\(s, h, b\) =>\s*", "", h)
    if h == "Task.CompletedTask":
        return "(空实现,收下即弃)"
    h = re.sub(r"^async \(s, h, b\) =>\s*\{\s*", "", h)
    h = h.rstrip("}").strip().rstrip(";").strip()
    h = re.sub(r"\s+", " ", h)
    if h.startswith("FOREACH:"):
        h = "GuildManagement.Handle(9 项同入口)"
    if h.startswith("{ "):
        h = h[2:]
    if len(h) > 70:
        h = h[:67] + "..."
    return h

buckets = {}
for g, val, name, handler in rows:
    doc = doc_for(g, name, val)
    if val.startswith("CONST:"):
        num, disp = "?" + val[6:].split(".")[0], val[6:]
        # 已知常量直接给号
        known = {
            "KnightShieldDeckBodyBuilder.ChangeDeckCommandType": ("0x0292", "CHANGE_DECK_INFO"),
            "RentalHandler.CommandType": ("0x03cf", "RENT_EQUIPMENT_ITEM"),
            "PvpChannelInfoHandler.CommandType": ("0x00c3", "PVP_CHANNEL_INFO"),
            "PvpRoomHandler.MakeRoomCommandType": ("0x0032", "MAKE_PVP_ROOM"),
            "PvpRoomHandler.EnterRoomCommandType": ("0x0033", "ENTER_PVP_ROOM"),
            "PvpRoomHandler.SetSeatStateCommandType": ("0x0034", "SET_PVP_SEAT_STATE"),
            "PvpRoomHandler.SetReadyStateCommandType": ("0x0035", "SET_PVP_READY_STATE"),
            "PvpRoomHandler.SetTeamModeCommandType": ("0x0036", "SET_PVP_TEAM_MODE"),
            "PvpRoomHandler.DiePvpCharacterCommandType": ("0x0037", "DIE_PVP_CHARACTER"),
            "PvpRoomHandler.PvpTimeOutCommandType": ("0x0038", "PVP_TIME_OUT"),
            "PvpRoomHandler.EndPvpResultCommandType": ("0x0039", "END_PVP_RESULT"),
            "PvpRoomHandler.PvpRankResponseCommandType": ("0x003a", "RES_PVP_RANK"),
            "PvpRoomHandler.CompleteLoadPvpCommandType": ("0x012a", "COMPLETE_LOAD_PVP"),
            "PvpRoomHandler.ConnectP2pPvpCommandType": ("0x012b", "CONNECT_P2P_PVP"),
            "PvpRoomHandler.PvpRequestFightCommandType": ("0x0070", "PVP_REQUEST_FIGHT"),
        }
        num, disp = known.get(val[6:], (num, disp))
    else:
        num = val
        disp = name or rev.get(val.lower(), "")
    h = clean_handler(handler)
    doc = {"03": "02/03", "04": "04/05"}.get(doc, doc)
    buckets.setdefault(doc, []).append((int(num, 16) if num.startswith("0x") else 10**9, num, disp, h))

sections = [
    ("01", "登录/选角/设置/杂项(分册 01)"),
    ("02/03", "背包/物品(分册 02、03)"),
    ("04/05", "副本(分册 04、05)"),
    ("06", "城镇/技能/任务(分册 06)"),
    ("07", "组队/聊天/交易/好友/黑名单/公会(分册 07)"),
    ("08", "Raid/PVP(分册 08)"),
    ("09", "商城/活动/邮箱/服务(分册 09)"),
    ("10", "宠物/佣兵/专家副职(分册 10)"),
]

total = sum(len(v) for v in buckets.values())
md = ["共 %d 个 CMD 注册项(含空实现)。空实现=收包后不做任何业务(多为 P2P 上报类),仅消掉 Unhandled 日志;未注册的 CMD 落兜底日志不回包。\n" % total]
for doc, label in sections:
    lines = sorted(buckets.get(doc, []))
    if not lines:
        continue
    md.append(f"### {label}\n")
    md.append("| CMD | 枚举名 | 处理入口 |")
    md.append("|---:|---|---|")
    for _, num, disp, h in lines:
        md.append(f"| {num} | {disp} | {h} |")
    md.append("")

leftover = [d for d in buckets if d not in [s[0] for s in sections]]
with io.open(LOOKUP + r"\dispatch-table.md", "w", encoding="utf-8") as f:
    f.write("\n".join(md))
print("total:", total, "| buckets:", {k: len(v) for k, v in buckets.items()}, "| leftover:", leftover)
