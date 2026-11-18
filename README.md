# LllmFacade

Workspace multi-interfaccia per organizzare progetti e conversazioni AI. Monorepo ispirata alla struttura di [DeliveryManager](https://github.com/KeyserDSoze/DeliveryManager).

## La POC

- **Web / GitHub Pages**: dashboard React con progetti locali, elenco delle schede ChatGPT e un collegamento diretto con l'estensione Chrome.
- **Estensione Chrome (Manifest V3)**: apre, elenca e attiva le schede; in modo sperimentale, da comando esplicito dell'utente, può inviare un prompt e mostrare l'ultima risposta della scheda selezionata. Il DOM di ChatGPT può cambiare e interrompere la funzionalità.
- **Desktop**: shell Electron/Chromium con fino a 12 pagine reali affiancate e sessione di browser condivisa tra riquadri.
- **Backend**: ASP.NET Core con i layer Applications, Business, Domain, Infrastructures; opzionale per la POC e non richiesto da GitHub Pages.

Non usiamo OpenAI API né copiamo credenziali/sessioni da Chrome. Le risposte lette dall'estensione sono mostrate temporaneamente e **non salvate** dalla dashboard. L'automazione della UI di servizi terzi va valutata rispetto ai rispettivi termini d'uso; nessuna integrazione DOM è garantita o ufficiale.

## Avvio rapido

1. Installa l'estensione dalla cartella `src/extension` con `chrome://extensions` → Modalità sviluppatore → Carica estensione non pacchettizzata.
2. In `src/frontend`: `npm install && npm run dev`. Apri l'indirizzo Vite.
3. Da `chrome://extensions`, copia l'**ID** dell'estensione nella dashboard; premi **Connetti**.
4. Apri una o più chat; usa i comandi sulle relative schede nella dashboard.
5. Per la vista browser desktop: `cd src/desktop && npm install && npm start`. L'accesso a ChatGPT in Electron può essere bloccato dai meccanismi di autenticazione: è un limite da verificare sul computer reale.

## Repository

```text
src/
  frontend/         dashboard GitHub Pages (React, TypeScript, Vite)
  extension/        estensione Chrome Manifest V3
  desktop/          browser multivista Electron
  backend/          ASP.NET Core (Applications/Business/Domain/Infrastructures)
tests/
  frontend/         Vitest
  extension/        test Node delle regole del bridge
  backend/          xUnit
ADR/                decisioni architetturali
docs/               architettura, sviluppo, sicurezza
features/           criteri di accettazione
script/             comandi di verifica
.github/workflows/  CI e Pages
```

Vedi `AGENTS.md` per le convenzioni e `docs/local-development.md` per i comandi. Il deploy Pages avviene su `main` quando il proprietario abilita **Settings → Pages → GitHub Actions**.
Lo sviluppo avviene direttamente su `main`, con TDD e avanzamento tracciato in [GitHub Issues](https://github.com/KeyserDSoze/LllmFacade/issues).

## Persistenza delle conversazioni

Le chat standard `/c/{id}` mantengono la loro associazione al progetto anche se cambia il tab ID Chrome. I collegamenti possono essere riaperti dalla dashboard quando la scheda è chiusa. Le associazioni dalla home di ChatGPT restano temporanee fino alla creazione di una conversazione e all'aggiornamento delle schede (ADR 0003). Non viene sincronizzato alcun dato fra dispositivi.

## Stato

Bootstrap della POC: non è ancora un prodotto e non abbiamo verificato su una sessione reale l'automazione dell'interfaccia ChatGPT o il login in Electron. Nessun endpoint API è esposto su GitHub Pages.


