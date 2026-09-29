import pefile, re, struct, json, sys

PATH = r"D:\IDA\60A1\DNF.exe"
pe = pefile.PE(PATH, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
print("ImageBase:", hex(base))
for s in pe.sections:
    print(s.Name.decode(errors="replace").rstrip("\x00"), hex(base + s.VirtualAddress), "vsize", hex(s.Misc_VirtualSize), "rawsize", hex(s.SizeOfRawData), "rawptr", hex(s.PointerToRawData))

data = pe.__data__
def va2off(va):
    rva = va - base
    for s in pe.sections:
        if s.VirtualAddress <= rva < s.VirtualAddress + max(s.Misc_VirtualSize, s.SizeOfRawData):
            delta = rva - s.VirtualAddress
            if delta < s.SizeOfRawData:
                return s.PointerToRawData + delta
    return None

# verify known string
off = va2off(0xA86D48)
print("check 0xA86D48:", data[off:off+40] if off else "unmapped")
