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

  let state = { items: [], drm: false, enabled: false };

  chrome.runtime.onMessage.addListener((message) => {
    if (message?.type !== "media") return;
    state = { ...state, items: message.items, drm: message.drm || state.drm };
    refresh();
  });

  send({ type: "getMedia" }).then((reply) => {
    if (!reply?.ok) return;
    state = reply;
    refresh();
  });

  function refresh() {
    if (!state.enabled || !state.items?.length) return;
    if (!ui) ui = createUi();
    ui.update();
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
          <footer><button class="link hide" type="button"></button></footer>
        </section>
        <button class="pill" type="button">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9m0 0l-3.5-3.5M10 12l3.5-3.5M4 15.5h12"/></svg>
          <span class="label"></span>
          <span class="count" hidden></span>
        </button>
      </div>`;
    const $ = (selector) => root.querySelector(selector);
    const pill = $(".pill");
    const panel = $(".panel");
    const body = $(".body");
    const status = $(".status");
    $(".label").textContent = t("mediaButton");
    $("h2").textContent = t("mediaPanelTitle");
    $(".close").setAttribute("aria-label", t("close"));
    $(".hide").textContent = t("mediaHideOnSite");
    panel.setAttribute("aria-label", t("mediaPanelTitle"));
    pill.setAttribute("aria-haspopup", "dialog");
    pill.setAttribute("aria-expanded", "false");

    const probes = new Map(); // stream address -> reply (or a pending promise)
    const choices = new Map(); // stream address -> { audioId, subtitleIds, subtitleMode } chosen in the panel
    let uid = 0;

    // Dragging: the button follows the pointer; a short move is still a click.
    let drag = null;
    chrome.storage.local.get("mediaButtonPosition").then(({ mediaButtonPosition: p }) => {
      if (p) place(p.right, p.bottom);
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
      place(drag.right - dx, drag.bottom - dy);
    });
    pill.addEventListener("pointerup", () => {
      if (drag?.moved) {
        const rect = host.getBoundingClientRect();
        chrome.storage.local.set({ mediaButtonPosition: { right: innerWidth - rect.right, bottom: innerHeight - rect.bottom } }).catch(() => {});
        pill.dataset.dragged = "1";
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

    function place(right, bottom) {
      const r = Math.min(Math.max(8, right), innerWidth - 60);
      const b = Math.min(Math.max(8, bottom), innerHeight - 50);
      host.style.right = `${r}px`;
      host.style.bottom = `${b}px`;
      // The panel opens on the side with more room.
      root.querySelector(".wrap").classList.toggle("below", b > innerHeight / 2);
    }

    $(".close").addEventListener("click", () => toggle(false));
    $(".hide").addEventListener("click", async () => {
      await send({ type: "hideOnSite" });
      host.remove();
      ui = null;
    });
    root.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !panel.hidden) {
        e.stopPropagation();
        toggle(false);
      }
    });

    function toggle(open) {
      panel.hidden = !open;
      pill.setAttribute("aria-expanded", String(open));
      if (open) {
        if (hasProtectedVideo()) reportDrm();
        render();
        $(".close").focus();
      } else {
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
      update() {
        const count = state.items.length;
        const badge = $(".count");
        badge.hidden = count < 2;
        badge.textContent = String(count);
        pill.setAttribute("aria-label", count > 1 ? t("mediaButtonCount", count) : t("mediaButton"));
        if (!panel.hidden) render();
      },
      setDrm() {
        state.drm = true;
        if (!panel.hidden) render();
      },
    };
  }

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; }
    .wrap {
      --bg: #ffffff; --fg: #1b2230; --muted: #5b6475; --line: #dfe3ea; --accent: #2457d1; --accent-fg: #fff;
      --soft: #eef3fd; --danger-bg: #fdf0ee; --danger: #a12a1c; --shadow: 0 10px 30px rgba(16, 24, 40, .18), 0 2px 6px rgba(16, 24, 40, .08);
      position: relative; font: 13px/1.4 "Segoe UI", system-ui, -apple-system, sans-serif; color: var(--fg);
      display: flex; flex-direction: column; align-items: flex-end; gap: 8px;
    }
    .wrap.below { flex-direction: column-reverse; }
    @media (prefers-color-scheme: dark) {
      .wrap { --bg: #1c1f26; --fg: #e7eaf0; --muted: #a7afbd; --line: #353b47; --accent: #82aaff; --accent-fg: #0a1428;
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
    .pill svg { width: 18px; height: 18px; fill: none; stroke: var(--accent); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
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
    footer { padding: 6px 14px 10px; border-top: 1px solid var(--line); margin-top: 6px; }
    .link { border: 0; background: none; padding: 0; color: var(--muted); font: inherit; font-size: 12px; cursor: pointer; text-decoration: underline; }
    .link:hover { color: var(--fg); }
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
