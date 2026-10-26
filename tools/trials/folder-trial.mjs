// Default folder = the trial user's Desktop (%TEMP%\dm-trials\Desktop, never the real one), downloads from Edge through
// the extension: node folder-trial.mjs "<msedge.exe>"
// Three rounds, the sandboxed app restarted between them with new settings in its database: category subfolders on,
// off, and the Compressed category with an absolute folder of its own (it must win, as the user set it).
// Headless, temporary profile; needs prepare.ps1 first (isolated data, site on port 8765).
import puppeteer from "puppeteer-core";
import { WORK, DATA, DB, BROWSER_DL, CHROMIUM_EXT } from "./config.mjs";
import { mkdtempSync, mkdirSync, existsSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { execFileSync } from "node:child_process";

const [exe] = process.argv.slice(2);
const ID = "fngmjmgaglmoebphcjohnhlaceogneno";
const SITE = "http://127.0.0.1:8765";
const DESKTOP = join(WORK, "Desktop");
const ELSEWHERE = join(WORK, "Altrove");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const report = (step, ok, detail = "") => {
  results.push({ step, ok, detail });
  console.log(`${ok ? "OK  " : "FAIL"} ${step}${detail ? " — " + detail : ""}`);
};
const py = (code, ...args) => execFileSync("python", ["-I", "-c", code, DB, ...args]).toString();
const rows = (sql) => JSON.parse(py("import sqlite3,json,sys; c=sqlite3.connect(sys.argv[1]); print(json.dumps(c.execute(sys.argv[2]).fetchall()))", sql));

// Only the sandboxed app of this trial (its command line has no BATON_DATA, so it is found by the pipe's data folder):
// the instance the native host started with --background after the first round.
function stopTrialApp() {
  execFileSync("powershell", ["-NoProfile", "-Command",
    "Get-CimInstance Win32_Process -Filter \"Name='Baton.exe'\" | Where-Object { $_.CommandLine -like '*--background*' } | " +
    "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"]);
}

function configure(subfolders, compressedFolder) {
  stopTrialApp();
  py(`import sqlite3,sys
c=sqlite3.connect(sys.argv[1])
for k,v in (('DownloadFolder',sys.argv[2]),('CategorySubfolders',sys.argv[3]),('ConfirmBrowserDownloads','0')):
    c.execute("insert or replace into settings(key,value) values(?,?)",(k,v))
# The built-in subfolders, as a new installation has them; the general category stays the default folder itself.
for key,name in (('compressed','Compressi'),('documents','Documenti'),('music','Musica'),('video','Video'),('programs','Programmi')):
    c.execute("update categories set folder=? where key=?",(name,key))
if sys.argv[4]: c.execute("update categories set folder=? where key='compressed'",(sys.argv[4],))
c.commit()`, DESKTOP, subfolders ? "1" : "0", compressedFolder);
}

mkdirSync(DESKTOP, { recursive: true });
const profile = mkdtempSync(join(tmpdir(), "dm-folder-"));
const browserDl = join(BROWSER_DL, "folder");
mkdirSync(join(profile, "Default"), { recursive: true });
mkdirSync(browserDl, { recursive: true });
writeFileSync(join(profile, "Default", "Preferences"), JSON.stringify({ download: { default_directory: browserDl, prompt_for_download: false } }));

const rounds = [
  { name: "subfolders on", subfolders: true, compressed: "", zip: join(DESKTOP, "Compressi"), pdf: join(DESKTOP, "Documenti") },
  { name: "subfolders off", subfolders: false, compressed: "", zip: DESKTOP, pdf: DESKTOP },
  { name: "Compressed with its own folder", subfolders: true, compressed: ELSEWHERE, zip: ELSEWHERE, pdf: join(DESKTOP, "Documenti") },
];
let browser;
try {
  for (const [i, round] of rounds.entries()) {
    configure(round.subfolders, round.compressed);
    browser = await puppeteer.launch({
      executablePath: exe, headless: true, pipe: true, enableExtensions: [CHROMIUM_EXT], userDataDir: profile,
      env: { ...process.env, BATON_DATA: DATA }, args: ["--no-first-run", "--no-default-browser-check"],
    });
    if (i === 0) console.log("browser:", await browser.version());
    await browser.waitForTarget((t) => t.type() === "service_worker" && t.url().includes(ID), { timeout: 15000 });
    const page = await browser.newPage();
    // The test site's files are small and PDFs are not in the default list: "always send" for this site, as a user can set.
    await page.goto(`chrome-extension://${ID}/popup.html`);
    await page.evaluate(() => chrome.storage.local.set({ alwaysSites: "127.0.0.1" }));
    await page.goto(`${SITE}/links.html`);
    // A PDF link would open in Edge's viewer: as a download, the way sites offer documents ("download" attribute).
    await page.evaluate(() => document.querySelectorAll("a[href$='.pdf']").forEach((a) => a.setAttribute("download", "")));
    await sleep(1000);
    const since = rows("select coalesce(max(id), 0) from downloads")[0][0]; // earlier trials left rows with the same names
    for (const [kind, file] of [["zip", `archive-${i}.zip`], ["pdf", `manual-${i}.pdf`]]) {
      await page.click(`a[href="/files/${file}"]`);
      const done = await (async () => {
        for (let t = 0; t < 120; t++) {
          const r = rows(`select directory, status from downloads where url like '%/${file}' and id > ${since}`);
          if (r.length && r[0][1] === 3) return r[0];
          await sleep(250);
        }
        return rows(`select directory, status from downloads where url like '%/${file}' and id > ${since}`)[0];
      })();
      report(`${round.name}: ${file}`, !!done && done[0] === round[kind] && existsSync(join(round[kind], file)),
        done ? `${done[0]} (stato ${done[1]})` : "non arrivato all'app");
    }
    await browser.close();
    browser = null;
  }
} finally {
  await browser?.close();
  stopTrialApp();
}
console.log(JSON.stringify(results));
