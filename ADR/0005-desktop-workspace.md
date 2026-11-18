# ADR 0005 — Workspace desktop locale, indipendente dalla sessione Chromium

- Stato: Accepted (POC, issue #11, sotto-attività #5).
- Problema: a ogni riavvio l'utente perdeva layout e URL dei riquadri Electron, vanificando la vista a 12 chat.
- Decisione: salvare solo `version:1`, layout `1|4|6|12` e 12 URL sanitizzati in `workspace.json` sotto `app.getPath('userData')`. La partizione sessione Chromium `persist:lllmfacade` è separata e non viene serializzata.
- Sicurezza: URL `https:` oppure `http:` su localhost/127.0.0.1; rifiutare credenziali nella URL, protocolli privilegiati, valori eccessivamente lunghi; eliminare query e fragment prima della persistenza. Gli URL non sono cifrati e possono rivelare identificativi conversazione: il dispositivo deve essere sotto controllo dell'utente.
- Scrittura atomica con permessi `0600` POSIX, limite di 16 KiB, fallback su dati invalidi o versioni sconosciute; lo schema dovrà essere migrato esplicitamente in revisioni future.
- UI: il renderer recupera layout e URL via preload/IPC ristretto, non usa filesystem o API Node. La navigazione aggiorna la configurazione; la riduzione a 1/4/6 non cancella i riquadri nascosti.
- Test: unit Node per normalizzazione, roundtrip e gestione di dati ostili; CI assicura sintassi e logica pura, non autentica provider né valida un test grafico su Windows/macOS.
- Conseguenze: non è un backup né una sincronizzazione tra dispositivi (issue #7). La sessione ChatGPT in Electron può essere rifiutata dal provider; resta da verificare in #5.
