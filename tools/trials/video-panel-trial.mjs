// The video panel in a real Chromium browser (Edge, Opera) on a public HLS demo:
//   node video-panel-trial.mjs <name> <browser exe> [page]     (default page: https://hlsjs.video-dev.org/demo/)
// A temporary profile, headless: the user's own browser windows are not touched and nothing takes the focus.
// Checks the ways it appears and goes: the tab (default), the panel opened from it, × until a different video, the "⋯"
// menu (minimize, hide on the page, never on the site), full screen, "only from the extension's menu" with the badge,
// and that it never sits on the player's controls. Screenshots, light and dark, in %TEMP%\dm-trials\shots\<name>-video.
import puppeteer from "puppeteer-core";
import { DATA, SHOTS, CHROMIUM_EXT } from "./config.mjs";
import { mkdtempSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const [name, exe, demo = "https://hlsjs.video-dev.org/demo/"] = process.argv.slice(2);
const ID = "fngmjmgaglmoebphcjohnhlaceogneno";
const OTHER = "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8";
const THIRD = "https://devstreaming-cdn.apple.com/videos/streaming/examples/bipbop_4x3/bipbop_4x3_variant.m3u8";
const shots = join(SHOTS, `${name}-video`);
mkdirSync(shots, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const report = (step, ok, detail = "") => {
  results.push({ step, ok, detail });
  console.log(`${ok ? "OK  " : "FAIL"} ${step}${detail ? " — " + detail : ""}`);
};
async function waitFor(fn, ms = 20000, step = 250) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

const browser = await puppeteer.launch({
  executablePath: exe,
  headless: true,
  pipe: true,
  enableExtensions: [CHROMIUM_EXT],
  userDataDir: mkdtempSync(join(tmpdir(), `dm-${name}-video-`)),
  env: { ...process.env, BATON_DATA: DATA },
  args: ["--no-first-run", "--no-default-browser-check", "--window-size=1280,900", "--autoplay-policy=no-user-gesture-required"],
  defaultViewport: { width: 1280, height: 900 },
});
console.log("browser:", await browser.version());
const worker = await (await browser.waitForTarget((t) => t.type() === "service_worker" && t.url().includes(ID), { timeout: 15000 })).worker();
const page = await browser.newPage();
const cdp = await page.createCDPSession();
const gesture = (expression) => cdp.send("Runtime.evaluate", { expression, userGesture: true, awaitPromise: true }).catch(() => null);

// ---- the closed shadow root, through the DevTools protocol (pierce), as a user's clicks would reach it
async function find(cls) {
  const { root } = await cdp.send("DOM.getDocument", { depth: -1, pierce: true });
  const found = [];
  (function walk(n) {
    const a = n.attributes ?? [];
    for (let i = 0; i < a.length; i += 2) if (a[i] === "class" && a[i + 1].split(/\s+/).includes(cls)) found.push(n.backendNodeId);
    for (const c of [...(n.children ?? []), ...(n.shadowRoots ?? [])]) walk(c);
  })(root);
  return found[0] ?? null;
}
async function box(cls) {
  const id = await find(cls);
  if (!id) return null;
  try {
    const { model } = await cdp.send("DOM.getBoxModel", { backendNodeId: id });
    const q = model.border;
    return model.width > 0 && model.height > 0 ? { left: q[0], top: q[1], right: q[4], bottom: q[5] } : null;
  } catch {
    return null; // not displayed
  }
}
const shown = async (cls) => Boolean(await box(cls));
async function click(cls) {
  const b = await box(cls);
  if (!b) return false;
  await page.mouse.click((b.left + b.right) / 2, (b.top + b.bottom) / 2);
  return true;
}
async function clickItem(act) {
  const { root } = await cdp.send("DOM.getDocument", { depth: -1, pierce: true });
  let id = null;
  (function walk(n) {
    const a = n.attributes ?? [];
    for (let i = 0; i < a.length; i += 2) if (a[i] === "data-act" && a[i + 1] === act) id = n.backendNodeId;
    for (const c of [...(n.children ?? []), ...(n.shadowRoots ?? [])]) walk(c);
  })(root);
  const { model } = await cdp.send("DOM.getBoxModel", { backendNodeId: id });
  const q = model.border;
  await page.mouse.click((q[0] + q[4]) / 2, (q[1] + q[5]) / 2);
}
const nothingShown = async () => !(await shown("bar")) && !(await shown("tab"));
const loadManifest = (url) => page.evaluate((u) => fetch(u).then(() => true, () => true), url);
const badge = () => worker.evaluate(async (url) => {
  const [tab] = await chrome.tabs.query({ url });
  return tab ? chrome.action.getBadgeText({ tabId: tab.id }) : "";
}, demo + "*");

// 1. The default: a small tab on the edge, once the page plays a stream
await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "light" }]);
await page.goto(demo, { waitUntil: "domcontentloaded" });
const tab = await waitFor(() => box("tab"), 30000);
await sleep(1500);
await page.screenshot({ path: join(shots, "1-tab.png") });
report("minimized tab appears (default)", Boolean(tab), JSON.stringify(tab));

