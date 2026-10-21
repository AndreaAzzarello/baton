// Baton's icons, all made from the official brand kit in docs/brand/kit (Baton_Brand_Kit.zip): the kit's own PNGs are
// used as they are; only the sizes it lacks are rendered from its SVGs, and the grey "context menu only" icons are the
// kit's PNGs desaturated. Nothing is redrawn. Rendered by headless Edge through puppeteer-core (installed in tools/trials).
//   node docs/brand/brand.mjs           -> docs/brand/out: icon frames, social preview, preview page (index.html)
//   node docs/brand/brand.mjs --apply   -> also extension/icons and docs/brand/social-preview.png; then run
//                                          python tools/make_icons.py for app.ico (also the tray's icon)
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const kit = join(here, "kit");
const out = join(here, "out");
const require = createRequire(join(root, "tools", "trials", "package.json"));
const puppeteer = require("puppeteer-core");
const EDGE = process.env.EDGE || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const apply = process.argv.includes("--apply");

const K = {
  appDark: (s) => join(kit, "03_Icone_Windows", `BATON_windows_${s}.png`), // the kit's app icon (dark tile)
  appDarkSvg: join(kit, "01_Logo_SVG", "BATON_icona_app_scura.svg"),
  ext: (s) => join(kit, "04_Icone_Browser", `baton_extension_${s}.png`),
  logoDark: join(kit, "01_Logo_SVG", "BATON_logo_orizzontale_scuro.svg"), // dark text, for light backgrounds
  logoWhite: join(kit, "01_Logo_SVG", "BATON_logo_orizzontale_bianco.svg"),
};
const KIT_SIZES = [16, 24, 32, 48, 64, 128, 256, 512];
const dataUrl = (file) => `data:${file.endsWith(".svg") ? "image/svg+xml" : "image/png"};base64,${readFileSync(file).toString("base64")}`;

