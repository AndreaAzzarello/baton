import { Rules, loadSettings, sendNative, localize, t } from "./shared.js";

localize();
const $ = (id) => document.getElementById(id);
const settings = await loadSettings();
const save = (values) => chrome.storage.local.set(values);

// Mode
for (const radio of document.querySelectorAll("input[name=mode]")) {
  radio.checked = radio.value === settings.mode;
  radio.addEventListener("change", () => radio.checked && save({ mode: radio.value }));
}

// Rules
const text = (id, key, transform = (v) => v.trim()) => {
  $(id).value = settings[key];
  $(id).addEventListener("change", () => save({ [key]: transform($(id).value) }));
};
text("extensions", "extensions");
text("alwaysSites", "alwaysSites");
text("neverSites", "neverSites");
text("mediaDisabledSites", "mediaDisabledSites");
$("minSize").value = settings.minSizeMB;
$("minSize").addEventListener("change", () => save({ minSizeMB: Math.max(0, Number($("minSize").value) || 0) }));
for (const radio of document.querySelectorAll("input[name=mediaPanel]")) {
  radio.checked = radio.value === settings.mediaPanel;
  radio.addEventListener("change", () => radio.checked && save({ mediaPanel: radio.value }));
}

// This site
const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
const host = /^https?:/i.test(tab?.url ?? "") ? Rules.hostOf(tab.url) : "";
if (host) {
  $("site").hidden = false;
  $("siteName").textContent = t("siteTitle", host);
  const listed = (key) => Rules.parseSites(settings[key]).includes(host);
  $("siteRule").value = listed("alwaysSites") ? "always" : listed("neverSites") ? "never" : "";
  $("siteRule").addEventListener("change", async () => {
    const current = await loadSettings();
    const without = (key) => Rules.parseSites(current[key]).filter((s) => s !== host);
    const always = without("alwaysSites");
    const never = without("neverSites");
    if ($("siteRule").value === "always") always.push(host);
    if ($("siteRule").value === "never") never.push(host);
    await save({ alwaysSites: always.join(" "), neverSites: never.join(" ") });
    $("alwaysSites").value = always.join(" ");
    $("neverSites").value = never.join(" ");
  });
  $("siteButton").checked = !listed("mediaDisabledSites");
  $("siteButton").addEventListener("change", async () => {
    const current = await loadSettings();
    const sites = Rules.parseSites(current.mediaDisabledSites).filter((s) => s !== host);
    if (!$("siteButton").checked) sites.push(host);
    await save({ mediaDisabledSites: sites.join(" ") });
    $("mediaDisabledSites").value = sites.join(" ");
  });
}

// The videos this tab played: listed here (the only place with "only from the extension's menu"), each with its qualities.
if (tab?.id !== undefined) {
  const media = await chrome.runtime.sendMessage({ type: "getMedia", tabId: tab.id }).catch(() => null);
  if (media?.ok && media.items?.length) showVideos(media);
}

function showVideos(media) {
  $("videos").hidden = false;
  $("showPanel").addEventListener("click", async () => {
    await chrome.tabs.sendMessage(tab.id, { type: "showPanel" }, { frameId: 0 }).catch(() => {});
    window.close();
  });
  const list = $("videoList");
  if (media.drm) {
    const li = document.createElement("li");
    li.className = "hint";
    li.textContent = t("mediaDrm");
    list.append(li);
    return;
  }
  [...media.items].reverse().forEach((item, index) => {
    const li = document.createElement("li");
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = t("mediaStream", index + 1, item.kind === "hls" ? "HLS" : item.kind === "dash" ? "DASH" : t("mediaKindFile"));
    name.title = item.url;
    const qualities = document.createElement("button");
    qualities.type = "button";
    qualities.className = "small";
    qualities.textContent = t("popupQualities");
    qualities.addEventListener("click", () => listQualities(item, li, qualities));
    li.append(name, qualities);
    list.append(li);
  });
}

async function listQualities(item, li, opener) {
  opener.disabled = true;
  opener.textContent = t("mediaLoading");
  const reply = await chrome.runtime.sendMessage({ type: "probe", tabId: tab.id, url: item.url }).catch(() => null);
  opener.remove();
  const box = document.createElement("ul");
  box.className = "qualities";
  box.setAttribute("aria-label", t("mediaQualities"));
  li.append(box);
  const media = reply?.ok ? reply.media : null;
  if (!media || media.blocked || !media.variants?.length) {
    const p = document.createElement("li");
    p.className = "hint";
    p.textContent = !reply?.ok ? reply?.error || t("errorBadReply")
      : ({ drm: t("mediaDrm"), terms: t("mediaTerms"), live: t("mediaLive") }[media?.blocked] ?? media?.message ?? t("errorBadReply"));
    box.append(p);
    return;
  }
  for (const variant of media.variants) {
    const row = document.createElement("li");
    const label = document.createElement("span");
    const size = Rules.formatSize(variant.estimatedBytes, chrome.i18n.getUILanguage());
    label.textContent = size ? `${variant.label} · ${variant.kind === "file" ? size : "≈ " + size}` : variant.label;
    const get = document.createElement("button");
    get.type = "button";
    get.className = "small primary";
    get.textContent = t("mediaDownload");
    get.setAttribute("aria-label", t("mediaDownloadQuality", variant.label));
    get.addEventListener("click", async () => {
      get.disabled = true;
      get.textContent = t("mediaSending");
      const result = await chrome.runtime.sendMessage({ type: "addMedia", tabId: tab.id, url: item.url, variantId: variant.id, label: variant.label })
        .catch(() => null);
      get.textContent = result?.ok ? t("mediaSent") : t("mediaDownload");
      get.disabled = Boolean(result?.ok);
      if (!result?.ok) get.title = result?.error || t("errorBadReply");
    });
    row.append(label, get);
    box.append(row);
  }
}

// Queues and connection state. Asking never starts the app.
showQueues([], settings.queue);
$("queue").addEventListener("change", () => save({ queue: $("queue").value }));
try {
  const reply = await sendNative({ type: "hello" }, 5000);
  const state = Rules.connectionState(reply);
  setStatus(state, state === "running" || state === "ready" ? "ok" : "bad");
  if (state === "running" || (state === "outdated" && reply?.ok !== false)) {
    const list = await sendNative({ type: "queues" }, 5000);
    showQueues(list?.queues ?? [], settings.queue);
  }
} catch (error) {
  setStatus(Rules.connectionState(null, error), "bad");
}

// The first queue is the main one, sent as "" so it keeps working if renamed in the app.
function showQueues(names, selected) {
  const queue = $("queue");
  queue.replaceChildren();
  const all = names.length ? names : [t("mainQueue")];
  all.forEach((name, index) => queue.add(new Option(name, index === 0 ? "" : name)));
  if (selected && !all.includes(selected)) queue.add(new Option(t("queueMissing", selected), selected));
  queue.value = selected || "";
}

/** A short label in the chip; the full sentence as its tooltip. */
function setStatus(state, kind) {
  $("statusText").textContent = t(`chip_${state}`);
  $("status").title = t(`state_${state}`);
  $("status").className = `chip ${kind}`;
}
