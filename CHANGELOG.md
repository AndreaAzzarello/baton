# Changelog

Versioni di Baton (app ed estensione), che fino alla 1.1 si chiamava **DownloadManager**. Formato ispirato a
[Keep a Changelog](https://keepachangelog.com/it-IT/1.1.0/); numerazione [SemVer](https://semver.org/lang/it/). Le date sono
quelle dei commit.

## [1.3.3] — 2026-10-22

L'estensione si aggiorna insieme all'app; manifest winget allo schema 1.12, privacy e roadmap pubbliche.

- **Pacchetto dell'estensione firmato** in ogni release: `baton-extension-<versione>.zip` (la cartella `extension` per
  Chromium e `baton-firefox.xpi`) con `.sha256` e `.sig`, lo stesso schema di firma di `Baton.exe` («versione + nome del
  file + SHA-256»). `sign-release.ps1` e `verify-release.ps1` lo gestiscono (versione letta dal nome, niente Authenticode
  per uno zip); `build-packages.ps1` lo crea con gli stessi file dello zip portable.
- **Aggiornamento dall'app**: prima dello scambio scarica il pacchetto, ne verifica hash e firma, lo estrae in
  `extension.new` accanto al programma (solo `extension/…` e l'xpi; rifiutati percorsi fuori dalla cartella, nomi
  assoluti o con `..`, collegamenti simbolici, file doppi, più di 2000 voci o più di 128 MB estratti, contati durante la
  scrittura) e controlla che `manifest.json` abbia la versione della nuova app. Poi cartella e xpi in uso diventano
  `extension.old` e `baton-firefox.xpi.old`; uno scambio interrotto rimette i precedenti. Se il pacchetto manca, la firma
  non è valida o la cartella non si può scrivere, il programma si aggiorna e l'estensione no, con un avviso che spiega
  perché e come aggiornarla. Vale per la copia installata e per la portable.
- **Ripristina la versione precedente** (e il guardiano, se la nuova versione non parte) rimette anche `extension.old`,
  solo se è della versione ripristinata.
- **Dalla 1.3.2**: quella versione aggiorna solo `Baton.exe`; al primo avvio la 1.3.3 trova la cartella `extension` più
  vecchia e installa da sé il pacchetto firmato della propria versione (release `v1.3.3`). L'app contatta le release solo
  dopo un aggiornamento e con il controllo degli aggiornamenti.
- **Ricaricamento nei browser Chromium**: la risposta a `hello` contiene la versione della cartella su disco
  (`extensionVersion`); se l'app è più recente e la cartella è già aggiornata, l'estensione chiama
  `chrome.runtime.reload()` una volta per versione (al risveglio, al massimo ogni 15 minuti, o aprendo popup e verifica
  della connessione). Se dopo il ricaricamento la versione non cambia non riprova, e il popup dice di ricaricarla da
  `chrome://extensions`. In Firefox il popup spiega come caricare il nuovo `baton-firefox.xpi`.
- L'installer toglie anche `extension.old`, `extension.new` e `baton-firefox.xpi.old` alla disinstallazione.
- Il messaggio dell'host nativo «Baton non ha risposto in tempo» (e «Impossibile avviare Baton», prima scritto in
  italiano nel codice) usa la lingua scelta nell'app, letta solo quando serve un testo d'errore.
- Il testo sul pacchetto per Firefox e l'avviso di «Avvia con Windows» nella portable seguono il cambio di lingua a
  finestra aperta. «Avvia con Windows» della procedura guidata e delle impostazioni è un solo modello condiviso, e la
  finestra di permesso del registro della portable è un unico blocco `using` (prima due copie della stessa logica).
- Finestre di avanzamento, proprietà e certificato: un valore su due righe non sposta più il valore accanto rispetto
  alla sua etichetta.
