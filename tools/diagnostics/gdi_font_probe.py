"""Probe which font file GDI actually uses for a face name.

Usage:
  python gdi_font_probe.py [--register <font file>]... [--face SimSun]
                           [--height 12] [--text 赛丽亚] [--out render.png]

Registers the given font files for this process only (AddFontResourceEx with
FR_PRIVATE, the same call the client patch uses) and renders the text through
CreateFontW/TextOutW into a 32-bit DIB, then prints the selected face name and
a hash of the pixels. Run it once per configuration in a fresh process to see
whether a private registration changes what GDI picks for a duplicated family
name (see PENDING P-42).
"""
import argparse
import ctypes
import hashlib
import struct
import zlib
from ctypes import wintypes

FR_PRIVATE = 0x10
BI_RGB = 0
DIB_RGB_COLORS = 0
TRANSPARENT = 1


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def write_png(path: str, width: int, height: int, bgra: bytes) -> None:
    rgb = bytearray()
    for y in range(height):
        row = bgra[y * width * 4:(y + 1) * width * 4]
        rgb.append(0)
        for x in range(width):
            b, g, r = row[x * 4], row[x * 4 + 1], row[x * 4 + 2]
            rgb += bytes((r, g, b))

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    data = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", header)
            + chunk(b"IDAT", zlib.compress(bytes(rgb), 9))
            + chunk(b"IEND", b""))
    with open(path, "wb") as handle:
        handle.write(data)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--register", action="append", default=[])
    parser.add_argument("--face", default="SimSun")
    parser.add_argument("--height", type=int, default=12)
    parser.add_argument("--text", default="赛丽亚")
    parser.add_argument("--width", type=int, default=160)
    parser.add_argument("--height-px", type=int, default=40, dest="canvas_height")
    parser.add_argument("--out")
    args = parser.parse_args()

    gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
    registered = []
    for path in args.register:
        count = gdi32.AddFontResourceExW(ctypes.c_wchar_p(path), FR_PRIVATE, None)
        registered.append((path, count))

    hdc = gdi32.CreateCompatibleDC(None)
    info = BITMAPINFO()
    info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    info.bmiHeader.biWidth = args.width
    info.bmiHeader.biHeight = -args.canvas_height
    info.bmiHeader.biPlanes = 1
    info.bmiHeader.biBitCount = 32
    info.bmiHeader.biCompression = BI_RGB
    bits = ctypes.c_void_p()
    bitmap = gdi32.CreateDIBSection(
        hdc, ctypes.byref(info), DIB_RGB_COLORS, ctypes.byref(bits), None, 0)
    gdi32.SelectObject(hdc, bitmap)

    memory = (ctypes.c_char * (args.width * args.canvas_height * 4)).from_address(bits.value)
    ctypes.memset(memory, 0xFF, len(memory))

    ctypes.windll.gdi32.CreateFontW.restype = ctypes.c_void_p
    font = gdi32.CreateFontW(
        args.height, 0, 0, 0, 400, 0, 0, 0, 0x86, 0, 0, 0, 0,
        ctypes.c_wchar_p(args.face))
    previous = gdi32.SelectObject(hdc, ctypes.c_void_p(font))
    gdi32.SetTextColor(hdc, 0x000000)
    gdi32.SetBkMode(hdc, TRANSPARENT)
    gdi32.TextOutW(hdc, 2, 2, ctypes.c_wchar_p(args.text), len(args.text))

    face_buffer = ctypes.create_unicode_buffer(64)
    gdi32.GetTextFaceW(hdc, 64, face_buffer)

    pixels = bytes(memory)
    dark = sum(1 for i in range(0, len(pixels), 4) if pixels[i] < 0x80)
    print("registered: %s" % (registered,))
    print("requested face: %s" % args.face)
    print("selected face : %s" % face_buffer.value)
    print("ink pixels    : %d" % dark)
    print("pixel sha256  : %s" % hashlib.sha256(pixels).hexdigest()[:32])
    if args.out:
        write_png(args.out, args.width, args.canvas_height, pixels)
        print("png           : %s" % args.out)

    gdi32.SelectObject(hdc, previous)
    gdi32.DeleteObject(ctypes.c_void_p(font))
    gdi32.DeleteObject(bitmap)
    gdi32.DeleteDC(hdc)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
