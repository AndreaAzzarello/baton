// Helpers shared by the background script and the extension's pages (popup, connection check).
import "./rules.js";

export const Rules = globalThis.DMRules;
export const HOST = "com.downloadmanager.host";
export const DEFAULTS = Rules.DEFAULTS;

/** True in Firefox, which has no downloads.onDeterminingFilename and other small differences. */
export const IS_FIREFOX = typeof chrome.downloads?.onDeterminingFilename === "undefined";

export async function loadSettings() {
  return Rules.normalizeSettings(await chrome.storage.local.get([...Object.keys(DEFAULTS), "intercept"]));
}

export function t(key, ...substitutions) {
  return chrome.i18n.getMessage(key, substitutions.map(String)) || key;
}

/** Sends a message to the app through the native host, giving up after `timeoutMs`. */
export async function sendNative(message, timeoutMs = Rules.APP_TIMEOUT_MS) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(t("errorTimeout"))), timeoutMs);
  });
  try {
    return await Promise.race([chrome.runtime.sendNativeMessage(HOST, { ...message, version: Rules.PROTOCOL }), timeout]);
  } finally {
    clearTimeout(timer);
  }
}

/**
 * The cookies the browser would send to `url` for this kind of request, in the app's format: SameSite is honoured
 * against `initiator` (the page that started it), as the browser would. An unknown initiator (a page that hides its
 * referrer) counts as another site: SameSite=Strict cookies are never handed over on a guess. Never for private windows.
 */
export async function cookiesFor(url, incognito, { initiator = null, topLevel = true, method = "GET" } = {}) {
  if (incognito || !/^https?:/i.test(url)) return [];
  const crossSite = !initiator || !/^https?:/i.test(initiator) || !Rules.sameSite(url, initiator);
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.filter((c) => Rules.cookieAllowed(c, { crossSite, topLevel, method })).map(toAppCookie);
  } catch {
    return [];
  }
}

export function toAppCookie(c) {
  return { name: c.name, value: c.value, domain: c.domain, path: c.path, secure: c.secure, hostOnly: c.hostOnly };
}

/** Fills elements marked data-i18n (text), data-i18n-title, data-i18n-placeholder and data-i18n-aria. */
export function localize(root = document) {
  document.documentElement.lang = chrome.i18n.getUILanguage();
  for (const el of root.querySelectorAll("[data-i18n]")) el.textContent = t(el.dataset.i18n);
  for (const el of root.querySelectorAll("[data-i18n-title]")) el.title = t(el.dataset.i18nTitle);
  for (const el of root.querySelectorAll("[data-i18n-placeholder]")) el.placeholder = t(el.dataset.i18nPlaceholder);
  for (const el of root.querySelectorAll("[data-i18n-aria]")) el.setAttribute("aria-label", t(el.dataset.i18nAria));
}
