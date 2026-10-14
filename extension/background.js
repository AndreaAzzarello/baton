// Background of the extension: a non-persistent service worker (Chromium) or event page (Firefox). It wakes up only
// for browser events, never polls, and keeps what it must remember in chrome.storage.session.
import { Rules, HOST, IS_FIREFOX, loadSettings, sendNative, cookiesFor, toAppCookie, t } from "./shared.js";

const MENU_LINK = "dm-link";
const MENU_MEDIA = "dm-media";
const MENU_ALL = "dm-all";

/** A form submission is matched to the download it starts within this time. */
const POST_TTL_MS = 60_000;

/** Alt+click: the next download of that link stays in the browser for this long. */
const SKIP_TTL_MS = 15_000;

const MAX_MEDIA_PER_TAB = 20;

// ---------------------------------------------------------------- setup

chrome.runtime.onInstalled.addListener(setup);
chrome.runtime.onStartup.addListener(setup);
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.mode) updateIcon();
});

function setup() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: MENU_LINK, title: t("menuLink"), contexts: ["link"] });
    chrome.contextMenus.create({ id: MENU_MEDIA, title: t("menuLink"), contexts: ["image", "video", "audio"] });
    chrome.contextMenus.create({ id: MENU_ALL, title: t("menuAll"), contexts: ["page", "frame", "selection"] });
  });
  updateIcon();
}

/** The icon tells the mode: colored when downloads are intercepted, grey when only the context menu is used. */
async function updateIcon() {
  const { mode } = await loadSettings();
  const suffix = mode === "auto" ? "" : "-off";
  await chrome.action.setIcon({ path: { 16: `icons/icon16${suffix}.png`, 32: `icons/icon32${suffix}.png` } });
  await chrome.action.setTitle({ title: t(mode === "auto" ? "titleAuto" : "titleMenu") });
}

function notify(message) {
  chrome.notifications.create({ type: "basic", iconUrl: "icons/icon128.png", title: "DownloadManager", message });
}

/** "!" on the icon after the app could not be reached; cleared by the next success. */
async function setProblem(problem) {
  await chrome.action.setBadgeBackgroundColor({ color: "#C62828" });
  await chrome.action.setBadgeText({ text: problem ? "!" : "" });
}

// ---------------------------------------------------------------- handing downloads to the app

/** Sends a download to the app; throws when the app refuses it or does not answer in time. */
async function handOver({ url, referrer, fileName, incognito, post }) {
  const { queue } = await loadSettings();
  const download = {
    url,
    fileName: fileName || null,
    referrer: referrer && /^https?:/i.test(referrer) ? referrer : null,
    userAgent: navigator.userAgent,
    cookies: await cookiesFor(url, incognito),
    queue: queue || null, // older apps ignore it; newer ones use the main queue for unknown names
  };
  if (post) Object.assign(download, { method: "POST", body: post.body, bodyType: post.bodyType });
  const reply = await sendNative({ type: "add", download });
  if (!reply?.ok) throw new Error(reply?.error || t("errorBadReply"));
  await setProblem(false);
}

/** Alt+click marks from the content script, with their expiry. */
async function getSkips() {
  const { skips } = await chrome.storage.session.get("skips");
  return Rules.pruneSkips(skips, Date.now());
}

if (!IS_FIREFOX) {
  // Chromium: the browser has already followed redirects and knows the name, size and type. The download waits here
  // (suggest is not called yet) while the app is asked; only once the app has it is the browser's copy cancelled.
  // If the app does not answer, suggest() lets the browser carry on: nothing is lost, nothing is downloaded twice.
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    interceptChromium(item, suggest);
    return true; // suggest is called asynchronously
  });
} else {
  // Firefox has no onDeterminingFilename: the download is paused as soon as it is created, then cancelled and its
  // partial file removed once the app has it, or resumed if the app does not answer.
  chrome.downloads.onCreated.addListener(interceptFirefox);
}

async function interceptChromium(item, suggest) {
  let released = false;
  const release = () => {
    if (released) return;
    released = true;
    try {
      suggest();
    } catch {
      // The download is gone already.
    }
  };
  try {
    const target = await interceptionTarget(item);
    if (!target) return release();
    await handOver(target);
    released = true; // the browser's copy is dropped, never resumed
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } catch (error) {
    release();
    await setProblem(true);
    notify(t("fallbackNotice", error.message));
  }
}

