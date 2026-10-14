// "Check connection": is the native host registered for this browser, and does the app speak this extension's protocol?
import { Rules, IS_FIREFOX, sendNative, localize, t } from "./shared.js";

localize();
const $ = (id) => document.getElementById(id);
const manifest = chrome.runtime.getManifest();
const browserName = IS_FIREFOX ? "Firefox" : navigator.userAgentData?.brands?.find((b) => !/Not.A.Brand|Chromium/i.test(b.brand))?.brand ?? "Chromium";
$("extension").textContent = t("checkExtensionValue", manifest.version, browserName, Rules.PROTOCOL);
$("retry").addEventListener("click", check);
check();

async function check() {
  show("checking");
  $("host").textContent = "…";
  $("app").textContent = "…";
  let reply = null;
  let error = null;
  try {
    reply = await sendNative({ type: "hello" }, 8000);
  } catch (e) {
    error = e;
  }
  const state = Rules.connectionState(reply, error);
  $("host").textContent = state === "missing" || state === "forbidden" ? t("checkHostMissing") : t("checkHostFound");
  $("app").textContent = reply?.protocol
    ? t("checkAppValue", reply.appVersion ?? "?", reply.protocol, reply.running === false ? t("checkAppStopped") : t("checkAppRunning"))
    : reply ? t("checkAppOld") : error ? error.message : "—";
  show(state);
}

function show(state) {
  const ok = state === "running" || state === "ready";
  $("result").className = `result ${state === "checking" ? "" : ok ? "ok" : "bad"}`;
  $("resultTitle").textContent = t(`state_${state}`);
  $("resultText").textContent = state === "checking" ? "" : t(`checkText_${state}`);
  const steps = state === "missing" || state === "forbidden" || state === "outdated"
    ? [t("checkStepStart"), t("checkStepRegister"), IS_FIREFOX ? t("checkStepFirefox") : t("checkStepChromium"), t("checkStepRetry")]
    : [];
  $("steps").replaceChildren(...steps.map((text) => Object.assign(document.createElement("li"), { textContent: text })));
  $("steps").hidden = steps.length === 0;
  $("register").hidden = steps.length === 0;
}
