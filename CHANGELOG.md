# Changelog

Versioni di Baton (app ed estensione), che fino alla 1.1 si chiamava **DownloadManager**. Formato ispirato a
[Keep a Changelog](https://keepachangelog.com/it/1.1.0/); numerazione [SemVer](https://semver.org/lang/it/). Le date sono
quelle dei commit.

## [1.2.0] — 2026-10-21

Prima versione pubblica, con il nuovo nome **Baton**: il testimone della staffetta, come le connessioni che si passano i
pezzi del file nella segmentazione dinamica. Comprende la 1.1.1, mai pubblicata.

**Nuovo nome e identità**
- App `Baton.exe`, progetti `Baton.*`, estensione «Baton» con le voci «Scarica con Baton», «Scarica tutto con Baton…» e
  «Cattura questo sito con Baton…»; user agent `Baton/1.2`.
- Nuovo logo: tre barre (le connessioni in parallelo) che si uniscono in una sola freccia (il file). Icone dell'app e
  delle finestre, area di notifica in versione per barra delle applicazioni chiara e scura (disegnata pixel per pixel a
  16–32 px), estensione (anche grigia per «solo menu»), notifiche, logo del README e immagine per GitHub.
- Colori del tema allineati al logo dove il contrasto resta ≥ 4,5:1 (accento del tema scuro, segmenti nella finestra di
  avanzamento).

**Passaggio da DownloadManager (automatico)**
- Al primo avvio i dati di `%LOCALAPPDATA%\DownloadManager` (download, impostazioni, credenziali e certificati, progetti
  del grabber, log, controlli in sospeso) vengono **copiati** in `%LOCALAPPDATA%\Baton`: database tramite il backup di
  SQLite e verificato prima dell'uso, poi messo al suo posto per ultimo. La cartella vecchia non viene toccata; se la copia
  non riesce, Baton usa la cartella vecchia, lo segnala e riprova al prossimo avvio.
  Se DownloadManager è ancora aperto, Baton chiede di chiuderlo prima di copiare (le due app non lavorano mai insieme
  sugli stessi download).
- Registrazioni rinominate: host nativo `com.andreaazzarello.baton` (Chrome, Edge, Chromium, Brave, Opera, Firefox), link
  delle notifiche `baton:` firmati, identità delle notifiche `Baton.App`. Quelle di DownloadManager vengono rimosse; la
  voce di avvio con Windows passa a Baton solo se c'era.
- Aggiornamento dalla 1.1: la 1.1 installa `Baton.exe` al posto di `DownloadManager.exe`; al primo avvio Baton prende il
  suo nome, sposta la versione precedente in `Baton.exe.old` (per «Ripristina la versione precedente», che rimette la 1.1
  come `DownloadManager.exe` con i suoi dati) e aggiorna i collegamenti su Desktop, menu Start e barra delle applicazioni.
  Il ripristino automatico della 1.1 resta valido finché Baton non segnala di essere partito.
- Estensione: nuovo ID per Firefox (`baton@andreaazzarello`, va reinstallata); in Chromium l'ID resta lo stesso (chiave
  pubblica nel manifest). Esportazioni con formato `Baton`; quelle di DownloadManager si importano ancora.

**Dalla 1.1.1 (mai pubblicata)**
- **Aggiornamenti**: indirizzo predefinito le release di questo repository
  (`https://api.github.com/repos/AndreaAzzarello/baton/releases/latest`); il controllo automatico resta disattivato
  finché non lo attivi. Un indirizzo vuoto (o quello provvisorio della 1.1.1) diventa quello predefinito.
- **Firma**: aggiunta la chiave pubblica di riserva `cc691630b30bf7f1` (protetta da password, conservata fuori dal PC)
  accanto alla principale `b80f666050fcbc7d`. `tools/verify-release.ps1` verifica SHA-256 e firma di una release.
  `tools/sign-release.ps1` legge le chiavi da `%LOCALAPPDATA%\Baton-signing` e da quella di prima della rinomina.
- Repository: licenza MIT, `THIRD-PARTY-NOTICES.md` con i testi completi in `THIRD-PARTY-LICENSES.txt` (allegato anche
  alla release), `SECURITY.md`, questo changelog, CI su GitHub Actions (build e test,
  nessuna firma), README per GitHub con screenshot.

**Test**
- Nuovi test: migrazione dei dati (anche interrotta a metà e con database danneggiato), rimozione delle vecchie
  registrazioni (registro in memoria), schema `baton:`, aggiornamento da `DownloadManager.exe` a `Baton.exe` con
  ripristino, collegamenti, icone, nessun testo visibile con il vecchio nome.
- Corretti tre test instabili: timer lunghi che tenevano vivo Node (da 15 s a meno di 1 s), lettura del log mentre altri
  test scrivono, contesto di un modulo letto un istante prima del salvataggio.

## [1.1.0] — 2026-10-19

Rafforzamento della sicurezza.

