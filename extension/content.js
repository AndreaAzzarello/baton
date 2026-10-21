// Runs in every page (and frame). In all frames: notices Alt+click on links (that download stays in the browser)
// and protected playback (Encrypted Media Extensions). In the top frame only: the floating "download this video"
// button, shown when the background has seen the page load a video or audio stream.
(() => {
  "use strict";
  if (window.__batonContent) return;
  window.__batonContent = true;

  const Rules = globalThis.DMRules;
  const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String)) || key;
  const send = (message) => chrome.runtime.sendMessage(message).catch(() => null);

  // Alt+click on a link: this one download is left to the browser. Chromium downloads the link by itself; Firefox
  // ignores Alt+click, so there the link is opened once it is marked.
  const firefox = navigator.userAgent.includes("Firefox/");
  document.addEventListener("click", (event) => {
    if (!event.altKey || event.button !== 0) return;
    const link = event.target instanceof Element ? event.target.closest("a[href], area[href]") : null;
    if (!link?.href || !/^https?:/i.test(link.href)) return;
    const marked = send({ type: "skip", url: link.href });
    if (firefox) {
      event.preventDefault();
      marked.then(() => location.assign(link.href));
    }
  }, true);

  let ui = null; // the floating button, top frame only

  // A protected stream fires "encrypted" on its media element (the event does not bubble, so it is caught on the way down).
  let drmReported = false;
  const reportDrm = () => {
    if (drmReported) return;
    drmReported = true;
    send({ type: "drm" });
    if (ui) ui.setDrm();
  };
  document.addEventListener("encrypted", reportDrm, true);

  if (window.top !== window) return;

  let state = { items: [], drm: false, enabled: false, mode: "minimized" };
  // This page only (gone when it is loaded again): what was closed, hidden or minimized (see Rules.mediaView).
  const local = { dismissed: [], hiddenOnPage: false, minimized: true, requested: false };

  chrome.runtime.onMessage.addListener((message, sender) => {
    if (sender.id !== chrome.runtime.id) return;
    if (message?.type === "media") {
      state = { ...state, items: message.items, drm: message.drm || state.drm };
      refresh();
    } else if (message?.type === "showPanel") {
      // "Show in the page" from the popup: whatever was closed or hidden here, the panel opens.
      Object.assign(local, { dismissed: [], hiddenOnPage: false, minimized: false, requested: true });
      refresh();
      ui?.open();
    }
  });

  send({ type: "getMedia" }).then((reply) => {
    if (!reply?.ok) return;
    state = reply;
    local.minimized = state.mode === "minimized";
    refresh();
  });

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes.mediaPanel) return;
    state.mode = changes.mediaPanel.newValue ?? "minimized";
    local.minimized = state.mode === "minimized";
    refresh();
  });

  // Full screen: nothing over the video while it lasts.
  for (const name of ["fullscreenchange", "webkitfullscreenchange"]) document.addEventListener(name, () => refresh());

  function refresh() {
    const view = Rules.mediaView({ ...state, ...local, fullscreen: Boolean(document.fullscreenElement || document.webkitFullscreenElement) });
    if (view === "none") {
      ui?.hide();
      return;
    }
    if (!ui) ui = createUi();
    ui.show(view);
  }

  function hasProtectedVideo() {
    return [...document.querySelectorAll("video, audio")].some((m) => m.mediaKeys);
  }

  // ------------------------------------------------------------ the button and its panel

  function createUi() {
    const host = document.createElement("div");
    host.style.cssText = "all: initial; position: fixed; z-index: 2147483647; right: 24px; bottom: 24px;";
    const root = host.attachShadow({ mode: "closed" });
    root.innerHTML = `
      <style>${STYLE}</style>
      <div class="wrap">
        <section class="panel" role="dialog" aria-modal="false" hidden>
          <header>
            <h2></h2>
            <button class="icon close" type="button"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8"/></svg></button>
          </header>
          <div class="body"></div>
          <p class="status" role="status" aria-live="polite"></p>
        </section>
        <div class="bar" role="group">
          <button class="pill" type="button"><span class="logo">${symbol("p")}</span><span class="label"></span><span class="count" hidden></span></button>
          <button class="icon more" type="button" aria-haspopup="menu" aria-expanded="false"><svg viewBox="0 0 16 16" aria-hidden="true"><path class="dots" d="M3.5 8h.01M8 8h.01M12.5 8h.01"/></svg></button>
          <button class="icon dismiss" type="button"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8"/></svg></button>
          <div class="menu" role="menu" hidden>
            <button type="button" role="menuitem" data-act="page"></button>
            <button type="button" role="menuitem" data-act="site"></button>
            <button type="button" role="menuitem" data-act="minimize"></button>
          </div>
        </div>
        <button class="tab" type="button" hidden><span class="logo">${symbol("t")}</span><span class="count" hidden></span></button>
      </div>`;
    const $ = (selector) => root.querySelector(selector);
    const pill = $(".pill");
    const panel = $(".panel");
    const body = $(".body");
    const status = $(".status");
    const bar = $(".bar");
    const tab = $(".tab");
    const more = $(".more");
    const menu = $(".menu");
    const wrap = $(".wrap");
    $(".label").textContent = t("mediaButton");
    $("h2").textContent = t("mediaPanelTitle");
    $(".close").setAttribute("aria-label", t("close"));
    $(".close").title = t("close");
    for (const [selector, key] of [[".dismiss", "close"], [".more", "mediaMore"], [".tab", "mediaOpenTab"]]) {
      $(selector).setAttribute("aria-label", t(key));
      $(selector).title = t(key);
    }
    $("[data-act=page]").textContent = t("mediaHidePage");
    $("[data-act=site]").textContent = t("mediaHideOnSite");
    $("[data-act=minimize]").textContent = t("mediaMinimize");
    bar.setAttribute("aria-label", "Baton");
    menu.setAttribute("aria-label", t("mediaMore"));
    panel.setAttribute("aria-label", t("mediaPanelTitle"));
    pill.setAttribute("aria-haspopup", "dialog");
    pill.setAttribute("aria-expanded", "false");
    let view = "none";

    const probes = new Map(); // stream address -> reply (or a pending promise)
    const choices = new Map(); // stream address -> { audioId, subtitleIds, subtitleMode } chosen in the panel
    let uid = 0;

    // Dragging: the button follows the pointer; a short move is still a click. The place is remembered for each site.
    let drag = null;
    let saved = { right: 24, bottom: 24 };
    const site = location.hostname;
    chrome.storage.local.get("mediaButtonPositions").then(({ mediaButtonPositions: all }) => {
      if (all?.[site]) saved = all[site];
      place();
    }).catch(() => {});
    pill.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      const rect = host.getBoundingClientRect();
      drag = { x: e.clientX, y: e.clientY, right: innerWidth - rect.right, bottom: innerHeight - rect.bottom, moved: false };
      pill.setPointerCapture(e.pointerId);
    });
    pill.addEventListener("pointermove", (e) => {
      if (!drag) return;
      const dx = e.clientX - drag.x;
      const dy = e.clientY - drag.y;
      if (!drag.moved && Math.hypot(dx, dy) < 5) return;
      drag.moved = true;
      saved = { right: drag.right - dx, bottom: drag.bottom - dy };
      place(false);
    });
    pill.addEventListener("pointerup", async () => {
      if (drag?.moved) {
        pill.dataset.dragged = "1";
        place();
        const { mediaButtonPositions: all } = await chrome.storage.local.get("mediaButtonPositions").catch(() => ({}));
        chrome.storage.local.set({ mediaButtonPositions: Rules.rememberPosition(all, site, saved) }).catch(() => {});
      }
      drag = null;
    });
    pill.addEventListener("click", () => {
      if (pill.dataset.dragged) {
        delete pill.dataset.dragged;
        return;
      }
      toggle(panel.hidden);
    });

    /** The remembered place (the tab keeps only its height, on the right edge), moved above the players' controls. */
    function place(avoidControls = true) {
      const right = view === "tab" ? 0 : Math.min(Math.max(8, saved.right), innerWidth - 60);
      let bottom = Math.min(Math.max(8, saved.bottom), innerHeight - 50);
      host.style.right = `${right}px`;
      host.style.bottom = `${bottom}px`;
      if (avoidControls && view !== "none") {
        const players = [...document.querySelectorAll("video")].map((v) => v.getBoundingClientRect()).filter((r) => r.width > 0 && r.height > 0);
        const box = bar.hidden ? tab.getBoundingClientRect() : bar.getBoundingClientRect();
        const below = host.getBoundingClientRect().bottom - box.bottom; // the open panel, when it is under the bar
        bottom = Math.min(Rules.clearOfControls(box, players, innerHeight) - below, innerHeight - 50);
        host.style.bottom = `${bottom}px`;
      }
      // The panel opens on the side with more room.
      wrap.classList.toggle("below", bottom > innerHeight / 2);
    }
    let pending = 0;
    const replace = () => {
      if (!pending && view !== "none") pending = requestAnimationFrame(() => { pending = 0; place(); });
    };
    addEventListener("resize", replace, { passive: true });
    addEventListener("scroll", replace, { passive: true, capture: true });

    $(".close").addEventListener("click", () => toggle(false));
    $(".dismiss").addEventListener("click", dismiss);
    more.addEventListener("click", () => showMenu(menu.hidden));
    tab.addEventListener("click", () => {
      local.minimized = false;
      refresh();
      open();
    });
    menu.addEventListener("click", async (e) => {
      const act = e.target instanceof Element ? e.target.closest("[data-act]")?.dataset.act : null;
      if (!act) return;
      showMenu(false);
      local.requested = false;
      if (act === "minimize") {
        local.minimized = true;
        toggle(false);
      } else {
        if (act === "site") await send({ type: "hideOnSite" }); // the list of sites without the button, in the popup
        local.hiddenOnPage = true;
      }
      refresh();
    });
    root.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      e.stopPropagation();
      if (!menu.hidden) {
        showMenu(false);
        more.focus();
      } else if (!panel.hidden) {
        toggle(false);
      } else {
        dismiss();
      }
    });

    /** Closed (×, or Esc): for this tab, until a video at a new address appears or the page is loaded again. */
    function dismiss() {
      local.dismissed = state.items.map((item) => item.url);
      local.requested = false;
      showMenu(false);
      toggle(false, false);
      refresh();
    }

    function showMenu(show) {
      menu.hidden = !show;
      more.setAttribute("aria-expanded", String(show));
      if (show) menu.querySelector("button").focus();
    }

    function open() {
      if (panel.hidden) toggle(true);
    }

    function toggle(open, focus = true) {
      panel.hidden = !open;
      pill.setAttribute("aria-expanded", String(open));
      if (open) {
        if (hasProtectedVideo()) reportDrm();
        render();
        $(".close").focus();
      } else if (focus && !bar.hidden) {
        pill.focus();
      }
    }

    function setStatus(text) {
      status.textContent = text || "";
    }

    function notice(text, kind = "info") {
      const p = document.createElement("p");
      p.className = `notice ${kind}`;
      p.textContent = text;
      return p;
    }

    function render() {
      body.replaceChildren();
      if (Rules.isRestricted(location.href)) {
        body.append(notice(t("mediaTerms"), "blocked"));
        return;
      }
      if (state.drm || drmReported) {
        body.append(notice(t("mediaDrm"), "blocked"));
        return;
      }
      const streams = [...state.items].reverse(); // newest first: usually what is playing now
      streams.forEach((item, index) => body.append(renderStream(item, index, streams.length)));
    }

    function renderStream(item, index, total) {
      const section = document.createElement("div");
      section.className = "stream";
      if (total > 1) {
        const title = document.createElement("h3");
        title.textContent = t("mediaStream", index + 1, kindLabel(item.kind));
        title.title = item.url;
        section.append(title);
      }
      const list = document.createElement("ul");
      list.setAttribute("aria-label", t("mediaQualities"));
      section.append(list);

      const known = probes.get(item.url);
      if (known && !(known instanceof Promise)) {
        fill(list, item, known);
      } else {
        const loading = document.createElement("li");
        loading.className = "loading";
        loading.textContent = t("mediaLoading");
        list.append(loading);
        const pending = known ?? send({ type: "probe", url: item.url });
        probes.set(item.url, pending);
        pending.then((reply) => {
          probes.set(item.url, reply ?? { ok: false, error: t("errorBadReply") });
          if (!panel.hidden) render();
        });
      }
      return section;
    }

    function fill(list, item, reply) {
      if (!reply.ok) {
        list.replaceWith(notice(reply.blocked === "terms" ? t("mediaTerms") : reply.outdated ? t("mediaAppOutdated") : reply.error, "blocked"));
        return;
      }
      const media = reply.media;
      if (media.blocked) {
        const text = { drm: t("mediaDrm"), terms: t("mediaTerms"), live: t("mediaLive") }[media.blocked] ?? media.message;
        list.replaceWith(notice(text, "blocked"));
        return;
      }
      if (!choices.has(item.url)) {
        const audio = media.audioTracks?.find((a) => a.isDefault) ?? media.audioTracks?.[0];
        choices.set(item.url, { audioId: audio?.id ?? null, subtitleIds: [], subtitleMode: "vtt" });
      }
      const choice = choices.get(item.url);
      if (media.audioTracks?.length > 1 || media.subtitles?.length) list.before(renderTracks(media, choice));
      for (const variant of media.variants) {
        const li = document.createElement("li");
        const label = document.createElement("span");
        label.className = "quality";
        label.textContent = variant.label;
        const size = document.createElement("span");
        size.className = "size";
        const formatted = Rules.formatSize(variant.estimatedBytes, chrome.i18n.getUILanguage());
        size.textContent = formatted ? (variant.kind === "file" ? formatted : `≈ ${formatted}`) : t("mediaSizeUnknown");
        const button = document.createElement("button");
        button.type = "button";
        button.className = "get";
        button.textContent = t("mediaDownload");
        button.setAttribute("aria-label", t("mediaDownloadQuality", variant.label));
        button.addEventListener("click", async () => {
          button.disabled = true;
          button.textContent = t("mediaSending");
          const result = await send({ type: "addMedia", url: item.url, variantId: variant.id, label: variant.label,
            audioId: choice.audioId, subtitleIds: choice.subtitleIds, subtitleMode: choice.subtitleMode });
          if (result?.ok) {
            button.textContent = t("mediaSent");
            button.classList.add("done");
            setStatus(t("mediaSentStatus", variant.label));
          } else {
            button.disabled = false;
            button.textContent = t("mediaDownload");
            setStatus(result?.error || t("errorBadReply"));
          }
        });
        li.append(label, size, button);
        list.append(li);
      }
      if (media.kind !== "file" && media.variants.length) {
        const hint = document.createElement("li");
        hint.className = "hint";
        hint.textContent = t("mediaEstimateHint");
        list.append(hint);
      }
    }

    /** Audio language and subtitles of a stream, chosen before its quality. */
    function renderTracks(media, choice) {
      const box = document.createElement("div");
      box.className = "tracks";
      const id = `dm-${++uid}`;
      if (media.audioTracks?.length > 1) {
        const row = document.createElement("div");
        row.className = "row";
        const label = document.createElement("label");
        label.htmlFor = `${id}-audio`;
        label.textContent = t("mediaAudioLabel");
        const select = document.createElement("select");
        select.id = `${id}-audio`;
        for (const track of media.audioTracks) select.add(new Option(track.label, track.id, false, track.id === choice.audioId));
        select.addEventListener("change", () => { choice.audioId = select.value; });
        row.append(label, select);
        box.append(row);
      }
      if (media.subtitles?.length) {
        const group = document.createElement("fieldset");
        const legend = document.createElement("legend");
        legend.textContent = t("mediaSubtitles");
        group.append(legend);
        const format = document.createElement("select");
        format.id = `${id}-format`;
        for (const mode of Rules.SUBTITLE_MODES) format.add(new Option(t(`mediaSubtitle_${mode}`), mode, false, mode === choice.subtitleMode));
        format.disabled = choice.subtitleIds.length === 0;
        format.addEventListener("change", () => { choice.subtitleMode = format.value; });
        for (const track of media.subtitles) {
          const line = document.createElement("label");
          line.className = "check";
          const input = document.createElement("input");
          input.type = "checkbox";
          input.checked = choice.subtitleIds.includes(track.id);
          input.addEventListener("change", () => {
            choice.subtitleIds = input.checked ? [...choice.subtitleIds, track.id] : choice.subtitleIds.filter((s) => s !== track.id);
            format.disabled = choice.subtitleIds.length === 0;
          });
          line.append(input, document.createTextNode(track.label));
          line.title = track.language || "";
          group.append(line);
        }
        const row = document.createElement("div");
        row.className = "row";
        const label = document.createElement("label");
        label.htmlFor = format.id;
        label.textContent = t("mediaSubtitleFormat");
        row.append(label, format);
        group.append(row);
        box.append(group);
      }
      return box;
    }

    function kindLabel(kind) {
      return kind === "hls" ? "HLS" : kind === "dash" ? "DASH" : t("mediaKindFile");
    }

    document.documentElement.append(host);
    return {
      /** "panel": the button (and its list when open); "tab": the small tab on the edge. */
      show(next) {
        view = next;
        host.style.display = ""; // (the host's inline "all: initial" would beat a hidden attribute)
        bar.hidden = next !== "panel";
        tab.hidden = next !== "tab";
        if (next !== "panel") toggle(false, false);
        const count = state.items.length;
        for (const badge of root.querySelectorAll(".count")) {
          badge.hidden = count < 2;
          badge.textContent = String(count);
        }
        pill.setAttribute("aria-label", count > 1 ? t("mediaButtonCount", count) : t("mediaButton"));
        place();
        if (!panel.hidden) render();
      },
      hide() {
        view = "none";
        host.style.display = "none";
        toggle(false, false);
      },
      open,
      setDrm() {
        state.drm = true;
        if (!panel.hidden) render();
      },
    };
  }

  // The brand kit's symbol, inline: an image from the extension would let any page detect it.
  const SYMBOL = `<svg aria-hidden="true" focusable="false" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 365 365"><defs><linearGradient id="top" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#0449ef"/><stop offset="0.47" stop-color="#047aff"/><stop offset="1" stop-color="#04c5e9"/></linearGradient><linearGradient id="bottom" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#095eff"/><stop offset="0.53" stop-color="#049aff"/><stop offset="1" stop-color="#12dbc6"/></linearGradient><clipPath id="top-clip"><rect x="0" y="0" width="365" height="188"/></clipPath><clipPath id="bottom-clip"><rect x="0" y="188" width="365" height="177"/></clipPath></defs><g ><g><path d="M 40,27 L 33,35 L 29,44 L 29,173 L 40,185 L 46,189 L 43,197 L 36,203 L 29,215 L 29,322 L 35,334 L 42,340 L 48,342 L 49,344 L 262,344 L 263,342 L 282,337 L 292,332 L 306,322 L 314,314 L 324,300 L 331,284 L 334,270 L 334,250 L 332,239 L 324,220 L 309,202 L 294,192 L 273,185 L 274,182 L 287,175 L 305,159 L 318,138 L 324,119 L 325,96 L 322,81 L 312,59 L 301,45 L 289,35 L 277,28 L 264,23 L 249,20 L 55,20 Z M 205,93 L 209,98 L 209,220 L 249,218 L 252,221 L 252,225 L 175,298 L 170,298 L 98,226 L 98,221 L 100,219 L 119,219 L 135,221 L 142,220 L 142,96 L 146,93 Z" fill="url(#top)" fill-rule="evenodd" clip-path="url(#top-clip)"/><path d="M 40,27 L 33,35 L 29,44 L 29,173 L 40,185 L 46,189 L 43,197 L 36,203 L 29,215 L 29,322 L 35,334 L 42,340 L 48,342 L 49,344 L 262,344 L 263,342 L 282,337 L 292,332 L 306,322 L 314,314 L 324,300 L 331,284 L 334,270 L 334,250 L 332,239 L 324,220 L 309,202 L 294,192 L 273,185 L 274,182 L 287,175 L 305,159 L 318,138 L 324,119 L 325,96 L 322,81 L 312,59 L 301,45 L 289,35 L 277,28 L 264,23 L 249,20 L 55,20 Z M 205,93 L 209,98 L 209,220 L 249,218 L 252,221 L 252,225 L 175,298 L 170,298 L 98,226 L 98,221 L 100,219 L 119,219 L 135,221 L 142,220 L 142,96 L 146,93 Z" fill="url(#bottom)" fill-rule="evenodd" clip-path="url(#bottom-clip)"/></g></g></svg>`;

  /** A copy of the symbol whose gradient ids are its own: with two copies in one shadow root, the first id would win. */
  const symbol = (prefix) => SYMBOL.replaceAll('id="', `id="${prefix}-`).replaceAll("url(#", `url(#${prefix}-`);

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; }
    .wrap {
      --bg: #ffffff; --fg: #1b2230; --muted: #5b6475; --line: #dfe3ea; --accent: #0449ef; --accent-fg: #fff;
      --soft: #eef3fd; --danger-bg: #fdf0ee; --danger: #a12a1c; --shadow: 0 10px 30px rgba(16, 24, 40, .18), 0 2px 6px rgba(16, 24, 40, .08);
      position: relative; font: 13px/1.4 "Segoe UI", system-ui, -apple-system, sans-serif; color: var(--fg);
      display: flex; flex-direction: column; align-items: flex-end; gap: 8px;
    }
    .wrap.below { flex-direction: column-reverse; }
    @media (prefers-color-scheme: dark) {
      .wrap { --bg: #1c1f26; --fg: #e7eaf0; --muted: #a7afbd; --line: #353b47; --accent: #049aff; --accent-fg: #0a1428;
              --soft: #25365c; --danger-bg: #3a2020; --danger: #ffb4a8; --shadow: 0 10px 30px rgba(0, 0, 0, .5); }
    }
    .pill {
      display: inline-flex; align-items: center; gap: 6px; height: 34px; padding: 0 12px 0 10px; border-radius: 17px;
      border: 1px solid var(--line); background: var(--bg); color: var(--fg); box-shadow: var(--shadow);
      font: 600 12.5px/1 inherit; font-family: inherit; cursor: grab; opacity: .82; touch-action: none; user-select: none;
      transition: opacity .15s, transform .15s;
    }
    .pill:hover, .pill:focus-visible, .pill[aria-expanded="true"] { opacity: 1; }
    .pill:active { cursor: grabbing; }
    .logo { display: inline-grid; width: 18px; height: 18px; }
    .logo svg { width: 100%; height: 100%; }
    [hidden] { display: none !important; }
    .bar { position: relative; display: inline-flex; align-items: center; gap: 2px; padding: 2px; border-radius: 19px;
           background: var(--bg); border: 1px solid var(--line); box-shadow: var(--shadow); }
    .bar .pill { border: 0; box-shadow: none; opacity: 1; }
    .bar .icon { width: 30px; height: 30px; border-radius: 15px; color: var(--fg); }
    .dots { stroke-width: 3.2; }
    .menu { position: absolute; right: 0; bottom: calc(100% + 6px); min-width: 230px; padding: 4px; display: grid;
            background: var(--bg); border: 1px solid var(--line); border-radius: 10px; box-shadow: var(--shadow); }
    .below .menu { bottom: auto; top: calc(100% + 6px); }
    .menu button { text-align: left; padding: 8px 10px; border: 0; border-radius: 6px; background: none; color: var(--fg);
                   font: inherit; cursor: pointer; }
    .menu button:hover, .menu button:focus-visible { background: var(--soft); }
    .tab { display: inline-flex; flex-direction: column; align-items: center; gap: 4px; padding: 8px 6px 8px 8px;
           border: 1px solid var(--line); border-right: 0; border-radius: 12px 0 0 12px; background: var(--bg);
           box-shadow: var(--shadow); cursor: pointer; }
    .tab .logo { width: 22px; height: 22px; }
    .count { min-width: 18px; height: 18px; padding: 0 5px; border-radius: 9px; background: var(--accent); color: var(--accent-fg);
             font-size: 11px; line-height: 18px; text-align: center; }
    button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
    .panel { width: 330px; max-height: min(60vh, 460px); display: flex; flex-direction: column; background: var(--bg);
             border: 1px solid var(--line); border-radius: 12px; box-shadow: var(--shadow); overflow: hidden; }
    .panel[hidden] { display: none; }
    header { display: flex; align-items: center; justify-content: space-between; padding: 10px 8px 6px 14px; }
    h2 { margin: 0; font-size: 14px; font-weight: 600; }
    h3 { margin: 6px 0 4px; font-size: 12px; font-weight: 600; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .icon { width: 28px; height: 28px; border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer; display: grid; place-items: center; }
    .icon:hover { background: var(--soft); color: var(--fg); }
    .icon svg { width: 14px; height: 14px; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; }
    .body { padding: 0 14px 4px; overflow-y: auto; }
    ul { list-style: none; margin: 0 0 8px; padding: 0; }
    li { display: grid; grid-template-columns: 1fr auto auto; align-items: center; gap: 10px; padding: 7px 0; border-top: 1px solid var(--line); }
    li:first-child { border-top: 0; }
    li.loading, li.hint { display: block; color: var(--muted); }
    li.hint { font-size: 11.5px; border-top: 0; padding-top: 2px; }
    .quality { font-weight: 600; font-variant-numeric: tabular-nums; }
    .size { color: var(--muted); font-variant-numeric: tabular-nums; }
    .get { height: 28px; padding: 0 12px; border-radius: 6px; border: 0; background: var(--accent); color: var(--accent-fg);
           font: 600 12px/1 inherit; font-family: inherit; cursor: pointer; }
    .get:hover { filter: brightness(1.08); }
    .get:disabled { cursor: default; opacity: .75; }
    .get.done { background: var(--soft); color: var(--fg); opacity: 1; }
    .notice { margin: 4px 0 10px; padding: 10px 12px; border-radius: 8px; background: var(--soft); }
    .notice.blocked { background: var(--danger-bg); color: var(--danger); }
    .status { margin: 0; padding: 0 14px; color: var(--muted); font-size: 12px; }
    .status:empty { display: none; }
    .tracks { margin: 2px 0 8px; padding: 8px 10px; border-radius: 8px; background: var(--soft); display: grid; gap: 6px; }
    .tracks .row { display: grid; grid-template-columns: auto 1fr; align-items: center; gap: 8px; }
    .tracks label { color: var(--muted); }
    .tracks select { min-width: 0; height: 26px; padding: 0 6px; border: 1px solid var(--line); border-radius: 6px;
                     background: var(--bg); color: var(--fg); font: inherit; }
    .tracks select:disabled { opacity: .6; }
    .tracks fieldset { margin: 0; padding: 0; border: 0; display: grid; gap: 4px; }
    .tracks legend { padding: 0; margin-bottom: 2px; color: var(--muted); }
    .tracks .check { display: flex; align-items: center; gap: 6px; color: var(--fg); cursor: pointer; }
    .tracks input { margin: 0; accent-color: var(--accent); }
    select:focus-visible, input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
    @media (prefers-reduced-motion: reduce) { .pill { transition: none; } }
  `;
})();
