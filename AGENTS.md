# Istruzioni per contribuire — LllmFacade

Lingua dei documenti e della UI: italiano. Identificatori e codice: inglese. Basato sulle convenzioni di DeliveryManager, adattate a browser/estensione.

## Organizzazione

- `src/frontend`: React + TypeScript + Vite, distribuibile staticamente su GitHub Pages.
- `src/extension`: Chrome MV3. Permessi minimi, nessuna credenziale, origin esplicitamente consentiti.
- `src/desktop`: Electron con WebContentsView per rendering di vere pagine web, non iframe.
- `src/backend/applications`, `business`, `domain`, `infrastructures`: API sottile, regole pure, entità, adapter.
- `tests/frontend`, `tests/extension`, `tests/backend`: test separati.
- `ADR`, `docs`, `features`: decisioni, runbook, acceptance test.

## Invarianti

1. La dashboard su Pages è statica: non mettere chiavi, token, cookie o altri segreti nel frontend, nell'estensione o nel repository.
2. I messaggi `onMessageExternal` sono accettati solo dagli origin definiti in `src/extension/background.js` e `manifest.json`. Non introdurre wildcard generiche.
3. Prima di usare `chrome.scripting`, controllare URL e tab ID: solo tab ChatGPT scelte dall'utente. Nessuna operazione su qualunque sito.
4. Le risposte possono essere mostrate in memoria; non memorizzarle in localStorage, log o backend senza una funzione esplicita e consenso.
5. Il bridge è sperimentale: il DOM di ChatGPT cambia. Evitare loop aggressivi, scraping massivo e presunzioni che l'accesso web sia un'API supportata.
6. Electron: `nodeIntegration: false`, `contextIsolation: true`, `sandbox: true`, preload ristretto; mai esporre Node a pagine remote.
7. Nuove feature: test di logica e aggiornamento `docs/`, `features/` o ADR quando cambia l'architettura.
8. Non dichiarare test o login verificati se non sono stati effettivamente eseguiti.

## Verifica

- `cd src/frontend && npm install && npm run build`
- `cd tests/frontend && npm install && npm run test:unit`
- `node --test tests/extension/*.test.cjs`
- `node --check src/extension/background.js`
- `node --check src/desktop/main.cjs`
- `dotnet run --project tests/backend/LllmFacade.Business.Tests/LllmFacade.Business.Tests.csproj` (SDK 10)

Prima di un merge, la CI deve completare i test automatici. Per la prova reale, seguire `docs/local-development.md`.

