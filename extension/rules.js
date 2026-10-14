// Pure decision logic shared by the background script, the content script and the tests (node --test).
// A classic script on purpose: content scripts cannot import modules, so it publishes itself on globalThis.
(function (root) {
  "use strict";

  /** Version of the messages exchanged with the app (1 = extension 0.2-0.5, 2 = 0.6). */
  const PROTOCOL = 2;

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
    // The floating "download this video" button.
    mediaButton: true,
    mediaDisabledSites: "",
  };

  /** Settings as stored, completed with defaults; "intercept: false" of 0.5 and earlier becomes the context-menu mode. */
  function normalizeSettings(stored) {
    const settings = { ...DEFAULTS, ...stored };
    if (stored && stored.mode === undefined && stored.intercept === false) settings.mode = "menu";
    if (settings.mode !== "auto" && settings.mode !== "menu") settings.mode = DEFAULTS.mode;
    settings.minSizeMB = Math.max(0, Number(settings.minSizeMB) || 0);
    delete settings.intercept;
    return settings;
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

  const api = {
    PROTOCOL,
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
    formatSize,
  };
  root.DMRules = api;
  if (typeof module === "object" && module.exports) module.exports = api;
})(globalThis);
