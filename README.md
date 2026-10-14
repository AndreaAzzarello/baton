# DownloadManager

Download manager per Windows (C# / .NET 10 LTS / WPF) con download segmentato, pausa/ripresa persistente,
code con nome e pianificazione, categorie, FTP/FTPS, proxy, autenticazione e integrazione con il browser. Versione 0.5.0.

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/DownloadManager.Core` | Motore: segmenti, connessioni, checkpoint, code e scheduler, categorie, SQLite; `Net` (trasporti HTTP e FTP, proxy, PAC), `Security` (credenziali, certificati, segreti DPAPI), `Platform` (registro iniettabile e registrazioni di sistema); testi in `Resources/Strings.resx` |
| `src/DownloadManager.App` | Interfaccia WPF in MVVM: `ViewModels`, `Views`, `Services` (finestre, tema, notifiche, area di notifica, avvio con Windows, risparmio energia), `Themes`, `Controls` |
| `tests/DownloadManager.Tests` | Test xUnit con server locali avviati dai test: HTTP (Range, ETag, 429, cookie, Basic/Digest), FTP/FTPS, proxy HTTP e SOCKS5 |
| `extension` | Estensione per Chrome/Edge/Opera/Brave (Manifest V3) |
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

## Interfaccia

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
- **Tastiera**: Ctrl+N aggiungi, Ctrl+V incolla link, Ctrl+F cerca, Canc rimuovi, Invio avanzamento, Alt+Invio proprietà.
  Tutti i controlli hanno un nome leggibile dagli screen reader.

## Dati

`%LOCALAPPDATA%\DownloadManager\downloads.db` (SQLite). Lo schema ha un numero di versione (`PRAGMA user_version`, ora 4):
all'avvio i database delle versioni precedenti vengono aggiornati un passo alla volta in un'unica transazione, dopo averne
salvato una copia accanto (`downloads.db.v<versione>.bak`). Un database creato da una versione più recente non viene modificato.
Passando alla 3, tutti i download entrano nella Coda principale, che eredita il vecchio limite e lo stato avviata/fermata;
il nuovo limite totale parte da 10. La 4 aggiunge le tabelle delle credenziali (password cifrate) e dei certificati accettati.

Pubblicazione: eseguibile singolo self-contained non compresso (circa 164 MB). Il trimming non è possibile con WPF (l'SDK lo
rifiuta, NETSDK1168); ReadyToRun è stato misurato (finestra in 604 ms invece di 675, ma +66 MB su disco e +10 MB di RAM) e
non è usato.

## Integrazione con il browser (Chrome, Edge, Opera, Brave, Chromium)

1. Avvia DownloadManager almeno una volta: registra da solo l'host di native messaging
   (`HKCU\Software\<browser>\NativeMessagingHosts\com.downloadmanager.host`).
2. Apri `edge://extensions`, `chrome://extensions` oppure `opera://extensions`, attiva **Modalità sviluppatore**,
   scegli **Carica estensione decompressa** e seleziona la cartella `extension`.

Cosa fa:
- **Tasto destro → Scarica con DownloadManager** su link, immagini, video e audio.
- **Intercettazione**: i download del browser con le estensioni scelte nel popup vengono passati all'app.
  Il popup mostra se l'app è collegata e permette di disattivare l'intercettazione.
- L'app riceve URL, pagina di provenienza, user agent e cookie della sessione, così funzionano anche i siti con login.
  I cookie sono cifrati con DPAPI nel database e cancellati a download completato.
  Le finestre in incognito non vengono intercettate e i loro cookie non vengono mai inviati.
- Il download arriva come una piccola finestra di conferma in primo piano; la finestra principale resta dov'è.
  Se l'app è chiusa, il browser la avvia ridotta a icona; se non risponde, il file viene scaricato dal browser.
- Se è attiva anche l'estensione di un altro download manager, entrambe intercettano lo stesso download: tienine attiva una sola.
- In Impostazioni si può scegliere se confermare ogni download in arrivo dal browser o avviarlo subito.

Protocollo: il messaggio `add` accetta dalla 0.4 un campo facoltativo `queue` (nome della coda); le estensioni che non lo
inviano usano la Coda principale. Dalla 0.5 il messaggio `queues` restituisce i nomi delle code: il popup dell'estensione li
mostra per scegliere la coda dei download intercettati (senza mai avviare l'app solo per chiederli). Il menu *Scarica con
DownloadManager* accetta anche link ftp, ftps e ftpes.

Come funziona: l'estensione avvia `DownloadManager.exe` come host di native messaging (stesso eseguibile,
senza finestra), che inoltra il messaggio all'app tramite una named pipe accessibile solo all'utente corrente.
È attiva una sola istanza dell'app: un secondo avvio porta in primo piano la finestra esistente.

Limiti: i download che il sito avvia con un POST (moduli) vengono ripetuti come GET e potrebbero non funzionare.
Firefox non è ancora supportato.

## Comandi

```powershell
$dn = "C:\Program Files\dotnet\dotnet.exe"
& $dn test                                    # test
& $dn run --project src\DownloadManager.App   # avvio in debug
& $dn publish src\DownloadManager.App -c Release -r win-x64 -o publish   # exe self-contained
```

`DOWNLOADMANAGER_DATA=<cartella>` avvia l'app su un'altra cartella dati (utile per le prove).
`DOWNLOADMANAGER_SIMULATE_POWER=1` sostituisce sospensione, ibernazione e spegnimento con una riga in `simulated-power.log`
nella cartella dati, per provare le azioni di fine coda senza spegnere il PC.
Con `DOWNLOADMANAGER_DATA` l'app è **isolata**: registro tenuto in memoria (avvio con Windows, browser e notifiche non vengono
toccati), pipe e istanza singola separate, quindi può girare accanto all'app vera. Nei test il registro reale è proprio vietato:
qualsiasi accesso fa fallire il test.

I testi dell'interfaccia sono in `src/DownloadManager.Core/Resources/Strings.resx`; per tradurre l'app basta aggiungere
un file `Strings.<lingua>.resx`.

## Non ancora presente

Firefox, FTP, grabber video (il link di una pagina YouTube scarica la pagina HTML, non il video),
sincronizzazione dei siti, SFTP (SSH), FTP tramite proxy HTTPS.