async function interceptFirefox(item) {
  const target = await interceptionTarget(item);
  if (!target) return;
  await chrome.downloads.pause(item.id).catch(() => {});
  try {
    await handOver(target);
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.removeFile(item.id).catch(() => {}); // the partial file, if Firefox kept it
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } catch (error) {
    await setProblem(true);
    try {
      await chrome.downloads.resume(item.id);
    } catch {
      // Not resumable: start it again in the browser (marked as ours, so it is not intercepted again).
      const again = { url: item.url };
      if (target.post) Object.assign(again, { method: "POST", body: target.post.body,
        headers: [{ name: "Content-Type", value: "application/x-www-form-urlencoded" }] });
      await chrome.downloads.cancel(item.id).catch(() => {});
      await chrome.downloads.erase({ id: item.id }).catch(() => {});
      await chrome.downloads.download(again).catch(() => {});
    }
    notify(t("fallbackNotice", error.message));
  }
}

/** What to send to the app for a browser download, or null when the browser keeps it. */
async function interceptionTarget(item) {
  const settings = await loadSettings();
  const skips = await getSkips();
  const decision = Rules.decide(item, settings, skips, chrome.runtime.id, Date.now());
  if (!decision.intercept) return null;

  // A download started by submitting a form: the app repeats the POST, unless the browser was redirected to a GET.
  const post = await findPost(item.url);
  if (post?.unsupported) return null; // a file upload or an encoding that cannot be repeated: left to the browser
  const redirected = item.finalUrl && item.finalUrl !== item.url;
  return {
    url: (post && !redirected) ? item.url : (item.finalUrl || item.url),
    referrer: post?.referrer || item.referrer,
    fileName: Rules.baseName(item.filename),
    incognito: item.incognito === true,
    post: post && !redirected ? post : null,
  };
}

// ---------------------------------------------------------------- forms (POST)

// Forms seen while this background runs. The download may be created before the copy in storage.session is written
// (it happens in Firefox), so memory is checked first; storage covers a background that was restarted in between.
const recentPosts = new Map();
const postWrites = new Set();

function remember(values) {
  const write = chrome.storage.session.set(values).finally(() => postWrites.delete(write));
  postWrites.add(write);
}

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (details.method !== "POST" || details.tabId < 0) return;
    const body = details.requestBody;
    const entry = {
      url: details.url,
      tabId: details.tabId,
      time: Date.now(),
      formData: body?.formData ?? null,
      unsupported: !body || !!body.raw || !!body.error,
      referrer: details.documentUrl || details.originUrl || null,
    };
    recentPosts.set(`post:${details.requestId}`, entry);
    remember({ [`post:${details.requestId}`]: entry });
  },
  { urls: ["<all_urls>"], types: ["main_frame", "sub_frame"] },
  ["requestBody"]);

chrome.webRequest.onBeforeSendHeaders.addListener(
  (details) => {
    if (details.method !== "POST" || details.tabId < 0) return;
    const type = details.requestHeaders?.find((h) => h.name.toLowerCase() === "content-type")?.value ?? "";
    recentPosts.set(`posttype:${details.requestId}`, type);
    remember({ [`posttype:${details.requestId}`]: type });
  },
  { urls: ["<all_urls>"], types: ["main_frame", "sub_frame"] },
  ["requestHeaders"]);

/** The latest form submitted to `url` in the last minute, encoded for the app; old entries are forgotten. */
async function findPost(url) {
  await Promise.allSettled([...postWrites]);
  const all = { ...(await chrome.storage.session.get(null)), ...Object.fromEntries(recentPosts) };
  const now = Date.now();
  const stale = [];
  let best = null;
  for (const [key, value] of Object.entries(all)) {
    if (!key.startsWith("post:")) continue;
    const id = key.slice(5);
    if (now - value.time > POST_TTL_MS) {
      stale.push(key, `posttype:${id}`);
      continue;
    }
    if (value.url === url && (!best || value.time > best.time)) best = { ...value, type: all[`posttype:${id}`] };
  }
  if (stale.length) {
    stale.forEach((key) => recentPosts.delete(key));
    await chrome.storage.session.remove(stale);
  }
  if (!best) return null;
  const encoded = best.unsupported ? null : Rules.encodeForm({ formData: best.formData }, best.type);
  if (!encoded) return { unsupported: true };
  let referrer = best.referrer;
  if (!referrer && best.tabId >= 0) referrer = (await chrome.tabs.get(best.tabId).catch(() => null))?.url ?? null;
  return { ...encoded, referrer };
}

