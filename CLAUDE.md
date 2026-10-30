# Baton — regole per Claude Code

Baton, download manager per Windows: C# / .NET 10 / WPF (tema Fluent), più estensione MV3 per Chromium e Firefox.
Fino alla 1.1 si chiamava DownloadManager (la cartella del progetto ha ancora quel nome: non rinominarla). Licenza MIT, repository GitHub `AndreaAzzarello/baton`.
I tag e le release pubblicati restano dove sono: le release sono firmate e gli aggiornamenti si basano su quei tag, quindi spostarli romperebbe aggiornamenti e verifica delle firme.
L'utente scrive in italiano: rispondi in italiano; codice e commenti in inglese.

Le regole generali (piano, conferme, report finale) e il vault delle lezioni stanno in `~/.claude/CLAUDE.md`.

## Finito quando
`dotnet build Baton.slnx` e `dotnet test Baton.slnx` passano con zero avvisi; il test include i test Node dell'estensione, `HardCodedTextTests` e `WindowGalleryTests`. Nel report dichiara ciò che non hai provato: prove reali nei browser, installer, firma, Firefox/AMO. A fine fase usa la skill `fine-fase`.

## Vincoli permanenti
- Usa solo codice originale, grafica, icone e nomi propri: niente materiale di altri prodotti.
- Di "Download with…" (MPL 2.0) riprendi solo idee, mai codice (dichiarato nei README).

