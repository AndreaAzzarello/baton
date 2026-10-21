# Builds the app's .ico files from the frames rendered by docs/brand/brand.mjs (run that first):
#   python tools/make_icons.py
# app.ico: 16-256 px (small sizes on the pixel grid, the white tile from 32 px); tray-light.ico / tray-dark.ico:
# 16, 20, 24, 32 px for a light or a dark taskbar. Frames are stored as PNG, which Windows reads since Vista.
import os
import struct
import sys

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
frames = os.path.join(root, "docs", "brand", "out")
app = os.path.join(root, "src", "Baton.App")


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


ico(os.path.join(app, "app.ico"), [f"app-{s}.png" for s in (16, 20, 24, 32, 40, 48, 64, 256)])
for theme in ("light", "dark"):
    ico(os.path.join(app, f"tray-{theme}.ico"), [f"tray-{theme}-{s}.png" for s in (16, 20, 24, 32)])