- Manifest winget allo schema 1.12.0 (verificati con `winget validate`); un test controlla schema, hash e testo italiano.
- `PRIVACY.md` (nessuna telemetria, con chi parla l'app, dati solo in locale, segreti con DPAPI, nulla a terzi) e
  `ROADMAP.md`; GitHub Discussions attive. GIF del README in inglese, quella italiana in `README.it.md`.

## [1.3.2] — 2026-10-22

Versione di correzione, emersa provando la portable su un altro PC: l'estensione è inclusa, nessun download separato.

- L'installer (`{app}\extension`), lo zip portable e Scoop contengono l'estensione della stessa versione accanto a
  `Baton.exe`: la cartella `extension` (build Chromium non pacchettizzata, «Carica estensione non pacchettizzata») e
  `baton-firefox.xpi` (non firmato da Mozilla: in Firefox normale si carica come componente temporaneo).
- Procedura guidata e Impostazioni › Generale mostrano il percorso completo della cartella accanto a `Baton.exe`, con
  **Apri la cartella dell'estensione** (ora attivo) e **Copia il percorso**; indicano il pacchetto per Firefox e, in una
  build di sviluppo senza quei file, rimandano alla pagina della release. Prima i testi parlavano della «cartella
  extension del progetto» e il pulsante restava spento.
- **Avvia Baton con Windows** non è preselezionato in nessuna modalità. La portable ora può attivarlo, con una voce
  propria («Baton portable») che non prende il posto di quella della copia installata: un avviso spiega che punta a quella
  cartella e smette di funzionare se la sposti. Nella portable il registro accetta solo quella voce, solo durante la scelta.
- Limite noto: l'aggiornamento dall'app sostituisce solo `Baton.exe`, l'estensione accanto resta alla versione precedente
  finché non si esegue il nuovo setup o si estrae il nuovo zip.
- Il workflow di rilascio compila l'estensione prima dei pacchetti; `tools\build-packages.ps1` controlla che lo zip la
  contenga.
- Manifest winget: la descrizione italiana della 1.3.1 aveva le lettere accentate rovinate («piÃ¹»), perché
  `tools\make-manifests.ps1` aveva perso il BOM e Windows PowerShell lo leggeva come ANSI. Un test controlla ora il BOM
  di ogni script con testo accentato.

## [1.3.1] — 2026-10-22

Versione di correzione: verdetto dei controlli dopo il download sempre presente, rilascio riproducibile.

- Verifica dell'hash (a fine download e con «Copia hash»): un file appena scritto e tenuto aperto per un attimo da un
  altro programma (per esempio l'antivirus) non lascia più il controllo senza esito: si legge insieme agli altri lettori e,
  se un programma lo sta scrivendo, si riprova fino a 5 volte (mai durante una scrittura); un file sparito fallisce subito.
- Un download con controlli (hash o antivirus) non viene più mostrato per un istante come «Completato» prima di
  «Completato · verifica…»: chi lo leggeva in quel momento lo trovava senza verdetto.
- `tools\build-packages.ps1` accetta cartelle relative (il workflow di rilascio si fermava).
- Build riproducibile anche tra PC e GitHub Actions: `.gitattributes` fissa il fine riga (LF), i generatori dei testi
  scrivono LF e il workflow non esporta più `VERSION`, che MSBuild applicava come versione a tutti i progetti. Lo stesso
  commit dà lo stesso `Baton.exe` byte per byte (verificato); installer e zip restano diversi tra macchine.
- Test indipendenti dalla lingua di Windows (la CI gira in inglese) e da PowerShell 7 come processo padre.

## [1.3.0] — 2026-10-21

**Ready for everyone**: Baton in inglese, con installer, winget, Scoop e versione portable.

**Interfaccia in inglese e italiano**
- Tutta l'app è tradotta: finestre, menu, barra degli strumenti, area di notifica, notifiche, dialoghi, procedura guidata,
  grabber, conto alla rovescia, testi per lo screen reader e i messaggi di errore del motore (29 che erano scritti in inglese
  nel codice). `Strings.resx` è ora in inglese (lingua neutra) e `Strings.it.resx` in italiano, generati da un'unica tabella
  di coppie (`tools/make_resx.py`), che si ferma se i segnaposto delle due lingue differiscono.
- Lingua automatica dalla lingua di Windows (italiano → italiano, qualsiasi altra → inglese) o scelta in **Impostazioni ›
  Generale › Lingua**; il cambio vale subito, senza riavvio (anche menu costruiti nel codice e pannello laterale).
- Date, ore, numeri e dimensioni seguono la lingua scelta (formato regionale di Windows se della stessa lingua).
- Categorie predefinite e Coda principale seguono la lingua finché non vengono rinominate.
- Pseudo-localizzazione (`--pseudo`, `BATON_PSEUDO=1`) e test che apre ogni finestra in entrambe le lingue cercando testi
  tagliati: corretti la conferma della password in chiaro (usciva dalla finestra) e il segnaposto della ricerca.

**Barra di stato**
- La selezione mostra il già scaricato e la dimensione totale: «3 selezionati · 1,2 GB di 4,8 GB»; con una dimensione
  ignota il totale è «?», con il suggerimento «Dimensione totale sconosciuta». Si aggiorna mentre scarica.

**Installer, pacchetti e versione portable**
- Installer per singolo utente (Inno Setup 6, nessun diritto di amministratore) in `%LOCALAPPDATA%\Programs\Baton`, menu
  Start e Desktop facoltativo, voce in App installate, italiano e inglese, tema chiaro o scuro come Windows.
  Disinstallazione completa (host nativo, link `baton:`, notifiche, avvio con Windows solo se puntano a quella copia) con la
  domanda se tenere i dati, «tieni» come scelta predefinita.
- L'installer trova copie precedenti: importa i dati di una copia portable (con la copia sicura già usata per DownloadManager),
  ripunta le registrazioni di altre copie, lascia la migrazione da DownloadManager al primo avvio.
- **Versione portable** (`Baton-<versione>-portable.zip`): dati nella cartella `Data` accanto all'exe, nessuna scrittura
  nel registro tranne l'host nativo dopo conferma; notifiche dall'icona dell'area di notifica.
- Manifest per **winget** (`AndreaAzzarello.Baton`) e **Scoop** (`baton.json` con `checkver` e `autoupdate`), generati dai
  file della release (`tools/make-manifests.ps1`).
- Gli aggiornamenti dall'app funzionano per la copia installata (aggiornando anche la versione in App installate) e per
  quella portable; restano firma, ritorno automatico e `Baton.exe.old`.

**Rilascio e firma**
- Workflow di rilascio (`release.yml`, da tag `v*`): build riproducibile (SDK fissato in `global.json`), installer, zip,
  pacchetti dell'estensione, `SHA256SUMS.txt`; firma Authenticode con **SignPath Foundation** quando il progetto sarà
  approvato, altrimenti gli stessi file senza. La chiave della release (ECDSA) resta sul PC dell'autore e firma i file già
  firmati da SignPath. Policy di firma in `docs/SIGNING.md`.
- `tools/verify-release.ps1` controlla anche la firma Authenticode, quando c'è, e verifica l'installer come `Baton.exe`.

**Altro**
- `SatelliteResourceLanguages`: solo le risorse inglesi e italiane di .NET nell'exe (da 173 a 165 MB).
- User agent predefinito `Baton/1.3`.
- Colonne «Aggiunto» e «Completato» più larghe (minimo 150 px anche con un layout salvato): l'orario inglese con AM/PM veniva tagliato.

## [1.2.0] — 2026-10-21

Prima versione pubblica, con il nuovo nome **Baton**: il testimone della staffetta, come le connessioni che si passano i
pezzi del file nella segmentazione dinamica. Comprende la 1.1.1, mai pubblicata.

**Mouse e selezione multipla**
- Le righe selezionate non si vedevano: il template delle celle toglieva l'evidenziazione e il tema Fluent ignora lo sfondo
  della riga. Ora le celle disegnano selezione (sfondo pieno, testo ≥ 4,5:1), passaggio del mouse e focus da tastiera,
  nell'elenco principale, in "Scarica tutto" e nell'anteprima del grabber.
- Mouse come in Esplora file: clic sul vuoto deseleziona; clic su una riga di una selezione multipla la riduce solo al rilascio
  (si trascinano più file); trascinamento oltre la distanza di sistema; tasto destro che seleziona solo se la riga non lo era;
  menu anche con il tasto Menu e Maiusc+F10.
- Menu contestuale sull'intera selezione con Riprova, Cambia categoria e le voci "Rimuovi N download" / "Elimina N download
  dalla lista e dal disco"; Canc rimuove dalla lista (conferma se più di uno o se un download non è finito), **Maiusc+Canc** sposta i file nel Cestino dopo
  una conferma con numero e dimensione: prima "elimina i file" li cancellava definitivamente. Pulsante facoltativo *Elimina dal
  disco* nella barra. Barra di stato con "N selezionati · dimensione".

**Riquadro dei video**
- Si chiude (× o Esc) per la scheda, ricompare con un video diverso o ricaricando la pagina; menu ⋯ con *Nascondi su questa
  pagina*, *Non mostrare mai su questo sito*, *Riduci a icona* (linguetta con il logo).
- Comparsa a scelta nel popup: sempre visibile, ridotto a icona (predefinita), solo dal menu dell'estensione (badge con il
  numero di video e download dal popup).
- Mai sopra i controlli del player, posizione ricordata per sito, nascosto a schermo intero; colori del brand kit.
- Corretto: dopo un ricaricamento il badge restava vuoto e i video della pagina non venivano ricontati.

**Nuovo nome e identità**
- App `Baton.exe`, progetti `Baton.*`, estensione «Baton» con le voci «Scarica con Baton», «Scarica tutto con Baton…» e
  «Cattura questo sito con Baton…»; user agent `Baton/1.2`.
- Logo e icone dal **brand kit ufficiale** (una "B" con la freccia di download): app, finestre, area di notifica, notifiche,
  estensione (anche grigia per «solo menu»), logo del README per tema chiaro e scuro, immagine per GitHub. Generati solo i
  formati mancanti (20 e 40 px dagli SVG del kit, le grigie desaturando i PNG del kit). Il marchio non rientra nella licenza MIT.
- Accento del tema dai colori del kit (#0449EF chiaro, #049AFF scuro) con contrasto ≥ 4,5:1.

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
  ripristino, collegamenti, nessun testo visibile con il vecchio nome.
- Nuovo progetto `tests/Baton.UiTests` (WPF su thread STA, finestra fuori schermo, Ctrl e Maiusc impostati solo sul
  thread del test): selezione con mouse e tastiera, menu da tastiera, comandi sulla selezione, Cestino con conferma.
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
