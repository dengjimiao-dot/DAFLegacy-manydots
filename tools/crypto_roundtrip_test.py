# -*- coding: utf-8 -*-
"""双向加解密回环验证: 服务端实现(DFLegacy语义) <-> 客户端实现(IDA 60A1 语义)"""
import struct, zlib

def ror(b, n): n &= 7; return ((b >> n) | (b << (8-n))) & 0xFF if n else b
def rol(b, n): n &= 7; return ((b << n) | (b >> (8-n))) & 0xFF if n else b

# ---------- 回包方向: 服务端 GameServerPacket(1.0.1.9) -> 客户端 sub_776F80(null seed) ----------
def server_encrypt_payload(plain):           # DFLegacy GamePayloadCipher.EncryptServerPayload
    return bytes(ror(b ^ 0xB5, 6) for b in plain)
def client_decrypt_null_seed(cipher):        # IDA sub_776F80, a3=null -> n16053=0x3EB5
    key = 0x3EB5 & 0xFF                      # 0xB5
    rot = (0x3EB5 >> 8) & 7                  # 6
    return bytes((b ^ key) if False else (rol(b, rot) ^ key) for b in cipher)

plain = bytes([0, 3, 3, 0, 0, 1, 0])          # NOTI 3 USER_STATE: uid=0x0300, state=1 (例)
cipher = server_encrypt_payload(plain)
back = client_decrypt_null_seed(cipher)
print("== 回包(服务端->客户端) ==")
print(" 明文      :", plain.hex(" "))
print(" 服务端加密:", cipher.hex(" "))
print(" 客户端解密:", back.hex(" "), "=> 一致" if back == plain else "=> 失败!")

# ---------- 发包方向: 客户端 sub_717CF0 + sub_777310 -> 服务端 GameClientPayloadCipher ----------
def lcg_advance(state):                        # IDA sub_717CF0 = 服务端 Advance()
    a = (state * 0x41C64E6D + 0x3039) & 0xFFFFFFFF
    b = (a * 0x41C64E6D + 0x3039) & 0xFFFFFFFF
    c = (b * 0x41C64E6D + 0x3039) & 0xFFFFFFFF
    rv = ((c & 0x3FF) ^ (((b >> 16) & 0x3FF ^ ((a >> 16) & 0x7FF) << 10) << 10)) & 0xFFFF
    return c, rv
def client_encrypt(body_plain, seed):          # IDA sub_777310
    seed, rv = lcg_advance(seed)
    key = rv & 0xFF; rot = (rv >> 8) & 7
    return seed, bytes(ror(b ^ key, rot) for b in body_plain)
def server_decrypt(cipher, seed):              # DFLegacy TryDecode 语义(已知种子时)
    seed, rv = lcg_advance(seed)
    key = rv & 0xFF; rot = (rv >> 8) & 7
    return seed, bytes(rol(b, rot) ^ key for b in cipher)

seq = 0x0042
payload = bytes([27, 1, 2, 0, 0x50, 0, 0, 0, 5, 0])   # CMD 27 COMPOUND_ITEM 示例体
seed = 0x1234
enc_seed, cipher = client_encrypt(payload, seed)
crc = zlib.crc32(bytes([seq & 0xFF, seq >> 8]) + payload) & 0xFFFFFFFF   # CRC 对 序号+明文
packet = bytes([1, 27]) + struct.pack('<II', 12 + len(payload), crc) + struct.pack('<H', seq) + cipher
print("\n== 发包(客户端->服务端) ==")
print(" 完整包帧 :", packet.hex(" "))
# 服务端盲猜种子(从 0x8000 个候选里找 CRC 匹配)
found = None
for s in range(0x8000):
    _, rv = lcg_advance(s)
    key = rv & 0xFF; rot = (rv >> 8) & 7
    dec = bytes(rol(b, rot) ^ key for b in cipher)
    if zlib.crc32(bytes([seq & 0xFF, seq >> 8]) + dec) & 0xFFFFFFFF == crc:
        found = (s, dec); break
if found:
    s, dec = found
    print(f" 服务端盲解: 种子=0x{s:04X} 明文={dec.hex(' ')}", "=> 一致" if dec == payload else "=> 失败!")
else:
    print(" 服务端盲解: 失败")
