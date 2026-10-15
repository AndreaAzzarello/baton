// Where the real-browser trials keep their files. Everything lives in one work folder (default %TEMP%\dm-trials,
// or DM_TRIALS), never on the Desktop and never in the real app's data.
import { existsSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

export const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
export const WORK = process.env.DM_TRIALS || join(tmpdir(), "dm-trials");
export const DATA = join(WORK, "data"); // DOWNLOADMANAGER_DATA of the sandboxed app
export const DB = join(DATA, "downloads.db");
export const DOWNLOADS = join(WORK, "downloads"); // the sandboxed app's download folder
export const SITE = "http://127.0.0.1:8765";
export const SITE_LOG = join(WORK, "site-log.jsonl");
export const SHOTS = join(WORK, "shots");
export const BROWSER_DL = join(WORK, "browser-downloads"); // what the browsers keep for themselves
export const CHROMIUM_EXT = join(ROOT, "dist", "chromium");
export const FIREFOX_TRIAL_EXT = join(WORK, "ext-firefox-trial"); // dist/firefox + a line that opens check.html

/** The portable Firefox downloaded by prepare.ps1 (any version), or null. */
export const FIREFOX_EXE = (() => {
  const base = join(WORK, "firefox-cache", "firefox");
  if (!existsSync(base)) return null;
  for (const dir of readdirSync(base)) {
    const exe = join(base, dir, "core", "firefox.exe");
    if (existsSync(exe)) return exe;
  }
  return null;
})();

/**
 * Closes the "download all" window the trial opened in the sandboxed app (Annulla): while it is open, later requests
 * from the browsers wait in the app's queue, and the next trial would see nothing arrive.
 */
export function closeBatchWindow() {
  const ui = join(ROOT, "tools", "trials", "ui.ps1");
  const run = spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ui, "-Window", "Scarica tutto", "-Action", "invoke", "-Name", "Annulla"],
    { encoding: "utf8" });
  return run.status === 0;
}