// ---------------------------------------------------------------- context menu

chrome.contextMenus.onClicked.addListener((info, tab) => onMenuClicked(info, tab));

// The same handler, reachable from the extension's own DevTools console (and automated tests, which cannot open a
// context menu). Only the extension's own pages can reach its service worker's globals.
globalThis.dmMenuClicked = (info, tab) => onMenuClicked(info, tab);

async function onMenuClicked(info, tab) {
  if (info.menuItemId === MENU_ALL) return downloadAll(info, tab);
  const url = info.menuItemId === MENU_MEDIA ? info.srcUrl : info.linkUrl;
  if (url?.startsWith("blob:")) return notify(t("errorBlobMedia"));
  if (!url || !/^(https?|ftps?|ftpes):/i.test(url)) return notify(t("errorNotDownloadable"));
  try {
    await handOver({ url, referrer: info.pageUrl ?? tab?.url, incognito: tab?.incognito === true });
  } catch (error) {
    await setProblem(true);
    notify(t("errorNotReceived", error.message));
  }
}

/** "Download all": every link and image of the page (or frame), chosen from in the app. */
async function downloadAll(info, tab) {
  if (!tab?.id) return;
  let page;
  try {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId: tab.id, frameIds: [info.frameId || 0] },
      func: collectPageLinks,
    });
    page = result?.result;
  } catch (error) {
    return notify(t("errorPageAccess", error.message));
  }
  if (!page?.items?.length) return notify(t("errorNoLinks"));

  // Cookies of the sites the links point to (each one is later sent only to its own site).
  const hosts = [...new Set([Rules.hostOf(page.url), ...page.items.map((i) => Rules.hostOf(i.url))].filter(Boolean))].slice(0, 50);
  const cookies = [];
  if (!tab.incognito) {
    const seen = new Set();
    for (const host of hosts) {
      for (const c of await chrome.cookies.getAll({ domain: host }).catch(() => [])) {
        const key = `${c.domain}|${c.path}|${c.name}`;
        if (!seen.has(key) && cookies.length < 1200) {
          seen.add(key);
          cookies.push(toAppCookie(c));
        }
      }
    }
  }
  const { queue } = await loadSettings();
  try {
    const reply = await sendNative({
      type: "batch",
      batch: { pageUrl: page.url, pageTitle: page.title, items: page.items, userAgent: navigator.userAgent, cookies, queue: queue || null },
    });
    if (!reply?.ok) throw new Error(reply?.error || t("errorBadReply"));
    await setProblem(false);
  } catch (error) {
    await setProblem(true);
    notify(t("errorNotReceived", error.message));
  }
}

/** Runs in the page: its links and images, once each, with their text. */
function collectPageLinks() {
  const items = [];
  const seen = new Set();
  const add = (url, text, kind) => {
    if (!url || seen.has(url) || !/^(https?|ftps?|ftpes):/i.test(url)) return;
    seen.add(url);
    items.push({ url: url.slice(0, 8000), text: String(text || "").replace(/\s+/g, " ").trim().slice(0, 300), kind });
  };
  for (const a of document.querySelectorAll("a[href], area[href]")) add(a.href, a.textContent || a.title || a.getAttribute("aria-label"), "link");
  for (const img of document.images) add(img.currentSrc || img.src, img.alt || img.title, "image");
  for (const media of document.querySelectorAll("video[src], audio[src], source[src]")) add(media.src, media.title, "link");
  return { url: location.href, title: document.title, items: items.slice(0, 10000) };
}

// ---------------------------------------------------------------- media detection

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0 || details.statusCode < 200 || details.statusCode >= 300 || details.incognito) return;
    const header = (name) => details.responseHeaders?.find((h) => h.name.toLowerCase() === name)?.value;
    const length = Number(header("content-length"));
    const kind = Rules.classifyMedia(details.url, header("content-type"), details.type, Number.isFinite(length) ? length : -1);
    if (kind) recordMedia(details.tabId, { url: details.url, kind, size: kind === "file" && length > 0 ? length : null });
  },
  { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "object", "other"] },
  ["responseHeaders"]);

// Writes to one tab's list are chained so two responses arriving together do not overwrite each other.
const mediaWrites = new Map();

