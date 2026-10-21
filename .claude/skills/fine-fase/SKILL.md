---
name: fine-fase
description: Chiude una fase di Baton (ex DownloadManager) - test, revisione del codice e di sicurezza con correzioni, ripubblicazione, riavvio dell'app reale, controlli di registro e manifest, versione, README e commit. Usala quando l'utente chiede di chiudere o completare una fase, di pubblicare o di fare il commit finale.
---

# Fine fase

Segui i passi in ordine; se uno fallisce, correggi e riparti da quel passo. Non saltare la revisione.

1. **Versione**: aggiorna `<Version>` in `src/Baton.App/Baton.App.csproj` e `version` in
   `extension/manifest.json`; `AppSettings.DefaultUserAgent` se cambia la versione minore.
2. **Testi**: `python tools/make_resx.py src` e `python tools/make_locales.py extension` — nessuna chiave mancante o inutilizzata.
3. **Build e test**: `"C:\Program Files\dotnet\dotnet.exe" build Baton.slnx` con zero avvisi, poi
   `dotnet test Baton.slnx` **due volte** (include i test Node dell'estensione). Anche `node tools/build-extension.mjs`.
4. **Revisione**: esegui `/code-review` sul diff della fase (rispetto all'ultimo commit di fase) e poi `/security-review`.
   Correggi ogni problema confermato (con un test quando possibile), poi ripeti il passo 3. Annota cosa è stato trovato e
   cosa no (per il riepilogo), senza nascondere i punti lasciati aperti.
5. **README**: `README.it.md` (guida completa: versione, funzioni nuove, installazione per browser, schema del DB, limiti,
   comandi) e, in sintesi, `README.md` in inglese (funzioni, known issues); screenshot e GIF con dati di prova.
6. **Ripubblicazione** (chiude l'app dell'utente):
   - controlla che non ci siano download in corso nel DB reale (`%LOCALAPPDATA%\Baton\downloads.db`, `status=1`);
     se ci sono, chiedi all'utente;
   - ferma solo `publish\Baton.exe`, poi
     `dotnet publish src\Baton.App -c Release -r win-x64 -o publish`.
7. **Riavvio e verifica** dell'app reale, senza variabili `BATON_DATA`/`DOWNLOADMANAGER_DATA`: `Start-Process publish\Baton.exe --tray`, poi
   - `user_version` del DB e numero di download invariati, backup `.v<N>.bak` presente se c'è stata migrazione;
   - `HKCU\Software\{Google\Chrome,Microsoft\Edge,Chromium,BraveSoftware\Brave-Browser,Mozilla}\NativeMessagingHosts\com.andreaazzarello.baton` (e nessuna `com.downloadmanager.host`)
     e i due manifest in `%LOCALAPPDATA%\Baton` puntano a `publish\Baton.exe`;
   - `HKCU\Software\Classes\baton\shell\open\command` e l'icona di `AppUserModelId\Baton.App` corretti, niente `Classes\downloadmanager`;
   - la chiave Run non ha voci Baton o DownloadManager che l'utente non ha chiesto;
   - nessun riferimento a `bin\Debug`, `dm-trials` o alla scratchpad: `reg query HKCU /s /f <testo> /d`;
   - memoria a riposo nella tray (working set e privata dopo 10 s), confrontata con la fase precedente.
8. **Prove reali**: skill `prova-reale` se la fase tocca rete, estensione o interfaccia, chiusura con `finish.ps1` compresa.
9. **Commit**: `git status` pulito a parte la fase; messaggio descrittivo in inglese (titolo "Phase N: ... (x.y.z)", corpo per
   area, numero di test), terminato con la riga Co-Authored-By indicata nelle istruzioni. Mai `git push` senza richiesta.
10. **Memoria**: aggiorna il file di memoria del progetto (stato, commit, lezioni non deducibili dal codice).
11. **Riepilogo** per l'utente, in italiano e in forma di prompt da incollare: fatto, test, problemi corretti (compresi quelli
    emersi dalla revisione), limiti, cosa provare a mano.
