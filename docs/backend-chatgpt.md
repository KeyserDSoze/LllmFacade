# Backend ChatGPT headless (F07)

## Avvio con Docker

```bash
node script/init-env.mjs   # crea .env con una password PostgreSQL casuale (non sovrascrive)
docker compose up --build
```

Apri `http://127.0.0.1:63400` (unica porta pubblicata, solo su localhost; PostgreSQL resta nella rete interna). Primo accesso: `admin` / `admin`, poi cambio password obbligatorio (minimo 12 caratteri). Dietro un reverse proxy HTTPS imposta `LLLMFACADE_HTTPS_COOKIE=true`.

Volumi: `postgres-data` (database) e `api-keys` (chiavi Data Protection). Perdere `api-keys` rende illeggibili cookie e token salvati: vanno reinseriti.

## Uso

1. **Endpoint OpenAI** → *Nuovo endpoint*: nome, modelli (es. `chatgpt-thinking` → `gpt-5-thinking`), valori di thinking, user agent, lingua, fuso, header, cookie e token. Dalla scheda *Rete* di DevTools si possono incollare gli header copiati (o cURL bash): cookie, token e user agent vanno nei campi giusti.
2. La chiave API (`lf_...`) è mostrata una sola volta. Base URL: `http://127.0.0.1:63400/v1`.

```bash
curl http://127.0.0.1:63400/v1/chat/completions \
  -H "Authorization: Bearer lf_..." -H "Content-Type: application/json" \
  -d '{"model":"chatgpt-thinking","reasoning_effort":"high","stream":true,"messages":[{"role":"user","content":"Ciao"}]}'
```

`reasoning_effort` (anche `reasoning.effort`) è tradotto con la mappa dell'endpoint; senza valore si usa il predefinito del modello. Supportati solo testo e `n = 1`; l'uso dei token è una stima (~4 caratteri/token).

3. **Tutti i project** legge i project da ChatGPT e permette di attivarli; **Project attivi** mostra le chat in ordine cronologico, il prompt di continuazione e **Porta avanti il lavoro**; **Project disattivati** permette la riattivazione.

## Come funziona

- Un solo Chromium headless per processo, un contesto (cookie jar) isolato per endpoint, con user agent, lingua, fuso e header configurati.
- Ogni richiesta passa dalla pagina di ChatGPT: la web app invia il proprio POST, il backend ne sostituisce modello/thinking/prompt e ne copia lo stream SSE. Lettura di project e chat tramite `fetch` nella pagina con il token Bearer, come la web app.
- I cookie del browser sono ricavati dopo ogni uso e salvati cifrati se cambiati. Un cookie incollato come header `Cookie` e poi riemesso da ChatGPT sotto un altro scope sostituisce quello importato.
- Worker di rinnovo: ogni minuto controlla gli endpoint; rinnova via `/api/auth/session` all'intervallo configurato (5–1440 min), 15 minuti prima della scadenza del JWT, con pausa di 5 minuti dopo un errore.
- Autopilot: controlla la chat ogni 30 s; a risposta finale invia il prompt una sola volta (nodo in attesa + 5 minuti di tolleranza); se la connessione cade riprova ogni 60 s; si ferma al limite di invii. Lo stato arriva alla UI via SSE.

## Configurazione

Variabili d'ambiente (doppio underscore): `ConnectionStrings__Postgres`, `DataProtection__KeysPath`, `Admin__*`, `Workers__*`, `Autopilot__*`, `Browser__*`, `ChatGpt__*` (percorsi e selettori della web app).

## Test

```bash
dotnet run --project tests/backend/LllmFacade.Business.Tests/LllmFacade.Business.Tests.csproj
dotnet run --project tests/backend/LllmFacade.Browser.Tests/LllmFacade.Browser.Tests.csproj   # scarica Chromium
dotnet run --project tests/backend/LllmFacade.Api.Tests/LllmFacade.Api.Tests.csproj           # richiede Docker
cd tests/admin && npm install && npm run test:unit && npx playwright install chromium && npm run test:e2e
```
