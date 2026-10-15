# Talks to DownloadManager through its native host, exactly like the extension: python host.py <exe> <data dir> '<json>' ...
import json, os, struct, subprocess, sys

exe, data = sys.argv[1], sys.argv[2]
env = dict(os.environ, DOWNLOADMANAGER_DATA=data)
p = subprocess.Popen([exe, "chrome-extension://fngmjmgaglmoebphcjohnhlaceogneno/"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=env)
for raw in sys.argv[3:]:
    if raw.startswith("@"):
        raw = open(raw[1:], encoding="utf-8-sig").read()  # PowerShell 5.1 writes a BOM
    body = json.dumps(json.loads(raw)).encode()
    p.stdin.write(struct.pack("<I", len(body)) + body)
    p.stdin.flush()
    size = struct.unpack("<I", p.stdout.read(4))[0]
    print(json.dumps(json.loads(p.stdout.read(size)), ensure_ascii=False))
p.stdin.close()
p.wait(timeout=30)
