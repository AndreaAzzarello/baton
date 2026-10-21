# Baton — regole per Claude Code

Baton, download manager per Windows: C# / .NET 10 / WPF (tema Fluent), più estensione MV3 per Chromium e Firefox.
Fino alla 1.1 si chiamava DownloadManager (la cartella del progetto ha ancora quel nome: non rinominarla). Fasi 1–8 completate,
versione **1.2.0** (licenza MIT, repository GitHub `AndreaAzzarello/baton`). L'utente scrive in italiano: rispondi in italiano; codice e commenti in inglese.

## Vincoli permanenti
- Nessun codice, grafica, icona, nome o marchio di altri prodotti. Solo codice originale.
- Di "Download with…" (MPL 2.0) si riprendono solo idee, mai codice (dichiarato nel README).

## Architettura (dove mettere le cose)
- `src/Baton.Core` — motore senza UI: `DownloadService` (code, scheduler, quote), `DownloadJob` (file a segmenti) e `Media/MediaJob` (HLS/DASH, lingue audio, sottotitoli WebVTT, DASH multi-periodo) dietro `IDownloadJob`, `Grabber`
  (`GrabberEngine` scansione statica con AngleSharp, `RobotsTxt`, `HostThrottle`, `TrapGuard`, `LocalPaths` per percorsi sicuri,
  `GrabberService` progetti, sincronizzazione e pianificazione), `Net` (trasporti HTTP/FTP, proxy, PAC), `Security` (credenziali DPAPI, certificati), `Integration` (protocollo con l'estensione, `MessageValidator`, host nativo, pipe), `Platform` (registro), `DownloadStore` (SQLite), `Resources/Strings.resx`.
- `src/Baton.App` — WPF in MVVM: `ViewModels`, `Views`, `Services` (dialoghi, tray, tema, notifiche). Le view model non creano finestre: passano da `IDialogService`.
- `extension/` — sorgente unico dell'estensione, caricabile così com'è in Chromium; `node tools/build-extension.mjs` crea `dist/chromium` e `dist/firefox`. Logica pura in `rules.js` (testata con Node).
- `src/Baton.App/Controls/ListSelection.cs` — mouse delle liste come in Esplora file; stili `ListCell`/`ListRow` in `Themes/Controls.xaml` (selezione disegnata dalle celle). Usarli per ogni nuovo DataGrid.
- `docs/brand/kit` — brand kit ufficiale (non rientra nella licenza MIT): mai ridisegnarlo; `docs/brand/brand.mjs` ne ricava solo i formati mancanti, `tools/make_icons.py` compone `app.ico`.
- `tests/` — xUnit con server locali (HTTP, FTP/FTPS, proxy, fixture HLS/DASH, sito multi-host `GrabberTestSite`) e `tests/extension` (`node --test`), eseguiti anche da `dotnet test`; `tests/Baton.UiTests` prova le liste WPF in-process (finestra fuori schermo, Ctrl/Maiusc solo sul thread del test con `SetKeyboardState`: mai input simulato per l'utente).

## Comandi
- Usa sempre `"C:\Program Files\dotnet\dotnet.exe"`: quello x86 viene prima nel PATH.
- Build `dotnet build Baton.slnx`; test `dotnet test Baton.slnx` (tutti devono passare, zero avvisi); pubblicazione `dotnet publish src\Baton.App -c Release -r win-x64 -o publish`.
- Il progetto ha il suo repository git; anche la home dell'utente è un repo: non fare commit lì.
- PowerShell 5.1 toglie le virgolette al JSON passato ai programmi esterni: passa i messaggi tramite file.

## Dati, registro, sicurezza
- Database: ogni modifica di schema incrementa `PRAGMA user_version` (`DownloadStore.SchemaVersion`) con un `MigrateToN`, eseguito in un'unica transazione dopo il backup `downloads.db.v<N>.bak`; mai perdere download. Il DB reale contiene download dell'utente.
- Registro solo tramite `IUserRegistry` / `SystemRegistration` (avvio con Windows, host nativo `com.andreaazzarello.baton` per Chromium e Firefox, notifiche, link `baton:`). I test vietano il registro reale (`WindowsUserRegistry.Forbidden`): non aggirarlo.
- Dati in `%LOCALAPPDATA%\Baton`: al primo avvio `LegacyData` copia (mai sposta) `%LOCALAPPDATA%\DownloadManager`;
  `SystemRegistration.RemoveLegacy` toglie le registrazioni del vecchio nome. Le entropie DPAPI e il testo firmato degli
  aggiornamenti contengono ancora "DownloadManager": non cambiarli (segreti e firme esistenti diventerebbero illeggibili).
- Segreti (password, cookie, campi dei moduli) cifrati con DPAPI, mai in chiaro nel DB, nei log o nell'interfaccia. Ogni messaggio dal browser passa da `MessageValidator`.
- Protocollo con l'estensione versionato (`Messages.ProtocolVersion`, ora 3: `grab`, lingue e sottotitoli) e retrocompatibile con le estensioni precedenti.
- Grabber: solo HTML/CSS statici (mai script), robots.txt rispettato per impostazione, ogni percorso locale passa da `LocalPaths`
  (mai fuori dalla cartella della copia), le espressioni regolari dell'utente solo con `RegexOptions.NonBacktracking`.
- Aggiornamenti firmati (dalla 1.1.0): chiavi private solo in `%LOCALAPPDATA%\Baton-signing` o, se create prima della
  rinomina, `%LOCALAPPDATA%\DownloadManager-signing` (lì c'è la principale `b80f666050fcbc7d`; `sign-release.ps1` le legge entrambe),
  mai nel repository né nell'output; le pubbliche in `Updates/release-keys.txt`. I test usano chiavi proprie, mai quella reale.
- Firma di Firefox: `tools\sign-firefox.ps1` legge `AMO_JWT_ISSUER`/`AMO_JWT_SECRET` dall'ambiente; mai chiavi nel repository,
  mai eseguirlo senza richiesta dell'utente (pubblica su AMO).

## Testi e accessibilità
- Testi dell'app solo in `Strings.resx`, generato da `python tools/make_resx.py src` (la tabella è nello script; segnala chiavi mancanti o inutilizzate). Testi dell'estensione in `_locales/it` ed `_locales/en`, generati da `python tools/make_locales.py extension`. Non modificare a mano i file generati.
- Ogni controllo ha `AutomationProperties.Name` e la navigazione da tastiera; nell'estensione aria-label, focus visibile e tema chiaro/scuro.

## Prove reali (mai sui dati o sul Desktop dell'utente)
- Usa la skill `prova-reale` (browser: `chromium-trial.mjs`, `firefox-trial.mjs`; grabber: `grab-trial.ps1` sul sito locale
  `/grab/` o su books.toscrape.com con pochi giri): app isolata con `BATON_DATA` (registro in memoria, pipe e istanza propria; `DOWNLOADMANAGER_DATA` da sola isola allo stesso modo, accanto a `BATON_DATA` indica i dati 1.1 da migrare), cartella di lavoro `%TEMP%\dm-trials`, cartelle delle categorie riportate lì prima di scaricare (quelle reali puntano al Desktop).
- L'utente lavora al PC durante le prove: **mai** input simulato di mouse o tastiera sul desktop. App solo via UI Automation (`tools/trials/ui.ps1`, screenshot con `shot.ps1 -NoFront`); browser solo headless con profili temporanei.
- A fine prova `tools\trials\finish.ps1` (prima senza `-Apply`): niente processi di prova aperti, nessuna traccia di Firefox portabile nel registro.

## Fine fase
- Skill `fine-fase`: test (due volte), `/code-review` e `/security-review` del diff con correzioni, ripubblicazione, versione, README, commit.
- Dopo la ripubblicazione l'app reale va riavviata nella tray: estensioni (Chromium e Firefox), notifiche e host nativo devono puntare a `publish\Baton.exe`, nessun riferimento a build di prova nel registro, chiave Run senza voci non richieste.

## Hook del progetto (`.claude/hooks`)
- `guard.ps1` blocca la scrittura su `...\CurrentVersion\Run` e `NativeMessagingHosts` e le cancellazioni sul Desktop fuori dal progetto. Se un comando legittimo viene bloccato, spiega all'utente cosa serve: non aggirarlo.
- `extension-check.ps1` controlla sintassi e test Node dopo ogni modifica in `extension/`; `stop.ps1` fa una build incrementale a fine risposta e segnala i processi di prova rimasti. Test degli hook: `.claude\hooks\test-hooks.ps1`.
