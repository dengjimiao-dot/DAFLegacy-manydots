"""Print the family and full names of TrueType and TrueType-collection files.

Usage: python font_family_names.py <font file> [<font file> ...]

Purpose: confirm which family name a font file exposes before registering it
through the client patch (see PENDING P-42). The engine asks GDI for the
hard-coded family name "SimSun", so only files exposing that family take
effect without also patching the family-name operand.
"""
import struct
import sys


def read_names(data: bytes, offset: int) -> dict:
    num_tables = struct.unpack_from('>H', data, offset + 4)[0]
    tables = {}
    for i in range(num_tables):
        rec = offset + 12 + 16 * i
        tag = data[rec:rec + 4].decode('latin1')
        toff, tlen = struct.unpack_from('>II', data, rec + 8)
        tables[tag] = (toff, tlen)
    if 'name' not in tables:
        return {}
    noff, _nlen = tables['name']
    _fmt, count, str_off = struct.unpack_from('>HHH', data, noff)
    out = {}
    for i in range(count):
        rec = noff + 6 + 12 * i
        pid, _eid, _lid, nid, length, off = struct.unpack_from('>HHHHHH', data, rec)
        val = data[noff + str_off + off: noff + str_off + off + length]
        try:
            text = val.decode('utf-16-be') if pid == 3 else val.decode('latin1')
        except Exception:
            text = repr(val)
        out.setdefault((pid, nid), set()).add(text.strip('\x00'))
    return out


for path in sys.argv[1:]:
    data = open(path, 'rb').read()
    tag = data[:4]
    print('===', path.rsplit('\\', 1)[-1], 'tag', tag, 'size', len(data))
    if tag == b'ttcf':
        count = struct.unpack_from('>I', data, 8)[0]
        offsets = list(struct.unpack_from('>%dI' % count, data, 12))
        print('  fonts in collection:', count)
    else:
        offsets = [0]
    for idx, off in enumerate(offsets[:6]):
        names = read_names(data, off)
        fam = names.get((3, 1)) or names.get((1, 1))
        full = names.get((3, 4)) or names.get((1, 4))
        typo = names.get((3, 16)) or names.get((1, 16))
        print('  [%d] family=%s full=%s typographic=%s' % (
            idx,
            sorted(fam) if fam else None,
            sorted(full) if full else None,
            sorted(typo) if typo else None))
