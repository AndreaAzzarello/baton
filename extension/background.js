// Hands downloads over to the DownloadManager app through its native messaging host.
import { HOST, loadSettings, extensionOf } from "./shared.js";

const MENU_LINK = "dm-link";
const MENU_MEDIA = "dm-media";

// MIME types whose extension is not visible in the URL (e.g. download.php?id=42).
const MIME_EXTENSIONS = {
  "application/zip": "zip",
  "application/x-zip-compressed": "zip",
  "application/vnd.rar": "rar",
  "application/x-rar-compressed": "rar",
  "application/x-7z-compressed": "7z",
  "application/gzip": "gz",
  "application/x-gzip": "gz",
  "application/x-tar": "tar",
  "application/x-msdownload": "exe",
  "application/x-msdos-program": "exe",
  "application/vnd.microsoft.portable-executable": "exe",
  "application/x-msi": "msi",
  "application/x-ms-installer": "msi",
  "application/x-iso9660-image": "iso",
  "application/vnd.android.package-archive": "apk",
  "application/x-apple-diskimage": "dmg",
  "video/mp4": "mp4",
  "video/x-matroska": "mkv",
  "video/webm": "webm",
  "video/quicktime": "mov",
  "video/x-msvideo": "avi",
  "audio/mpeg": "mp3",
  "audio/flac": "flac",
  "audio/wav": "wav",
};

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: MENU_LINK, title: "Scarica con DownloadManager", contexts: ["link"] });
    chrome.contextMenus.create({ id: MENU_MEDIA, title: "Scarica con DownloadManager", contexts: ["image", "video", "audio"] });
  });
  updateBadge();
});
chrome.runtime.onStartup.addListener(updateBadge);
chrome.storage.onChanged.addListener(updateBadge);

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.menuItemId === MENU_MEDIA ? info.srcUrl : info.linkUrl;
  if (!url || !/^(https?|ftps?|ftpes):/i.test(url)) {
    notify("Questo collegamento non può essere scaricato con DownloadManager.");
    return;
  }
  try {
    await sendToApp({ url, referrer: info.pageUrl ?? tab?.url, incognito: tab?.incognito === true });
  } catch (error) {
    notify(`DownloadManager non ha ricevuto il download: ${error.message}`);
  }
});

// Intercepts downloads the browser starts by itself, for the file types chosen in the popup.
chrome.downloads.onCreated.addListener(async (item) => {
  const settings = await loadSettings();
  if (!settings.intercept || !shouldIntercept(item, settings)) return;

  // Stop the browser first so the file is not downloaded twice.
  try {
    await chrome.downloads.cancel(item.id);
  } catch {
    return;
  }

  const url = item.finalUrl || item.url;
  try {
    await sendToApp({ url, referrer: item.referrer, fileName: baseName(item.filename), incognito: false });
    await chrome.downloads.erase({ id: item.id });
  } catch (error) {
    // The app could not take it: let the browser download it after all.
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
    await chrome.downloads.download({ url: item.url });
    notify(`DownloadManager non è raggiungibile (${error.message}). Il file viene scaricato dal browser.`);
  }
});

function shouldIntercept(item, settings) {
  if (item.byExtensionId === chrome.runtime.id) return false; // our own fallback download
  if (item.incognito || item.state !== "in_progress") return false;
  if (Date.now() - Date.parse(item.startTime) > 30_000) return false; // restored at browser startup
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return false; // blob:, data:, file: only exist inside the browser
  const minBytes = settings.minSizeMB * 1024 * 1024;
  if (minBytes > 0 && item.totalBytes > 0 && item.totalBytes < minBytes) return false;

  const wanted = new Set(settings.extensions.split(/[\s,;]+/).map((e) => e.replace(/^\./, "").toLowerCase()).filter(Boolean));
  const candidates = [
    extensionOf(baseName(item.filename)),
    extensionOf(new URL(url).pathname),
    MIME_EXTENSIONS[(item.mime || "").toLowerCase()],
  ];
  return candidates.some((ext) => ext && wanted.has(ext));
}

async function sendToApp({ url, referrer, fileName, incognito }) {
  // Cookies of a private window are never handed to the app, which keeps them until the download ends.
  // Cookies exist only for web pages; a private window's are never handed to the app.
  const cookies = incognito || !/^https?:/i.test(url) ? [] : await chrome.cookies.getAll({ url });
  const { queue } = await loadSettings();
  const message = {
    type: "add",
    download: {
      url,
      fileName: fileName || null,
      referrer: referrer && /^https?:/i.test(referrer) ? referrer : null,
      userAgent: navigator.userAgent,
      cookies: cookies.map((c) => ({ name: c.name, value: c.value, domain: c.domain, path: c.path, secure: c.secure, hostOnly: c.hostOnly })),
      queue: queue || null, // older apps ignore it; newer ones fall back to the main queue for unknown names
    },
  };
  const reply = await chrome.runtime.sendNativeMessage(HOST, message);
  if (!reply?.ok) throw new Error(reply?.error || "risposta non valida");
}

function baseName(path) {
  return path ? path.split(/[\\/]/).pop() : "";
}

async function updateBadge() {
  const { intercept } = await loadSettings();
  await chrome.action.setBadgeText({ text: intercept ? "" : "off" });
  await chrome.action.setBadgeBackgroundColor({ color: "#6B7385" });
}

function notify(message) {
  chrome.notifications.create({ type: "basic", iconUrl: "icons/icon128.png", title: "DownloadManager", message });
}
