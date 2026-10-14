import { HOST, loadSettings } from "./shared.js";

const status = document.getElementById("status");
const intercept = document.getElementById("intercept");
const extensions = document.getElementById("extensions");
const minSize = document.getElementById("minSize");

const settings = await loadSettings();
intercept.checked = settings.intercept;
extensions.value = settings.extensions;
minSize.value = settings.minSizeMB;

intercept.addEventListener("change", () => chrome.storage.local.set({ intercept: intercept.checked }));
extensions.addEventListener("change", () => chrome.storage.local.set({ extensions: extensions.value.trim() }));
minSize.addEventListener("change", () => chrome.storage.local.set({ minSizeMB: Math.max(0, Number(minSize.value) || 0) }));

try {
  const reply = await chrome.runtime.sendNativeMessage(HOST, { type: "ping" });
  setStatus(reply?.running ? "App collegata e in esecuzione." : "App collegata (si avvierà al primo download).", "ok");
} catch {
  setStatus("App non trovata: avvia DownloadManager una volta per collegarla al browser.", "bad");
}

function setStatus(text, kind) {
  status.textContent = text;
  status.className = `status ${kind}`;
}
