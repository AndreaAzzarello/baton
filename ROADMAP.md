# Roadmap

**English** · [Italiano](#italiano)

Where Baton is heading. There are no dates: each item ships when it is ready, tested and signed.

| Status | Meaning |
|---|---|
| **In progress** | being built now |
| **Planned** | decided, after what is in progress |
| **Exploring** | an idea being evaluated: it may change or not happen |

## In progress

### 1.3.x — polish

- **The browser extension updated together with the app**: the in-app update installs the signed extension package of
  the same version next to `Baton.exe`, and Chromium browsers reload it by themselves.
- **Packaging**: the winget package, and code signing of `Baton.exe` and the installer through SignPath Foundation, so
  Windows SmartScreen recognizes the files.

## Done

### 1.4.0 — "Automation", first step

- **Command line** (`batonctl`): add, list, pause and query downloads from scripts, see [docs/CLI.md](docs/CLI.md).
- **Duplicate detection** before downloading the same file again, with notification buttons that work even when Baton is closed.
- **Clipboard monitor**: offers to download a copied link (off by default).
- **IDM-style save folders**: one folder or a subfolder per file type, your choice.

## Planned

### 1.4.1 and 1.4.2 — "Automation", the rest

- **Local API**: the same operations for other programs on the same computer, never exposed to the network.
- **MCP server**, so AI agents (Claude Code, Claude Desktop and others) can control Baton: add downloads, read their
  state, manage queues.
- **Automatic rules**: site → folder, queue or schedule.

### 1.5 — "AI, optional and privacy-first"

- **Natural-language commands**: "download all PDFs from this page into Invoices".
- **Assisted grabber filters**: describe what you want, review the rules it proposes.
- **Smart file names** for downloads whose name says nothing.
- **Plain-language error diagnosis**: why a download failed and what to try.

Off by default, with **your own API key**. Cookies, passwords and form data are **never sent**. See [PRIVACY.md](PRIVACY.md).

## Exploring

- Multi-mirror and **Metalink** downloads.
- **Archive auto-extraction** after the download.
- **Statistics**: what was downloaded, when, at what speed (kept on your computer).
- The extension on the **browser stores** (Chrome Web Store, Edge Add-ons, Firefox Add-ons).
- More **interface languages**.

### Cross-platform 2.x

- The interface on **Avalonia**, then **Linux** (AppImage/Flatpak) and possibly **macOS**.

---

## Italiano

Dove va Baton, senza date: ogni punto esce quando è pronto, provato e firmato. Stati: **In progress** (in corso),
**Planned** (deciso), **Exploring** (idea da valutare, può cambiare o non arrivare).

- **In progress — 1.3.x**: l'estensione del browser aggiornata insieme all'app; pacchetti (winget) e firma del codice
  tramite SignPath Foundation.
- **Fatto — 1.4.0**: riga di comando (`batonctl`), rilevamento dei duplicati, monitor degli appunti, cartelle «alla IDM».
- **Planned — 1.4.1 e 1.4.2**: API locale e regole automatiche (sito → cartella, coda o pianificazione); server MCP perché
  gli agenti AI (Claude Code, Claude Desktop…) possano comandare Baton.
- **Planned — 1.5 "AI, opzionale e rispettosa della privacy"**: comandi in linguaggio naturale, filtri del grabber
  assistiti, nomi dei file intelligenti, spiegazione degli errori in parole semplici. Spenta all'inizio, con la tua chiave
  API; cookie e password mai inviati.
- **Exploring**: download da più mirror e Metalink, estrazione automatica degli archivi,
  statistiche, estensioni negli store dei browser, altre lingue; 2.x multipiattaforma (interfaccia su Avalonia, poi Linux e
  forse macOS).

È una direzione, non una promessa: idee e voti sono benvenuti nelle Discussions (link qui sotto).

---

This roadmap is a direction, not a promise. Ideas and votes are welcome in
[GitHub Discussions](https://github.com/AndreaAzzarello/baton/discussions/categories/ideas).
