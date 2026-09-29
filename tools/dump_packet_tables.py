import pefile, struct, json

PATH = r"D:\IDA\60A1\DNF.exe"
pe = pefile.PE(PATH, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = pe.__data__

secs = []
for s in pe.sections:
    secs.append((base + s.VirtualAddress, s.Misc_VirtualSize, s.SizeOfRawData, s.PointerToRawData))

def va2off(va):
    rva = va - base
    for vaddr, vsz, rsz, raw in secs:
        if vaddr <= va < vaddr + max(vsz, rsz):
            d = va - vaddr
            if d < rsz:
                return raw + d
    return None

def read_cstr(va):
    off = va2off(va)
    if off is None: return None
    end = data.find(b'\x00', off)
    if end == -1 or end - off > 120: return None
    try: return data[off:end].decode('ascii')
    except: return None

def dump_table(table_va, max_entries=512):
    # 表项为 4 字节指针;遇 NULL 结尾槽,或指针指向非可打印字符串(表后紧邻数据/RTTI)即到表尾
    out = []
    for i in range(max_entries):
        va = table_va + i*4
        off = va2off(va)
        if off is None: break
        ptr = struct.unpack_from('<I', data, off)[0]
        if ptr == 0: break
        name = read_cstr(ptr)
        if name is None or not all(32 <= ord(c) < 127 for c in name):
            break
        out.append((i, name if name.startswith('ENUM_') else f"<<{name}>>"))
    return out

noti = dump_table(0xB54170)
cmd = dump_table(0xB54430)

res = {"noti": {i: n for i, n in noti if n}, "cmd": {i: n for i, n in cmd if n}}
res["noti_holes"] = [i for i, n in noti if n is None]
res["cmd_holes"] = [i for i, n in cmd if n is None]
json.dump(res, open(r'E:\github\DFLegacy\tools\packet_id_map.json','w'), indent=1)
print("NOTI entries:", len(res["noti"]), "holes:", res["noti_holes"][:20])
print("CMD entries:", len(res["cmd"]), "holes:", res["cmd_holes"][:20])
print("\n--- NOTI first 20 ---")
for i in range(20):
    print(i, res["noti"].get(i))
print("\n--- CMD first 20 ---")
for i in range(20):
    print(i, res["cmd"].get(i))
