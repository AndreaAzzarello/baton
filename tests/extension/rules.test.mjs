// Interception rules, form encoding, media detection and connection states (node --test tests/extension).
import { test } from "node:test";
import assert from "node:assert/strict";
import "../../extension/rules.js";

const R = globalThis.DMRules;
const now = Date.parse("2026-10-14T12:00:00Z");
const settings = (over = {}) => R.normalizeSettings({ minSizeMB: 1, ...over });
const item = (over = {}) => ({
  url: "https://files.test/get?id=1",
  finalUrl: "https://cdn.files.test/archive.zip",
  filename: "C:\\Users\\x\\Downloads\\archive.zip",
  mime: "application/zip",
  totalBytes: 5 * 1024 * 1024,
  incognito: false,
  startTime: new Date(now - 1000).toISOString(),
  referrer: "https://files.test/page",
  ...over,
});

test("a listed type larger than the minimum is intercepted", () => {
  assert.deepEqual(R.decide(item(), settings(), {}, "me", now), { intercept: true, reason: "type" });
});

test("context-menu mode never intercepts", () => {
  assert.equal(R.decide(item(), settings({ mode: "menu" }), {}, "me", now).reason, "menu-only");
});

test("0.5's 'intercept: false' becomes the context-menu mode", () => {
  assert.equal(R.normalizeSettings({ intercept: false }).mode, "menu");
  assert.equal(R.normalizeSettings({ intercept: true }).mode, "auto");
  assert.equal(R.normalizeSettings({ intercept: false, mode: "auto" }).mode, "auto");
});

test("files smaller than the minimum stay in the browser; unknown sizes are intercepted", () => {
  assert.equal(R.decide(item({ totalBytes: 200 * 1024 }), settings(), {}, "me", now).reason, "small");
  assert.equal(R.decide(item({ totalBytes: -1 }), settings(), {}, "me", now).intercept, true);
  assert.equal(R.decide(item({ totalBytes: 200 * 1024 }), settings({ minSizeMB: 0 }), {}, "me", now).intercept, true);
});

test("the type is found in the file name, the address or the MIME type", () => {
  const noName = { filename: "", finalUrl: "https://x.test/download.php", url: "https://x.test/download.php" };
  assert.equal(R.decide(item({ ...noName, mime: "application/x-7z-compressed" }), settings(), {}, "me", now).intercept, true);
  assert.equal(R.decide(item({ ...noName, mime: "text/html" }), settings(), {}, "me", now).reason, "type-not-listed");
  assert.equal(R.decide(item({ filename: "report.pdf", mime: "application/pdf", finalUrl: "https://x.test/r" }), settings(), {}, "me", now).reason,
    "type-not-listed"); // pdf is not in the default list
});

test("never-sites win over always-sites; always-sites ignore type and size", () => {
  const s = settings({ alwaysSites: "files.test", neverSites: "" });
  assert.deepEqual(R.decide(item({ filename: "x.html", mime: "text/html", totalBytes: 10 }), s, {}, "me", now),
    { intercept: true, reason: "always-site" });
  const both = settings({ alwaysSites: "files.test", neverSites: "*.files.test, other.test" });
  assert.equal(R.decide(item(), both, {}, "me", now).reason, "never-site");
  // The page that started the download counts too.
  assert.equal(R.decide(item({ finalUrl: "https://elsewhere.test/a.zip" }), settings({ neverSites: "files.test" }), {}, "me", now).reason,
    "never-site");
});

test("site lists accept addresses, wildcards and separators", () => {
  assert.deepEqual(R.parseSites("https://A.test/path, *.b.test;c.test\n d.test"), ["a.test", "b.test", "c.test", "d.test"]);
  assert.ok(R.hostMatches("dl.b.test", "b.test"));
  assert.ok(!R.hostMatches("notb.test", "b.test"));
});

test("Alt+click skips that link once, until the mark expires", () => {
  const skips = { "https://files.test/get?id=1": now + 5000 };
  assert.equal(R.decide(item(), settings(), skips, "me", now).reason, "skip");
  assert.equal(R.decide(item(), settings(), skips, "me", now + 6000).intercept, true);
  assert.deepEqual(R.pruneSkips(skips, now + 6000), {});
});

