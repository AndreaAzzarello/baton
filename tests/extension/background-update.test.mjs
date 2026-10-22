// After the app updated itself and the folder the extension was loaded from: one reload, never a loop.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createFakeChrome, settle } from "./fake-chrome.mjs";

let generation = 0;

/** Starts the background as the browser would after a (re)load: a new module instance with the given storage. */
async function start({ version = "1.3.2", local = {}, session = {}, hello = { appVersion: "1.3.3", extensionVersion: "1.3.3" }, installType,
  add = async () => ({ ok: true }) } = {}) {
  const fake = createFakeChrome({ version, local, session, installType,
    nativeReply: async (message) => (message.type === "add" ? add(message) : { ok: true, protocol: 3, running: true, ...hello }) });
  globalThis.chrome = fake.chrome;
  await import(`../../extension/background.js?start=${++generation}`);
  await settle();
  const named = (name) => fake.calls.filter((c) => c[0] === name);
  return { fake, named, hellos: named("native").filter((c) => c[1].type === "hello").length, reloads: named("reload").length };
}

test("an older extension whose folder is up to date reloads once", async () => {
  const first = await start();
  assert.equal(first.hellos, 1);
  assert.equal(first.reloads, 1);
  assert.equal(first.fake.local.reloadTried, "1.3.3"); // written before the reload

  // The browser loaded the extension from another folder: still 1.3.2 after the reload. No second reload.
  const again = await start({ local: first.fake.local });
  assert.equal(again.hellos, 1);
  assert.equal(again.reloads, 0);

  // Up to date after the reload: nothing more.
  const updated = await start({ version: "1.3.3", local: first.fake.local });
  assert.equal(updated.reloads, 0);
});

test("the app is asked at most every 15 minutes, and an older folder is never reloaded", async () => {
  assert.equal((await start({ session: { appCheckedAt: Date.now() - 60_000 } })).hellos, 0);
  assert.equal((await start({ session: { appCheckedAt: Date.now() - 16 * 60_000 } })).hellos, 1);
  assert.equal((await start({ hello: { appVersion: "1.3.3", extensionVersion: "1.3.2" } })).reloads, 0);
  assert.equal((await start({ hello: { appVersion: "1.3.2" } })).reloads, 0); // an app before 1.3.3 says nothing of the folder
  assert.equal((await start({ installType: "normal" })).reloads, 0); // from a store: the store updates it
});

test("a download being handed over is never cut short by the reload: it waits until the browser's copy is dropped", async () => {
  // The service worker wakes up for a download; the app takes a while to accept it, and meanwhile says it is newer.
  let accept;
  const accepted = new Promise((resolve) => { accept = resolve; });
  const fake = createFakeChrome({ version: "1.3.2", session: { appCheckedAt: Date.now() },
    nativeReply: async (message) => (message.type === "add" ? accepted : { ok: true, protocol: 3, appVersion: "1.3.3", extensionVersion: "1.3.3" }) });
  globalThis.chrome = fake.chrome;
  await import(`../../extension/background.js?start=${++generation}`);
  const determine = fake.listeners["downloads.onDeterminingFilename"][0];
  determine({ id: 7, url: "https://files.test/big.zip", finalUrl: "https://files.test/big.zip", filename: "big.zip", mime: "application/zip",
    totalBytes: 50 * 1024 * 1024, startTime: new Date().toISOString(), incognito: false }, () => {});
  await settle();
  const popup = fake.listeners["runtime.onMessage"][0];
  let decision;
  popup({ type: "appUpdate", reply: { ok: true, protocol: 3, appVersion: "1.3.3", extensionVersion: "1.3.3" } },
    { id: fake.chrome.runtime.id, url: "chrome-extension://test-extension/popup.html" }, (d) => { decision = d; });
  await settle();
  assert.equal(decision.action, "reload");
  assert.equal(fake.calls.filter((c) => c[0] === "reload").length, 0); // the download is still with the app
  accept({ ok: true });
  await settle();
  const order = fake.calls.map((c) => c[0]).filter((n) => n === "cancel" || n === "erase" || n === "reload");
  assert.deepEqual(order, ["cancel", "erase", "reload"]); // the browser's copy dropped first, then the reload
  assert.equal(fake.local.reloadTried, "1.3.3");
});

test("only the popup and the check page may ask for the app update to be followed", async () => {
  const { fake } = await start({ session: { appCheckedAt: Date.now() } });
  const listener = fake.listeners["runtime.onMessage"][0];
  const message = { type: "appUpdate", reply: { ok: true, appVersion: "1.3.3", extensionVersion: "1.3.3" } };
  assert.equal(listener(message, { id: fake.chrome.runtime.id, url: "https://evil.test/", tab: { id: 3 } }, () => {}), false);
  assert.equal(listener(message, { id: "another-extension", url: "chrome-extension://test-extension/popup.html" }, () => {}), false);
  await settle();
  assert.equal(fake.calls.filter((c) => c[0] === "reload").length, 0);
});
