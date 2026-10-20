# Changelog

Versioni di DownloadManager (app ed estensione). Formato ispirato a [Keep a Changelog](https://keepachangelog.com/it/1.1.0/);
numerazione [SemVer](https://semver.org/lang/it/). Le date sono quelle dei commit.

## [1.1.1] — 2026-10-20

Prima versione pubblica su GitHub.

- **Aggiornamenti**: indirizzo predefinito le release di questo repository
  (`https://api.github.com/repos/AndreaAzzarello/DownloadManager/releases/latest`); il controllo automatico resta
  disattivato finché non lo attivi. Chi aveva lasciato l'indirizzo vuoto riceve quello predefinito.
- **Firma**: aggiunta la chiave pubblica di riserva `cc691630b30bf7f1` (protetta da password, conservata fuori dal PC)
  accanto alla principale `b80f666050fcbc7d`: se la principale andasse persa, le release firmate con la riserva verranno
  accettate dalla 1.1.1 in poi.
- `tools/verify-release.ps1`: verifica SHA-256 e firma di una release scaricata, senza installarla.
- Repository: licenza MIT, `THIRD-PARTY-NOTICES.md`, `SECURITY.md`, questo changelog, CI su GitHub Actions (build e test,
  nessuna firma), README per GitHub con screenshot.
- Test dell'estensione: i timer lunghi non tengono più vivo il processo di Node (da 15 s a meno di 1 s).
- Estensione 1.1.1 (nessuna modifica funzionale).

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

[1.1.1]: https://github.com/AndreaAzzarello/DownloadManager/releases/tag/v1.1.1
[1.1.0]: https://github.com/AndreaAzzarello/DownloadManager/releases/tag/v1.1.0
[1.0.1]: https://github.com/AndreaAzzarello/DownloadManager/releases/tag/v1.0.1
[1.0.0]: https://github.com/AndreaAzzarello/DownloadManager/releases/tag/v1.0.0