mkdirSync(out, { recursive: true });
const browser = await puppeteer.launch({ executablePath: EDGE, headless: true, args: ["--force-device-scale-factor=1"] });
const page = await browser.newPage();
const made = []; // what was generated rather than taken from the kit
async function render(src, size, file, filter = "none") {
  await page.setViewport({ width: size, height: size, deviceScaleFactor: 1 });
  await page.setContent(`<html><body style="margin:0;background:transparent"><img style="display:block;width:${size}px;height:${size}px;filter:${filter}"
    src="${dataUrl(src)}"></body></html>`);
  await page.waitForFunction(() => document.images[0].complete);
  await page.screenshot({ path: file, omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
}
async function frame(kitPng, svg, size, file) {
  if (KIT_SIZES.includes(size)) copyFileSync(kitPng(size), file);
  else {
    await render(svg, size, file);
    made.push(`${file.split(/[\\/]/).pop()} (${size} px, from ${svg.split(/[\\/]/).pop()})`);
  }
}

// App icon (exe, windows, notification area on light and dark taskbars, notifications): the kit's Windows icon, dark tile.
for (const s of [16, 20, 24, 32, 40, 48, 64, 256]) await frame(K.appDark, K.appDarkSvg, s, join(out, `app-${s}.png`));
// Extension: the kit's browser icons; grey ones for "context menu only" = the same PNGs desaturated.
for (const s of [16, 32, 48, 128]) copyFileSync(K.ext(s), join(out, `ext-${s}.png`));
for (const s of [16, 32]) {
  await render(K.ext(s), s, join(out, `ext-${s}-off.png`), "grayscale(1)");
  made.push(`ext-${s}-off.png (baton_extension_${s}.png desaturated)`);
}
// GitHub social preview: the kit's horizontal logo and the project's line.
const social = `<!doctype html><html><head><meta charset="utf-8"><style>
  html, body { margin: 0; width: 1280px; height: 640px; }
  body { display: grid; place-items: center; background: #F5F7FB; font-family: "Lato", "Segoe UI", sans-serif; }
  main { display: grid; justify-items: center; gap: 36px; }
  img { width: 760px; }
  p { margin: 0; font-size: 42px; color: #3A4A63; letter-spacing: .2px; }
</style></head><body><main><img src="${dataUrl(K.logoDark)}" alt=""><p>A fast segmented download manager for Windows</p></main></body></html>`;
await page.setViewport({ width: 1280, height: 640, deviceScaleFactor: 1 });
await page.setContent(social);
await page.waitForFunction(() => document.images[0].complete);
await page.screenshot({ path: join(out, "social-preview.png"), clip: { x: 0, y: 0, width: 1280, height: 640 } });
made.push("social-preview.png (kit logo + text)");
await browser.close();

// The preview page: every icon at its real size, on light and dark backgrounds.
const img = (f, s, alt) => `<img width="${s}" height="${s}" alt="${alt}" src="${dataUrl(join(out, f))}">`;
const row = (title, cells) => `<h2>${title}</h2>${["light", "dark"].map((t) => `<div class="row ${t}">${cells}</div>`).join("")}`;
const fig = (inner, caption) => `<figure>${inner}<figcaption>${caption}</figcaption></figure>`;
const html = `<!doctype html><html lang="it"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Baton — icone dal brand kit</title><style>
  body { margin: 0; padding: 24px 16px 48px; font: 14px/1.5 "Segoe UI", sans-serif; background: #f6f6f8; color: #1b1f2a; }
  h1 { margin: 0 0 6px; font-size: 22px; } h2 { margin: 28px 0 8px; font-size: 16px; }
  .row { display: flex; flex-wrap: wrap; gap: 18px; align-items: flex-end; padding: 14px; border-radius: 10px; margin-top: 8px; }
  .light { background: #fff; border: 1px solid #e3e3e3; } .dark { background: #202020; color: #ddd; }
  figure { margin: 0; display: grid; justify-items: center; gap: 4px; font-size: 11px; text-align: center; }
  .bar { display: flex; gap: 10px; align-items: center; padding: 6px 12px; border-radius: 6px; font-size: 12px; }
  .light .bar { background: #eef0f3; } .dark .bar { background: #1c1c1c; }
  .note { color: #5b6275; max-width: 80ch; } code { font-size: 12px; }
  .wide img { max-width: 100%; height: auto; }
</style></head><body>
<h1>Baton — icone dal brand kit</h1>
<p class="note">Tutto viene da <code>Baton_Brand_Kit.zip</code>. Generati da me (non presenti nel kit): ${made.join("; ")}.</p>
${row("Icona dell'app (app.ico: exe, finestre, notifiche) — dal kit, tile scuro", [16, 20, 24, 32, 40, 48, 64, 256].map((s) => fig(img(`app-${s}.png`, Math.min(s, 128), `${s} px`), `${s}${s === 20 || s === 40 ? " (generato)" : ""}`)).join(""))}
<h2>Area di notifica (16 e 20 px)</h2>
<div class="row light">${fig(`<div class="bar">${img("app-16.png", 16, "")}${img("app-20.png", 20, "")} 21:44</div>`, "barra chiara")}</div>
<div class="row dark">${fig(`<div class="bar">${img("app-16.png", 16, "")}${img("app-20.png", 20, "")} 21:44</div>`, "barra scura")}</div>
${row("Estensione (barra del browser 16/32, pagina estensioni 48/128) e grigia per «solo menu»", [16, 32, 48, 128].map((s) => fig(img(`ext-${s}.png`, s, `${s} px`), `${s}`)).join("") + [16, 32].map((s) => fig(img(`ext-${s}-off.png`, s, `${s} px grigia`), `${s} grigia (generata)`)).join(""))}
${row("Notifica (icona 256 px dell'app)", fig(img("app-256.png", 96, "notifica"), "256 → mostrata a 48–96"))}
<h2>Logo del README (&lt;picture&gt;)</h2>
<div class="row light wide">${fig(`<img width="420" alt="logo, tema chiaro" src="${dataUrl(K.logoDark)}">`, "tema chiaro: BATON_logo_orizzontale_scuro.svg")}</div>
<div class="row dark wide">${fig(`<img width="420" alt="logo, tema scuro" src="${dataUrl(K.logoWhite)}">`, "tema scuro: BATON_logo_orizzontale_bianco.svg")}</div>
<h2>Social preview per GitHub (1280×640, generata)</h2>
<div class="row light wide">${fig(img("social-preview.png", 640, "social preview").replace('height="640"', 'height="320"'), "")}</div>
</body></html>`;
writeFileSync(join(out, "index.html"), html);

if (apply) {
  const ext = join(root, "extension", "icons");
  for (const s of [16, 32, 48, 128]) copyFileSync(join(out, `ext-${s}.png`), join(ext, `icon${s}.png`));
  for (const s of [16, 32]) copyFileSync(join(out, `ext-${s}-off.png`), join(ext, `icon${s}-off.png`));
  copyFileSync(join(out, "social-preview.png"), join(here, "social-preview.png"));
}
console.log(`brand: frames and preview in ${out}${apply ? "; extension icons and social preview applied" : ""}`);
console.log("generated:", made.join("; "));
