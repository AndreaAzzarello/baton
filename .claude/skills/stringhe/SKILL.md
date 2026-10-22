---
name: stringhe
description: Aggiunge, modifica o controlla i testi di Baton - Strings.resx dell'app e _locales (italiano e inglese) dell'estensione - tramite i generatori in tools/. Usala quando si aggiunge o cambia un testo visibile, un'etichetta, un messaggio o una traduzione, o per cercare testi non usati.
---

# Testi dell'app e dell'estensione

I file `src/Baton.Core/Resources/Strings.resx` (inglese, lingua neutra), `Strings.it.resx` (italiano) ed
`extension/_locales/*/messages.json` sono **generati**: la fonte è la tabella dentro i generatori. Non modificarli a mano
(verrebbero sovrascritti).

## App (inglese e italiano)
1. Nel codice usa `Strings.NomeChiave` (C#) o `{l:Loc NomeChiave}` (XAML, con `xmlns:l="clr-namespace:Baton.App.Services"`):
   `{l:Loc}` segue il cambio di lingua dal vivo, `x:Static` no. Per un menu costruito nel codice:
   `new MenuItem { Command = … }.Text(nameof(Strings.NomeChiave))`. Mai testi scritti direttamente in XAML o nel codice
   dell'app (`HardCodedTextTests` lo controlla).
2. Aggiungi la chiave al dizionario `S` in `tools/make_resx.py`, nella sezione giusta, come coppia `(inglese, italiano)`.
   Stile: frasi brevi e naturali in entrambe le lingue, "_" prima della lettera di accesso rapido nelle etichette (anche in
   inglese, possibilmente unica nella finestra), `{0}` per i parametri, stessi segnaposto nelle due lingue (il generatore si
   ferma se differiscono; i formati come `{0:dddd d MMMM}` possono cambiare ordine).
3. `python tools/make_resx.py src` — si ferma se una chiave usata nel codice manca; elenca quelle non più usate (toglile
   dalla tabella, salvo che siano usate in modo dinamico).
4. Build: le proprietà di `Strings` sono generate dal resx; l'italiano diventa l'assembly satellite `it` (incluso nell'exe).
5. Testi troncati: `WindowGalleryTests` (test UI) apre ogni finestra fuori schermo in inglese e italiano e fallisce se un
   testo non sta. Per guardarle: `BATON_SHOTS=<cartella> dotnet test tests\Baton.UiTests --filter WindowGallery` salva i PNG
   nei due temi e `cut-texts.txt`. Con `python tools/make_resx.py src --pseudo` (crea `Strings.qps-ploc.resx`, inglese +40%,
   mai committato) la galleria aggiunge la pseudo-lingua; l'app la mostra con `BATON_PSEUDO=1`. Poi rilancia senza
   `--pseudo`: cancella il file e gli assembly satellite già compilati.
6. Nomi predefiniti salvati nel DB (categorie predefinite, Coda principale): si mostrano con `DisplayName`, che segue la
   lingua finché l'utente non li rinomina.

## Estensione (italiano e inglese)
1. Nel codice `t("chiave", ...)` (JS) o `data-i18n="chiave"` / `data-i18n-title` / `data-i18n-placeholder` / `data-i18n-aria` (HTML).
   Parametri come `$1`, `$2`.
2. Aggiungi la chiave alla tabella `M` in `tools/make_locales.py` come coppia `(inglese, italiano)`: le due lingue hanno
   sempre le stesse chiavi.
3. `python tools/make_locales.py extension`.
4. Controllo delle chiavi usate: `node --test "tests/extension/*.test.mjs"` (lo fa anche l'hook dopo ogni modifica in
   `extension/`); per un controllo completo cerca `t("` e `data-i18n` nei file dell'estensione e confrontali con
   `_locales/en/messages.json`. Le chiavi composte (`state_${...}`, `chip_${...}`, `checkText_${...}`) vanno tenute per
   tutti gli stati di `Rules.connectionState`.

## Revisione dei testi
- Coerenza: stessi termini ovunque (download, coda, categoria, Impostazioni › Scheda).
- Nell'estensione l'etichetta breve va nel chip, la frase intera nel tooltip; nei pannelli niente testi troncati
  (verifica con gli screenshot della skill `prova-reale`).
- Accessibilità: ogni controllo nuovo ha un nome accessibile (`AutomationProperties.Name`, `aria-label`).
