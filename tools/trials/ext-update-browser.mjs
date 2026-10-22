// Edge (headless, temporary profile) with the extension loaded from the folder next to a trial Baton.exe, while
// ext-update-trial.ps1 updates that copy: node ext-update-browser.mjs <browser exe>
// DM_CHROMIUM_EXT = the folder, DM_TRIAL_DATA = the trial app's data (BATON_DATA for the host), DM_TRIAL_VERSION = the update.
import puppeteer from "puppeteer-core";
import { mkdtempSync, mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { SHOTS } from "./config.mjs";

const [exe] = process.argv.slice(2);
const EXT = process.env.DM_CHROMIUM_EXT;
const TARGET = process.env.DM_TRIAL_VERSION;
const ID = "fngmjmgaglmoebphcjohnhlaceogneno";
const shots = join(SHOTS, "ext-update");
mkdirSync(shots, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = (step, ok, detail = "") => console.log(`${ok ? "OK  " : "FAIL"} ${step}${detail ? " — " + detail : ""}`);
const onDisk = () => JSON.parse(readFileSync(join(EXT, "manifest.json"), "utf8")).version;

async function waitFor(fn, ms, step = 500) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const v = await fn().catch(() => null);
    if (v) return v;
    await sleep(step);
  }
  return null;
}

const within = (promise, ms = 3000) => Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new Error("timeout")), ms))]);

/** The running extension's version, read from its service worker (a new one after a reload; a dead one never answers). */
async function runningVersion(browser) {
  for (const target of browser.targets().filter((t) => t.type() === "service_worker" && t.url().includes(ID)).reverse()) {
    const version = await within(target.worker().then((w) => w?.evaluate(() => chrome.runtime.getManifest().version))).catch(() => null);
    if (version) return version;
  }
  return null;
}

async function check(browser, name) {
  const page = await browser.newPage();
  await page.goto(`chrome-extension://${ID}/check.html`);
  await page.waitForFunction(() => /ok|bad/.test(document.getElementById("result").className), { timeout: 20000 });
  const result = await page.evaluate(() => ({ ok: document.getElementById("result").className.includes("ok"), title: resultTitle.textContent,
    facts: [...document.querySelectorAll("dd")].map((d) => d.textContent), notice: updateNotice.hidden ? "" : updateNotice.textContent }));
  await page.screenshot({ path: join(shots, name) });
  await page.close().catch(() => {});
  return result;
}

const browser = await puppeteer.launch({
  executablePath: exe,
  headless: true,
  pipe: true,
  enableExtensions: [EXT],
  userDataDir: mkdtempSync(join(tmpdir(), "dm-ext-update-")),
  env: { ...process.env, BATON_DATA: process.env.DM_TRIAL_DATA },
  args: ["--no-first-run", "--no-default-browser-check", "--window-size=1280,900"],
  defaultViewport: { width: 1280, height: 900 },
});
try {
  console.log("browser:", await browser.version());
  const before = await waitFor(() => runningVersion(browser), 15000);
  report("extension loaded from the folder next to Baton.exe", !!before, `version ${before}`);
  const first = await check(browser, "1-check-before.png");
  report("check connection before the update", first.ok, `${first.title} | ${first.facts.join(" | ")}`);

  // The update replaces the folder on disk (ext-update-trial.ps1 drives the app).
  const updated = await waitFor(async () => onDisk() === TARGET, 300000, 1000);
  report(`folder on disk updated to ${TARGET}`, !!updated, onDisk());

  // The popup asks the app its version: the extension reloads by itself, and never again after that.
  // Not awaited: the reload closes the page, and its navigation would never be reported as finished.
  const popup = await browser.newPage();
  popup.goto(`chrome-extension://${ID}/popup.html`).catch(() => {});
  const after = await waitFor(async () => ((await runningVersion(browser)) === TARGET ? TARGET : null), 30000);
  report("the extension reloaded by itself", after === TARGET, `running ${await runningVersion(browser).catch(() => "?")}`);
  if (after !== TARGET) {
    const page = await browser.newPage();
    await page.goto(`chrome-extension://${ID}/check.html`).catch(() => {});
    await sleep(3000);
    console.log("diagnostics:", JSON.stringify(await page.evaluate(async () => ({
      tried: await chrome.storage.local.get("reloadTried"), self: await chrome.management.getSelf().then((s) => s.installType),
      hello: await chrome.runtime.sendNativeMessage("com.andreaazzarello.baton", { type: "hello", version: 3 }) })).catch((e) => e.message)));
  }
  const second = await check(browser, "2-check-after.png");
  report("check connection after the reload", second.ok && second.facts.some((f) => f.includes(TARGET)) && !second.notice,
    `${second.title} | ${second.facts.join(" | ")}${second.notice ? " | notice: " + second.notice : ""}`);
  const page = await browser.newPage();
  await page.goto(`chrome-extension://${ID}/check.html`);
  const tried = await page.evaluate(() => chrome.storage.local.get("reloadTried"));
  report("one reload remembered (no loop)", tried.reloadTried === TARGET, JSON.stringify(tried));
} finally {
  await browser.close();
}
