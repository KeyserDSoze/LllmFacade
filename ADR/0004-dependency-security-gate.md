# ADR 0004 — Gate di sicurezza npm distinto per superficie distribuita

- Stato: Accepted (POC, issue #9)
- Contesto: la diagnostica `npm audit` ha segnalato advisory su toolchain Vite/Vitest ed Electron. La correzione automatica proponeva versioni incompatibili fra loro o reintroduceva altri advisory: il numero di avvisi da solo non dimostra la sicurezza di una versione.
- Decisione: applicare un gate CI bloccante (`node script/npm-audit-report.mjs --gate`) per criticità **high** e **critical** nelle dipendenze di produzione della SPA e dei test frontend. Per Electron, che è distribuito insieme all'app anche se npm lo classifica `devDependency`, bloccare su **tutte** le dipendenze.
- Gli advisory delle dipendenze usate **solo** per sviluppo/build/test non bloccano automaticamente la distribuzione statica, ma devono essere visualizzati in chiaro, analizzati e tracciati nella issue #9. L'opzione `--strict` impone il gate anche sul tooling.
- Limitare Vite dev a `127.0.0.1`. Utilizzare per il momento versioni esplicite del toolchain, evitando downgrade suggeriti da audit che reintroducono criticità. Una release desktop richiede comunque test reali.
- Prossimi passi: lockfile riproducibili per tutti i workspace, aggiornamenti patch verificati, revisione periodica degli advisory e possibile irrigidimento del gate.
- Conseguenze: una CI verde non implica zero advisory nel tooling né la sicurezza complessiva del prodotto; le eccezioni non vengono nascoste.
