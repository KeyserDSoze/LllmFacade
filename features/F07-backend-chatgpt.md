# F07 — Backend ChatGPT headless e autopilot dei project

Issue: #12. Backend ASP.NET Core (.NET 10) in Docker con PostgreSQL e Chromium headless (Playwright .NET), frontend amministrativo in `src/admin`.

## Criteri di accettazione

- `docker compose up` avvia PostgreSQL e API (Chromium nell'immagine); le migrazioni EF Core sono applicate all'avvio.
- Primo accesso `admin` / `admin` con cambio password obbligatorio; prima del cambio nessun'altra API admin è utilizzabile; rate limiting sul login.
- Endpoint OpenAI compatibili: modelli esposti → modello ChatGPT, mappa `reasoning_effort` → thinking, user agent, header, lingua, fuso, cookie (header `Cookie` o JSON), token. Segreti cifrati (Data Protection), mai restituiti; chiave API mostrata una sola volta.
- Il browser usa cookie, user agent e header configurati; i cookie cambiati dalle risposte sono salvati.
- Job in background: rinnovo del token via `/api/auth/session` a intervallo configurabile o vicino alla scadenza del JWT; errori visibili in UI.
- `GET /v1/models`, `POST /v1/chat/completions` (SSE e non), errori in formato OpenAI.
- Viste: tutti i project (da ChatGPT) con attivazione, attivi, disattivati con riattivazione.
- Project attivi: chat in ordine cronologico, "Porta avanti il lavoro" con prompt salvato, controllo della chat, invio del prompt a risposta terminata, retry ogni minuto se la connessione cade, stato live via SSE.
- Test xUnit (regole, API con PostgreSQL via Testcontainers, browser contro ChatGPT simulato) e Vitest/Playwright per l'admin.
- Registro attività persistente e consultabile (filtri, aggiornamento live, retention) senza segreti né testo di prompt/risposte.
- Project attivi a mosaico (1/2/4 per riga), accordion per project, pallino di stato e ultimo testo ricevuto, un solo stream SSE per tutti i tile, feedback esplicito su salvataggio, avvio e arresto.

## Limiti

- Le API web di ChatGPT non sono pubbliche né stabili: percorsi e selettori sono configurabili (`ChatGpt__*`).
- Nessuna risoluzione di challenge anti-bot e nessuna tecnica stealth: se ChatGPT blocca il browser l'errore è mostrato.
- L'automazione di ChatGPT può violare i termini d'uso: valutazione a carico del proprietario.
- Login reale e invio su un account ChatGPT vanno verificati manualmente: i test usano un ChatGPT simulato.
