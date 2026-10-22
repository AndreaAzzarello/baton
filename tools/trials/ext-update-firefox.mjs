// Firefox (portable, headless) with an extension older than the trial app: its popup explains how to load the new
// baton-firefox.xpi (a temporary add-on stays as loaded). node ext-update-firefox.mjs
// DM_TRIAL_DATA = the trial app's data (BATON_DATA for the host), DM_TRIAL_VERSION = the app's version.
import puppeteer from "puppeteer-core";
import { cpSync, readFileSync, writeFileSync, appendFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { ROOT, WORK, FIREFOX_EXE } from "./config.mjs";

if (!FIREFOX_EXE) {
  console.error("Portable Firefox missing: run tools\\trials\\prepare.ps1 first.");
  process.exit(2);
}
const GECKO_ID = "baton@andreaazzarello";
const UUID = "6b3e7a52-8d1f-4c3a-9b8e-2f5d0c7a1e44";
const TARGET = process.env.DM_TRIAL_VERSION;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = (step, ok, detail = "") => console.log(`${ok ? "OK  " : "FAIL"} ${step}${detail ? " — " + detail : ""}`);

// This code's Firefox build, labelled as the version before the app's, and opening its popup itself (BiDi cannot open
// moz-extension:// pages).
const ext = join(WORK, "ext-firefox-update");
rmSync(ext, { recursive: true, force: true });
cpSync(join(ROOT, "dist", "firefox"), ext, { recursive: true });
const manifest = JSON.parse(readFileSync(join(ext, "manifest.json"), "utf8"));
const [major, minor, patch] = TARGET.split(".").map(Number);
manifest.version = `${major}.${minor}.${patch - 1}`;
writeFileSync(join(ext, "manifest.json"), JSON.stringify(manifest, null, 2));
appendFileSync(join(ext, "background.js"), "\n// TRIAL ONLY: open the popup as a page so automation can read it.\nsetTimeout(() => chrome.tabs.create({ url: \"popup.html\" }), 1500);\n");

const browser = await puppeteer.launch({
  browser: "firefox",
  executablePath: FIREFOX_EXE,
  headless: true,
  userDataDir: mkdtempSync(join(tmpdir(), "dm-firefox-update-")),
  env: { ...process.env, BATON_DATA: process.env.DM_TRIAL_DATA },
  args: ["-remote-allow-system-access"],
  extraPrefsFirefox: { "extensions.webextensions.uuids": JSON.stringify({ [GECKO_ID]: UUID }), "intl.locale.requested": "en-US" },
});
try {
  console.log("browser:", await browser.version());
  const id = await browser.installExtension(ext);
  report("extension installed (temporary)", id === GECKO_ID, `${id}, version ${manifest.version}`);
  let popup = null;
  for (let i = 0; i < 40 && !popup; i++) {
    await sleep(500);
    for (const p of await browser.pages()) {
      if ((await p.evaluate(() => location.href).catch(() => "")).endsWith("/popup.html")) popup = p;
    }
  }
  report("popup opened", !!popup);
  await popup.waitForFunction(() => /ok|bad/.test(document.getElementById("status").className), { timeout: 20000 });
  const state = await popup.evaluate(() => ({ status: statusText.textContent, hidden: updateNotice.hidden, notice: updateNotice.textContent,
    role: updateNotice.getAttribute("role") }));
  report("Firefox: the popup explains how to load the new version", !state.hidden && state.notice.includes(TARGET) && state.notice.includes("baton-firefox.xpi"),
    `${state.status} | ${state.notice}`);
  report("the notice is announced to screen readers", state.role === "status");
} finally {
  await browser.close();
}