## Architettura (dove mettere le cose)
- `src/Baton.Core` — motore senza UI: `DownloadService` (code, scheduler, quote), `DownloadJob` (file a segmenti) e `Media/MediaJob` (HLS/DASH, lingue audio, sottotitoli WebVTT, DASH multi-periodo) dietro `IDownloadJob`, `Grabber`
  (`GrabberEngine` scansione statica con AngleSharp, `RobotsTxt`, `HostThrottle`, `TrapGuard`, `LocalPaths` per percorsi sicuri,
  `GrabberService` progetti, sincronizzazione e pianificazione), `Net` (trasporti HTTP/FTP, proxy, PAC), `Security` (credenziali DPAPI, certificati), `Integration` (protocollo con l'estensione, `MessageValidator`, host nativo, pipe), `Platform` (registro), `DownloadStore` (SQLite), `Resources/Strings.resx`.
- `src/Baton.App` — WPF in MVVM: `ViewModels`, `Views`, `Services` (dialoghi, tray, tema, notifiche). Le view model non creano finestre: passano da `IDialogService`.
- `extension/` — sorgente unico dell'estensione, caricabile così com'è in Chromium; `node tools/build-extension.mjs` crea `dist/chromium` e `dist/firefox`. Logica pura in `rules.js` (testata con Node).
- `src/Baton.App/Controls/ListSelection.cs` — mouse delle liste come in Esplora file; stili `ListCell`/`ListRow` in `Themes/Controls.xaml` (selezione disegnata dalle celle). Usali per ogni nuovo DataGrid.
- `docs/brand/kit` — brand kit ufficiale, fuori dalla licenza MIT: non ridisegnarlo; `docs/brand/brand.mjs` ne ricava solo i formati mancanti, `tools/make_icons.py` compone `app.ico`.
- `installer/Baton.iss` — installer Inno Setup per utente (AppId fisso, uguale a `SystemRegistration.UninstallKey`): la logica
  sta in `Baton.exe --setup-check/--import-data/--uninstall` (`SetupCommands`, `Core.Setup.SetupCheck`). Portable =
  `portable.flag` accanto all'exe (`AppEnvironment.IsPortable`, dati in `Data`, registro tramite `PortableUserRegistry`).
  Pacchetti: `tools/build-packages.ps1` (Inno 6 in `%LOCALAPPDATA%\Programs\InnoSetup6`), manifest `tools/make-manifests.ps1`.
- `tests/` — xUnit con server locali (HTTP, FTP/FTPS, proxy, fixture HLS/DASH, sito multi-host `GrabberTestSite`) e `tests/extension` (`node --test`), eseguiti anche da `dotnet test`; `tests/Baton.UiTests` prova le liste WPF in-process (finestra fuori schermo, Ctrl/Maiusc solo sul thread del test con `SetKeyboardState`, senza simulare input sul desktop dell'utente).

## Comandi
- Usa `"C:\Program Files\dotnet\dotnet.exe"`: quello x86 viene prima nel PATH.
- Build `dotnet build Baton.slnx`; test `dotnet test Baton.slnx`; pubblicazione `dotnet publish src\Baton.App -c Release -r win-x64 -o publish`.
- Il progetto ha il suo repository git; anche la home dell'utente è un repo: non fare commit lì.
- PowerShell 5.1 toglie le virgolette al JSON passato ai programmi esterni: passa i messaggi tramite file.

## Dati, registro, sicurezza
- Database: ogni modifica di schema incrementa `PRAGMA user_version` (`DownloadStore.SchemaVersion`) con un `MigrateToN`, eseguito in un'unica transazione dopo il backup `downloads.db.v<N>.bak`. I download esistenti non devono andare persi: il DB reale contiene quelli dell'utente.
- Il registro si tocca solo tramite `IUserRegistry` / `SystemRegistration` (avvio con Windows, host nativo `com.andreaazzarello.baton` per Chromium e Firefox, notifiche, link `baton:`). I test vietano il registro reale (`WindowsUserRegistry.Forbidden`): non aggirare il divieto.
- Dati in `%LOCALAPPDATA%\Baton`: al primo avvio `LegacyData` copia (senza spostare) `%LOCALAPPDATA%\DownloadManager`;
  `SystemRegistration.RemoveLegacy` toglie le registrazioni del vecchio nome. Le entropie DPAPI e il testo firmato degli
  aggiornamenti contengono ancora "DownloadManager": non cambiarli (segreti e firme esistenti diventerebbero illeggibili).
- Segreti (password, cookie, campi dei moduli) cifrati con DPAPI: non finiscono in chiaro nel DB, nei log o nell'interfaccia. Ogni messaggio dal browser passa da `MessageValidator`.
- Protocollo con l'estensione versionato (`Messages.ProtocolVersion`, ora 3: `grab`, lingue e sottotitoli) e retrocompatibile con le estensioni precedenti.
- Grabber: solo HTML/CSS statici (nessuno script), robots.txt rispettato per impostazione, ogni percorso locale passa da `LocalPaths`
  (resta dentro la cartella della copia), le espressioni regolari dell'utente solo con `RegexOptions.NonBacktracking`.
- Rilascio: la procedura (tag `v*` → `release.yml`, SignPath, firma ECDSA, pubblicazione) è in `docs/SIGNING.md`. Vincoli: `tools\sign-release.ps1 -Exe <file>` si esegue sui file già firmati con Authenticode, perché Authenticode cambia l'hash; la chiave ECDSA non va mai nella CI.
- Aggiornamenti firmati (dalla 1.1.0): le chiavi private stanno solo in `%LOCALAPPDATA%\Baton-signing` o, se create prima della
  rinomina, `%LOCALAPPDATA%\DownloadManager-signing` (lì c'è la principale `b80f666050fcbc7d`; `tools\sign-release.ps1` le legge entrambe)
  e non vanno nel repository né nell'output; le pubbliche sono in `src/Baton.Core/Updates/release-keys.txt`. I test usano chiavi proprie, mai quella reale.
- Firma di Firefox: `tools\sign-firefox.ps1` legge `AMO_JWT_ISSUER`/`AMO_JWT_SECRET` dall'ambiente; nessuna chiave nel repository.
  Eseguilo solo su richiesta dell'utente: pubblica su AMO.

## Documentazione
- `README.md` in inglese, breve (presentazione, installazione, verifica, known issues); `README.it.md` è la guida completa in
  italiano: le funzioni nuove vanno descritte lì, e in sintesi nel README inglese. File di comunità: `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `.github/ISSUE_TEMPLATE`, `.github/PULL_REQUEST_TEMPLATE.md`.

## Testi e accessibilità
- Testi dell'app solo in `Strings.resx` (inglese, neutra) e `Strings.it.resx`, generati da `python tools/make_resx.py src`
  (tabella di coppie inglese/italiano nello script; segnala chiavi mancanti o inutilizzate). In XAML `{l:Loc Chiave}`, non `x:Static`
  (non segue il cambio di lingua dal vivo); menu nel codice con `.Text(nameof(Strings.X))`. `WindowGalleryTests` controlla i testi tagliati in en/it (`BATON_SHOTS` per gli screenshot).
  Testi dell'estensione in `_locales/it` ed `_locales/en`, generati da `python tools/make_locales.py extension`. I file generati non si modificano a mano.
- Ogni controllo ha `AutomationProperties.Name` e la navigazione da tastiera; nell'estensione aria-label, focus visibile e tema chiaro/scuro.

## Prove reali: non toccare i dati né il Desktop dell'utente
- Usa la skill `prova-reale` (browser: `chromium-trial.mjs`, `firefox-trial.mjs`; grabber: `grab-trial.ps1` sul sito locale
  `/grab/` o su books.toscrape.com con pochi giri): app isolata con `BATON_DATA` (registro in memoria, pipe e istanza propria; `DOWNLOADMANAGER_DATA` da sola isola allo stesso modo, accanto a `BATON_DATA` indica i dati 1.1 da migrare), cartella di lavoro `%TEMP%\dm-trials`, cartelle delle categorie riportate lì prima di scaricare (quelle reali puntano al Desktop).
- L'utente lavora al PC durante le prove: non simulare input di mouse o tastiera sul desktop. App solo via UI Automation (`tools/trials/ui.ps1`, screenshot con `shot.ps1 -NoFront`); browser solo headless con profili temporanei.
- Installer, portable, Scoop: `install-trial.ps1` (installa in `dm-trials\inst` con `BATON_DATA`; si rifiuta se Baton è
  installato davvero), `portable-trial.ps1` (alla domanda sull'host nativo rispondi sempre «No»), Scoop solo in `dm-trials\scoop` con
  `XDG_CONFIG_HOME` reindirizzato e PATH ripristinato con il suo tipo. Pulsanti di Inno: `ui.ps1 -Action click` (BM_CLICK).
- A fine prova `tools\trials\finish.ps1` (prima senza `-Apply`): niente processi di prova aperti, nessuna traccia di Firefox portabile nel registro.

## Fine fase
Usa la skill `fine-fase`. L'app reale dell'utente è installata con l'installer in `%LOCALAPPDATA%\Programs\Baton`; `publish\` è solo l'output di build. Estensioni (Chromium e Firefox), notifiche e host nativo devono puntare a quell'exe, senza riferimenti a build di prova o a `publish\` nel registro e senza voci non richieste nella chiave Run.

## Hook del progetto (`.claude/hooks`)
- `guard.ps1` blocca la scrittura su `...\CurrentVersion\Run` e `NativeMessagingHosts` e le cancellazioni sul Desktop fuori dal progetto. Se un comando legittimo viene bloccato, spiega all'utente cosa serve invece di aggirare il blocco.
- `extension-check.ps1` controlla sintassi e test Node dopo ogni modifica in `extension/`; `stop.ps1` fa una build incrementale a fine risposta e segnala i processi di prova rimasti. Test degli hook: `.claude\hooks\test-hooks.ps1`.
