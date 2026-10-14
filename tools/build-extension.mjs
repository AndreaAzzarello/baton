// Builds the browser extension from its single source folder (extension/) for Chromium and for Firefox:
//   dist/chromium/  and  dist/downloadmanager-chromium-<version>.zip
//   dist/firefox/   and  dist/downloadmanager-firefox-<version>.xpi
// Only the manifest differs. Usage: node tools/build-extension.mjs
import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync, statSync, copyFileSync } from "node:fs";
import { join, relative, dirname, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateRawSync } from "node:zlib";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const source = join(root, "extension");
const dist = join(root, "dist");

/** Firefox's extension ID: the native host manifest for Firefox allows exactly this one. */
export const FIREFOX_ID = "downloadmanager@extension.local";

const base = JSON.parse(readFileSync(join(source, "manifest.json"), "utf8"));

function firefoxManifest(manifest) {
  const m = structuredClone(manifest);
  delete m.key; // Chromium's way to fix the ID; Firefox uses browser_specific_settings
  delete m.minimum_chrome_version;
  // Firefox runs MV3 backgrounds as event pages (non-persistent), not service workers.
  m.background = { scripts: ["background.js"], type: "module" };
  m.browser_specific_settings = {
    gecko: {
      id: FIREFOX_ID,
      strict_min_version: "128.0", // storage.session, MV3 host permissions granted at install
      data_collection_permissions: { required: ["none"] },
    },
  };
  return m;
}

function files(folder) {
  return readdirSync(folder).flatMap((name) => {
    const path = join(folder, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

function build(name, manifest) {
  const out = join(dist, name);
  rmSync(out, { recursive: true, force: true });
  const entries = [];
  for (const file of files(source)) {
    const rel = relative(source, file).split(sep).join("/");
    if (rel === "manifest.json") continue;
    mkdirSync(dirname(join(out, rel)), { recursive: true });
    copyFileSync(file, join(out, rel));
    entries.push([rel, readFileSync(file)]);
  }
  const json = Buffer.from(JSON.stringify(manifest, null, 2) + "\n");
  writeFileSync(join(out, "manifest.json"), json);
  entries.unshift(["manifest.json", json]);
  return entries;
}

// ---------------------------------------------------------------- a minimal ZIP writer (deflate, no dependencies)

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (data) => {
  let c = 0xffffffff;
  for (const byte of data) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  // A fixed date (1 Jan 2026) keeps the archive identical for identical sources.
  const dosTime = 0;
  const dosDate = ((2026 - 1980) << 9) | (1 << 5) | 1;
  for (const [name, data] of entries) {
    const nameBytes = Buffer.from(name, "utf8");
    const packed = deflateRawSync(data, { level: 9 });
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // UTF-8 names
    local.writeUInt16LE(8, 8);
    local.writeUInt16LE(dosTime, 10);
    local.writeUInt16LE(dosDate, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(packed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    locals.push(local, nameBytes, packed);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt16LE(dosTime, 12);
    central.writeUInt16LE(dosDate, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(packed.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBytes);
    offset += 30 + nameBytes.length + packed.length;
  }
  const centralSize = centrals.reduce((n, b) => n + b.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralSize, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, ...centrals, end]);
}

mkdirSync(dist, { recursive: true });
const version = base.version;
writeFileSync(join(dist, `downloadmanager-chromium-${version}.zip`), zip(build("chromium", base)));
writeFileSync(join(dist, `downloadmanager-firefox-${version}.xpi`), zip(build("firefox", firefoxManifest(base))));
console.log(`extension ${version}: dist/chromium, dist/firefox and their packages`);
