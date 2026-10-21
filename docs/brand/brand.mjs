// Baton's visual identity: the SVG sources and every raster made from them (app and tray icons, extension icons,
// README logo, social preview). Rendered by headless Edge through puppeteer-core (installed in tools/trials).
//   node docs/brand/brand.mjs            -> docs/brand/*.svg|png, docs/brand/out/*.png (frames for tools/make_icons.py)
// Three rounded bars (parallel connections) join into one arrowhead (one file): outer bars blue, middle bar teal.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createRequire } from "node:module";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const require = createRequire(join(root, "tools", "trials", "package.json"));
const puppeteer = require("puppeteer-core");
const EDGE = process.env.EDGE || "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";

export const BLUE = "#2563EB", TEAL = "#14B8A6";
// For a dark taskbar: the same hues, light enough for 4.5:1 on #202020.
export const BLUE_DARK_BG = "#60A5FA", TEAL_DARK_BG = "#2DD4BF";
export const GREY = "#8A9099", GREY_LIGHT = "#B4B9C0"; // the extension's "context menu only" mode

/** The mark on a 256 grid, transparent. */
export const glyph = (blue = BLUE, teal = TEAL) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="73" y="52" width="30" height="110" rx="15" fill="${blue}"/>
  <rect x="113" y="34" width="30" height="128" rx="15" fill="${teal}"/>
  <rect x="153" y="64" width="30" height="98" rx="15" fill="${blue}"/>
  <polygon points="62,146 194,146 128,216" fill="${blue}" stroke="${blue}" stroke-width="12" stroke-linejoin="round"/>
</svg>`;

/** The app icon: the mark, a little larger, on a white rounded square (readable on any taskbar or wallpaper). */
export const tile = () => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="8" y="8" width="240" height="240" rx="56" fill="#FFFFFF"/>
  <rect x="8.5" y="8.5" width="239" height="239" rx="55.5" fill="none" stroke="#0F172A" stroke-opacity=".08"/>
  <g transform="translate(128 125) scale(1.12) translate(-128 -125)">${glyph().replace(/^<svg[^>]*>|<\/svg>$/g, "")}</g>
</svg>`;

/**
 * Hand-placed on the pixel grid, so the gaps between the bars survive at small sizes:
 * bars [x, top] with width w, arrow from (ax0, ay) to (ax1, ay) with its tip at (tx, ty).
 */
const PIXEL = {
  16: { w: 2, bars: [[4, 2], [7, 1], [10, 3]], bottom: 9, arrow: [2, 9, 14, 8, 15] },
  20: { w: 2, bars: [[5, 3], [9, 2], [13, 4]], bottom: 11, arrow: [3, 11, 17, 10, 18] },
  24: { w: 4, bars: [[5, 4], [10, 2], [15, 5]], bottom: 14, arrow: [3, 14, 21, 12, 22] },
  32: { w: 4, bars: [[8, 4], [14, 2], [20, 6]], bottom: 18, arrow: [4, 18, 28, 16, 30] },
};
export const pixel = (size, blue = BLUE, teal = TEAL) => {
  const p = PIXEL[size];
  const [ax0, ay, ax1, tx, ty] = p.arrow;
  const bars = p.bars.map(([x, top], i) =>
    `<rect x="${x}" y="${top}" width="${p.w}" height="${p.bottom - top}" fill="${i === 1 ? teal : blue}"/>`).join("");
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size} ${size}" shape-rendering="crispEdges">${bars}<polygon points="${ax0},${ay} ${ax1},${ay} ${tx},${ty}" fill="${blue}"/></svg>`;
};

const social = () => `<!doctype html><html><head><meta charset="utf-8"><style>
  html, body { margin: 0; width: 1280px; height: 640px; }
  body { display: flex; align-items: center; gap: 64px; padding: 0 112px; box-sizing: border-box;
    background: #F8FAFC; font-family: "Segoe UI Variable Display", "Segoe UI", sans-serif; color: #0F172A; position: relative; }
  .mark { width: 300px; height: 300px; flex: none; filter: drop-shadow(0 12px 28px rgba(15, 23, 42, .12)); }
  h1 { margin: 0; font-size: 132px; font-weight: 700; letter-spacing: -3px; line-height: 1; }
  p { margin: 22px 0 0; font-size: 38px; color: #475569; line-height: 1.25; max-width: 640px; }
  .bar { position: absolute; left: 0; right: 0; bottom: 0; height: 14px;
    background: linear-gradient(90deg, ${BLUE} 0 33.3%, ${TEAL} 33.3% 66.6%, ${BLUE} 66.6%); }
</style></head><body>
  <img class="mark" src="data:image/svg+xml;charset=utf-8,${encodeURIComponent(tile())}" alt="">
  <div><h1>Baton</h1><p>A fast segmented download manager for Windows</p></div>
  <div class="bar"></div>
</body></html>`;

const out = join(here, "out");
mkdirSync(out, { recursive: true });
const sources = {
  "baton.svg": glyph(),
  "baton-app.svg": tile(),
  "baton-16.svg": pixel(16), "baton-20.svg": pixel(20), "baton-24.svg": pixel(24), "baton-32.svg": pixel(32),
};
for (const [name, svg] of Object.entries(sources)) writeFileSync(join(here, name), svg + "\n");

const browser = await puppeteer.launch({ executablePath: EDGE, headless: true, args: ["--force-device-scale-factor=1"] });
const page = await browser.newPage();
async function render(svg, size, file) {
  await page.setViewport({ width: size, height: size, deviceScaleFactor: 1 });
  await page.setContent(`<html><body style="margin:0;background:transparent"><img style="display:block;width:${size}px;height:${size}px"
    src="data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}"></body></html>`);
  await page.waitForFunction(() => document.images[0].complete);
  await page.screenshot({ path: file, omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
}

// App icon frames: pixel-grid mark for the small sizes (title bar, small icons), the tile from 32 px up.
for (const size of [16, 20, 24]) await render(pixel(size), size, join(out, `app-${size}.png`));
for (const size of [32, 40, 48, 64, 256]) await render(tile(), size, join(out, `app-${size}.png`));
// Tray: light and dark taskbar, 16/20/24/32 (100/125/150/200 %).
for (const size of [16, 20, 24, 32]) {
  await render(pixel(size), size, join(out, `tray-light-${size}.png`));
  await render(pixel(size, BLUE_DARK_BG, TEAL_DARK_BG), size, join(out, `tray-dark-${size}.png`));
}
// Extension: toolbar sizes on the pixel grid, store/extension page sizes as the tile; grey for "context menu only".
const ext = join(root, "extension", "icons");
await render(pixel(16), 16, join(ext, "icon16.png"));
await render(pixel(32), 32, join(ext, "icon32.png"));
await render(tile(), 48, join(ext, "icon48.png"));
await render(tile(), 128, join(ext, "icon128.png"));
await render(pixel(16, GREY, GREY_LIGHT), 16, join(ext, "icon16-off.png"));
await render(pixel(32, GREY, GREY_LIGHT), 32, join(ext, "icon32-off.png"));
// README logo and GitHub social preview (1280x640).
await render(tile(), 256, join(here, "baton-256.png"));
await page.setViewport({ width: 1280, height: 640, deviceScaleFactor: 1 });
await page.setContent(social());
await page.waitForFunction(() => document.images[0].complete);
await page.screenshot({ path: join(here, "social-preview.png"), clip: { x: 0, y: 0, width: 1280, height: 640 } });
await browser.close();
console.log("brand: svg sources, extension icons, README logo, social preview, icon frames in docs/brand/out");
