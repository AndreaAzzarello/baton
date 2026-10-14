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
$("mediaButton").checked = settings.mediaButton;
$("mediaButton").addEventListener("change", () => save({ mediaButton: $("mediaButton").checked }));

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
