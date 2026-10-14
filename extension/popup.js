import { HOST, loadSettings } from "./shared.js";

const status = document.getElementById("status");
const intercept = document.getElementById("intercept");
const extensions = document.getElementById("extensions");
const minSize = document.getElementById("minSize");
const queue = document.getElementById("queue");

const settings = await loadSettings();
intercept.checked = settings.intercept;
extensions.value = settings.extensions;
minSize.value = settings.minSizeMB;

intercept.addEventListener("change", () => chrome.storage.local.set({ intercept: intercept.checked }));
extensions.addEventListener("change", () => chrome.storage.local.set({ extensions: extensions.value.trim() }));
minSize.addEventListener("change", () => chrome.storage.local.set({ minSizeMB: Math.max(0, Number(minSize.value) || 0) }));
queue.addEventListener("change", () => chrome.storage.local.set({ queue: queue.value }));
showQueues([], settings.queue);

try {
  const reply = await chrome.runtime.sendNativeMessage(HOST, { type: "ping" });
  setStatus(reply?.running ? "App collegata e in esecuzione." : "App collegata (si avvierà al primo download).", "ok");
  if (reply?.running) {
    // The app's queues; asking never starts the app.
    const list = await chrome.runtime.sendNativeMessage(HOST, { type: "queues" });
    showQueues(list?.queues ?? [], settings.queue);
  }
} catch {
  setStatus("App non trovata: avvia DownloadManager una volta per collegarla al browser.", "bad");
}

// The first queue is the main one, sent as "" so it keeps working if renamed in the app.
function showQueues(names, selected) {
  queue.replaceChildren();
  const all = names.length ? names : ["Coda principale"];
  all.forEach((name, index) => queue.add(new Option(name, index === 0 ? "" : name)));
  if (selected && !all.includes(selected)) queue.add(new Option(`${selected} (non trovata: verrà usata la principale)`, selected));
  queue.value = selected || "";
}

function setStatus(text, kind) {
  status.textContent = text;
  status.className = `status ${kind}`;
}
