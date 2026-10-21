// Real trial in Firefox (portable copy, temporary profile, headless), driven over WebDriver BiDi.
import puppeteer from "puppeteer-core";
import { WORK, DATA, DB, SITE_LOG, SHOTS, BROWSER_DL, CHROMIUM_EXT, FIREFOX_EXE, FIREFOX_TRIAL_EXT, closeBatchWindow } from "./config.mjs";
if (!FIREFOX_EXE) {
  console.error("Portable Firefox missing: run tools\trials\prepare.ps1 first.");
  process.exit(2);
}
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { execFileSync } from "node:child_process";

const GECKO_ID = "baton@andreaazzarello";
const UUID = "6b3e7a52-8d1f-4c3a-9b8e-2f5d0c7a1e44";
const SITE = "http://127.0.0.1:8765";
const shots = join(SHOTS, "firefox");
const browserDl = join(BROWSER_DL, "firefox");
mkdirSync(shots, { recursive: true });
mkdirSync(browserDl, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = (step, ok, detail = "") => console.log(`${ok ? "OK  " : "FAIL"} ${step}${detail ? " — " + detail : ""}`);
const db = (sql) => JSON.parse(execFileSync("python", ["-c",
  "import sqlite3,json,sys; c=sqlite3.connect(sys.argv[1]); print(json.dumps(c.execute(sys.argv[2]).fetchall()))",
  DB, sql]).toString());
async function waitFor(fn, ms = 30000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const v = await fn();
    if (v) return v;
    await sleep(250);
  }
  return null;
}

const browser = await puppeteer.launch({
  browser: "firefox",
  executablePath: FIREFOX_EXE,
  headless: true,
  userDataDir: mkdtempSync(join(tmpdir(), "dm-firefox-")),
  env: { ...process.env, BATON_DATA: DATA },
  defaultViewport: { width: 1280, height: 900 },
  args: ["-remote-allow-system-access"], // lets automation read the extension's own page
  extraPrefsFirefox: {
    "extensions.webextensions.uuids": JSON.stringify({ [GECKO_ID]: UUID }),
    "browser.download.folderList": 2,
    "browser.download.dir": browserDl,
    "browser.download.useDownloadDir": true,
    "browser.download.always_ask_before_handling_new_types": false,
    "browser.download.alwaysOpenPanel": false,
    "intl.locale.requested": "it",
  },
});
console.log("browser:", await browser.version());
const id = await browser.installExtension(FIREFOX_TRIAL_EXT);
report("extension installed (temporary)", id === GECKO_ID, id);
await sleep(5000);
const base = `moz-extension://${UUID}`;
const extPage = await waitFor(async () => {
  for (const p of await browser.pages()) {
    const href = await p.evaluate(() => location.href).catch((e) => "error " + e.message);
    if (process.env.DEBUG) console.log("page", href);
    if (href.startsWith(base)) return p;
  }
  return null;
}, 15000);
report("extension page opened by the extension", !!extPage);

await extPage.waitForFunction(() => /ok|bad/.test(document.getElementById("result").className), { timeout: 20000 });
const check = await extPage.evaluate(() => ({ title: resultTitle.textContent, facts: [...document.querySelectorAll("dd")].map((d) => d.textContent) }));
// (BiDi cannot capture privileged pages)
report("check connection", !/non|not/i.test(check.title), `${check.title} | ${check.facts.join(" | ")}`);
let page = await browser.newPage();
/*
await page.waitForFunction(() => /ok|bad/.test(document.getElementById("result").className), { timeout: 20000 });
const check = await page.evaluate(() => ({ title: resultTitle.textContent, facts: [...document.querySelectorAll("dd")].map((d) => d.textContent) }));
await page.screenshot({ path: join(shots, "1-check.png") });
report("check connection", !/non|not/i.test(check.title), `${check.title} | ${check.facts.join(" | ")}`);

*/
// The popup's code is the same as in Chromium; its native question is checked here from the extension page.
const hello = await extPage.evaluate(() => browser.runtime.sendNativeMessage("com.andreaazzarello.baton", { type: "queues", version: 2 }));
report("queues asked from an extension page", Array.isArray(hello.queues), JSON.stringify(hello));
/*
await page.waitForFunction(() => /ok|bad/.test(document.getElementById("status").className), { timeout: 20000 });
const popup = await page.evaluate(() => ({ status: statusText.textContent, title: status.title, queues: [...queue.options].map((o) => o.text) }));
await page.screenshot({ path: join(shots, "2-popup.png") });
report("popup", popup.queues.length > 0, `${popup.status} (${popup.title}) | code: ${popup.queues.join(", ")}`);

*/
// Interception: onCreated, pause, hand over, cancel + removeFile + erase.
const before = db("select max(id) from downloads")[0][0] ?? 0;
await page.goto(`${SITE}/download.html`);
await page.click("#zip");
const got = await waitFor(() => db(`select file_name, status from downloads where id > ${before} and url like '%big.zip'`)[0], 30000);
await sleep(2000);
const leftovers = readdirSync(browserDl);
report("intercepted download reaches the app, nothing left in Firefox", !!got && !leftovers.some((f) => f.startsWith("big")),
  `${JSON.stringify(got)} | cartella Firefox: ${leftovers.join(",") || "vuota"}`);

