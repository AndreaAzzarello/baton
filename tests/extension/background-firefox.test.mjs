// Firefox: no onDeterminingFilename. The download is paused when created; cancelled, its file removed and erased
// once the app has it, or resumed (or restarted) when the app does not take it.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createFakeChrome, settle } from "./fake-chrome.mjs";

let reply = async () => ({ ok: true });
const fake = createFakeChrome({ firefox: true, nativeReply: (message) => reply(message) });
globalThis.chrome = fake.chrome;
globalThis.DM_LATE_FORM_STEP_MS = 5; // the real 100 ms steps would only slow the tests
await import("../../extension/background.js");

test("onDeterminingFilename is not used in Firefox", () => {
  assert.equal(fake.listeners["downloads.onDeterminingFilename"], undefined);
  assert.equal(fake.listeners["downloads.onCreated"].length, 1);
});

test("Firefox never asks the app's version at start nor reloads: a temporary add-on stays as loaded", async () => {
  await settle();
  assert.equal(fake.calls.filter((c) => c[0] === "native" && c[1].type === "hello").length, 0);
  assert.equal(fake.calls.filter((c) => c[0] === "reload").length, 0);
});

const created = fake.listeners["downloads.onCreated"]?.[0];
const download = { id: 9, url: "https://files.test/big.iso", filename: "C:\\dl\\big.iso", mime: "application/x-iso9660-image",
  totalBytes: -1, startTime: new Date().toISOString(), incognito: false };
const names = () => fake.calls.map((c) => c[0]).filter((n) => n !== "badge");

test("accepted: pause, then cancel, remove the partial file and erase", async () => {
  fake.calls.length = 0;
  reply = async () => ({ ok: true });
  await created(download);
  await settle();
  assert.deepEqual(names(), ["pause", "native", "cancel", "removeFile", "erase"]);
});

test("app unreachable: the paused download is resumed in Firefox", async () => {
  fake.calls.length = 0;
  reply = async () => {
    throw new Error("No such native application com.andreaazzarello.baton");
  };
  await created(download);
  await settle();
  assert.deepEqual(names(), ["pause", "native", "resume", "notify"]);
});

test("not resumable: Firefox downloads it again itself, and that copy is not intercepted", async () => {
  fake.calls.length = 0;
  reply = async () => ({ ok: false, error: "no" });
  fake.chrome.downloads.resume = async () => {
    throw new Error("cannot resume");
  };
  await created(download);
  await settle();
  assert.deepEqual(names(), ["pause", "native", "cancel", "erase", "download", "notify"]);
  const again = fake.calls.find((c) => c[0] === "download")[1];
  assert.equal(again.url, download.url);

  fake.calls.length = 0;
  await created({ ...download, id: 10, byExtensionId: "test-extension" });
  await settle();
  assert.deepEqual(names(), []);
});

test("a form whose request event arrives after the download is still sent as POST", async () => {
  fake.calls.length = 0;
  reply = async () => ({ ok: true });
  fake.chrome.downloads.resume = async () => {};
  const form = { id: 11, url: "https://files.test/export", filename: "C:\dl\report.zip", mime: "application/zip",
    totalBytes: -1, startTime: new Date().toISOString(), incognito: false };
  const pending = created(form); // the download first...
  await new Promise((resolve) => setTimeout(resolve, 12)); // within the 5 × 5 ms the extension waits here
  fake.listeners["webRequest.onBeforeRequest"][0]({ requestId: "late", method: "POST", tabId: 2, url: form.url,
    documentUrl: "https://files.test/form", requestBody: { formData: { year: ["2026"] } } }); // ...then the form
  await pending;
  await settle();
  const sent = fake.calls.find((c) => c[0] === "native")[1].download;
  assert.equal(sent.method, "POST");
  assert.equal(sent.body, "year=2026");
});
