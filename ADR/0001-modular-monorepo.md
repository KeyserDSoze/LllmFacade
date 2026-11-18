# ADR 0001 — Monorepo modulare, tre interfacce e backend opzionale

- Stato: Accepted (POC)
- Contesto: il prodotto deve funzionare come dashboard web, estensione Chrome e browser desktop, con evoluzione verso un catalogo progetti centralizzato.
- Decisione: usare una sola repository. `src/frontend`, `src/extension`, `src/desktop` hanno runtime distinti; `src/backend` replica i layer Applications, Business, Domain e Infrastructures di DeliveryManager.
- Motivazione: separare deploy e permessi, riusare convenzioni di test/documentazione e non obbligare la Pages a usare un backend che non serve alla POC.
- Conseguenze: il deploy GitHub Pages pubblica **solo** `src/frontend/dist`; il browser desktop è un'installazione locale; l'estensione si carica a mano. L'integrazione desktop e quella browser non condividono automaticamente le sessioni.

