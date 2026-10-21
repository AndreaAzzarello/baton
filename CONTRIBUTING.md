# Contributing to Baton

Thanks for your interest! Bug reports, translations, documentation and code are all welcome.
*In italiano: vedi [più sotto](#contribuire-in-italiano).*

## Before you start

- **Security issues**: never in a public issue. Follow [SECURITY.md](SECURITY.md) (private report on GitHub).
- **Bugs and ideas**: open an [issue](https://github.com/AndreaAzzarello/baton/issues/new/choose) with the templates; for
  larger changes, discuss them in an issue first.
- Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Build and test

Requirements: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org) 22 or later.

```powershell
dotnet build Baton.slnx          # must build with zero warnings
dotnet test Baton.slnx           # xUnit (local HTTP/FTP/proxy servers), WPF UI tests, extension tests (node --test)
node tools/build-extension.mjs   # dist\chromium and dist\firefox
```

Run the app on separate data while you work, so your own downloads are never touched:
`$env:BATON_DATA = "$env:TEMP\baton-dev"; dotnet run --project src\Baton.App`. With `BATON_DATA` the app keeps its
registry entries in memory and uses its own pipe and single-instance lock. Tests must never touch the real registry: any
access fails the test.

## Where things live

| Path | What |
|---|---|
| `src/Baton.Core` | Engine without UI: downloads, queues, network, media, grabber, browser protocol, SQLite store |
| `src/Baton.App` | WPF app (MVVM): view models never open windows themselves, they go through `IDialogService` |
| `extension/` | The browser extension (Manifest V3); pure logic in `rules.js`, tested with Node |
| `tests/` | `Baton.Tests` (xUnit), `Baton.UiTests` (WPF lists, in process), `extension` (Node) |

The [full guide](README.it.md#struttura) (Italian) describes every folder.

## Pull requests

1. Fork, create a branch from `main`, keep the change focused on one topic.
2. Add or update tests for what you change; `dotnet build` with zero warnings and `dotnet test` must pass (CI checks both).
3. Fill in the [pull request template](.github/PULL_REQUEST_TEMPLATE.md): what, why, how you tested it.
4. A database schema change needs a new `MigrateToN` step (bump `DownloadStore.SchemaVersion`, backup first): users'
   downloads must never be lost.

## Code style

- C# / .NET 10, nullable enabled, the conventions already in the file you touch; code and comments in **English**.
- Comments explain *why*, not *what*. Small, readable changes beat clever ones.
- Every control has an accessible name (`AutomationProperties.Name`) and keyboard navigation; in the extension,
  `aria-label`, visible focus, light and dark theme, contrast ≥ 4.5:1.
- Secrets (passwords, cookies, form fields) are encrypted with DPAPI and never logged; every browser message goes
  through `MessageValidator`.
- Only original code. No code, graphics, icons, names or trademarks of other products; ideas from other open-source
  projects are fine when credited, their code is not copied.

## Texts and translations

Texts are generated, never edited by hand:

- **App**: keys and Italian texts live in the table inside `tools/make_resx.py`; run `python tools/make_resx.py src` to
  regenerate `src/Baton.Core/Resources/Strings.resx` (it reports missing and unused keys).
- **Extension**: `tools/make_locales.py` holds `(English, Italian)` pairs; run `python tools/make_locales.py extension`.
- **New app language**: add `src/Baton.Core/Resources/Strings.<lang>.resx` (for example `Strings.en.resx`) with the
  same keys as `Strings.resx`. Translations are very welcome: the app UI is currently Italian only.

## Brand

The Baton name, logo and icons ([`docs/brand/kit`](docs/brand/kit)) are not covered by the MIT license and must not be
redrawn or altered in contributions. Missing sizes are derived by `docs/brand/brand.mjs`.

---

## Contribuire (in italiano)

Grazie! Sono benvenuti segnalazioni, traduzioni, documentazione e codice.

- **Vulnerabilità**: mai in una issue pubblica, segui [SECURITY.md](SECURITY.md).
- **Bug e proposte**: apri una [issue](https://github.com/AndreaAzzarello/baton/issues/new/choose) con i modelli; per
  modifiche grandi parliamone prima in una issue.
- **Compilare e provare**: .NET 10 SDK e Node.js; `dotnet build Baton.slnx` (zero avvisi), `dotnet test Baton.slnx`,
  `node tools/build-extension.mjs`. Per provare l'app senza toccare i tuoi dati usa `BATON_DATA` (vedi sopra).
- **Pull request**: un ramo da `main`, una modifica per argomento, test aggiornati, il modello compilato. Un cambio di
  schema del database richiede un passo `MigrateToN` con backup.
- **Stile**: codice e commenti in inglese, commenti sul *perché*, nomi accessibili e navigazione da tastiera per ogni
  controllo, segreti cifrati con DPAPI e mai nei log, solo codice originale e nessun riferimento ad altri prodotti.
- **Testi**: mai a mano nei file generati. App: tabella in `tools/make_resx.py`, poi `python tools/make_resx.py src`.
  Estensione: `tools/make_locales.py`, poi `python tools/make_locales.py extension`. Per una nuova lingua dell'app aggiungi
  `Strings.<lingua>.resx` accanto a `Strings.resx`.
