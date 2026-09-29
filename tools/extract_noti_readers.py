#!/usr/bin/env python3
"""Extract the client's packet-body read sequence for NOTI ids from a live IDA
via the ida-pro-mcp HTTP endpoint (default http://127.0.0.1:13337/mcp).

The 60A1 client parses every server->client packet with a tiny global-cursor
reader family inside sub_419420 (NOTI dispatcher, switch on packet[1], jump table
at .text 0x42A081, 152 cases).  This script dumps, per NOTI id, the ordered read
calls in that id's case body; the sum of reader advances is the exact payload
length the server must write.

Readers (confirmed by decompiling each):
    sub_8F5120 -> u8   (+1)      sub_8F5140 -> u16 (+2)
    sub_8F5160 -> u32  (+4)      sub_8F5180(dst,n) -> n-byte block
    sub_8B3250(dst,cap,fallback) -> dstr (u32 length + bytes; on invalid
        length the cursor does NOT advance and dst gets the fallback text)
    sub_8AA5A0(id) only fetches fallback text from dnf.str -- it never
        touches the cursor, so it is not part of the wire layout.
    sub_8F5110 -> init cursor (called by receive path with decrypted payload)
    sub_8F51C0/sub_8F51D0 -> cursor position

Usage:
    python extract_noti_readers.py 58 59 60 61 62 63 64 70 71
    python extract_noti_readers.py --all
"""
import json
import sys
import urllib.request

MCP_URL = "http://127.0.0.1:13337/mcp"
NOTI_DISPATCHER = 0x419420
JUMP_TABLE = 0x42A081  # switch @0x419474, 152 cases, elem size 4, lowcase 0
DEFAULT_ADDR = 0x42A069  # shared default/ignore tail inside sub_419420
READERS = {0x8F5120: "u8", 0x8F5140: "u16", 0x8F5160: "u32",
           0x8B3250: "dstr"}


def call(name, args):
    payload = json.dumps({"jsonrpc": "2.0", "method": "tools/call",
                          "params": {"name": name, "arguments": args}, "id": 1}).encode()
    req = urllib.request.Request(MCP_URL, data=payload,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        res = json.loads(r.read())["result"]
    for c in res.get("content", []):
        if c.get("type") == "text":
            return c["text"]
    return res.get("structuredContent", {})


def py(code):
    text = call("py_eval", {"code": code})
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return {"stdout": text, "stderr": ""}


def extract(ids):
    id_list = ", ".join(str(i) for i in ids)
    code = f"""
import idaapi, idc
tbl = {JUMP_TABLE}
N = 152
handlers = {{}}
for i in range(N):
    handlers[i] = idaapi.get_dword(tbl + i*4)
sorted_addrs = sorted(handlers.values())
READERS = {READERS!r}
WIDTH = {{ "u8": 1, "u16": 2, "u32": 4, "dstr": 4 }}
out = []
for gid in [{id_list}]:
    start = handlers[gid]
    pos = sorted_addrs.index(start)
    end = sorted_addrs[pos+1] if pos+1 < len(sorted_addrs) else {DEFAULT_ADDR:#x}
    seq = []
    ea = start
    while ea < end:
        ins = idc.GetDisasm(ea)
        if ins.strip().startswith("call"):
            tgt = idc.get_operand_value(ea, 0)
            nm = idc.get_name(tgt)
            if tgt in READERS:
                seq.append(READERS[tgt])
            elif nm == "sub_8F5180":
                seq.append("block")
            elif nm == "sub_8F5110":
                seq.append("INIT")
        ea = idc.next_head(ea, end)
    out.append((gid, start, end, seq))
for gid, start, end, seq in out:
    fixed = 0
    for s in seq:
        if s != "block":
            fixed += WIDTH.get(s, 0)
    print("NOTI %3d  %08X..%08X  fixed>=%dB  seq: %s" % (gid, start, end, fixed, " -> ".join(seq) if seq else "(no reads -> empty payload)"))
"""
    res = py(code)
    if res.get("stderr"):
        print(res["stderr"], file=sys.stderr)
    print(res.get("stdout", ""))


if __name__ == "__main__":
    argv = sys.argv[1:]
    if not argv or argv[0] == "--all":
        extract(range(152))
    else:
        extract(int(a) for a in argv)
