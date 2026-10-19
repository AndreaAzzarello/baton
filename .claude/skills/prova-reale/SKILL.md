---
name: prova-reale
description: Prove reali di DownloadManager con l'app isolata e i browser veri (Edge, Opera, Firefox headless) sul sito di prova locale e su uno stream HLS pubblico, senza toccare i dati reali, il registro reale o il Desktop. Usala quando l'utente chiede prove reali, prove nei browser o la verifica dell'estensione, o prima di chiudere una fase.
---

# Prova reale

Obiettivo: verificare l'app pubblicata e l'estensione come le userebbe l'utente, con tutto isolato in `%TEMP%\dm-trials`
(o `DM_TRIALS`). Regole (vedi CLAUDE.md): niente input simulato sul desktop, solo UI Automation e browser headless.

## 1. Preparazione
1. Serve `publish\DownloadManager.exe` aggiornato. Se l'app è cambiata, prima ripubblica (vedi skill `fine-fase`, passi
   6–7) oppure chiedi all'utente: la ripubblicazione chiude la sua app.
2. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\trials\prepare.ps1` — opzioni: `-Ffmpeg <percorso>` per provare
   l'unione in MP4, `-SkipFirefox` se non serve. Crea dati isolati con download in `%TEMP%\dm-trials\downloads`, build
   dell'estensione, copia di prova per Firefox, Firefox portabile, dipendenze npm e sito locale (porta 8765).
3. Per un DB già esistente che si vuole riusare: `python tools\trials\prep_data.py <db> <cartella download> [ffmpeg]` —
   riporta cartella e categorie fuori dal Desktop.

## 2. Prove
Esegui da `tools\trials` (ogni riga stampa `OK`/`FAIL` con i dettagli; screenshot in `%TEMP%\dm-trials\shots`):
- `node chromium-trial.mjs edge "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"`
- `node chromium-trial.mjs opera "C:\Program Files\Opera\<versione>\opera.exe"` (l'eseguibile interno, non il launcher)
- `node firefox-trial.mjs`
Coprono: verifica connessione, popup, intercettazione, Alt+clic, modulo POST, pulsante video con qualità e invio, DRM
(Clear Key); `chromium-trial.mjs ... --batch` invia anche "Scarica tutto".

"Scarica tutto" apre una finestra nell'app isolata: controllala con `tools\trials\ui.ps1 -Window "Scarica tutto" -Action dump`
(azioni `set`, `invoke`, `select`) e `shot.ps1 -Title "Scarica tutto" -Out <png> -NoFront`, poi chiudila con `-Action invoke -Name Annulla` (gli script dei browser la chiudono da soli a fine prova: finché è aperta,
le richieste successive dei browser restano in attesa nell'app e la prova seguente sembra fallire).

Stream HLS pubblico senza browser, attraverso l'host nativo come farebbe l'estensione:
`python tools\trials\host.py publish\DownloadManager.exe %TEMP%\dm-trials\data @msg.json` con file JSON come
`{"type":"probeMedia","version":2,"media":{"url":"https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8"}}`
e poi `addMedia` con il `variantId` della qualità più bassa. Verifica il file con ffprobe se disponibile. I messaggi usano
`"version":3` dalla 0.7.

Grabber (Fase 5), con l'app isolata avviata da sé (`--background`) e il messaggio `grab` inviato tramite l'host nativo come fa
il menu «Cattura questo sito»; la procedura guidata e l'anteprima sono pilotate solo con UI Automation:
- sito locale (sezione `/grab/`: cartelle, CSS con `url()`, robots.txt che esclude `/grab/private/`, un 429 con Retry-After,
  un calendario infinito, 3000 PDF in `many.html` per la fluidità della tabella):
  `powershell -File tools\trials\grab-trial.ps1 -Mode Files -Extensions pdf -Send` e `... -Mode Mirror -Extensions pdf`
  (la copia offline va in `%TEMP%\dm-trials\mirror`, i file mandati alla coda restano in coda: la coda principale va fermata
  prima se non si vuole scaricarli);
- sito pubblico pensato per il crawling, con pochi giri e pagine:
  `... -Url https://books.toscrape.com/ -Mode Files -Extensions jpg -Depth 1 -MaxPages 5 -Rate 1`.
Controlla negli screenshot `grab-*.png` la procedura (5 passi) e l'anteprima (contatori, stato, nessun testo troncato).
Nel log del sito (`site-log.jsonl`) nessuna richiesta a `/grab/private/` e al massimo una pagina per secondo diviso il ritmo scelto.

Video con lingue e sottotitoli: lo stream di esempio avanzato di Apple (`.../img_bipbop_adv_example_fmp4/master.m3u8`) ha più
lingue audio e sottotitoli WebVTT; `probeMedia` deve elencarli in `audioTracks` e `subtitles`, e `addMedia` con `audioId`,
`subtitleIds` e `subtitleMode` deve produrre il video e i file `.vtt`/`.srt` accanto.

Aggiornamenti firmati (dalla 1.1.0), senza toccare `publish\` né l'app reale:
`powershell -File tools\trials\update-trial.ps1 -Build` pubblica il codice come 1.1.0 (`%TEMP%\dm-trials\update\app`) e 1.1.1
firmata con la chiave reale, più due manifest ostili per 1.1.2 (`replay.json`: firma vera della 1.1.1 riusata; `bad.json`:
programma alterato), serviti su 127.0.0.1:8766. Poi `-Start bad.json`, `-Start replay.json` (prima cancella
`data\updates\reported`, altrimenti il messaggio uguale non viene ripetuto) e `-Start good/latest.json`: dopo 30 s compare
la finestra «Aggiornamento» (`ui.ps1 -Window Aggiornamento -Action dump`/`invoke`). Gli ostili devono essere rifiutati senza
scaricare nulla; il valido deve finire con `app\DownloadManager.exe` 1.1.1, `.old` 1.1.0 e `data\updates\started`.

Guarda sempre gli screenshot del pannello video e del popup (grafica, testi troncati, tema).

## 3. Chiusura (obbligatoria)
1. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\trials\finish.ps1` — elenca app di prova, sito, tracce di
   Firefox (avvio automatico, chiavi `HKCU\Software\Mozilla\Firefox`, cache della shell).
2. Se l'elenco è quello atteso: `finish.ps1 -Apply`. Non tocca l'app reale nella tray.
3. Controlla: nessun file nuovo sul Desktop o in Download dell'utente; `reg query HKCU /s /f dm-trials /d` vuoto.

## 4. Resoconto
Per ogni browser: versione, esito di ogni passo, problemi trovati e corretti. Distingui i fallimenti dello script di prova
(da correggere in `tools/trials`) da quelli del prodotto.