- **Aggiornamenti firmati**: ECDSA P-256 su "versione + nome del file + SHA-256" nell'allegato `.sig`; chiavi pubbliche
  incorporate con rotazione; firme assenti, errate, di chiavi sconosciute o ripresentate vengono rifiutate.
  `tools/sign-release.ps1` crea la chiave fuori dal repository (DPAPI o password) e firma.
- **Credenziali** legate a schema + host + porta; conferma esplicita per password in chiaro (Basic su http, FTP). DB v8.
- **Proxy**: password solo ai proxy scelti; PAC solo https o file locale, mai da cartelle di rete.
- **Video**: solo http/https, mai indirizzi locali o privati da pagine pubbliche (controllo dopo il DNS, alla connessione
  e a ogni redirect).
- **Pipe** con connessioni limitate e timeout; **link delle notifiche** legati al download e con scadenza.
- **Import** sicuro (cartelle solo locali e non di sistema, nulla preselezionato, rete a parte con avviso).
- **Nomi dei file** senza caratteri bidi/invisibili né nomi di dispositivo; **ffmpeg** con formato esplicito e solo
  protocollo `file`; **copia offline** senza script per impostazione.
- Estensione 1.1.0: cookie secondo SameSite, moduli tenuti al massimo 15 s, conferma per i POST di un altro sito,
  permesso `alarms`.

## [1.0.1] — 2026-10-18

- L'hash di un aggiornamento da GitHub si legge solo dall'allegato `DownloadManager.exe.sha256` (più formati); una release
  senza allegato valido non viene installata e l'app spiega il motivo.
- Testo unico per l'esito dell'antivirus.

## [1.0.0] — 2026-10-18

- Verifica dell'hash (MD5/SHA-1/SHA-256) e scansione antivirus facoltativa a fine download.
- Esportazione e importazione di elenco e impostazioni (senza segreti).
- Controllo degli aggiornamenti (disattivato per impostazione) con installazione, guardiano e ritorno automatico alla
  versione precedente.
- Prima configurazione guidata; proposta di spostare le cartelle dal Desktop.
- Barra degli strumenti personalizzabile, log ruotati e senza dati riservati, finestra d'errore con "Copia dettagli",
  virtualizzazione delle righe e memoria restituita nella tray. DB v7.

## [0.7.0] — 2026-10-15

- **Grabber**: procedura in cinque passi, modalità File e Copia offline (solo HTML/CSS statici), robots.txt, limiti e
  protezione dalle trappole, progetti salvati, sincronizzazione e pianificazione.
- **Video**: scelta della lingua audio, sottotitoli WebVTT (file accanto o incorporati con ffmpeg), DASH multi-periodo.
- Estensione: "Cattura questo sito"; protocollo 3. Firma "non elencata" per Firefox (`tools/sign-firefox.ps1`). DB v6.

## [0.6.0] — 2026-10-15

- **Video** HLS e DASH con qualità e dimensioni stimate; unione con ffmpeg se presente (mai incluso); DRM, dirette e siti
  che lo vietano esclusi.
- **Moduli** (POST) ripetuti dall'app; "Scarica tutto"; protocollo 2 con `MessageValidator` nell'host e nella pipe.
- Estensione 0.6.0 con un solo sorgente per Chromium e Firefox, modalità automatica o solo menu, regole, pulsante video.
- Correzione successiva per Firefox: attesa breve dell'evento del modulo arrivato dopo il download. DB v5.

## [0.5.0] — 2026-10-14

- FTP/FTPS (esplicito e implicito) con ripresa e segmenti; certificati mai accettati in silenzio.
- Proxy (sistema, HTTP/HTTPS/SOCKS5, PAC in sandbox) e autenticazione HTTP (Basic, Digest, NTLM, Negotiate) con
  credenziali cifrate con DPAPI.
- Registro dietro `IUserRegistry`, esecuzione isolata con `DOWNLOADMANAGER_DATA`; icona della tray senza Windows Forms. DB v4.

## [0.4.0] — 2026-10-14

- Code con nome, limiti, tentativi, quota oraria e pianificazione (anche con timer di riattivazione).
- Azioni di fine coda con conto alla rovescia accessibile; icona nella tray con limite di velocità; avvio con Windows. DB v3.

## [0.3.0] — 2026-10-14

- Categorie predefinite e personali; interfaccia MVVM con albero laterale, colonne configurabili, ricerca.
- Temi chiaro, scuro e di sistema (Fluent); finestra di avanzamento con mappa dei segmenti; notifiche di Windows.
- "Aggiorna indirizzo" che conserva i dati scaricati; schema versionato con migrazione e backup. DB v2.

## [0.2.0] — 2026-10-14

- Prima versione: download segmentato con pausa e ripresa persistenti, coda, integrazione con il browser tramite native
  messaging.

[1.2.0]: https://github.com/AndreaAzzarello/baton/releases/tag/v1.2.0
[1.1.0]: https://github.com/AndreaAzzarello/baton/releases/tag/v1.1.0
[1.0.1]: https://github.com/AndreaAzzarello/baton/releases/tag/v1.0.1
[1.0.0]: https://github.com/AndreaAzzarello/baton/releases/tag/v1.0.0
