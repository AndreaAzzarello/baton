# Privacy

**English** · [Italiano](#italiano)

Baton is a download manager that runs on your computer. It has no account, no server of its own and no advertising.

## No telemetry

Baton collects nothing about you or how you use it: no usage statistics, no crash reports, no identifiers, no "phone
home". The browser extension does not either.

## Who Baton talks to

Only these, and only for what you ask:

- **The servers you download from**: the addresses you add, those the browser extension hands over, the pages and files
  the grabber visits on the sites you choose (including their `robots.txt`), FTP/FTPS servers, and the proxy or PAC
  address you configure. With each request Baton sends what that download needs: its address, the referrer, and the
  cookies, user name or password that belong to it.
- **This repository's releases on GitHub** (`api.github.com` and `github.com`), for updates, only when you turn on
  *Check for updates at startup* (off by default) or press *Check now*, and right after an update you installed, to fetch
  the signed browser extension of that same version. Nothing about you is sent: GitHub sees an ordinary request for the
  latest release, as for any visitor of the page.

The browser extension talks only to the Baton app on the same computer (through the browser's native messaging), never
to a server.

## Your data stays on your computer

- The download list, queues, categories, settings and logs are kept in `%LOCALAPPDATA%\Baton` (the portable version:
  the `Data` folder next to `Baton.exe`). The extension keeps its settings in the browser's own storage.
- **Passwords, cookies and form data** that come with a download are encrypted with Windows DPAPI, tied to your
  Windows account: they are never written in clear in the database, in the logs or on screen.
- Uninstalling asks whether to keep this data; downloaded files are never deleted.

## Nothing goes to third parties

Baton does not sell, share or send your data to anyone. The only other programs involved are those you choose: the
antivirus that checks finished downloads (Windows Defender by default, which follows its own settings) and ffmpeg for
videos, both run on your computer.

Questions: open an issue, or for anything sensitive use the private channel in [SECURITY.md](SECURITY.md).

---

## Italiano

Baton è un download manager che funziona sul tuo computer: nessun account, nessun server suo, nessuna pubblicità.

**Nessuna telemetria.** Baton non raccoglie nulla su di te o su come lo usi: niente statistiche d'uso, segnalazioni di
crash, identificativi o contatti nascosti. Neanche l'estensione del browser.

**Con chi parla Baton**, solo per quello che chiedi:
- **i server da cui scarichi**: gli indirizzi che aggiungi, quelli che passa l'estensione, le pagine e i file che il
  grabber visita sui siti scelti da te (compreso il loro `robots.txt`), i server FTP/FTPS e il proxy o il file PAC che
  imposti. A ogni richiesta invia ciò che serve a quel download: indirizzo, referrer e cookie, utente o password che gli
  appartengono;
- **le release di questo repository su GitHub**, per gli aggiornamenti, solo se attivi *Controlla gli aggiornamenti
  all'avvio* (spento all'inizio) o premi *Controlla ora*, e subito dopo un aggiornamento installato, per scaricare
  l'estensione firmata della stessa versione. Non viene inviato nulla su di te.

L'estensione parla solo con l'app Baton sullo stesso computer (native messaging del browser), mai con un server.

**I tuoi dati restano sul tuo computer**: elenco dei download, code, categorie, impostazioni e registri in
`%LOCALAPPDATA%\Baton` (portable: cartella `Data` accanto a `Baton.exe`). **Password, cookie e campi dei moduli** sono
cifrati con DPAPI di Windows, legati al tuo account: mai in chiaro nel database, nei registri o sullo schermo.

**Nessun dato a terzi**: Baton non vende, non condivide e non invia i tuoi dati a nessuno. Gli unici altri programmi
coinvolti sono quelli che scegli tu (l'antivirus che controlla i download, Windows Defender per impostazione, e ffmpeg per
i video), entrambi eseguiti sul tuo computer.
