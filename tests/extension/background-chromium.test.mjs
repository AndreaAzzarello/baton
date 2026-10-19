// Chromium: a download waits in onDeterminingFilename while the app is asked; it goes back to the browser if the app fails.
import { test, mock } from "node:test";
import assert from "node:assert/strict";
import { createFakeChrome, settle } from "./fake-chrome.mjs";

let reply = async () => ({ ok: true });
const fake = createFakeChrome({ nativeReply: (message) => reply(message) });
globalThis.chrome = fake.chrome;
await import("../../extension/background.js");
const determine = fake.listeners["downloads.onDeterminingFilename"][0];

const download = (over = {}) => ({
  id: 7,
  url: "https://files.test/archive.zip",
  finalUrl: "https://files.test/archive.zip",
  filename: "archive.zip",
  mime: "application/zip",
  totalBytes: 8 * 1024 * 1024,
  startTime: new Date().toISOString(),
  incognito: false,
  ...over,
});

function run(item) {
  fake.calls.length = 0;
  let suggested = 0;
  const keepsListening = determine(item, () => suggested++);
  return { keepsListening, suggested: () => suggested };
}

const named = (name) => fake.calls.filter((c) => c[0] === name);

test("accepted by the app: the browser's copy is cancelled and erased, never released", async () => {
  reply = async () => ({ ok: true });
  const r = run(download());
  assert.equal(r.keepsListening, true);
  await settle();
  assert.equal(named("native")[0][1].type, "add");
  assert.equal(named("native")[0][1].version, 3);
  assert.equal(named("native")[0][1].download.url, "https://files.test/archive.zip");
  assert.equal(named("cancel").length, 1);
  assert.equal(named("erase").length, 1);
  assert.equal(r.suggested(), 0);
});

test("app missing: the download continues in the browser, nothing cancelled, the user is told", async () => {
  reply = async () => {
    throw new Error("Specified native messaging host not found.");
  };
  const r = run(download());
  await settle();
  assert.equal(r.suggested(), 1);
  assert.equal(named("cancel").length, 0);
  assert.equal(named("notify").length, 1);
  assert.ok(named("badge").some((c) => c[1].text === "!"));
});

test("app refuses the download: back to the browser", async () => {
  reply = async () => ({ ok: false, error: "no" });
  const r = run(download());
  await settle();
  assert.equal(r.suggested(), 1);
  assert.equal(named("cancel").length, 0);
});

test("app silent: after the timeout the browser keeps the download", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    reply = () => new Promise(() => {}); // never answers
    const r = run(download());
    await settle();
    assert.equal(r.suggested(), 0, "still waiting for the app");
    mock.timers.tick(15_000);
    await settle();
    assert.equal(r.suggested(), 1);
    assert.equal(named("cancel").length, 0);
    assert.ok(named("notify")[0][1].includes("fallbackNotice"));
  } finally {
    mock.timers.reset();
  }
});

test("not matching the rules: released at once, the app is not asked", async () => {
  const r = run(download({ filename: "page.html", url: "https://files.test/page.html", finalUrl: "https://files.test/page.html", mime: "text/html" }));
  await settle();
  assert.equal(r.suggested(), 1);
  assert.equal(named("native").length, 0);
});

test("a form download is repeated as POST with the form's fields", async () => {
  reply = async () => ({ ok: true });
  const before = fake.listeners["webRequest.onBeforeRequest"][0];
  const headers = fake.listeners["webRequest.onBeforeSendHeaders"][0];
  before({ requestId: "r1", method: "POST", tabId: 3, url: "https://files.test/export", documentUrl: "https://files.test/form",
    requestBody: { formData: { year: ["2026"], format: ["zip"] } } });
  headers({ requestId: "r1", method: "POST", tabId: 3, requestHeaders: [{ name: "Content-Type", value: "application/x-www-form-urlencoded" }] });
  await settle();
  const r = run(download({ url: "https://files.test/export", finalUrl: "https://files.test/export", filename: "export.zip" }));
  await settle();
  const sent = named("native")[0][1].download;
  assert.equal(sent.method, "POST");
  assert.equal(sent.body, "year=2026&format=zip");
  assert.equal(sent.bodyType, "application/x-www-form-urlencoded");
  assert.equal(sent.referrer, "https://files.test/form");
  assert.equal(r.suggested(), 0);
});

