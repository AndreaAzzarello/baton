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

TYPES = {".zip": "application/zip", ".pdf": "application/pdf", ".jpg": "image/jpeg", ".exe": "application/vnd.microsoft.portable-executable",
         ".png": "image/png", ".css": "text/css", ".js": "text/javascript"}

# Grabber trial: a small site under /grab/ with folders, a style sheet, robots.txt, an endless calendar, a busy file (429)
# and a page with 3000 files for the preview table.
GRAB = {
    "/grab/": """<!doctype html><html><head><meta charset="utf-8"><title>Sito di prova del grabber</title>
<link rel="stylesheet" href="style.css"></head><body><h1>Grabber</h1><img src="img/logo.png" alt="logo">
<ul><li><a href="a.html">Pagina A</a></li><li><a href="b.html">Pagina B</a></li><li><a href="many.html">Molti file</a></li>
<li><a href="private/secret.html">Riservata (robots.txt)</a></li><li><a href="busy.zip">Occupato (429)</a></li>
<li><a href="https://example.org/">Esterno</a></li><li><a href="a.html?PHPSESSID=123">A con sessione</a></li></ul></body></html>""",
    "/grab/a.html": """<!doctype html><title>Pagina A</title><a href="./">home</a> <a href="docs/manuale.pdf">Manuale</a>
<img src="img/foto.png" srcset="img/foto.png 1x, img/foto-2x.png 2x" alt="foto"> <div style="background:url(img/sfondo.png)">x</div>""",
    "/grab/b.html": """<!doctype html><title>Pagina B</title><a href="./">home</a> <a href="cal?m=1">Calendario</a> <a href="docs/listino.pdf">Listino</a>""",
    "/grab/many.html": "<!doctype html><title>Molti file</title><ul>"
        + "".join(f'<li><a href="f/file-{i:04}.pdf">File {i}</a></li>' for i in range(3000)) + "</ul>",
    "/grab/private/secret.html": """<!doctype html><title>Riservata</title><a href="../docs/segreto.pdf">segreto</a>""",
}
GRAB_CSS = b"body { background: #fff url(img/bg.png) } h1 { background-image: url('img/titolo.png') }"
ROBOTS = b"User-agent: *\nDisallow: /grab/private/\n"
busy_seen = set()


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_HEAD(self):
        self.respond(head=True)

    def do_GET(self):
        self.respond(head=False)

    def send_bytes(self, body, ctype, extra=None, head=False):
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        if not head:
            self.wfile.write(body)

    def respond(self, head):
        parts = urllib.parse.urlsplit(self.path)
        path = parts.path
        log({"method": "HEAD" if head else "GET", "path": path + (("?" + parts.query) if parts.query else ""),
             "cookie": self.headers.get("Cookie", ""), "referer": self.headers.get("Referer", ""), "ua": self.headers.get("User-Agent", "")})
        if path in PAGES:
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Set-Cookie", "sessione=abc123; Path=/")
            self.end_headers()
            if not head:
                self.wfile.write(PAGES[path].encode())
        elif path == "/robots.txt":
            self.send_bytes(ROBOTS, "text/plain", head=head)
        elif path in GRAB:
            self.send_bytes(GRAB[path].encode(), "text/html; charset=utf-8", head=head)
        elif path == "/grab/cal":
            month = int(urllib.parse.parse_qs(parts.query).get("m", ["1"])[0])
            body = f'<!doctype html><title>Mese {month}</title><a href="cal?m={month + 1}">mese dopo</a>'.encode()
            self.send_bytes(body, "text/html; charset=utf-8", head=head)
        elif path == "/grab/style.css":
            self.send_bytes(GRAB_CSS, "text/css", head=head)
        elif path == "/grab/busy.zip" and path not in busy_seen:
            busy_seen.add(path)  # the first request is refused, the next one waits the second it asks for
            self.send_response(429)
            self.send_header("Retry-After", "1")
            self.send_header("Content-Length", "0")
            self.end_headers()
        elif path.startswith("/grab/"):
            name = path[path.rfind("/") + 1:]
            ext = name[name.rfind("."):]
            self.send_bytes(payload(name, 2048 if "/f/" in path else 16 * 1024), TYPES.get(ext, "application/octet-stream"),
                            {"ETag": f'"{name}"'}, head=head)
        elif path.startswith("/files/"):
            name = path[7:]
            ext = name[name.rfind("."):]
            self.send_bytes(payload(name, 2 * 1024 * 1024 if ext in (".zip", ".exe") else 64 * 1024), TYPES.get(ext, "application/octet-stream"),
                            head=head)
        elif path.startswith("/page-"):
            self.send_bytes(b"<!doctype html><title>pagina</title>", "text/html", head=head)
        else:
            self.send_response(404)
            self.send_header("Content-Length", "0")
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
