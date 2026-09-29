# -*- coding: utf-8 -*-
import json

res = json.load(open(r'E:\github\DFLegacy\tools\packet_id_map.json'))

NOTI_TOTAL = max(map(int, res["noti"])) + 1
CMD_TOTAL = max(map(int, res["cmd"])) + 1

def emit(names, count, var):
    lines = ["        "]
    out = []
    for i in range(count):
        n = names.get(str(i))
        out.append(n if n else "null")
    # wrap 2 per line
    rows = []
    for i in range(0, count, 2):
        rows.append("            " + ", ".join('"%s"' % v if v else "null" for v in out[i:i+2]) + ",")
    return "\n".join(rows)

body = f'''using System.Diagnostics.CodeAnalysis;

namespace DFLegacy.Protocol;

/// <summary>
/// Packet enum names recovered from the DNF.exe 60A1 client binary
/// (name tables at .rdata 0xB54170 NOTI / 0xB54430 CMD; see
/// docs/protocol/client-packet-enums-60cn.md). Regenerate with
/// tools/gen_packet_names.py after re-extracting from a new client build.
/// </summary>
public static class PacketNames
{{
    private static readonly string?[] Noti =
    [
{emit(res["noti"], NOTI_TOTAL, "noti")}
    ];

    private static readonly string?[] Cmd =
    [
{emit(res["cmd"], CMD_TOTAL, "cmd")}
    ];

    /// <summary>Returns the client enum name for a packet type/protocol pair, or null.</summary>
    public static bool TryGet(byte type, byte protocolId, [NotNullWhen(true)] out string? name)
    {{
        var table = type == 0 ? Noti : type == 1 ? Cmd : null;
        if (table is not null && protocolId < table.Length)
        {{
            name = table[protocolId];
            return name is not null;
        }}

        name = null;
        return false;
    }}
}}
'''

out = r"E:\github\DFLegacy\Server\DFLegacy.Server\Protocol\PacketNames.cs"
open(out, "w", encoding="utf-8", newline="\n").write(body)
print("written", out)
