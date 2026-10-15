# Local test site for the browser trials: python -I server.py <port> <log file>
import http.server, json, sys, threading, urllib.parse

PORT, LOG = int(sys.argv[1]), sys.argv[2]
APPLE = "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8"
lock = threading.Lock()


def log(entry):
    with lock, open(LOG, "a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")


def payload(name, size=2 * 1024 * 1024):
    seed = sum(name.encode()) % 251
    return bytes((i * 7 + seed) % 256 for i in range(256)) * (size // 256)


LINKS = "".join(f'<li><a href="/files/archive-{i}.zip">Archivio {i}</a></li>' for i in range(12))
LINKS += "".join(f'<li><a href="/files/manual-{i}.pdf">Manuale {i}</a></li>' for i in range(8))
LINKS += "".join(f'<li><a href="/page-{i}.html">Pagina {i}</a></li>' for i in range(15))
LINKS += "".join(f'<li><img src="/files/photo-{i}.jpg" alt="Foto {i}" width="20"></li>' for i in range(10))
LINKS += '<li><a href="mailto:info@example.test">Mail</a></li><li><a href="#top">Su</a></li><li><a href="javascript:void(0)">Menu</a></li>'
LINKS += '<li><a href="/files/setup.exe">Installer</a></li><li><a href="/files/setup.exe#again">Installer (di nuovo)</a></li>'

PAGES = {
    "/links.html": f"<!doctype html><title>Pagina con molti link</title><h1>Download</h1><ul>{LINKS}</ul>",
    "/download.html": '<!doctype html><title>Download diretto</title><a id="zip" href="/files/big.zip">big.zip</a> <a id="alt" href="/files/alt.zip">alt.zip</a>',
    "/form.html": """<!doctype html><title>Esportazione</title>
<form id="f" method="post" action="/export"><input name="anno" value="2026"><input name="nome" value="Relazione finale & co">
<select name="formato"><option>zip</option></select><button id="go">Esporta</button></form>""",
    "/video.html": f"""<!doctype html><title>Video di prova HLS</title><h1>Video</h1><video id="v" width="320" controls></video>
<script>fetch("{APPLE}").then(r => r.text()).then(t => document.title = "Video di prova HLS (" + t.length + ")");</script>""",
    "/drm.html": f"""<!doctype html><title>Video protetto</title><video id="v" width="320"></video>
<script>
navigator.requestMediaKeySystemAccess("org.w3.clearkey", [{{initDataTypes: ["keyids"], videoCapabilities: [{{contentType: 'video/mp4; codecs="avc1.42E01E"'}}]}}])
  .then(a => a.createMediaKeys()).then(k => document.getElementById("v").setMediaKeys(k))
  .then(() => {{ document.title = "drm-ready"; return fetch("{APPLE}"); }})
  .catch(e => document.title = "drm-error " + e);
</script>""",
}

TYPES = {".zip": "application/zip", ".pdf": "application/pdf", ".jpg": "image/jpeg", ".exe": "application/vnd.microsoft.portable-executable"}


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send_bytes(self, body, ctype, extra=None):
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urllib.parse.urlsplit(self.path).path
        log({"method": "GET", "path": path, "cookie": self.headers.get("Cookie", ""), "referer": self.headers.get("Referer", ""),
             "ua": self.headers.get("User-Agent", "")})
        if path in PAGES:
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Set-Cookie", "sessione=abc123; Path=/")
            self.end_headers()
            self.wfile.write(PAGES[path].encode())
        elif path.startswith("/files/"):
            name = path[7:]
            ext = name[name.rfind("."):]
            self.send_bytes(payload(name, 2 * 1024 * 1024 if ext in (".zip", ".exe") else 64 * 1024), TYPES.get(ext, "application/octet-stream"))
        elif path.startswith("/page-"):
            self.send_bytes(b"<!doctype html><title>pagina</title>", "text/html")
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length).decode("utf-8", "replace")
        log({"method": "POST", "path": self.path, "body": body, "type": self.headers.get("Content-Type", ""),
             "cookie": self.headers.get("Cookie", ""), "referer": self.headers.get("Referer", "")})
        fields = urllib.parse.parse_qs(body)
        if self.path == "/export" and fields.get("anno") == ["2026"]:
            self.send_bytes(payload("report", 3 * 1024 * 1024), "application/zip", {"Content-Disposition": 'attachment; filename="relazione-2026.zip"'})
        else:
            self.send_response(400)
            self.end_headers()


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
