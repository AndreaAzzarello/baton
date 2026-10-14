# DownloadManager

Download manager per Windows (C# / .NET 10 LTS / WPF) con download segmentato, pausa/ripresa persistente,
coda, categorie e integrazione con il browser.

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/DownloadManager.Core` | Motore: probe HTTP, segmenti, connessioni, checkpoint, coda, categorie, SQLite, testi dell'interfaccia (`Resources/Strings.resx`) |
| `src/DownloadManager.App` | Interfaccia WPF in MVVM: `ViewModels`, `Views`, `Services` (finestre, tema, notifiche, shell), `Themes`, `Controls` |
| `tests/DownloadManager.Tests` | Test xUnit con un server HTTP locale (Range, ETag, Last-Modified, throttling, errori, 429, cookie) |
| `extension` | Estensione per Chrome/Edge/Opera/Brave (Manifest V3) |
| `publish/DownloadManager.exe` | Build Release self-contained win-x64 (non richiede il runtime .NET) |

## Come funziona il download

- **Probe**: `GET` con `Range: bytes=0-0`. Una risposta 206 con lunghezza totale indica che il server supporta i range.
  Se il server accetta la connessione ma non risponde entro 30 s, la richiesta viene ritentata invece di restare bloccata.
- **Segmentazione**: il file viene diviso in N range (minimo 1 MB ciascuno) scritti in un unico file `.part` preallocato e sparse.
- **Segmentazione dinamica**: una connessione che finisce il suo range prende la metà non scaricata del range più grande ancora attivo.
- **Pausa/ripresa**: ogni secondo i segmenti vengono salvati in SQLite, dopo il flush su disco.
  Alla ripresa, `If-Range` con ETag/Last-Modified garantisce che il file sul server non sia cambiato; se è cambiato, il download riparte da zero.
- **Server che limitano le connessioni** (429/503): le connessioni in eccesso si ritirano e le altre completano il loro range.
- **Coda**: "Avvia" parte subito; "In coda" attende il proprio turno, con un limite di download simultanei configurabile.
- **Limite di velocità**: token bucket globale condiviso da tutte le connessioni.
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
- **Tastiera**: Ctrl+N aggiungi, Ctrl+V incolla link, Ctrl+F cerca, Canc rimuovi, Invio avanzamento, Alt+Invio proprietà.
  Tutti i controlli hanno un nome leggibile dagli screen reader.

## Dati

`%LOCALAPPDATA%\DownloadManager\downloads.db` (SQLite). Lo schema ha un numero di versione (`PRAGMA user_version`, ora 2):
all'avvio i database delle versioni precedenti vengono aggiornati un passo alla volta in un'unica transazione, dopo averne
salvato una copia accanto (`downloads.db.v0.bak`). Un database creato da una versione più recente non viene modificato.

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

`DOWNLOADMANAGER_DATA=<cartella>` avvia l'app su un'altra cartella dati (utile per le prove). Attenzione: anche
l'integrazione con il browser e le notifiche vengono registrate su quell'istanza finché non riavvii quella normale.

I testi dell'interfaccia sono in `src/DownloadManager.Core/Resources/Strings.resx`; per tradurre l'app basta aggiungere
un file `Strings.<lingua>.resx`.

## Non ancora presente

Firefox, FTP, grabber video (il link di una pagina YouTube scarica la pagina HTML, non il video),
pianificazione oraria, più code nominate.