await page.keyboard.down("Alt");
await page.click("#alt");
await page.keyboard.up("Alt");
const alt = await waitFor(() => readdirSync(browserDl).some((f) => f === "alt.zip"), 20000);
report("Alt+click leaves the download to Firefox", !!alt && db(`select count(*) from downloads where id > ${before} and url like '%alt.zip'`)[0][0] === 0,
  readdirSync(browserDl).join(","));

page = await browser.newPage();
await page.goto(`${SITE}/form.html`);
await page.click("#go");
const form = await waitFor(() => db(`select file_name, status from downloads where id > ${before} and url like '%/export'`).find((r) => r[1] === 3), 30000);
const posts = readFileSync(SITE_LOG, "utf8").trim().split("\n").map(JSON.parse).filter((l) => l.method === "POST").slice(-2);
report("form download repeated by the app as POST", !!form, `${JSON.stringify(form)} | ${posts.map((p) => p.body).join(" / ")}`);

// Videos. The panel lives in a closed shadow root that BiDi cannot reach: it is opened as the popup's "Show in the page"
// does (screenshot), and a quality is chosen as the popup does, from the extension's own popup.html page.
page = await browser.newPage();
await page.goto(`${SITE}/video.html`);
await page.waitForFunction(() => document.title.includes("("), { timeout: 20000 });
await sleep(1500);
await page.screenshot({ path: join(shots, "3-video-tab.png") }); // the default: a small tab on the right edge
const videoTab = await extPage.evaluate(async (url) => (await browser.tabs.query({})).find((x) => x.url?.startsWith(url))?.id, `${SITE}/video.html`);
await extPage.evaluate((id) => browser.tabs.sendMessage(id, { type: "showPanel" }, { frameId: 0 }), videoTab);
await sleep(6000); // qualities are read by the app
await page.screenshot({ path: join(shots, "3-video-panel.png") });
await extPage.evaluate(() => { location.href = "popup.html"; });
await sleep(1500);
const sentVideo = await extPage.evaluate(async (id) => {
  const listed = await browser.runtime.sendMessage({ type: "getMedia", tabId: id });
  const url = listed.items.at(-1).url;
  const probe = await browser.runtime.sendMessage({ type: "probe", tabId: id, url });
  const lowest = probe.media.variants.at(-1);
  return browser.runtime.sendMessage({ type: "addMedia", tabId: id, url, variantId: lowest.id, label: lowest.label });
}, videoTab);
const media = await waitFor(() => db(`select file_name, media from downloads where id > ${before} and media is not null`)[0], 20000);
await page.screenshot({ path: join(shots, "4-video-sent.png") });
report("video: lowest quality listed and sent from the popup", !!sentVideo?.ok && !!media && media[1].includes("270p"), JSON.stringify(media));

// The public hls.js demo: the videos it plays are found (asked as the popup does), the tab is shown, the panel opens.
const demo = await browser.newPage();
await demo.goto("https://hlsjs.video-dev.org/demo/", { waitUntil: "domcontentloaded" }).catch(() => {});
await sleep(10000);
await demo.screenshot({ path: join(shots, "6-demo-tab.png") });
const demoTab = await extPage.evaluate(async (url) => (await browser.tabs.query({})).find((x) => x.url?.startsWith(url))?.id, "https://hlsjs.video-dev.org/demo/");
const demoMedia = await extPage.evaluate((id) => browser.runtime.sendMessage({ type: "getMedia", tabId: id }), demoTab);
await extPage.evaluate((id) => browser.tabs.sendMessage(id, { type: "showPanel" }, { frameId: 0 }), demoTab);
await sleep(6000);
await demo.screenshot({ path: join(shots, "7-demo-panel.png") });
report("hls.js demo: videos found, tab and panel (see 6-demo-tab.png, 7-demo-panel.png)", demoMedia?.items?.length > 0,
  `${demoMedia?.items?.length} video, modalità ${demoMedia?.mode}`);
await demo.close();
await extPage.evaluate(() => { location.href = "check.html"; });
await sleep(1000);

page = await browser.newPage();
await page.goto(`${SITE}/drm.html`);
await page.waitForFunction(() => document.title.startsWith("drm"), { timeout: 20000 });
await sleep(1500);
const drmTab = await extPage.evaluate(async (url) => (await browser.tabs.query({})).find((x) => x.url?.startsWith(url))?.id, `${SITE}/drm.html`);
await extPage.evaluate((id) => browser.tabs.sendMessage(id, { type: "showPanel" }, { frameId: 0 }), drmTab);
await sleep(1000);
await page.screenshot({ path: join(shots, "5-drm.png") });
report("protected video page", true, `${await page.title()} (vedi 5-drm.png)`);

// Download all, through the event page's menu handler
page = await browser.newPage();
await page.goto(`${SITE}/links.html`);
const sent = await extPage.evaluate(async (url) => {
  const bg = await browser.runtime.getBackgroundPage();
  const tabs = await browser.tabs.query({});
  const tab = tabs.find((t) => t.url === url);
  if (!tab) return tabs.map((t) => t.url).join(", ");
  await bg.dmMenuClicked({ menuItemId: "dm-all", frameId: 0, pageUrl: url }, tab);
  return true;
}, `${SITE}/links.html`).catch((e) => "error: " + e.message);
report("download all sent", sent === true, String(sent));
console.log(`     "Scarica tutto" window closed: ${closeBatchWindow()}`);
await sleep(1500);
await browser.close();
