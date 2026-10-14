export const HOST = "com.downloadmanager.host";

export const DEFAULTS = {
  intercept: true,
  extensions: "zip rar 7z gz bz2 xz tar iso img exe msi msix apk dmg bin mp4 mkv avi mov wmv webm mp3 flac wav",
  minSizeMB: 0,
  // Queue for downloads sent from the browser; "" = the app's main queue.
  queue: "",
};

export async function loadSettings() {
  return { ...DEFAULTS, ...(await chrome.storage.local.get(Object.keys(DEFAULTS))) };
}

export function extensionOf(name) {
  const match = /\.([a-z0-9]{1,8})$/i.exec(name || "");
  return match ? match[1].toLowerCase() : null;
}