function recordMedia(tabId, entry) {
  const previous = mediaWrites.get(tabId) ?? Promise.resolve();
  const next = previous.then(async () => {
    const tab = await chrome.tabs.get(tabId).catch(() => null);
    if (!tab || tab.incognito) return; // private windows: nothing is recorded or sent
    const key = `media:${tabId}`;
    const state = (await chrome.storage.session.get(key))[key] ?? { items: [], drm: false };
    if (state.items.some((i) => i.url === entry.url)) return;
    state.items = [...state.items, { ...entry, time: Date.now() }].slice(-MAX_MEDIA_PER_TAB);
    await chrome.storage.session.set({ [key]: state });
    await announce(tabId, state);
  }).catch(() => {});
  mediaWrites.set(tabId, next);
}

/** Tells the page's top frame what was found, and shows the count on the icon for that tab. */
async function announce(tabId, state) {
  const streams = state.items.length;
  await chrome.action.setBadgeBackgroundColor({ tabId, color: "#2457D1" }).catch(() => {});
  await chrome.action.setBadgeText({ tabId, text: streams ? String(streams) : "" }).catch(() => {});
  await chrome.tabs.sendMessage(tabId, { type: "media", ...state }, { frameId: 0 }).catch(() => {});
}

async function forgetMedia(tabId) {
  mediaWrites.delete(tabId);
  await chrome.storage.session.remove(`media:${tabId}`);
  await chrome.action.setBadgeText({ tabId, text: "" }).catch(() => {});
}

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading" && change.url) forgetMedia(tabId);
});
chrome.tabs.onRemoved.addListener((tabId) => forgetMedia(tabId));

// ---------------------------------------------------------------- messages from the content script

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  const tab = sender.tab;
  if (!tab?.id || sender.id !== chrome.runtime.id) return false;
  handleContentMessage(message, tab, sender)
    .then(reply)
    .catch((error) => reply({ ok: false, error: error.message }));
  return true; // answered asynchronously
});

async function handleContentMessage(message, tab, sender) {
  const key = `media:${tab.id}`;
  switch (message?.type) {
    case "getMedia": {
      const settings = await loadSettings();
      const state = (await chrome.storage.session.get(key))[key] ?? { items: [], drm: false };
      const disabled = Rules.parseSites(settings.mediaDisabledSites).some((s) => Rules.hostMatches(Rules.hostOf(tab.url), s));
      return { ok: true, ...state, enabled: settings.mediaButton && !disabled && !tab.incognito };
    }
    case "drm": {
      // The page uses Encrypted Media Extensions: whatever it plays is protected.
      const state = (await chrome.storage.session.get(key))[key] ?? { items: [], drm: false };
      if (!state.drm) {
        state.drm = true;
        await chrome.storage.session.set({ [key]: state });
        if (sender.frameId !== 0) await announce(tab.id, state);
      }
      return { ok: true };
    }
    case "skip": {
      if (typeof message.url !== "string" || !/^https?:/i.test(message.url)) return { ok: false };
      const skips = await getSkips();
      skips[message.url] = Date.now() + SKIP_TTL_MS;
      await chrome.storage.session.set({ skips });
      return { ok: true };
    }
    case "hideOnSite": {
      const settings = await loadSettings();
      const host = Rules.hostOf(tab.url);
      if (host && !Rules.parseSites(settings.mediaDisabledSites).includes(host))
        await chrome.storage.local.set({ mediaDisabledSites: `${settings.mediaDisabledSites} ${host}`.trim() });
      return { ok: true };
    }
    case "probe":
    case "addMedia": {
      const state = (await chrome.storage.session.get(key))[key] ?? { items: [] };
      // Only addresses this tab actually loaded can be sent: a page cannot make the app fetch anything else.
      if (!state.items.some((i) => i.url === message.url)) return { ok: false, error: t("errorUnknownMedia") };
      if (Rules.isRestricted(tab.url) || Rules.isRestricted(message.url)) return { ok: false, blocked: "terms", error: t("mediaTerms") };
      const { queue } = await loadSettings();
      const media = {
        url: message.url,
        pageUrl: tab.url,
        title: tab.title,
        referrer: tab.url,
        userAgent: navigator.userAgent,
        cookies: await cookiesFor(message.url, tab.incognito),
      };
      if (message.type === "addMedia") Object.assign(media, { variantId: String(message.variantId || ""), label: message.label || null, queue: queue || null });
      const reply = await sendNative({ type: message.type === "probe" ? "probeMedia" : "addMedia", media }, 30_000);
      if (!reply?.ok) return { ok: false, error: reply?.error || t("errorBadReply"), outdated: !reply?.protocol };
      return { ok: true, media: reply.media };
    }
    default:
      return { ok: false };
  }
}
