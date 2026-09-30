import pefile, re, struct, json
from collections import defaultdict

PATH = r"D:\IDA\60A1\DNF.exe"
pe = pefile.PE(PATH, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = pe.__data__
filesize = len(data)

secs = []
for s in pe.sections:
    secs.append((base + s.VirtualAddress, s.Misc_VirtualSize, s.SizeOfRawData, s.PointerToRawData, s.Name.decode(errors='replace').rstrip('\x00')))

def va2off(va):
    rva = va - base
    for vaddr, vsz, rsz, raw, name in secs:
        if vaddr <= va < vaddr + max(vsz, rsz):
            d = va - vaddr
            if d < rsz:
                return raw + d
    return None

def off2va(off):
    for vaddr, vsz, rsz, raw, name in secs:
        if raw <= off < raw + rsz:
            return vaddr + (off - raw)
    return None

# 1) collect ENUM_*PACKET_* strings
str_re = re.compile(rb'ENUM_(?:CMD|NOTI|SVC)?_?PACKET_[A-Z0-9_]{2,}')
strings = {}  # va -> name
for m in str_re.finditer(data):
    start = m.start()
    # find NUL end
    end = data.find(b'\x00', m.end())
    if end == -1: end = m.end()
    name = data[start:end].decode('ascii', 'replace')
    va = off2va(start)
    if va: strings[va] = name

fam = defaultdict(list)
for va, name in sorted(strings.items()):
    if '_CMDPACKET_' in name: fam['CMD'].append((va, name))
    elif '_NOTIPACKET_' in name: fam['NOTI'].append((va, name))
    else: fam['OTHER'].append((va, name))
print({k: len(v) for k, v in fam.items()})
json.dump({hex(va): name for va, name in strings.items()}, open(r'E:\github\DAFLegacy\tools\packet_strings.json','w'), indent=0)

# 2) find all dword pointers to any packet string
ptr_hits = defaultdict(list)  # string_va -> [ptr locations va]
target = {va: 0 for va in strings}
step = 4
for off in range(0, filesize - 4, 4):
    v = struct.unpack_from('<I', data, off)[0]
    if v in target:
        pva = off2va(off)
        if pva: ptr_hits[v].append(pva)

hits_total = sum(len(v) for v in ptr_hits.values())
print("pointer hits:", hits_total, "strings referenced:", len(ptr_hits))

# 3) cluster pointer locations into arrays (same family adjacency)
all_locs = []
for va, locs in ptr_hits.items():
    for l in locs:
        all_locs.append((l, va))
all_locs.sort()

# group locations into contiguous runs (gap <= 8 bytes)
runs = []
cur = [all_locs[0]]
for item in all_locs[1:]:
    if item[0] - cur[-1][0] <= 8:
        cur.append(item)
    else:
        runs.append(cur); cur = [item]
runs.append(cur)
runs = [r for r in runs if len(r) >= 4]
print("runs>=4:")
for r in runs:
    print(" start", hex(r[0][0]), "end", hex(r[-1][0]), "count", len(r), "first", strings.get(r[0][1]))
json.dump([[hex(l), hex(v), strings.get(v)] for l, v in all_locs], open(r'E:\github\DAFLegacy\tools\packet_ptr_hits.json','w'), indent=0)
