# Sviluppo e prova manuale

## Requisiti

Node.js 22+, npm, Chrome. .NET SDK 10 solo per il backend. Per la UI desktop serve un sistema con interfaccia grafica.

## Web

```bash
cd src/frontend
npm install
npm run dev
```

Aprire `http://localhost:5173`. Il sito Github Pages userà `https://keyserdsoze.github.io/LllmFacade/` dopo il deploy.

## Estensione

1. Aprire `chrome://extensions`, attivare modalità sviluppatore.
2. Premere **Carica estensione non pacchettizzata** e scegliere `src/extension`.
3. Copiare l'ID mostrato da Chrome e incollarlo nella dashboard, poi **Connetti**.
4. Provare **Apri chat** e **Aggiorna schede**. Il bridge elenca solo tab ChatGPT.
5. Selezionare una scheda e usare **Invia prompt** o **Leggi risposta**: test manuale su una pagina reale, sperimentale e con possibile errore per cambio selettori.
6. Se modifichi il codice dell'estensione, premere **Ricarica** su `chrome://extensions` e ricaricare la dashboard.

Il contenuto delle risposte non va in storage: si perde al refresh. L'associazione progetto/tab è solo una comodità della POC.

## Desktop

```bash
cd src/desktop
npm install
npm start
```

Scegliere 1, 4, 6 o 12 riquadri. Le pagine sono Chrome-like tramite Electron Chromium, **non** vere schede di Chrome. Il login di provider terzi (incluso ChatGPT) potrebbe rifiutare embedded Chromium; verificarlo manualmente. Il desktop non ospita la dashboard GitHub Pages nella stessa sessione.

## Backend opzionale

```bash
dotnet run --project src/backend/applications/LllmFacade.Api
```

API locale dimostrativa su porta ASP.NET di sviluppo. Non è richiesta da Pages e non è protetta per esposizione Internet.

## Test

```bash
bash script/verify.sh
```

In alternativa eseguire singolarmente i comandi in `AGENTS.md`.

## Deploy

Il workflow `pages.yml` effettua build Vite con base path `/LllmFacade/` e pubblica `src/frontend/dist` ad ogni push su `main`. Il proprietario deve scegliere **GitHub Actions** in Settings → Pages. Il workflow non pubblica l'estensione o Electron.