// 2. Never on the player's controls (the last 64 px of a visible player)
const players = await page.evaluate(() => [...document.querySelectorAll("video")].map((v) => v.getBoundingClientRect()).filter((r) => r.width > 0)
  .map((r) => ({ left: r.left, right: r.right, top: r.top, bottom: r.bottom })));
const clear = (b) => players.every((p) => b.right <= p.left || b.left >= p.right || b.bottom <= p.bottom - 64 || b.top >= p.bottom);
report("off the player's controls", tab && clear(tab), `tab ${JSON.stringify(tab)} players ${JSON.stringify(players)}`);

// 3. The tab opens the panel with the qualities
await click("tab");
const qualities = await waitFor(async () => (await shown("panel")) && (await find("get")), 30000);
await page.screenshot({ path: join(shots, "2-panel-light.png") });
await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "dark" }]);
await sleep(300);
await page.screenshot({ path: join(shots, "3-panel-dark.png") });
await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "light" }]);
const bar = await box("bar");
report("the tab opens the panel, qualities listed", Boolean(qualities) && Boolean(bar) && clear(bar), JSON.stringify(bar));

// 4. × closes it for the tab; a different video brings it back
await page.keyboard.press("Escape"); // the panel first
await click("dismiss");
await sleep(500);
const closed = await nothingShown();
await loadManifest(OTHER);
const back = await waitFor(async () => (await shown("bar")) || (await shown("tab")), 15000);
report("× closes it; a different video brings it back", closed && Boolean(back), `closed ${closed}, back ${Boolean(back)}`);

// 5. "⋯": minimize to the tab
if (!(await shown("bar"))) await click("tab");
await waitFor(() => shown("bar"));
await click("more");
await waitFor(() => shown("menu"));
await page.screenshot({ path: join(shots, "4-menu-light.png") });
await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "dark" }]);
await sleep(300);
await page.screenshot({ path: join(shots, "5-menu-dark.png") });
await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "light" }]);
await clickItem("minimize");
await sleep(500);
report("⋯ › minimize: back to the tab", (await shown("tab")) && !(await shown("bar")));

// 6. "⋯": hide on this page, also when another video comes; back after a reload
await click("tab");
await waitFor(() => shown("bar"));
await click("more");
await waitFor(() => shown("menu"));
await clickItem("page");
await sleep(500);
const hidden = await nothingShown();
await loadManifest(THIRD);
await sleep(4000);
const stillHidden = await nothingShown();
await page.reload({ waitUntil: "domcontentloaded" });
const afterReload = await waitFor(() => box("tab"), 30000);
report("⋯ › hide on this page: stays hidden with a new video, back after a reload", hidden && stillHidden && Boolean(afterReload),
  `hidden ${hidden}, with a new video ${stillHidden}, after reload ${Boolean(afterReload)}`);

// 7. Full screen: hidden while it lasts
await gesture("document.querySelector('video').requestFullscreen()");
const inFull = await waitFor(async () => (await page.evaluate(() => Boolean(document.fullscreenElement))) && (await nothingShown()), 5000);
await gesture("document.exitFullscreen()");
const outOfFull = await waitFor(() => shown("tab"), 5000);
report("hidden in full screen", Boolean(inFull) && Boolean(outOfFull), `in full screen ${Boolean(inFull)}, after ${Boolean(outOfFull)}`);

// 8. "Only from the extension's menu": nothing in the page, the icon counts the videos
await worker.evaluate(() => chrome.storage.local.set({ mediaPanel: "menu" }));
const menuOnly = await waitFor(() => nothingShown(), 5000);
const count = await badge();
report("only from the extension's menu: nothing in the page, the count on the icon", Boolean(menuOnly) && Number(count) >= 1, `badge "${count}"`);
await worker.evaluate(() => chrome.storage.local.set({ mediaPanel: "always" }));
const always = await waitFor(() => shown("bar"), 5000);
await page.screenshot({ path: join(shots, "6-always.png") });
report("always visible: the button with × and ⋯", Boolean(always));

// 9. "⋯": never on this site (the list of sites without the button), then the list is put back
await click("more");
await waitFor(() => shown("menu"));
await clickItem("site");
await sleep(800);
const host = new URL(demo).hostname;
const listed = await worker.evaluate(async () => (await chrome.storage.local.get("mediaDisabledSites")).mediaDisabledSites ?? "");
await page.reload({ waitUntil: "domcontentloaded" });
await sleep(6000);
const never = await nothingShown();
report("⋯ › never on this site: listed, not shown after a reload", listed.includes(host) && never, `list "${listed}"`);
await worker.evaluate(() => chrome.storage.local.set({ mediaDisabledSites: "", mediaPanel: "minimized" }));

console.log(JSON.stringify(results));
await browser.close();
