# Builds src/Baton.App/app.ico from the frames prepared by docs/brand/brand.mjs (run that first): the brand kit's
# Windows icon at 16-256 px (20 and 40 px rendered from the kit's SVG). The exe, its windows, the notification area and
# the notifications all use it. Frames are stored as PNG, which Windows reads since Vista.
#   python tools/make_icons.py
import os
import struct
import sys

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
frames = os.path.join(root, "docs", "brand", "out")


def ico(path, names):
    images = [open(os.path.join(frames, n), "rb").read() for n in names]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries = b""
    for name, data in zip(names, images):
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            sys.exit(f"{name} is not a PNG")
        width, height = struct.unpack(">II", data[16:24])
        entries += struct.pack("<BBBBHHII", width % 256, height % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(header + entries + b"".join(images))
    print(f"{os.path.relpath(path, root)}: {', '.join(n.split('-')[-1][:-4] for n in names)} px")


ico(os.path.join(root, "src", "Baton.App", "app.ico"), [f"app-{s}.png" for s in (16, 20, 24, 32, 40, 48, 64, 256)])