test("SameSite: a form from another site, or from a page that hides its referrer, gets no Strict cookies (B1)", async () => {
  reply = async () => ({ ok: true });
  const original = chrome.cookies.getAll;
  chrome.cookies.getAll = async () => [
    { name: "strict", value: "s", domain: "files.test", path: "/", secure: true, hostOnly: true, sameSite: "strict" },
    { name: "lax", value: "l", domain: "files.test", path: "/", secure: true, hostOnly: true, sameSite: "lax" },
    { name: "none", value: "n", domain: "files.test", path: "/", secure: true, hostOnly: true, sameSite: "no_restriction" },
  ];
  try {
    const before = fake.listeners["webRequest.onBeforeRequest"][0];
    const sent = async (id, extra, file) => {
      before({ requestId: id, method: "POST", tabId: 3, url: "https://files.test/export", requestBody: { formData: { a: ["1"] } }, ...extra });
      await settle();
      run(download({ url: "https://files.test/export", finalUrl: "https://files.test/export", filename: file, referrer: "" }));
      await settle();
      return named("native")[0][1].download;
    };
    // Chromium gives the origin as "initiator", even when the page sends no Referer.
    const same = await sent("s1", { initiator: "https://www.files.test" }, "same.zip");
    assert.deepEqual(same.cookies.map((c) => c.name), ["strict", "lax", "none"]);
    assert.equal(same.referrer, "https://www.files.test");
    const cross = await sent("s2", { initiator: "https://attacker.test" }, "cross.zip");
    assert.deepEqual(cross.cookies.map((c) => c.name), ["none"]); // a cross-site POST: neither Strict nor Lax
    // Without an initiator the tab's page (known to the browser) stands in: here another site.
    const fromTab = await sent("s3", {}, "tab.zip");
    assert.equal(fromTab.referrer, "https://page.test/");
    assert.deepEqual(fromTab.cookies.map((c) => c.name), ["none"]);
    // A plain download from a page that sends no Referer: unknown origin, treated as another site (top-level GET: Lax too).
    run(download({ url: "https://files.test/plain.zip", finalUrl: "https://files.test/plain.zip", filename: "plain.zip", referrer: "" }));
    await settle();
    const hidden = named("native")[0][1].download;
    assert.equal(hidden.referrer, null);
    assert.deepEqual(hidden.cookies.map((c) => c.name), ["lax", "none"]);
  } finally {
    chrome.cookies.getAll = original;
  }
});

test("a form's data is dropped as soon as the app has it (B5)", async () => {
  reply = async () => ({ ok: true });
  const stored = Object.keys(await chrome.storage.session.get(null)).filter((k) => k.startsWith("post"));
  assert.deepEqual(stored, []); // the form of the previous test went with its download
  const r = run(download({ url: "https://files.test/export", finalUrl: "https://files.test/export", filename: "export-2.zip" }));
  await settle();
  const sent = named("native")[0][1].download;
  assert.equal(sent.method, undefined); // not repeated a second time: a plain GET now
  assert.equal(r.suggested(), 0);
});

test("forms left by a worker that was stopped are swept by the alarm, which every stored form sets (B5)", async () => {
  fake.calls.length = 0;
  const before = fake.listeners["webRequest.onBeforeRequest"][0];
  before({ requestId: "a1", method: "POST", tabId: 3, url: "https://files.test/login", requestBody: { formData: { password: ["x"] } } });
  await settle();
  assert.deepEqual(named("alarm").map((c) => c[1]), ["forget-forms"]);
  assert.ok(named("alarm")[0][2].delayInMinutes >= 0.5); // the shortest alarm browsers allow
  // As if the worker had been stopped before its timer ran: entries older than the limit, the timer gone with it.
  const old = Date.now() - 60_000;
  await chrome.storage.session.set({ "post:z9": { url: "https://files.test/login", time: old, formData: { pwd: ["y"] } },
    "posttype:z9": { type: "application/x-www-form-urlencoded", time: old } });
  fake.listeners["alarms.onAlarm"][0]({ name: "forget-forms" });
  await settle();
  const left = Object.keys(await chrome.storage.session.get(null));
  assert.ok(!left.includes("post:z9") && !left.includes("posttype:z9"));
  assert.ok(left.includes("post:a1")); // a recent form stays for its download
  await chrome.storage.session.remove(["post:a1", "posttype:a1"]);
});

test("forms of private windows are never kept (B5)", async () => {
  const original = chrome.tabs.get;
  chrome.tabs.get = async (id) => ({ id, url: "https://files.test/", incognito: id === 9 });
  try {
    const listener = fake.listeners["webRequest.onBeforeRequest"][0];
    listener({ requestId: "p1", method: "POST", tabId: 9, url: "https://files.test/login", requestBody: { formData: { password: ["hunter2"] } } });
    listener({ requestId: "p2", method: "POST", tabId: 3, url: "https://files.test/login", requestBody: { formData: { password: ["x"] } }, incognito: true });
    await settle();
    const stored = Object.keys(await chrome.storage.session.get(null)).filter((k) => k.startsWith("post"));
    assert.deepEqual(stored, []);
  } finally {
    chrome.tabs.get = original;
  }
});

test("a form that uploads a file is left to the browser", async () => {
  const before = fake.listeners["webRequest.onBeforeRequest"][0];
  before({ requestId: "r2", method: "POST", tabId: 3, url: "https://files.test/convert", requestBody: { raw: [{ bytes: new ArrayBuffer(8) }] } });
  await settle();
  const r = run(download({ url: "https://files.test/convert", finalUrl: "https://files.test/convert", filename: "out.zip" }));
  await settle();
  assert.equal(r.suggested(), 1);
  assert.equal(named("native").length, 0);
});

