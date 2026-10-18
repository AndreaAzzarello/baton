# DownloadManager

Download manager per Windows (C# / .NET 10 LTS / WPF) con download segmentato, pausa/ripresa persistente,
code con nome e pianificazione, categorie, FTP/FTPS, proxy, autenticazione, video HLS/DASH (con lingue e sottotitoli), grabber
di siti (file filtrati o copia offline, sincronizzabile e pianificabile), verifica dei file (hash e antivirus), esportazione e
importazione, aggiornamenti verificati e integrazione con Chrome, Edge, Opera, Brave e Firefox.
Versione 1.0.1.

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/DownloadManager.Core` | Motore: segmenti, connessioni, checkpoint, code e scheduler, categorie, SQLite; `Net` (trasporti HTTP e FTP, proxy, PAC), `Security` (credenziali, certificati, segreti DPAPI), `Platform` (registro iniettabile e registrazioni di sistema); testi in `Resources/Strings.resx` |
| `src/DownloadManager.App` | Interfaccia WPF in MVVM: `ViewModels`, `Views`, `Services` (finestre, tema, notifiche, area di notifica, avvio con Windows, risparmio energia), `Themes`, `Controls` |
| `src/DownloadManager.Core/Media` | Video: parser HLS (`HlsPlaylist`) e DASH (`DashManifest`), scelta delle qualità e controlli (`MediaCatalog`), download dei segmenti (`MediaJob`), ffmpeg facoltativo |
| `src/DownloadManager.Core/Grabber` | Grabber: `GrabberEngine` (visita per livelli, HTML/CSS statici con AngleSharp), `RobotsTxt`, `HostThrottle` (richieste per host, 429/Retry-After), `TrapGuard` e `CrawlUrls` (normalizzazione, trappole), `LocalPaths` (percorsi locali sicuri), `PageLinks` (link di HTML, CSS e srcset, anche per la riscrittura), `GrabberService` (progetti, sincronizzazione, pianificazione) |
| `src/DownloadManager.Core/PostDownload`, `Portable`, `Updates`, `Setup` | Dopo il download (`FileHash`, `VirusScanner`), esportazione e importazione senza segreti, aggiornamenti verificati con ripristino, primo avvio e spostamento dal Desktop; `Log` (log ruotati e mascherati) |
| `src/DownloadManager.Core/Integration` | Protocollo con l'estensione (`Messages`, versione 3), validazione (`MessageValidator`), host nativo, pipe, "Scarica tutto" (`BatchLinks`) |
| `tests/DownloadManager.Tests` | Test xUnit con server locali avviati dai test: HTTP (Range, ETag, 429, cookie, Basic/Digest, moduli POST), FTP/FTPS, proxy HTTP e SOCKS5; fixture HLS/DASH in `Fixtures/Media`; sito multi-host per il grabber (`GrabberTestSite`) |
| `extension` | Sorgente unico dell'estensione (Manifest V3); è anche la build per Chromium, caricabile così com'è |
| `tools/build-extension.mjs` | Build per Chromium e Firefox in `dist/` (cartelle e pacchetti .zip / .xpi) |
| `tests/extension` | Test dell'estensione con `node --test` (regole, moduli, media, ripiego), eseguiti anche da `dotnet test` |
| `tools/trials` | Prove reali nei browser (Edge, Opera, Firefox headless) con l'app isolata: `prepare.ps1`, `chromium-trial.mjs`, `firefox-trial.mjs`, `grab-trial.ps1`, `perf.ps1` con `fill_downloads.py` (elenco di migliaia di download, memoria), `finish.ps1`; sito di prova locale (anche `/grab/` per il grabber) |
| `tools/sign-firefox.ps1` | Firma "non elencata" dell'estensione per Firefox su addons.mozilla.org (chiavi solo da variabili d'ambiente) |
| `tools/make_resx.py`, `tools/make_locales.py` | Generatori dei testi: `Strings.resx` dell'app e `_locales` dell'estensione (la tabella è nello script) |
| `CLAUDE.md`, `.claude/` | Regole del progetto per Claude Code, hook (guardia su registro e Desktop, controlli dell'estensione, build incrementale) e skill `prova-reale`, `fine-fase`, `stringhe` |
| `publish/DownloadManager.exe` | Build Release self-contained win-x64 (non richiede il runtime .NET) |

## Come funziona il download

Il motore chiede al *trasporto* solo cosa è il file (dimensione, ripresa possibile, ETag/data) e uno stream da un offset:
segmentazione dinamica, ripresa, ripiego su una connessione, quote e code funzionano allo stesso modo per HTTP e FTP.

- **Probe**: `GET` con `Range: bytes=0-0`. Una risposta 206 con lunghezza totale indica che il server supporta i range.
  Se il server accetta la connessione ma non risponde entro 30 s, la richiesta viene ritentata invece di restare bloccata.
- **Segmentazione**: il file viene diviso in N range (minimo 1 MB ciascuno) scritti in un unico file `.part` preallocato e sparse.
- **Segmentazione dinamica**: una connessione che finisce il suo range prende la metà non scaricata del range più grande ancora attivo.
- **Pausa/ripresa**: ogni secondo i segmenti vengono salvati in SQLite, dopo il flush su disco.
  Alla ripresa, `If-Range` con ETag/Last-Modified garantisce che il file sul server non sia cambiato; se è cambiato, il download riparte da zero.
- **Server che limitano le connessioni** (429/503): le connessioni in eccesso si ritirano e le altre completano il loro range.
- **Avvio manuale e code**: "Avvia" parte subito, fuori da ogni limite e quota; "In coda" attende il proprio turno nella sua coda.
- **Limite di velocità**: token bucket globale condiviso da tutte le connessioni, modificabile anche dall'area di notifica.
- **File completati**: ricevono il contrassegno "scaricato da Internet" (`Zone.Identifier`) come quelli dei browser,
  così Windows applica SmartScreen e la visualizzazione protetta di Office.

## Categorie

Compressi, Documenti, Musica, Video, Programmi e Generale, ognuna con estensioni e cartella modificabili
(pulsante **Categorie** o tasto destro sul pannello laterale: aggiungi, rinomina, elimina; Generale non si elimina).

- La categoria si sceglie dall'estensione nel nome del file; se manca (es. `download.php?id=7`), dal `Content-Type` del server.
  La scelta fatta dall'indirizzo viene confermata quando il server comunica nome e tipo reali, prima di scrivere dati.
- Le cartelle predefinite sono relative (`Video`, `Compressi`…): diventano sottocartelle della cartella predefinita dei download
  e la seguono se la cambi. Una cartella scelta con Sfoglia è assoluta.
- Se nel dialogo di aggiunta cambi cartella a mano, quella cartella vale anche se la categoria cambia.
- Eliminando una categoria i suoi download passano a Generale; i file non vengono spostati.

## FTP e FTPS

- `ftp://` (in chiaro), `ftpes://` (FTPS esplicito, AUTH TLS, porta 21) e `ftps://` (FTPS implicito, porta 990), con FluentFTP:
  libreria mantenuta che gestisce TLS esplicito e implicito, REST, SIZE/MDTM, modalità attiva/passiva e proxy SOCKS5/HTTP CONNECT.
- **Identità del file**: SIZE e MDTM, usati anche da *Aggiorna indirizzo* (stessa dimensione e stessa data = stesso file).
- **Ripresa e segmenti**: REST; ogni segmento usa una sua connessione. Se il server rifiuta connessioni in più (421, oppure 530
  "troppi utenti" mentre un'altra connessione con lo stesso login funziona) le connessioni in eccesso si ritirano, come con il
  429 in HTTP; senza REST si scarica con una sola connessione dall'inizio.
- **Modalità passiva** predefinita; quella attiva si sceglie in Impostazioni › Rete.
- **Credenziali**: nell'indirizzo (`ftp://utente:password@host/file`: vengono tolte dall'indirizzo mostrato e salvato e
  conservate cifrate con il download fino alla fine), salvate per sito in Impostazioni › Credenziali, oppure chieste al primo 530.
  Senza credenziali si usa il login anonimo.
- **Certificati FTPS**: un certificato che Windows riconosce per quel server passa; qualsiasi altro non viene mai accettato in
  silenzio. Compare una finestra con nome, emittente, validità e impronta SHA-256, e *Rifiuta* come scelta predefinita;
  *Accetta solo questa volta* vale fino alla chiusura dell'app, *Accetta sempre per questo server* salva quell'impronta
  (revocabile in Impostazioni › Credenziali). Senza nessuno a cui chiedere, il certificato è rifiutato.

## Proxy e autenticazione

- **Proxy** (Impostazioni › Rete): nessuno, di sistema (predefinito, come nelle versioni precedenti: le impostazioni di Windows,
  incluso il rilevamento automatico), manuale HTTP / HTTPS / SOCKS5 con utente e password, oppure file PAC (http, https o locale),
  eseguito in Jint, un interprete JavaScript isolato, senza accesso a file o .NET, con limiti di tempo e memoria.
  Le eccezioni accettano `*`, `?` e `<local>`; **localhost, 127.0.0.0/8 e ::1 non usano mai il proxy**.
  FTP usa il proxy quando il tipo lo consente: SOCKS5 e HTTP (CONNECT); con un proxy HTTPS FTP va diretto.
- **Verifica**: scarica un indirizzo di prova con le impostazioni scritte, prima di salvarle, e dice se è passato dal proxy.
- **Autenticazione HTTP**: Basic, Digest, NTLM e Negotiate/Kerberos (gestiti da .NET). Con un 401 o un 407 senza credenziali il
  download va in pausa e compare la richiesta, con *Ricorda* (salvate cifrate) o solo per la sessione; più download dello stesso
  server condividono una sola richiesta. Le credenziali salvate sono offerte solo al loro server; *Usa il mio account di Windows*
  vale solo per NTLM/Negotiate. Le credenziali del proxy (407) vanno nelle impostazioni del proxy.
- **Segreti**: password dei siti e del proxy cifrate con DPAPI (account Windows); mai nell'interfaccia, nei log o negli indirizzi.
  I cookie del browser restano legati al loro sito anche dopo un redirect o attraverso un proxy.
- Altre impostazioni di rete: timeout (5–600 s), connessioni massime per server, user-agent.

## Code e pianificazione

- **Code con nome** (pulsante **Code**, oppure tasto destro sul nodo "Code" del pannello laterale): crea, rinomina, elimina.
  La **Coda principale** non si elimina; i download di una coda eliminata passano a lei.
- **Spostare i download**: tasto destro → *Sposta nella coda*, oppure trascinando le righe su una coda del pannello laterale
  (trascinandole su una categoria cambia la categoria). *Su* / *Giù* riordinano dentro la coda del download, con l'ordinamento
  "Ordine della coda".
- **Per ogni coda**: file simultanei, tentativi in caso di errore e pausa tra i tentativi, quota in MB/ora, azione a fine coda,
  pianificazione (ora di avvio, ora di arresto facoltativa, giorni della settimana oppure una data, ripetizione ogni N minuti,
  riattivazione del PC). Il limite totale di download simultanei (Impostazioni) vale per tutte le code insieme.
- **Stato**: nel pannello laterale ogni coda mostra il numero di download e lo stato (Attiva / Pianificata con il prossimo avvio /
  Ferma); il pulsante *Avvia coda* / *Ferma coda* agisce sulla coda selezionata, altrimenti sulla principale.
- **Come decide lo scheduler**: confronta lo stato con l'orologio ogni secondo, non aspetta un evento al minuto esatto.
  - App avviata dopo l'ora di avvio ma prima dell'arresto: la coda parte (senza ritentare i download falliti, così un riavvio
    dopo lo spegnimento di fine coda non può innescare un ciclo). Avviata dopo l'arresto: non parte.
  - PC sospeso o ibernato: al risveglio la coda parte se si è entrati nella finestra, si ferma se la finestra è finita, e non fa
    nulla se la finestra è passata del tutto. Con *Riattiva il PC* Windows sveglia il PC all'ora di avvio
    (serve "Consenti timer di riattivazione" nelle opzioni di risparmio energia).
  - Ora legale: un orario inesistente (es. 02:30 l'ultima domenica di marzo) slitta al primo minuto valido; un orario ripetuto
    (02:00–03:00 l'ultima domenica di ottobre) usa la prima occorrenza, quindi la coda parte una volta sola.
  - Una coda con pianificazione o con azione finale lavora "a giri": quando ha finito si ferma da sola. Una coda semplice
    resta attiva, così i download aggiunti dopo partono subito.
- **Errori**: un download avviato dalla coda che fallisce torna in attesa e viene ritentato dopo la pausa scelta, fino al numero
  di tentativi; il motivo e l'ora del prossimo tentativo sono nel tooltip.
- **Quote**: MB trasferiti negli ultimi 60 minuti, per coda e globale (Impostazioni). Una coda che supera la quota restituisce
  i suoi download all'attesa (entro un secondo) e riprende quando l'ora scorre; gli avvii manuali non sono bloccati ma contano.
- **A fine coda**: nessuna azione, chiudi app, sospendi, iberna, spegni il PC. Prima compare un conto alla rovescia di 60 secondi,
  sempre in primo piano, con *Annulla* come pulsante predefinito (Invio, Esc o chiusura annullano) e annunci per screen reader
  ogni 10 secondi. Prima di chiudere l'app o spegnere il PC i download vengono salvati; se lo spegnimento non avviene davvero,
  dopo un minuto le code ripartono.
- **Area di notifica**: chiudendo la finestra l'app resta attiva lì (disattivabile in Impostazioni). Un clic sull'icona (o Invio
  quando l'icona ha il focus) apre il menu: apri, limite di velocità (nessuno, 256 KB/s… 10 MB/s, personalizzato), avvia/ferma
  tutte le code, esci. Doppio clic apre la finestra. L'icona usa direttamente l'API `Shell_NotifyIcon` di Windows (nessuna
  dipendenza da Windows Forms o librerie esterne) e si ricrea se Esplora risorse si riavvia.
- **Avvio con Windows** (Impostazioni, facoltativo): l'app parte ridotta nell'area di notifica (`--tray`). Se una coda è
  pianificata e l'avvio con Windows è spento, la finestra principale e la finestra Code lo segnalano con un pulsante per attivarlo.

## Grabber

Pulsante **Grabber** (Ctrl+G) nella barra, oppure dal browser: tasto destro sulla pagina › **Cattura questo sito con
DownloadManager…** (l'estensione passa indirizzo, titolo e cookie del sito; mai dalle finestre private).

- **Procedura guidata** in cinque passi: indirizzo e modalità; ambito (profondità, solo questo sito o anche domini elencati,
  robots.txt, richieste al secondo per sito, user-agent); filtri (tipi con gruppi pronti — immagini, documenti, audio, video,
  archivi — e altre estensioni, dimensione minima e massima, espressioni regolari di inclusione ed esclusione); accesso
  (cookie del browser, credenziali salvate, proxy dell'app) e limiti; destinazione (cartella, categoria, coda) e pianificazione.
  Il passo corrente è indicato a sinistra; Alt+← / Alt+→ cambiano passo; ogni campo ha un nome per gli screen reader.
- **Modalità "file filtrati"**: le pagine vengono visitate livello per livello; dei file trovati si chiede solo l'intestazione
  (HEAD, o il primo byte se il server rifiuta HEAD) per tipo, dimensione, ETag e data. L'anteprima li elenca con tipo, dimensione,
  stato e pagina di origine; filtri per testo, tipo e stato; selezione con le caselle o la Barra spaziatrice; invio a una coda e
  a una categoria (o automatica per tipo). I file inviati diventano download normali (la pagina di origine come referrer, i
  cookie del progetto).
- **Modalità "copia offline"**: pagine, fogli di stile (anche `@import` e `url()`), script, immagini (anche `srcset`, `data-src`,
  stili in linea) e i file collegati che passano il filtro dei tipi vengono salvati in `<cartella>\<sito>\<percorso>`,
  mantenendo le cartelle del sito. Alla fine ogni pagina e ogni CSS vengono riscritti per puntare ai file locali (percorsi
  relativi, frammenti `#…` mantenuti); ciò che non è stato salvato resta un link assoluto, che funziona online. `<base>`,
  `integrity` e `crossorigin` vengono tolti (non servono e impedirebbero di aprire la copia da file), la codifica diventa UTF-8.
  Le pagine `.php`/`.asp` diventano `.php.html` ecc., così si aprono nel browser.
- **Rispetto dei siti**: robots.txt (RFC 9309: gruppo del proprio user-agent o `*`, Allow/Disallow con `*` e `$`, la regola
  più lunga vince; `Crawl-delay` rallenta il ritmo) è attivo per impostazione; disattivarlo richiede una conferma esplicita.
  Ogni host riceve al massimo il numero di richieste al secondo scelto; 429 e 503 fanno attendere **tutto l'host** per il
  `Retry-After` indicato (o una pausa crescente), poi la richiesta viene ripetuta; un'attesa oltre 2 minuti fa rinunciare.
  I redirect sono seguiti un passo alla volta: verso un sito fuori dal progetto, un indirizzo escluso o vietato da robots.txt
  non vengono seguiti.
- **Limiti e trappole**: numero massimo di pagine, di file e dimensione totale; indirizzi normalizzati (schema e host in
  minuscolo, porta predefinita, frammento, parametri di sessione e tracciamento come `PHPSESSID`, `jsessionid`, `utm_*`, `fbclid`
  tolti, parametri ordinati) e visitati una volta sola; scartati indirizzi oltre 2000 caratteri, con più di 30 cartelle o una
  cartella ripetuta tre volte (`/a/b/a/b/a/…`); al massimo N pagine "della stessa forma" (numeri sostituiti, valori dei parametri
  ignorati), così un calendario infinito si ferma.
- **Solo HTML statico**: le pagine sono lette con **AngleSharp**, un parser che segue l'algoritmo HTML5 dei browser (pagine
  malformate comprese), senza mai eseguire script; scelto rispetto a HtmlAgilityPack (non conforme a HTML5) e alle espressioni
  regolari (fragili). È gestito, MIT, senza dipendenze native. I contenuti creati da JavaScript non vengono trovati.
- **Percorsi sicuri**: ogni nome è reso valido per Windows (caratteri riservati, `CON`/`NUL`/`COM1`…, punti e spazi finali),
  `..` e `%2e%2e` non escono mai dalla cartella, nomi e percorsi troppo lunghi vengono accorciati con un hash (percorso completo
  sotto i 240 caratteri), due indirizzi non condividono mai un file (anche se differiscono solo per maiuscole). Le espressioni
  regolari dell'utente usano il motore senza backtracking di .NET (nessun blocco con espressioni patologiche; riferimenti
  all'indietro e lookaround non ammessi).
- **Progetti salvati** (Grabber › elenco): modifica, avvio, apertura della cartella, eliminazione (i file restano). Ultima
  esecuzione, risultato e prossima esecuzione pianificata per ciascuno.
- **Sincronizzazione**: ogni esecuzione confronta con la precedente. Nei "file filtrati" un file è *nuovo*, *modificato*
  (ETag diverso; altrimenti data o dimensione diverse) o *invariato*; vengono preselezionati solo nuovi e modificati. Nella copia
  offline immagini e script vengono richiesti con `If-None-Match`/`If-Modified-Since` (304 = non riscaricati); pagine e CSS sono
  sempre riletti per seguirne i link. I file scomparsi dal sito sono segnalati come **rimossi dal sito** (solo se la visita è
  stata completa, oppure con 404/410) e **mai cancellati**. Un file mandato alla coda conta come ottenuto solo quando il suo
  download è **completato**: se fallisce o viene rimosso dall'elenco, la sincronizzazione successiva lo ripropone.
- **Banda**: la copia offline rispetta il limite di velocità globale e la quota oraria globale, come i download in coda.
- **Cookie del progetto**: quelli catturati dal browser sono un accesso al sito, quindi per impostazione vengono **dimenticati
  dopo ogni esecuzione** (opzione nel passo "Accesso e limiti"); il pulsante **Cancella cookie** li elimina subito.
- **Pianificazione** con lo stesso modello e le stesse regole sull'ora legale delle code: una volta in una data oppure ogni
  settimana nei giorni scelti, con ripetizione facoltativa ogni N ore. Un'esecuzione per finestra, anche se l'app viene riavviata
  dentro la finestra. Nei "file filtrati" un'esecuzione automatica manda alla coda del progetto i file nuovi e modificati e la
  avvia; a fine esecuzione l'area di notifica mostra il risultato.

## Dopo il download

- **Hash**: MD5, SHA-1 o SHA-256, facoltativo. Si può indicare l'hash atteso aggiungendo il download (anche incollato come
  `sha256:ab12…`, con spazi o due punti: l'algoritmo si riconosce dalla lunghezza) o più tardi nelle **Proprietà**; a fine
  download viene calcolato (senza caricare il file in memoria) e confrontato. Impostazioni › *Dopo il download* può calcolarlo
  per ogni file. Le Proprietà mostrano *corrisponde* / *non corrisponde* e **Copia hash**.
- **Antivirus**: disattivato per impostazione. Se attivo, ogni file completato viene passato a Windows Defender
  (`MpCmdRun.exe -Scan -ScanType 3 -File "{file}"`) o al programma indicato, con parametri e codici di uscita "minaccia"
  modificabili e un tempo massimo (poi il controllo viene interrotto con tutti i suoi processi). Il file scaricato non viene mai
  aperto né eseguito: è solo un elemento dell'elenco degli argomenti (nessuna shell), e l'antivirus deve essere un `.exe`.
- I controlli girano uno alla volta in background, senza bloccare l'interfaccia, e riprendono se l'app viene chiusa a metà.
  Esito nella colonna *Stato* ("Completato · verifica…", "Completato · attenzione"), nel suggerimento della riga e nella
  notifica, che per un file segnalato non offre **Apri**.

## Interfaccia

- **Barra degli strumenti personalizzabile**: **Altro** › *Personalizza la barra degli strumenti…* (o tasto destro sulla
  barra): pulsanti visibili, ordine (Alt+Su/Giù), separatori, spazio flessibile che divide sinistra e destra, icone piccole o
  grandi, testo sì/no. Salvato tra una sessione e l'altra; *Ripristina* torna alla barra predefinita.
- **Esporta e importa** (**Altro**): la lista dei download in JSON o i soli indirizzi in testo; impostazioni, categorie, code e
  progetti del grabber in JSON. **Mai** password, cookie, campi dei moduli o altri segreti, né i percorsi dei programmi
  (ffmpeg, antivirus) e l'indirizzo degli aggiornamenti. L'importazione (JSON di DownloadManager o un testo qualsiasi con
  indirizzi) mostra prima un'anteprima: tipo, nome, già presente, non valido; si sceglie cosa importare e cosa fare dei
  duplicati (salta, sostituisci, mantieni entrambi); l'anteprima mostra anche le cartelle di categorie e progetti. Il file
  importato non è fidato: i download entrano in pausa nella cartella della loro categoria (mai in una cartella indicata dal file
  né come già completati), code e progetti senza pianificazione né azione di fine coda, e la password del proxy resta solo se
  il proxy è lo stesso.
- **Primo avvio** (solo se il database non esiste): cartella dei download (predefinita **Download**, mai il Desktop), cartelle
  delle categorie, istruzioni per ogni browser trovato con **Verifica connessione**, avvio con Windows e tema.
- **Download sul Desktop** (installazioni precedenti): se la cartella dei download o quelle delle categorie sono sul Desktop,
  all'avvio viene proposto **una sola volta** di spostarle in `Download\DownloadManager`. Nulla cambia senza conferma, e i file
  già scaricati vengono spostati solo dopo una seconda domanda separata.
- **Errori imprevisti**: una finestra con *Dettagli tecnici* e **Copia dettagli** (senza dati riservati); tutto finisce anche
  nel log.
- **Pannello laterale**: Tutti / Non completati / Completati, ciascuno suddiviso per categoria, con il numero di download.
- **Elenco**: colonne mostrabili/nascondibili, riordinabili e ridimensionabili (tasto destro sull'intestazione), ordinamento
  con clic sull'intestazione, "Ordine della coda" per tornare all'ordine di coda. Tutto viene salvato tra una sessione e l'altra.
- **Ricerca** (Ctrl+F): filtra per nome o indirizzo; più parole devono comparire tutte. Esc la cancella.
- **Tema**: chiaro, scuro o "segui il sistema" (Impostazioni). I colori del testo hanno contrasto almeno 4,5:1 (WCAG AA).
- **Drag & drop**: trascina uno o più link nella finestra; trascina i file completati verso Esplora file o altri programmi.
- **Aggiungi** (Ctrl+N): se negli appunti c'è un indirizzo http/https valido, il campo è già compilato. Ctrl+V nell'elenco aggiunge i link copiati.
- **Avanzamento** (Invio o doppio clic): mappa del file con le parti scaricate e la posizione di ogni connessione attiva,
  elenco dei segmenti, velocità, tempo rimasto, pausa/ripresa.
- **Proprietà** (Alt+Invio): dettagli, categoria e **Aggiorna indirizzo** per un link scaduto. L'app interroga il nuovo indirizzo:
  stessa dimensione e stesso ETag (o data di ultima modifica) → i dati già scaricati vengono mantenuti; file diverso → si
  ricomincia da zero dopo conferma; stessa dimensione ma nessun dato per verificare → decidi tu.
- **Notifiche di Windows** a fine download con i pulsanti **Apri** e **Apri cartella** (disattivabili in Impostazioni).
  I pulsanti usano link `downloadmanager:` firmati con una chiave dell'utente, quindi una pagina web non può usarli per aprire file.
- **Aggiungi / conferma dal browser**: scelta di categoria e coda; *Scarica ora* avvia subito, *Aggiungi alla coda* aspetta il turno.
- **Tastiera**: Ctrl+G grabber, Ctrl+N aggiungi, Ctrl+V incolla link, Ctrl+F cerca, Canc rimuovi, Invio avanzamento, Alt+Invio proprietà.
  Tutti i controlli hanno un nome leggibile dagli screen reader.

## Dati

`%LOCALAPPDATA%\DownloadManager\downloads.db` (SQLite). Lo schema ha un numero di versione (`PRAGMA user_version`, ora 7):
all'avvio i database delle versioni precedenti vengono aggiornati un passo alla volta in un'unica transazione, dopo averne
salvato una copia accanto (`downloads.db.v<versione>.bak`). Un database creato da una versione più recente non viene modificato.
Passando alla 3, tutti i download entrano nella Coda principale, che eredita il vecchio limite e lo stato avviata/fermata;
il nuovo limite totale parte da 10. La 4 aggiunge le tabelle delle credenziali (password cifrate) e dei certificati accettati.
La 5 aggiunge ai download la colonna `media` (tipo di stream e qualità scelta) per i video HLS/DASH.
La 6 aggiunge `grabber_projects` (impostazioni in JSON, pianificazione, cookie cifrati con DPAPI, ultima esecuzione) e
`grabber_files` (per progetto e indirizzo: percorso locale, ETag, data, dimensione, hash delle pagine, prima e ultima volta visto,
rimozione dal sito). Nella colonna `media` compaiono anche lingua audio e sottotitoli scelti.
La 7 aggiunge `downloads.post_check` (hash atteso e calcolato, esito dell'antivirus) e, in `grabber_files`, il download a cui il
file è stato mandato e se è stato davvero ottenuto (i file già presenti contano come ottenuti).

**Log**: `%LOCALAPPDATA%\DownloadManager\logs\app.log`, al massimo 1 MB per file e 5 file (`app.1.log` … `app.4.log`). Gli
indirizzi vi compaiono senza parametri né credenziali, e password, cookie e token vengono mascherati.

**Errori del disco**: disco pieno, cartella non scrivibile e percorso troppo lungo fermano il download con un messaggio
chiaro invece di riprovare all'infinito; i nomi dei file oltre 200 caratteri vengono accorciati mantenendo l'estensione.

Pubblicazione: eseguibile singolo self-contained non compresso (circa 165 MB). Il trimming non è possibile con WPF (l'SDK lo
rifiuta, NETSDK1168); ReadyToRun è stato misurato (finestra in 604 ms invece di 675, ma +66 MB su disco e +10 MB di RAM) e
non è usato.

## Aggiornamenti

Disattivati per impostazione. In Impostazioni › *Aggiornamenti* si indica l'indirizzo: le release di GitHub
(`https://api.github.com/repos/UTENTE/REPOSITORY/releases/latest`, con l'eseguibile tra gli allegati e il suo SHA-256 in un
allegato **`DownloadManager.exe.sha256`** — l'hash nei formati comuni: solo l'hash (anche scritto da PowerShell in UTF-16),
`hash  nome` di sha256sum, `SHA256 (nome) = hash`, l'output di certutil; se nomina un altro programma viene rifiutato. Un hash
nelle note o in un file `SHA256SUMS` non basta: senza quell'allegato una versione più recente non viene installata e l'app lo
dice, anche dopo il controllo automatico all'avvio, una sola volta per release) oppure un manifest JSON
`{ "version": "1.1.0", "notes": "…", "url": "https://…/DownloadManager.exe", "sha256": "…" }`. Solo indirizzi https.
Con il controllo all'avvio attivo, una versione più recente mostra le note; nulla viene scaricato senza **Scarica e installa**.
Il file scaricato deve corrispondere allo SHA-256 dichiarato ed essere un programma Windows, altrimenti viene eliminato.
Poi l'eseguibile in uso diventa `DownloadManager.exe.old`, il nuovo prende il suo posto e la versione precedente, avviata come
"guardiano", avvia la nuova e aspetta che segnali di essere partita: se si chiude o non risponde entro 90 secondi, la nuova
viene messa da parte come `.failed`, la precedente torna al suo posto e si riavvia con un avviso. **Ripristina la versione
precedente** fa lo stesso scambio a mano. Funziona solo con l'eseguibile pubblicato (un file unico).

## Prestazioni

Misure sull'eseguibile pubblicato, con 2000 download nell'elenco: finestra pronta in circa 4 s; righe virtualizzate (13
create per la parte visibile, riciclate scorrendo); 40 pagine di scorrimento in 0,6 s, otto salti attraverso l'elenco in
0,15 s, due ordinamenti per dimensione in 0,2 s, ricerca filtrata subito dopo la pausa di digitazione. Memoria nell'area di
notifica dopo 30 s: 66 MB di working set e 81 MB privati con 2000 download (163/97 MB prima della 1.0), 24/43 MB con
l'elenco vuoto: alcuni secondi dopo che la finestra si nasconde l'app restituisce a Windows la memoria della finestra.
Nessuna perdita di memoria: un test scarica e rimuove 100 download per volta e verifica che la memoria non cresca di giro in
giro (misurato: circa 30 KB per 100 download).

## Integrazione con il browser

### Installazione

In ogni caso, prima avvia DownloadManager almeno una volta: registra da sé, per l'utente corrente, l'host di native messaging
per Chrome, Edge, Chromium, Brave (Opera usa la registrazione di Chrome) e Firefox
(`HKCU\Software\<browser>\NativeMessagingHosts\com.downloadmanager.host` e `HKCU\Software\Mozilla\NativeMessagingHosts\...`).

- **Edge** — `edge://extensions` → attiva **Modalità sviluppatore** → **Carica decompressa** → cartella `extension`
  (oppure `dist\chromium`).
- **Chrome / Brave / Chromium** — `chrome://extensions` → **Modalità sviluppatore** → **Carica estensione non pacchettizzata**
  → cartella `extension`.
- **Opera** — `opera://extensions` → **Modalità sviluppatore** → **Carica estensione decompressa** → cartella `extension`.
  Se l'estensione era già caricata, premi **Aggiorna** sulla sua scheda.
- **Firefox** (128 o successivo) — esegui `node tools/build-extension.mjs`, poi `about:debugging#/runtime/this-firefox` →
  **Carica componente aggiuntivo temporaneo** → `dist\firefox\manifest.json`. Firefox in versione normale rimuove i componenti
  temporanei alla chiusura; per un'installazione permanente il pacchetto va firmato (vedi sotto) oppure si usa Firefox
  Developer Edition/Nightly con `xpinstall.signatures.required = false`.

### Firefox permanente: firma "non elencata" su addons.mozilla.org

La firma "unlisted" (distribuzione autonoma) rende l'estensione installabile in modo permanente in qualsiasi Firefox, senza
pubblicarla nel catalogo. Si fa una volta per ogni nuova versione:

1. Crea (o usa) un account Firefox su <https://addons.mozilla.org> e accedi.
2. Apri <https://addons.mozilla.org/developers/addon/api/key/> (Developer Hub › *Strumenti* › *Gestisci le chiavi API*),
   accetta l'accordo per gli sviluppatori se richiesto e premi **Genera nuove credenziali**.
3. Copia **JWT issuer** (`user:…`) e **JWT secret**. Non salvarli nel progetto: valgono come una password.
4. In PowerShell, nella cartella del progetto:
   ```powershell
   $env:AMO_JWT_ISSUER = "user:12345:67"
   $env:AMO_JWT_SECRET = "<il segreto>"
   .\tools\sign-firefox.ps1 -Check   # verifica senza caricare nulla
   .\tools\sign-firefox.ps1          # costruisce dist\firefox, lo carica come "unlisted" e attende la firma
   ```
   Lo script usa `web-ext` 10.7.0 tramite `npx` (nulla viene installato nel progetto) e passa le chiavi solo come variabili
   d'ambiente del processo, mai sulla riga di comando. Il pacchetto firmato arriva in `dist\signed\*.xpi`.
5. Firefox › `about:addons` › ingranaggio › **Installa componente aggiuntivo da file…** › il file `.xpi` firmato.
6. Alla versione successiva aumenta `version` in `extension\manifest.json` (AMO non firma due volte la stessa versione) e
   ripeti il passo 4. Le chiavi si possono revocare dalla stessa pagina di AMO.

L'ID (`downloadmanager@extension.local`) resta lo stesso, quindi l'host nativo già registrato funziona anche con l'estensione
firmata.

L'ID dell'estensione è fisso (chiave nel manifest per Chromium, `downloadmanager@extension.local` per Firefox): l'host accetta
solo quello.

### Verifica connessione

La pagina **Verifica connessione** (link nel popup, oppure *Opzioni* dell'estensione) controlla l'host nativo e la versione del
protocollo. Se l'host manca, rifiuta l'estensione o l'app è una versione precedente, mostra cosa fare e un pulsante
**Registra**: apre il link `downloadmanager:register`, che (dopo la conferma del browser) avvia DownloadManager e registra di nuovo
l'host per tutti i browser. Se l'app non è mai stata avviata, il link non è ancora registrato: basta avviarla una volta.

### Modalità (dal popup)

- **Intercetta automaticamente** (icona colorata): i download avviati dal browser che rispettano le regole passano all'app.
- **Solo menu contestuale** (icona grigia): il browser scarica come sempre; si usa il tasto destro.

Regole (popup › Regole, e "Su questo sito"): tipi di file, dimensione minima (predefinita 1 MB; se la dimensione non è nota il
download viene intercettato), siti da intercettare **sempre** (qualsiasi tipo e dimensione) o **mai** (vincono sui "sempre"),
anche per la pagina da cui parte il download. **Alt+clic** su un link: quel download resta al browser, una volta sola.
Le finestre in incognito non vengono mai intercettate e i loro cookie non vengono mai inviati.

Come avviene l'intercettazione:
- **Chromium**: `downloads.onDeterminingFilename` dà l'indirizzo finale dopo i redirect, il nome, la dimensione e il tipo.
  Il download resta in attesa (il nome non viene ancora confermato) mentre l'app viene interpellata; solo quando l'app lo ha
  accettato viene annullato e cancellato dall'elenco del browser. Rispetto a "annulla, poi invia" l'ordine è invertito di
  proposito: se l'app non risponde entro 15 s (o rifiuta), il download **prosegue nel browser senza ricominciare**, quindi
  nulla va perso e niente viene scaricato due volte; compare una notifica e un "!" sull'icona.
- **Firefox** non ha `onDeterminingFilename`: si usa `downloads.onCreated`. Il download viene messo in pausa subito, poi, se
  l'app lo accetta, annullato, il file parziale rimosso (`removeFile`) e cancellato dall'elenco (`erase`); se l'app non risponde
  viene ripreso (`resume`), e se il server non consente la ripresa il browser lo riscarica da capo. In Firefox la dimensione e il
  tipo possono non essere ancora noti alla creazione: in quel caso decidono il tipo di file e i siti.
- Firefox non scarica con Alt+clic come Chromium: lì l'estensione segna il link e lo apre lei, così il download resta al browser.
- In Firefox l'evento del download può arrivare prima di quello del modulo che lo ha avviato: se il modulo non è ancora noto,
  l'estensione lo attende fino a mezzo secondo (con il download in pausa) prima di decidere tra GET e POST.

### Moduli (POST)

I download avviati inviando un modulo vengono riconosciuti (`webRequest` vede il modulo inviato dalla pagina) e passati all'app
con metodo, campi, cookie e pagina di provenienza; l'app rinvia il modulo nello stesso formato (urlencoded o multipart).
Il modulo è inviato una sola volta per tentativo: la risposta della verifica iniziale è già il download. Un download da modulo
non si può riprendere a metà: se si interrompe, il modulo viene rinviato e il file riparte da capo. I moduli che caricano file
(o con corpi non rappresentabili come campi) restano al browser. I campi sono cifrati con DPAPI insieme ai cookie e cancellati
a download completato.

### Video (pulsante "Scarica questo video")

L'estensione nota le risorse video e audio caricate dalla pagina: manifest HLS (`.m3u8`, `application/vnd.apple.mpegurl`) e DASH
(`.mpd`, `application/dash+xml`) e file `video/*` / `audio/*` caricati da un elemento `<video>`/`<audio>` (non i segmenti che i
player scaricano a pezzi). Compare allora un pulsante discreto in basso a destra, trascinabile (la posizione viene ricordata),
con l'elenco delle qualità e delle dimensioni stimate (bitrate × durata, o la dimensione esatta per i file). Si può nascondere su
un sito (dal pannello o dal popup) o disattivare del tutto.

Nell'app un video è un download come gli altri (code, limiti di velocità, quote, proxy, cookie): i segmenti vengono scaricati in
parallelo in una cartella accanto al file finale e conservati uno per uno, quindi pausa e ripresa ripartono dal primo segmento
mancante. Al termine i segmenti vengono uniti (TS e MP4 frammentato sono fatti per essere concatenati) e, se c'è **ffmpeg**
(Impostazioni › Generale, oppure nel PATH; non è incluso nell'app), video e audio vengono copiati senza ricodifica in un unico
`.mp4`. Senza ffmpeg il video viene salvato in `.ts` o in due file (`.video.mp4` / `.audio.m4a`) e il download lo dice
("Completato (vedi nota)").

Non vengono scaricati, e il pannello lo spiega:
- contenuti **protetti**: pagine che usano Encrypted Media Extensions (DRM), DASH con `ContentProtection`, HLS con
  `EXT-X-KEY` / `EXT-X-SESSION-KEY` di qualsiasi tipo (anche AES-128 con chiave raggiungibile: è comunque una protezione);
- siti i cui termini di servizio lo vietano (YouTube, Netflix, Prime Video, Disney+, Twitch, Spotify, TikTok, Instagram,
  Facebook e altri; elenco in `MediaPolicy`/`rules.js`);
- dirette (HLS senza `EXT-X-ENDLIST`, DASH `dynamic`).
I manifest DASH sono letti con DTD disattivate (nessuna entità esterna). L'app interroga solo indirizzi che la scheda ha
davvero caricato.

### Lingue audio e sottotitoli

Se uno stream offre più lingue audio (HLS `EXT-X-MEDIA TYPE=AUDIO`, DASH un insieme di adattamento per lingua, con la sua
etichetta) o sottotitoli WebVTT (HLS `TYPE=SUBTITLES`, DASH `text/vtt`), il pannello del video mostra un elenco **Audio** e le
caselle **Sottotitoli** con il formato: file `.vtt` o `.srt` accanto al video (`Nome.it.vtt`), oppure **dentro il video** con
ffmpeg (MP4: `mov_text`, MKV: SubRip; lingua in ISO 639-2). I segmenti WebVTT vengono uniti in un solo file (i sottotitoli
ripetuti a cavallo di due segmenti restano uno); senza ffmpeg i sottotitoli richiesti "dentro" vengono salvati accanto e il
download lo dice. I DASH con **più periodi** vengono scaricati per intero: per ogni rappresentazione del primo periodo si
prende quella corrispondente dei successivi (stesso id, altrimenti stessa lingua e qualità più vicina), con il segmento di
inizializzazione del periodo quando cambia.

### Scarica tutto

Tasto destro sulla pagina → **Scarica tutto con DownloadManager…**: l'app apre una finestra con tutti i link e le immagini della
pagina (o del frame), una volta sola ciascuno, già spuntati quelli che sembrano file (non le pagine web). Filtri per testo
(nome, testo del link, indirizzo), tipo di file e link/immagini; selezione multipla (Barra spaziatrice sulle righe evidenziate,
"Seleziona/Deseleziona visibili"); categoria (automatica per tipo o una scelta), coda e cartella. "Scarica ora" avvia la coda,
così i file rispettano comunque il limite di download contemporanei.

### Permessi dell'estensione

| Permesso | Perché |
|---|---|
| `nativeMessaging` | parlare con l'app |
| `downloads` | vedere i download del browser per intercettarli, annullarli, metterli in pausa o lasciarli proseguire |
| `contextMenus` | le voci del tasto destro |
| `cookies` | passare all'app i cookie del sito, perché i download con login funzionino |
| `storage` | impostazioni (`local`) e stato del service worker non persistente (`session`) |
| `notifications` | avvisare quando un download torna al browser |
| `webRequest` | riconoscere video/manifest dalle intestazioni delle risposte e i moduli inviati (solo lettura, nessun blocco) |
| `scripting` | leggere link e immagini della pagina per "Scarica tutto", solo quando lo si chiede |
| host `<all_urls>` | tutto quanto sopra deve funzionare su qualsiasi sito; lo script di contenuto solo su http/https |

Non usa `tabs`, `webRequestBlocking`, `declarativeNetRequest` né `history`. Il service worker (o la pagina di eventi in
Firefox) non resta attivo: si sveglia solo per gli eventi del browser, non fa polling, e salva ciò che deve ricordare in
`chrome.storage.session`. L'estensione è localizzata in italiano e inglese (`_locales`).

### Protocollo

Versione esplicita: la 0.7 parla il **protocollo 3** (`version: 3` in ogni messaggio; `hello` risponde con `protocol` e
`appVersion` senza avviare l'app). Il 3 aggiunge `grab` (indirizzo, titolo, user-agent e cookie della pagina) e, in `probeMedia`
/ `addMedia`, `audioTracks`/`subtitles` e `audioId`/`subtitleIds`/`subtitleMode`; un'estensione 0.6 (protocollo 2) continua a
funzionare invariata, e con un'app 0.6 l'estensione 0.7 dice che per "Cattura questo sito" serve aggiornare l'app. Restano validi, invariati, i messaggi del protocollo 1 dell'estensione 0.5 (`ping`, `queues`,
`add` senza versione); un'app 0.5 risponde a `hello` con un errore e la pagina di verifica la indica come "da aggiornare".
Nuovi messaggi: `hello`, `probeMedia`, `addMedia`, `batch`, e `add` con `method`/`body`/`bodyType`.
Ogni messaggio viene validato due volte (nell'host, prima di avviare l'app, e nella pipe dell'app, raggiungibile da qualsiasi
programma dell'utente): tipi noti, solo indirizzi http/https/ftp/ftps/ftpes, referrer solo http/https, niente caratteri di
controllo, limiti a lunghezze, cookie (300), campi del modulo (1 MB) e link (10.000).

### Riferimenti

Le modalità di integrazione sono ispirate, come idea, a "Download with…" di webextension.org (licenza MPL 2.0). Nessuna parte
del suo codice è stata copiata o riusata: l'estensione è scritta da zero.

## Comandi

```powershell
$dn = "C:\Program Files\dotnet\dotnet.exe"
& $dn test                                    # test (anche quelli dell'estensione: serve Node.js)
node --test "tests/extension/*.test.mjs"      # solo i test dell'estensione
node tools/build-extension.mjs                # estensione per Chromium e Firefox in dist\
& $dn run --project src\DownloadManager.App   # avvio in debug
& $dn publish src\DownloadManager.App -c Release -r win-x64 -o publish   # exe self-contained
.\tools\sign-firefox.ps1 -Check                                          # firma dell'estensione per Firefox (vedi sopra)
powershell -File tools\trials\grab-trial.ps1 -Mode Mirror                 # prova reale del grabber (dopo prepare.ps1)
```

`DOWNLOADMANAGER_DATA=<cartella>` avvia l'app su un'altra cartella dati (utile per le prove).
`DOWNLOADMANAGER_SIMULATE_POWER=1` sostituisce sospensione, ibernazione e spegnimento con una riga in `simulated-power.log`
nella cartella dati, per provare le azioni di fine coda senza spegnere il PC.
Con `DOWNLOADMANAGER_DATA` l'app è **isolata**: registro tenuto in memoria (avvio con Windows, browser e notifiche non vengono
toccati), pipe e istanza singola separate, quindi può girare accanto all'app vera. Nei test il registro reale è proprio vietato:
qualsiasi accesso fa fallire il test.

I testi dell'interfaccia sono in `src/DownloadManager.Core/Resources/Strings.resx`; per tradurre l'app basta aggiungere
un file `Strings.<lingua>.resx`.

## Limiti noti

- Protocolli: niente SFTP (SSH) né FTP tramite proxy HTTPS.
- Grabber: niente JavaScript (i contenuti generati da script non vengono trovati); un link che sembra una pagina
  (`download.php?id=7`) viene riconosciuto come file solo se rientra nella profondità; le opzioni "proxy" e "credenziali" del
  progetto valgono per la scansione, mentre i file mandati a una coda usano la rete dell'app.
- Video: sottotitoli solo WebVTT (non TTML né sottotitoli dentro i segmenti MP4); nell'unione dei segmenti HLS si assume
  l'allineamento standard di `X-TIMESTAMP-MAP`. DASH multi-periodo: i sottotitoli dei periodi successivi vengono spostati
  all'inizio del loro periodo; video e audio vengono uniti così come sono (se un periodo riparte da zero, il file unito può avere
  un salto). I sottotitoli che non si riescono a scaricare non fanno fallire il video: il download lo segnala nella nota.
- L'app è solo in italiano (l'estensione in italiano e inglese); per tradurla basta un `Strings.<lingua>.resx`.
- Aggiornamenti automatici solo per l'eseguibile pubblicato in un unico file; l'aspetto della barra degli strumenti non fa
  parte dell'esportazione delle impostazioni.
- L'estensione per Firefox va firmata dall'utente con le proprie chiavi AMO (`tools\sign-firefox.ps1`).

## Risoluzione dei problemi

- **L'estensione dice che l'app non risponde / host mancante**: avvia DownloadManager una volta (registra l'host per l'utente
  corrente), poi apri **Verifica connessione** dal popup dell'estensione; se l'eseguibile è stato spostato, il pulsante
  *Registra* della pagina lo ripunta. In Opera l'host è quello di Chrome.
- **I download partono ma si fermano con "disco pieno" o "cartella non scrivibile"**: libera spazio o scegli un'altra cartella
  (Impostazioni o Proprietà), poi **Riprendi**: i dati già scaricati restano.
- **L'antivirus segnala "non riuscito"**: controlla in Impostazioni › *Dopo il download* il programma (percorso completo, `.exe`),
  i parametri (`{file}` è il file) e i codici di uscita; con un antivirus diverso da Defender consulta la sua documentazione
  della riga di comando.
- **Un aggiornamento non parte**: l'app torna da sola alla versione precedente e lo segnala; la versione scartata resta accanto
  come `DownloadManager.exe.failed`. Il dettaglio è nel log.
- **Qualcosa non va**: `%LOCALAPPDATA%\DownloadManager\logs\app.log`; nelle finestre d'errore **Copia dettagli** copia
  versione, sistema ed eccezione senza dati riservati.
- **Database**: prima di ogni aggiornamento di schema ne resta una copia `downloads.db.v<N>.bak` accanto all'originale.
