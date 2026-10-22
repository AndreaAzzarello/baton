// Pure decision logic shared by the background script, the content script and the tests (node --test).
// A classic script on purpose: content scripts cannot import modules, so it publishes itself on globalThis.
(function (root) {
  "use strict";

  /** Version of the messages exchanged with the app (1 = extension 0.2-0.5, 2 = 0.6, 3 = 0.7). */
  const PROTOCOL = 3;

  /** How subtitles are saved: a file next to the video (WebVTT or SubRip), or inside it (the app needs ffmpeg). */
  const SUBTITLE_MODES = ["vtt", "srt", "embed"];

  /** How long the app may take to accept a download before the browser keeps it (the host waits 12 s for a cold start). */
  const APP_TIMEOUT_MS = 15000;

  /** Sites whose terms of service forbid downloading their videos (kept in step with the app's list). */
  const RESTRICTED_SITES = [
    "youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com",
    "netflix.com", "nflxvideo.net",
    "primevideo.com", "amazonvideo.com",
    "disneyplus.com", "disney-plus.net", "dssott.com",
    "hulu.com", "max.com", "hbomax.com",
    "tv.apple.com",
    "spotify.com", "scdn.co",
    "twitch.tv", "ttvnw.net",
    "tiktok.com", "instagram.com", "facebook.com", "fbcdn.net",
    "dazn.com", "paramountplus.com", "peacocktv.com",
  ];

  const MANIFEST_TYPES = {
    "application/vnd.apple.mpegurl": "hls",
    "application/x-mpegurl": "hls",
    "audio/mpegurl": "hls",
    "audio/x-mpegurl": "hls",
    "application/dash+xml": "dash",
    "video/vnd.mpeg.dash.mpd": "dash",
  };

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
    "application/pdf": "pdf",
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

  const DEFAULTS = {
    // "auto": downloads the browser starts are handed to the app; "menu": only the context menu does.
    mode: "auto",
    extensions: "zip rar 7z gz bz2 xz tar iso img exe msi msix apk dmg bin mp4 mkv avi mov wmv webm mp3 flac wav",
    minSizeMB: 1,
    alwaysSites: "",
    neverSites: "",
    // Queue for downloads sent from the browser; "" = the app's main queue.
    queue: "",
    // How the page shows the videos it plays: "always" (the button with its panel), "minimized" (a small tab on the edge
    // that opens it, the default) or "menu" (nothing in the page: the extension's icon counts them and its popup lists them).
    mediaPanel: "minimized",
    mediaDisabledSites: "",
  };

  const MEDIA_PANELS = ["always", "minimized", "menu"];

  /** Settings as stored, completed with defaults; "intercept: false" of 0.5 and earlier becomes the context-menu mode. */
  function normalizeSettings(stored) {
    const settings = { ...DEFAULTS, ...stored };
    if (stored && stored.mode === undefined && stored.intercept === false) settings.mode = "menu";
    if (settings.mode !== "auto" && settings.mode !== "menu") settings.mode = DEFAULTS.mode;
    // Up to 1.1 the video button could only be on or off: off is now "only from the extension's menu".
    if (stored && stored.mediaPanel === undefined && stored.mediaButton === false) settings.mediaPanel = "menu";
    if (!MEDIA_PANELS.includes(settings.mediaPanel)) settings.mediaPanel = DEFAULTS.mediaPanel;
    settings.minSizeMB = Math.max(0, Number(settings.minSizeMB) || 0);
    delete settings.intercept;
    delete settings.mediaButton;
    return settings;
  }

  /**
   * What the page shows for its videos: "none", the small "tab" on the edge, or the "panel" (the button with its list).
   * Closing (×) hides it for the tab until a video at an address not seen when it was closed; "hide on this page" until
   * the page is loaded again; full screen hides it while it lasts.
   */
  function mediaView({ mode, enabled, items, dismissed = [], hiddenOnPage = false, minimized = false, fullscreen = false, requested = false }) {
    if (requested && !fullscreen && items?.length) return "panel"; // "Show in the page", asked from the popup
    if (!enabled || mode === "menu" || hiddenOnPage || fullscreen || !items?.length) return "none";
    if (items.every((item) => dismissed.includes(item.url))) return "none";
    return minimized ? "tab" : "panel";
  }

  /**
   * The distance from the bottom of the window that keeps a box (its current rectangle) off the strip where players
   * keep their controls (the last `controls` pixels of each visible player it overlaps): above that strip if needed.
   */
  function clearOfControls(box, players, viewportHeight, controls = 64, margin = 8) {
    const height = box.bottom - box.top;
    let bottom = viewportHeight - box.bottom;
    for (const player of players) {
      const top = viewportHeight - bottom - height;
      const strip = player.bottom - controls;
      if (box.left < player.right && box.right > player.left && viewportHeight - bottom > strip && top < player.bottom)
        bottom = viewportHeight - strip + margin;
    }
    return bottom;
  }

  /** The button's position on one more site; the sites used least recently are forgotten past `limit`. */
  function rememberPosition(positions, host, position, limit = 100) {
    const next = { ...(positions || {}) };
    delete next[host];
    next[host] = position;
    const hosts = Object.keys(next);
    for (const old of hosts.slice(0, Math.max(0, hosts.length - limit))) delete next[old];
    return next;
  }

  /** Host names (and "*.domain" patterns) from a text box: separated by spaces, commas, semicolons or new lines. */
  function parseSites(text) {
    return String(text || "")
      .split(/[\s,;]+/)
      .map((s) => s.trim().toLowerCase().replace(/^https?:\/\//, "").replace(/\/.*$/, "").replace(/^\*\./, ""))
      .filter(Boolean);
  }

  /** True when the host is the site or one of its subdomains. */
  function hostMatches(host, site) {
    host = String(host || "").toLowerCase().replace(/\.$/, "");
    site = String(site || "").toLowerCase();
    return host === site || host.endsWith("." + site);
  }

  function hostOf(url) {
    try {
      return new URL(url).hostname;
    } catch {
      return "";
    }
  }

  function isRestricted(url) {
    const host = hostOf(url);
    return !!host && RESTRICTED_SITES.some((site) => hostMatches(host, site));
  }

  function extensionOf(name) {
    const match = /\.([a-z0-9]{1,8})$/i.exec(String(name || ""));
    return match ? match[1].toLowerCase() : null;
  }

  function baseName(path) {
    return path ? String(path).split(/[\\/]/).pop() : "";
  }

  function pathOf(url) {
    try {
      return new URL(url).pathname;
    } catch {
      return "";
    }
  }

  /**
   * Whether a download the browser started goes to the app. `item` has url, finalUrl, filename, mime, totalBytes
   * (-1 or 0 when unknown), incognito, byExtensionId, startTime; `skips` maps addresses to an expiry time (Alt+click).
   * Returns { intercept, reason } so the decision can be explained and tested.
   */
  function decide(item, settings, skips, ownId, now) {
    const url = item.finalUrl || item.url || "";
    if (settings.mode !== "auto") return { intercept: false, reason: "menu-only" };
    if (item.incognito) return { intercept: false, reason: "incognito" };
    if (ownId && item.byExtensionId === ownId) return { intercept: false, reason: "own" };
    if (!/^https?:/i.test(url)) return { intercept: false, reason: "scheme" }; // blob:, data:, file: exist only in the browser
    if (item.startTime && now - Date.parse(item.startTime) > 60000) return { intercept: false, reason: "old" }; // restored at startup
    for (const candidate of [item.url, item.finalUrl, item.referrerLink]) {
      if (candidate && skips && skips[candidate] && skips[candidate] > now) return { intercept: false, reason: "skip" };
    }
    const host = hostOf(url);
    const pageHost = hostOf(item.referrer || "");
    const matchesAny = (list) => parseSites(list).some((site) => hostMatches(host, site) || (pageHost && hostMatches(pageHost, site)));
    if (matchesAny(settings.neverSites)) return { intercept: false, reason: "never-site" };
    if (matchesAny(settings.alwaysSites)) return { intercept: true, reason: "always-site" };

    const minBytes = settings.minSizeMB * 1024 * 1024;
    if (minBytes > 0 && item.totalBytes > 0 && item.totalBytes < minBytes) return { intercept: false, reason: "small" };

    const wanted = new Set(
      String(settings.extensions || "").split(/[\s,;]+/).map((e) => e.replace(/^\./, "").toLowerCase()).filter(Boolean));
    const candidates = [
      extensionOf(baseName(item.filename)),
      extensionOf(pathOf(url)),
      MIME_EXTENSIONS[String(item.mime || "").toLowerCase().split(";")[0]],
    ];
    return candidates.some((ext) => ext && wanted.has(ext))
      ? { intercept: true, reason: "type" }
      : { intercept: false, reason: "type-not-listed" };
  }

  /** Forgets Alt+click marks that expired. */
  function pruneSkips(skips, now) {
    const kept = {};
    for (const [url, until] of Object.entries(skips || {})) if (until > now) kept[url] = until;
    return kept;
  }

  /**
   * A form submission seen by webRequest, as the app needs it: fields URL-encoded and the form's encoding, or null
   * when it cannot be repeated faithfully (a file upload, or a body the browser only gives as raw bytes).
   */
  function encodeForm(requestBody, contentType) {
    if (!requestBody || requestBody.error || !requestBody.formData) return null;
    const type = String(contentType || "").toLowerCase();
    const bodyType = type.startsWith("multipart/form-data") ? "multipart/form-data" : "application/x-www-form-urlencoded";
    if (type && !type.startsWith("multipart/form-data") && !type.startsWith("application/x-www-form-urlencoded")) return null;
    const parts = [];
    for (const [name, values] of Object.entries(requestBody.formData)) {
      for (const value of values) parts.push(encodeURIComponent(name) + "=" + encodeURIComponent(value));
    }
    return { body: parts.join("&"), bodyType };
  }

  /**
   * What a response is, from webRequest: "hls" or "dash" for a manifest, "file" for a whole video or audio file,
   * null for anything else (including the small pieces players fetch to build a stream).
   */
  function classifyMedia(url, contentType, resourceType, length) {
    const type = String(contentType || "").toLowerCase().split(";")[0].trim();
    if (MANIFEST_TYPES[type]) return MANIFEST_TYPES[type];
    const path = pathOf(url).toLowerCase();
    if (path.endsWith(".m3u8")) return "hls";
    if (path.endsWith(".mpd")) return "dash";
    if (/^(video|audio)\//.test(type) && type !== "video/mp2t" && type !== "video/iso.segment") {
      // A <video src> or <audio src> loads the whole file; streaming players fetch segments with XHR.
      const whole = resourceType === "media" || resourceType === "object";
      if (whole && !(length >= 0 && length < 256 * 1024)) return "file";
    }
    return null;
  }

  /** The connection check: what the reply to "hello" (or the error) means for the user. */
  function connectionState(reply, error) {
    if (error) {
      const text = String(error.message || error);
      if (/forbidden|not allowed/i.test(text)) return "forbidden"; // registered, but not for this extension's ID
      if (/not found|no such native application|not registered/i.test(text)) return "missing";
      return "error";
    }
    if (!reply) return "error";
    if (!reply.ok) return reply.protocol ? "error" : "outdated"; // 0.5 and earlier do not know "hello"
    if (!reply.protocol || reply.protocol < PROTOCOL) return "outdated";
    return reply.running === false ? "ready" : "running";
  }

  /** Compares "1.3.2"-style versions: negative, 0 or positive; anything unreadable counts as 0.0.0. */
  function compareVersions(a, b) {
    const parts = (v) => String(v ?? "").split(".").map((n) => Number.parseInt(n, 10) || 0);
    const [x, y] = [parts(a), parts(b)];
    for (let i = 0; i < Math.max(x.length, y.length, 3); i++) {
      const d = (x[i] ?? 0) - (y[i] ?? 0);
      if (d) return d;
    }
    return 0;
  }

  /**
   * After the app updated itself (and the extension folder next to it, from 1.3.3), what this older extension does
   * with the reply to "hello":
   *  "reload"  — Chromium, the folder on disk is newer: chrome.runtime.reload() loads it; remember `target` first.
   *  "manual"  — Chromium, a reload to `target` was already tried and the version did not change (the browser loads
   *              the extension from elsewhere): never again, the popup says to reload it from chrome://extensions.
   *  "firefox" — Firefox keeps a temporary add-on as it was loaded: the popup explains how to load the new one.
   *  "none"    — nothing to do (up to date, older app, or the folder on disk is not newer).
   * `tried` is the target of the last reload, kept across reloads; a target newer than `tried` gets one try of its own.
   * `unpacked` is false for an extension installed from a store, which the store updates: nothing to do then.
   */
  function reloadDecision({ own, reply, tried, firefox, unpacked = true }) {
    const app = reply?.ok ? reply.appVersion : null;
    if (!app || compareVersions(app, own) <= 0) return { action: "none" };
    if (firefox) return { action: "firefox", target: app };
    if (!unpacked) return { action: "none" };
    const disk = reply.extensionVersion;
    if (!disk || compareVersions(disk, own) <= 0) return { action: "none" };
    return tried && compareVersions(tried, disk) >= 0 ? { action: "manual", target: disk } : { action: "reload", target: disk };
  }

  /** "Capture this site": the page address the app may crawl (http or https only, without the fragment), else null. */
  function grabTarget(url) {
    try {
      const parsed = new URL(String(url || ""));
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return null;
      parsed.hash = "";
      return parsed.href.length <= 8000 ? parsed.href : null;
    } catch {
      return null;
    }
  }

  /**
   * The audio language and subtitles chosen in the video panel, as the app accepts them: strings only, at most 20
   * subtitles, a known format; nothing at all when the choice is empty.
   */
  function mediaChoice(message) {
    const choice = {};
    if (typeof message?.audioId === "string" && message.audioId && message.audioId.length <= 200) choice.audioId = message.audioId;
    const subtitles = Array.isArray(message?.subtitleIds)
      ? [...new Set(message.subtitleIds.filter((id) => typeof id === "string" && id && id.length <= 200))].slice(0, 20)
      : [];
    if (subtitles.length) {
      choice.subtitleIds = subtitles;
      choice.subtitleMode = SUBTITLE_MODES.includes(message.subtitleMode) ? message.subtitleMode : "vtt";
    }
    return choice;
  }

  /** A size for people: "12,3 MB". */
  function formatSize(bytes, locale) {
    if (!(bytes > 0)) return "";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return new Intl.NumberFormat(locale, { maximumFractionDigits: value < 10 && unit > 0 ? 1 : 0 }).format(value) + " " + units[unit];
  }

  // Public suffixes of more than one label common enough to matter ("example.co.uk" is a site, "co.uk" is not).
  // shortcut: not the full Public Suffix List, a site under a rarer suffix is seen as broader than it is.
  const PUBLIC_SUFFIXES = new Set([
    "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk",
    "com.au", "net.au", "org.au", "edu.au", "gov.au", "co.nz", "org.nz", "co.jp", "ne.jp", "or.jp", "ac.jp",
    "com.br", "net.br", "org.br", "com.cn", "net.cn", "org.cn", "com.mx", "com.tr", "com.ar", "co.in", "co.za", "co.kr",
    "com.sg", "com.hk", "com.tw", "gov.it", "edu.it", "github.io", "gitlab.io", "blogspot.com", "herokuapp.com",
    "netlify.app", "vercel.app", "pages.dev", "workers.dev", "web.app", "firebaseapp.com", "azurewebsites.net",
    "cloudfront.net", "s3.amazonaws.com", "appspot.com",
  ]);

  /** The "site" of an address as browsers compare it for SameSite: scheme and registrable domain (or the IP address). */
  function siteOf(url) {
    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      return null;
    }
    const host = parsed.hostname.toLowerCase().replace(/\.$/, "");
    if (/^\d+\.\d+\.\d+\.\d+$/.test(host) || host.startsWith("[")) return `${parsed.protocol}//${host}`;
    const labels = host.split(".");
    // The longest listed suffix ("s3.amazonaws.com" has three labels), and one label more.
    let keep = 2;
    for (let n = labels.length - 1; n >= 2; n--) {
      if (PUBLIC_SUFFIXES.has(labels.slice(-n).join("."))) {
        keep = n + 1;
        break;
      }
    }
    return `${parsed.protocol}//${labels.slice(-keep).join(".")}`;
  }

  function sameSite(a, b) {
    const sa = siteOf(a);
    return sa !== null && sa === siteOf(b);
  }

  /**
   * Whether the browser itself would send `cookie` with a request: same-site requests carry every cookie; a cross-site
   * one never Strict cookies, Lax (and unspecified, which browsers treat as Lax) only for a top-level GET navigation,
   * None cookies only when Secure. The app must not carry cookies the browser would have withheld.
   */
  function cookieAllowed(cookie, { crossSite = false, topLevel = true, method = "GET" } = {}) {
    if (!crossSite) return true;
    const policy = String(cookie.sameSite || "unspecified").toLowerCase();
    if (policy === "strict") return false;
    if (policy === "no_restriction" || policy === "none") return cookie.secure === true;
    return topLevel && String(method).toUpperCase() === "GET";
  }

  const api = {
    PROTOCOL,
    siteOf,
    sameSite,
    cookieAllowed,
    APP_TIMEOUT_MS,
    RESTRICTED_SITES,
    MIME_EXTENSIONS,
    DEFAULTS,
    normalizeSettings,
    parseSites,
    hostMatches,
    hostOf,
    isRestricted,
    extensionOf,
    baseName,
    decide,
    pruneSkips,
    encodeForm,
    classifyMedia,
    connectionState,
    compareVersions,
    reloadDecision,
    formatSize,
    grabTarget,
    mediaChoice,
    mediaView,
    clearOfControls,
    rememberPosition,
    MEDIA_PANELS,
    SUBTITLE_MODES,
  };
  root.DMRules = api;
  if (typeof module === "object" && module.exports) module.exports = api;
})(globalThis);