test("Alt+click marks the link so its download stays in the browser", async () => {
  const onMessage = fake.listeners["runtime.onMessage"][0];
  const answer = await new Promise((resolve) =>
    onMessage({ type: "skip", url: "https://files.test/alt.zip" }, { id: "test-extension", tab: { id: 3, url: "https://files.test/" } }, resolve));
  assert.equal(answer.ok, true);
  const r = run(download({ url: "https://files.test/alt.zip", finalUrl: "https://files.test/alt.zip", filename: "alt.zip" }));
  await settle();
  assert.equal(r.suggested(), 1);
  assert.equal(named("native").length, 0);
});

test("the content script can only ask about streams the tab really loaded", async () => {
  const onMessage = fake.listeners["runtime.onMessage"][0];
  const ask = (message) => new Promise((resolve) =>
    onMessage(message, { id: "test-extension", tab: { id: 4, url: "https://videos.test/watch", title: "Clip" } }, resolve));
  const refused = await ask({ type: "probe", url: "https://internal.test/admin.m3u8" });
  assert.equal(refused.ok, false);

  fake.listeners["webRequest.onHeadersReceived"][0]({ tabId: 4, statusCode: 200, type: "xmlhttprequest", url: "https://cdn.videos.test/master.m3u8",
    responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }] });
  await settle();
  reply = async (message) => ({ ok: true, protocol: 2, media: { kind: "hls", variants: [], echo: message } });
  const allowed = await ask({ type: "probe", url: "https://cdn.videos.test/master.m3u8" });
  assert.equal(allowed.ok, true);
  assert.equal(allowed.media.echo.type, "probeMedia");
  assert.equal(allowed.media.echo.media.pageUrl, "https://videos.test/watch");
});

test("capture this site: the page, its title and cookies go to the app", async () => {
  fake.calls.length = 0;
  reply = async () => ({ ok: true, protocol: 3 });
  fake.chrome.cookies.getAll = async () => [{ name: "s", value: "1", domain: "books.test", path: "/", secure: true, hostOnly: true }];
  const click = fake.listeners["contextMenus.onClicked"][0];
  await click({ menuItemId: "dm-grab", pageUrl: "https://books.test/index.html#top" }, { id: 5, url: "https://books.test/index.html", title: "Books", incognito: false });
  await settle();
  const sent = named("native")[0][1];
  assert.equal(sent.type, "grab");
  assert.equal(sent.version, 3);
  assert.equal(sent.grab.url, "https://books.test/index.html");
  assert.equal(sent.grab.title, "Books");
  assert.equal(sent.grab.cookies[0].value, "1");
  fake.chrome.cookies.getAll = async () => [];
});

test("capture this site: never from a private window, and an old app is named", async () => {
  fake.calls.length = 0;
  const click = fake.listeners["contextMenus.onClicked"][0];
  await click({ menuItemId: "dm-grab", pageUrl: "https://secret.test/" }, { id: 6, url: "https://secret.test/", incognito: true });
  await settle();
  assert.equal(named("native").length, 0);
  assert.ok(named("notify")[0][1].startsWith("errorGrabIncognito"));

  fake.calls.length = 0;
  reply = async () => ({ ok: false, error: "Unknown message type: grab", protocol: 2 });
  await click({ menuItemId: "dm-grab", pageUrl: "https://books.test/" }, { id: 5, url: "https://books.test/", incognito: false });
  await settle();
  assert.ok(named("notify")[0][1].startsWith("errorGrabOutdated"));
});

test("the chosen audio language and subtitles reach the app with the video", async () => {
  const onMessage = fake.listeners["runtime.onMessage"][0];
  fake.listeners["webRequest.onHeadersReceived"][0]({ tabId: 8, statusCode: 200, type: "xmlhttprequest", url: "https://cdn.videos.test/lang.m3u8",
    responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }] });
  await settle();
  fake.calls.length = 0;
  reply = async () => ({ ok: true, protocol: 3 });
  const answer = await new Promise((resolve) => onMessage({ type: "addMedia", url: "https://cdn.videos.test/lang.m3u8", variantId: "hls:0", label: "720p",
    audioId: "it|Italiano", subtitleIds: ["en|English"], subtitleMode: "srt", extra: "ignored" },
    { id: "test-extension", tab: { id: 8, url: "https://videos.test/watch", title: "Clip" } }, resolve));
  assert.equal(answer.ok, true);
  const media = named("native")[0][1].media;
  assert.equal(media.audioId, "it|Italiano");
  assert.deepEqual(media.subtitleIds, ["en|English"]);
  assert.equal(media.subtitleMode, "srt");
  assert.equal(media.extra, undefined);
});
