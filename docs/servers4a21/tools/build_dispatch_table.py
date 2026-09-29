import re, io, sys, os

# ServerS4A21 工作区根目录经环境变量传入，不在脚本里写死本机路径：
#   set S4A21_ROOT=<ServerS4A21 工作区>\Server\DfoServer
ROOT = os.environ.get("S4A21_ROOT", "")
if not ROOT:
    sys.exit("usage: S4A21_ROOT=<ServerS4A21>/Server/DfoServer python build_dispatch_table.py")
SRC = ROOT + r"\Network\Protocol\GameProtocolHandler.cs"
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

src = io.open(SRC, encoding="utf-8-sig").read().splitlines()

# group registration methods -> group name
group_map = {
    "RegisterLoginHandlers": "login/character (01)",
    "RegisterCharacterHandlers": "login/character (01)",
    "RegisterPartyHandlers": "social (07)",
    "RegisterRaidHandlers": "raid/pvp (08)",
    "RegisterInventoryHandlers": "inventory (02/03)",
    "RegisterPetHandlers": "pet (10)",
    "RegisterSortItemLockHandlers": "inventory (03)",
    "RegisterEquipmentItemLockHandlers": "inventory (03)",
    "RegisterEquipmentSocketHandlers": "inventory (03)",
    "RegisterEquipmentEmblemHandlers": "inventory (03)",
    "RegisterAvatarSocketHandlers": "inventory (03)",
    "RegisterAvatarEmblemHandlers": "inventory (03)",
    "RegisterDungeonHandlers": "dungeon (04/05)",
    "RegisterSkillHandlers": "town/skill/quest (06)",
    "RegisterTownHandlers": "town/skill/quest (06) / shop-event (09)",
    "RegisterSettingsHandlers": "login/character (01)",
    "RegisterQuestHandlers": "town/skill/quest (06)",
    "RegisterMailboxHandlers": "shop-event-mail (09)",
    "RegisterCollectionBoxHandlers": "shop-event-mail (09)",
    "RegisterMercenaryHandlers": "pet/mercenary (10)",
    "RegisterRaidHandlers2": "raid/pvp (08)",
    "RegisterExpertJobHandlers": "pet/mercenary (10)",
    "RegisterMiscHandlers": "misc (01/08/09)",
    "RegisterEventJoustHandlers": "shop-event-mail (09)",
}

# extra class-internal command constants to resolve
extra_consts = {}

def resolve(tok):
    tok = tok.strip()
    m = re.match(r"^(?:\(ushort\)CmdPacketTypeA21\.|\(ushort\)CmdPacketType\.|CmdPacketTypeA21\.|CmdPacketType\.)(\w+)$", tok)
    if m:
        name = m.group(1)
        return cmd.get(name), name
    m = re.match(r"^0x[0-9A-Fa-f]+$", tok)
    if m:
        return tok.lower(), None
    # class constant e.g. KnightShieldDeckBodyBuilder.ChangeDeckCommandType
    m = re.match(r"^(\w+)\.(\w+)$", tok)
    if m:
        return ("CONST:" + tok, m.group(2))
    return (None, tok)

rows = []
cur_group = None
cur_method = None
for i, line in enumerate(src, 1):
    s = line.strip()
    gm = re.match(r"private void (Register\w+)\(", s)
    if gm:
        cur_group = gm.group(1)
        continue
    if cur_group is None:
        continue
    # registration lines: d[KEY] = VALUE;
    rm = re.match(r"d\[(.+?)\]\s*=\s*(.+?);", s)
    if rm:
        key_toks = [rm.group(1)]
        if "foreach" in s:
            continue
        val = rm.group(2)
        # multi-register inside for loops handled separately
        handler = val
        handler = re.sub(r"\s+", " ", handler)
        rows.append((i, cur_group, key_toks, handler))
        continue
    # foreach pattern: foreach (var command in new[] { A, B, ... }) d[(ushort)command] = X;
    fm = re.match(r"foreach \(var command in new\[\] \{(.+?)\}\)", s)
    if fm:
        rows.append((i, cur_group, [t.strip() for t in fm.group(1).split(",")], "FOREACH:" + s))
        continue

# resolve and print
out = []
for lineno, grp, keys, handler in rows:
    for k in keys:
        v, name = resolve(k)
        out.append((grp, v, name, handler, lineno))

with io.open(LOOKUP + r"\dispatch-table.txt", "w", encoding="utf-8") as f:
    for grp, v, name, handler, lineno in out:
        f.write(f"{grp}\t{v}\t{name or ''}\t{handler}\t{lineno}\n")

print(f"total registrations: {len(out)}")
unresolved = [(g, v, n, h, l) for g, v, n, h, l in out if v and (v.startswith("CONST:") or v is None)]
for u in unresolved:
    print("UNRESOLVED:", u)
