# DownloadManager

Download manager per Windows (C# / .NET 10 LTS / WPF) con download segmentato,
pausa/ripresa persistente e coda.

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/DownloadManager.Core` | Motore: probe HTTP, pianificazione segmenti, connessioni, checkpoint, coda, SQLite |
| `src/DownloadManager.App` | Interfaccia WPF |
| `tests/DownloadManager.Tests` | Test xUnit con un server HTTP locale (Range, ETag, throttling, errori, 429, cookie) |
| `extension` | Estensione per Chrome/Edge (Manifest V3) |
| `publish/DownloadManager.exe` | Build Release self-contained win-x64 (non richiede il runtime .NET) |

## Come funziona

- **Probe**: `GET` con `Range: bytes=0-0`. Una risposta 206 con lunghezza totale indica che il server supporta i range.
- **Segmentazione**: il file viene diviso in N range (minimo 1 MB ciascuno) scritti in un unico file `.part` preallocato e sparse.
- **Segmentazione dinamica**: una connessione che finisce il suo range prende la metà non scaricata del range più grande ancora attivo.
- **Pausa/ripresa**: ogni secondo i segmenti vengono salvati in SQLite, dopo il flush su disco.
  Alla ripresa, `If-Range` con ETag/Last-Modified garantisce che il file sul server non sia cambiato; se è cambiato, il download riparte da zero.
- **Server che limitano le connessioni** (429/503): le connessioni in eccesso si ritirano e le altre completano il loro range.
- **Coda**: "Avvia" parte subito; "In coda" attende il proprio turno, con un limite di download simultanei configurabile.
- **Limite di velocità**: token bucket globale condiviso da tutte le connessioni.

Dati dell'app: `%LOCALAPPDATA%\DownloadManager\downloads.db`.

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

## Fuori dall'MVP

Firefox, FTP, grabber video (il link di una pagina YouTube scarica la pagina HTML, non il video),
pianificazione oraria, più code nominate.
