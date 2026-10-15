// Real trial in a Chromium browser (Edge, Opera): node chromium-trial.mjs <name> <browser exe> [--batch]
// A temporary profile, headless: the user's own browser windows are not touched and nothing takes the focus.
import puppeteer from "puppeteer-core";
import { WORK, DATA, DB, SITE_LOG, SHOTS, BROWSER_DL, CHROMIUM_EXT, FIREFOX_EXE, FIREFOX_TRIAL_EXT, closeBatchWindow } from "./config.mjs";
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, existsSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { execFileSync } from "node:child_process";

const [name, exe] = process.argv.slice(2);
const ID = "fngmjmgaglmoebphcjohnhlaceogneno";
const SITE = "http://127.0.0.1:8765";
const shots = join(SHOTS, name);
mkdirSync(shots, { recursive: true });
const browserDl = join(BROWSER_DL, name);
mkdirSync(browserDl, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const report = (step, ok, detail = "") => {
  results.push({ step, ok, detail });
  console.log(`${ok ? "OK  " : "FAIL"} ${step}${detail ? " — " + detail : ""}`);
};

function db(sql) {
  return JSON.parse(execFileSync("python", ["-c",
    `import sqlite3,json,sys; c=sqlite3.connect(sys.argv[1]); print(json.dumps(c.execute(sys.argv[2]).fetchall()))`,
    DB, sql]).toString());
}
async function waitFor(fn, ms = 30000, step = 250) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

// The download folder is set in the profile, like a user would: DevTools' download override would bypass the
// extension's downloads API.
const profile = mkdtempSync(join(tmpdir(), `dm-${name}-`));
mkdirSync(join(profile, "Default"), { recursive: true });
writeFileSync(join(profile, "Default", "Preferences"), JSON.stringify({
  download: { default_directory: browserDl, prompt_for_download: false, directory_upgrade: true },
  savefile: { default_directory: browserDl },
}));
const browser = await puppeteer.launch({
  executablePath: exe,
  headless: true,
  pipe: true,
  enableExtensions: [CHROMIUM_EXT],
  userDataDir: profile,
  env: { ...process.env, DOWNLOADMANAGER_DATA: DATA },
  args: ["--no-first-run", "--no-default-browser-check", "--window-size=1280,900"],
  defaultViewport: { width: 1280, height: 900 },
});
const version = await browser.version();
console.log("browser:", version);

const worker = await (await browser.waitForTarget((t) => t.type() === "service_worker" && t.url().includes(ID), { timeout: 15000 })).worker();
report("extension loaded", !!worker, `service worker ${ID}`);

// 1. Connection check
const page = await browser.newPage();
await page.goto(`chrome-extension://${ID}/check.html`);
await page.waitForFunction(() => document.getElementById("result").className.includes("ok") || document.getElementById("result").className.includes("bad"), { timeout: 20000 });
const check = await page.evaluate(() => ({ title: resultTitle.textContent, facts: [...document.querySelectorAll("dd")].map((d) => d.textContent) }));
await page.screenshot({ path: join(shots, "1-check.png") });
report("check connection", check.title.length > 0 && !/non|not/i.test(check.title), `${check.title} | ${check.facts.join(" | ")}`);

// 2. Popup
await page.goto(`chrome-extension://${ID}/popup.html`);
await page.waitForFunction(() => document.getElementById("status").className.includes("ok") || document.getElementById("status").className.includes("bad"), { timeout: 20000 });
const popup = await page.evaluate(() => ({ status: statusText.textContent, queues: [...queue.options].map((o) => o.text), mode: document.querySelector("input[name=mode]:checked")?.value }));
await page.setViewport({ width: 360, height: 760 });
await page.screenshot({ path: join(shots, "2-popup.png") });
await page.setViewport({ width: 1280, height: 900 });
report("popup", popup.mode === "auto" && popup.queues.length > 0, `${popup.status} | code: ${popup.queues.join(", ")} | modalità ${popup.mode}`);

// 3. Interception of a direct download
const before = db("select count(*) from downloads")[0][0];
await page.goto(`${SITE}/download.html`);
await page.click("#zip");
const got = await waitFor(() => db("select file_name, status from downloads where url like '%big.zip'").length > 0);
await sleep(1500);
report("intercepted download reaches the app", !!got && !existsSync(join(browserDl, "big.zip")),
  JSON.stringify(db("select file_name, status from downloads where url like '%big.zip'")) + ` | nel browser: ${readdirSync(browserDl).join(",") || "nulla"}`);

// 4. Alt+click: this one stays in the browser
await page.keyboard.down("Alt");
await page.click("#alt");
await page.keyboard.up("Alt");
const inBrowser = await waitFor(() => existsSync(join(browserDl, "alt.zip")), 20000);
report("Alt+click leaves the download to the browser", !!inBrowser && db("select count(*) from downloads where url like '%alt.zip'")[0][0] === 0,
  `nel browser: ${readdirSync(browserDl).join(",")}`);

// 5. Form (POST)
await page.goto(`${SITE}/form.html`);
await page.click("#go");
const form = await waitFor(() => db("select file_name, status from downloads where url like '%/export'").find((r) => r[1] === 3), 30000);
const posts = readFileSync(SITE_LOG, "utf8").trim().split("\n").map(JSON.parse).filter((l) => l.method === "POST");
const appPost = posts.at(-1);
report("form download repeated by the app as POST", !!form && /anno=2026/.test(appPost.body) && /sessione=abc123/.test(appPost.cookie),
  `${JSON.stringify(form)} | ultimo POST: ${appPost.body} (${appPost.type}, referer ${appPost.referer}, cookie ${appPost.cookie})`);

// 6. Video button: HLS manifest loaded by the page
await page.goto(`${SITE}/video.html`);
await page.waitForFunction(() => document.title.includes("("), { timeout: 20000 });
const pill = await waitFor(() => findInShadow(page, "pill"), 10000);
report("video button appears", !!pill);
if (pill) {
  await clickNode(page, pill);
  await sleep(500);
  const gets = await waitFor(async () => { const g = await findAllInShadow(page, "get"); return g.length ? g : null; }, 30000);
  await page.screenshot({ path: join(shots, "3-video-panel.png") });
  const texts = gets ? await Promise.all(gets.map((n) => nodeText(page, n))) : [];
  const rows = await findAllInShadow(page, "quality");
  const labels = await Promise.all(rows.map((n) => nodeText(page, n)));
  const sizes = await Promise.all((await findAllInShadow(page, "size")).map((n) => nodeText(page, n)));
  report("qualities listed with sizes", labels.length >= 3, labels.map((l, i) => `${l} ${sizes[i]}`).join(" · "));
  if (gets) {
    await clickNode(page, gets.at(-1)); // the lowest quality
    const media = await waitFor(() => db("select file_name, media from downloads where media is not null and id > " + before).find((r) => r), 20000);
    await sleep(800);
    await page.screenshot({ path: join(shots, "4-video-sent.png") });
    report("chosen quality sent to the app", !!media, JSON.stringify(media));
  }
}

// 7. Protected video (Clear Key EME)
await page.goto(`${SITE}/drm.html`);
await page.waitForFunction(() => document.title.startsWith("drm"), { timeout: 20000 });
const drmTitle = await page.title();
const pill2 = await waitFor(() => findInShadow(page, "pill"), 10000);
if (pill2) {
  await clickNode(page, pill2);
  const notice = await waitFor(() => findInShadow(page, "notice"), 10000);
  await page.screenshot({ path: join(shots, "5-drm.png") });
  report("protected video: clear message, no download", !!notice, `${drmTitle} | ${notice ? await nodeText(page, notice) : "-"}`);
} else {
  report("protected video: clear message, no download", false, drmTitle);
}

// 8. Download all (the context menu's handler)
if (process.argv.includes("--batch")) {
  await page.goto(`${SITE}/links.html`);
  const ok = await worker.evaluate(async (url) => {
    const [tab] = await chrome.tabs.query({ url });
    await globalThis.dmMenuClicked({ menuItemId: "dm-all", frameId: 0, pageUrl: url }, tab);
    return !!tab;
  }, `${SITE}/links.html`);
  report("download all sent", ok);
  console.log(`     "Scarica tutto" window closed: ${closeBatchWindow()}`);
}

console.log(JSON.stringify(results));
await browser.close();

// ---- closed shadow roots are reached through the DevTools protocol (pierce), as a user's clicks would
async function flat(page) {
  const session = await page.createCDPSession();
  const { root } = await session.send("DOM.getDocument", { depth: -1, pierce: true });
  const all = [];
  (function walk(n) {
    all.push(n);
    for (const c of [...(n.children ?? []), ...(n.shadowRoots ?? []), ...(n.contentDocument ? [n.contentDocument] : [])]) walk(c);
  })(root);
  return { session, all };
}
function hasClass(n, cls) {
  const a = n.attributes ?? [];
  for (let i = 0; i < a.length; i += 2) if (a[i] === "class" && a[i + 1].split(/\s+/).includes(cls)) return true;
  return false;
}
async function findAllInShadow(page, cls) {
  const { session, all } = await flat(page);
  return all.filter((n) => hasClass(n, cls)).map((n) => ({ session, backendNodeId: n.backendNodeId }));
}
async function findInShadow(page, cls) {
  return (await findAllInShadow(page, cls))[0] ?? null;
}
async function clickNode(page, node) {
  const { model } = await node.session.send("DOM.getBoxModel", { backendNodeId: node.backendNodeId });
  const q = model.content;
  await page.mouse.click((q[0] + q[4]) / 2, (q[1] + q[5]) / 2);
}
async function nodeText(page, node) {
  const { object } = await node.session.send("DOM.resolveNode", { backendNodeId: node.backendNodeId });
  const { result } = await node.session.send("Runtime.callFunctionOn", { objectId: object.objectId, functionDeclaration: "function(){return this.textContent}", returnByValue: true });
  return result.value;
}
