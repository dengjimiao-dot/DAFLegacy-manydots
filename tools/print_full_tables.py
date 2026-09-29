import json
res = json.load(open(r'E:\github\DFLegacy\tools\packet_id_map.json'))
print("===== NOTI (server -> client) =====")
for i in range(145):
    n = res["noti"].get(str(i), "---")
    print(f"{i:3d} {n.replace('ENUM_NOTIPACKET_','')}")
print("\n===== CMD (client -> server) =====")
for i in range(165):
    n = res["cmd"].get(str(i), "---")
    print(f"{i:3d} {n.replace('ENUM_CMDPACKET_','')}")