test("private windows, own downloads, browser-only addresses and restored downloads are left alone", () => {
  assert.equal(R.decide(item({ incognito: true }), settings(), {}, "me", now).reason, "incognito");
  assert.equal(R.decide(item({ byExtensionId: "me" }), settings(), {}, "me", now).reason, "own");
  assert.equal(R.decide(item({ url: "blob:https://x.test/1", finalUrl: "" }), settings(), {}, "me", now).reason, "scheme");
  assert.equal(R.decide(item({ startTime: new Date(now - 120000).toISOString() }), settings(), {}, "me", now).reason, "old");
});

test("forms: fields are URL-encoded and keep their encoding; uploads and raw bodies are refused", () => {
  const formData = { q: ["a b&c"], tag: ["x", "y"], "é": ["€"] };
  assert.deepEqual(R.encodeForm({ formData }, "application/x-www-form-urlencoded; charset=UTF-8"), {
    body: "q=a%20b%26c&tag=x&tag=y&%C3%A9=%E2%82%AC",
    bodyType: "application/x-www-form-urlencoded",
  });
  assert.equal(R.encodeForm({ formData }, "multipart/form-data; boundary=----x").bodyType, "multipart/form-data");
  assert.equal(R.encodeForm({ raw: [{ bytes: new ArrayBuffer(4) }] }, "text/plain"), null);
  assert.equal(R.encodeForm({ formData }, "text/plain"), null);
  assert.equal(R.encodeForm(null, ""), null);
});

test("media: manifests by type or extension, whole files only from media elements", () => {
  assert.equal(R.classifyMedia("https://v.test/master.m3u8?t=1", "text/plain", "xmlhttprequest", 900), "hls");
  assert.equal(R.classifyMedia("https://v.test/play", "application/vnd.apple.mpegurl", "xmlhttprequest", 900), "hls");
  assert.equal(R.classifyMedia("https://v.test/manifest.mpd", "", "xmlhttprequest", 900), "dash");
  assert.equal(R.classifyMedia("https://v.test/movie.mp4", "video/mp4", "media", 50e6), "file");
  // Segments fetched by a player are not offered on their own.
  assert.equal(R.classifyMedia("https://v.test/seg1.m4s", "video/mp4", "xmlhttprequest", 2e6), null);
  assert.equal(R.classifyMedia("https://v.test/seg1.ts", "video/mp2t", "media", 2e6), null);
  assert.equal(R.classifyMedia("https://v.test/click.mp3", "audio/mpeg", "media", 20000), null); // a short sound
});

test("sites whose terms forbid downloads are recognized, subdomains included", () => {
  assert.ok(R.isRestricted("https://www.youtube.com/watch?v=x"));
  assert.ok(R.isRestricted("https://rr3---sn.googlevideo.com/videoplayback"));
  assert.ok(!R.isRestricted("https://notyoutube.com/"));
  assert.ok(!R.isRestricted("https://devstreaming-cdn.apple.com/videos/x.m3u8"));
});

test("connection states for the check page", () => {
  assert.equal(R.connectionState({ ok: true, running: true, protocol: 3 }), "running");
  assert.equal(R.connectionState({ ok: true, running: false, protocol: 3 }), "ready");
  assert.equal(R.connectionState({ ok: true, running: true, protocol: 2 }), "outdated"); // app 0.6: no site capture
  assert.equal(R.connectionState({ ok: false, error: "Tipo di messaggio sconosciuto: hello" }), "outdated"); // app 0.5
  assert.equal(R.connectionState({ ok: true, running: true }), "outdated");
  assert.equal(R.connectionState(null, new Error("Specified native messaging host not found.")), "missing");
  assert.equal(R.connectionState(null, new Error("No such native application com.downloadmanager.host")), "missing"); // Firefox
  assert.equal(R.connectionState(null, new Error("Access to the specified native messaging host is forbidden.")), "forbidden");
  assert.equal(R.connectionState(null, new Error("the app did not answer in time")), "error");
});

