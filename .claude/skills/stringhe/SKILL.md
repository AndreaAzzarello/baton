---
name: stringhe
description: Aggiunge, modifica o controlla i testi di DownloadManager - Strings.resx dell'app e _locales (italiano e inglese) dell'estensione - tramite i generatori in tools/. Usala quando si aggiunge o cambia un testo visibile, un'etichetta, un messaggio o una traduzione, o per cercare testi non usati.
---

# Testi dell'app e dell'estensione

I file `src/DownloadManager.Core/Resources/Strings.resx` ed `extension/_locales/*/messages.json` sono **generati**: la fonte è
la tabella dentro i generatori. Non modificarli a mano (verrebbero sovrascritti).

## App (italiano)
1. Nel codice usa `Strings.NomeChiave` (C#) o `{x:Static r:Strings.NomeChiave}` (XAML).
2. Aggiungi la chiave al dizionario `S` in `tools/make_resx.py`, nella sezione giusta. Stile: frasi brevi, italiano
   naturale, "_" prima della lettera di accesso rapido nelle etichette, `{0}` per i parametri.
3. `python tools/make_resx.py src` — si ferma se una chiave usata nel codice manca; elenca quelle non più usate (toglile
   dalla tabella, salvo che siano usate in modo dinamico).
4. Build: le proprietà di `Strings` sono generate dal resx.

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
