// A small stand-in for the extension APIs, enough to load background.js in Node and drive its listeners.
export function createFakeChrome({ firefox = false, nativeReply } = {}) {
  const listeners = {};
  const event = (name) => ({
    addListener: (fn) => {
      (listeners[name] ??= []).push(fn);
    },
  });
  const calls = [];
  const record = (name) => async (...args) => {
    calls.push([name, ...args]);
  };
  const local = {};
  const session = {};
  const area = (store) => ({
    get: async (keys) => {
      if (keys === null || keys === undefined) return { ...store };
      const list = Array.isArray(keys) ? keys : [keys];
      return Object.fromEntries(list.filter((k) => k in store).map((k) => [k, store[k]]));
    },
    set: async (values) => Object.assign(store, values),
    remove: async (keys) => [].concat(keys).forEach((k) => delete store[k]),
  });

  const downloads = {
    onCreated: event("downloads.onCreated"),
    cancel: record("cancel"),
    erase: record("erase"),
    pause: record("pause"),
    resume: record("resume"),
    removeFile: record("removeFile"),
    download: record("download"),
  };
  if (!firefox) downloads.onDeterminingFilename = event("downloads.onDeterminingFilename");

  const chrome = {
    runtime: {
      id: "test-extension",
      onInstalled: event("runtime.onInstalled"),
      onStartup: event("runtime.onStartup"),
      onMessage: event("runtime.onMessage"),
      sendNativeMessage: async (host, message) => {
        calls.push(["native", message]);
        return nativeReply(message);
      },
      getManifest: () => ({ version: "0.7.0" }),
    },
    storage: { local: area(local), session: area(session), onChanged: event("storage.onChanged") },
    downloads,
    contextMenus: { removeAll: (cb) => cb?.(), create: () => {}, onClicked: event("contextMenus.onClicked") },
    action: { setIcon: async () => {}, setTitle: async () => {}, setBadgeText: record("badge"), setBadgeBackgroundColor: async () => {} },
    notifications: { create: (options) => calls.push(["notify", options.message]) },
    cookies: { getAll: async () => [] },
    webRequest: {
      onBeforeRequest: event("webRequest.onBeforeRequest"),
      onBeforeSendHeaders: event("webRequest.onBeforeSendHeaders"),
      onHeadersReceived: event("webRequest.onHeadersReceived"),
    },
    tabs: { onUpdated: event("tabs.onUpdated"), onRemoved: event("tabs.onRemoved"), get: async (id) => ({ id, url: "https://page.test/", incognito: false }), sendMessage: async () => {} },
    scripting: { executeScript: async () => [] },
    i18n: { getMessage: (key, subs) => `${key}${subs?.length ? ":" + subs.join("|") : ""}`, getUILanguage: () => "it" },
  };
  return { chrome, listeners, calls, local, session };
}

/** Lets pending promise callbacks run. */
export async function settle(rounds = 20) {
  for (let i = 0; i < rounds; i++) await new Promise((resolve) => setImmediate(resolve));
}