test("sizes are formatted for people", () => {
  assert.equal(R.formatSize(0, "en"), "");
  assert.equal(R.formatSize(1536, "en"), "1.5 KB");
  assert.equal(R.formatSize(734003200, "it"), "700 MB");
});

test("site capture: only http(s) pages, without the fragment", () => {
  assert.equal(R.grabTarget("https://books.test/catalogue/page-1.html#top"), "https://books.test/catalogue/page-1.html");
  assert.equal(R.grabTarget("file:///C:/Users/"), null);
  assert.equal(R.grabTarget("chrome://settings"), null);
  assert.equal(R.grabTarget("not a url"), null);
  assert.equal(R.grabTarget("https://x.test/" + "a".repeat(9000)), null);
});

test("video panel choices: strings only, at most 20 subtitles, a known format", () => {
  assert.deepEqual(R.mediaChoice({}), {});
  assert.deepEqual(R.mediaChoice({ audioId: "it|Italiano", subtitleIds: [] }), { audioId: "it|Italiano" });
  assert.deepEqual(R.mediaChoice({ subtitleIds: ["en", "en", 7, "", "it"], subtitleMode: "exe" }),
    { subtitleIds: ["en", "it"], subtitleMode: "vtt" });
  assert.equal(R.mediaChoice({ subtitleIds: Array.from({ length: 30 }, (_, i) => `s${i}`), subtitleMode: "srt" }).subtitleIds.length, 20);
  assert.equal(R.mediaChoice({ subtitleIds: ["en"], subtitleMode: "embed" }).subtitleMode, "embed");
  assert.equal(R.mediaChoice({ audioId: { toString: () => "x" } }).audioId, undefined);
});

test("sites as SameSite compares them: scheme and registrable domain", () => {
  assert.equal(R.siteOf("https://www.shop.example.com/a"), "https://example.com");
  assert.equal(R.siteOf("https://news.bbc.co.uk/x"), "https://bbc.co.uk");
  assert.equal(R.siteOf("https://alice.github.io/"), "https://alice.github.io");
  assert.equal(R.siteOf("http://127.0.0.1:8080/"), "http://127.0.0.1");
  assert.ok(R.sameSite("https://a.example.com/", "https://b.example.com/x"));
  assert.ok(!R.sameSite("https://example.com/", "http://example.com/"));   // schemeful: http is another site
  assert.ok(!R.sameSite("https://bank.example/", "https://evil.test/"));
  assert.ok(!R.sameSite("https://alice.github.io/", "https://mallory.github.io/"));
  assert.equal(R.siteOf("https://victim.s3.amazonaws.com/f"), "https://victim.s3.amazonaws.com"); // a three-label suffix
  assert.ok(!R.sameSite("https://attacker.s3.amazonaws.com/", "https://victim.s3.amazonaws.com/"));
  assert.equal(R.siteOf("https://co.uk/"), "https://co.uk");
  assert.ok(!R.sameSite("not a url", "not a url"));
});

test("cookies: only those the browser itself would send (B1)", () => {
  const strict = { name: "s", sameSite: "strict", secure: true };
  const lax = { name: "l", sameSite: "lax", secure: true };
  const unspecified = { name: "u", sameSite: "unspecified", secure: false };
  const none = { name: "n", sameSite: "no_restriction", secure: true };
  const noneInsecure = { name: "x", sameSite: "no_restriction", secure: false };
  const all = [strict, lax, unspecified, none, noneInsecure];
  const sent = (ctx) => all.filter((c) => R.cookieAllowed(c, ctx)).map((c) => c.name).join("");
  assert.equal(sent({ crossSite: false }), "slunx");                               // same site: all
  assert.equal(sent({ crossSite: true, topLevel: true, method: "GET" }), "lun");   // cross-site link: no Strict
  assert.equal(sent({ crossSite: true, topLevel: true, method: "POST" }), "n");    // cross-site form: only None
  assert.equal(sent({ crossSite: true, topLevel: false, method: "GET" }), "n");    // a page's video from elsewhere
});
