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

3. **Tutti i project** legge i project da ChatGPT e permette di attivarli; **Project attivi** li mostra a mosaico (pulsante *1 / 2 / 4 per riga*, scelta ricordata dal browser); **Project disattivati** permette la riattivazione.
   - Ogni tile è un accordion (anche *Comprimi tutti* / *Espandi tutti*): da chiuso resta visibile il pallino di stato e l'ultimo pezzo di testo ricevuto. Verde pulsante = ChatGPT sta scrivendo; giallo = in attesa (prossimo controllo o conferma dell'invio); rosso = errore o connessione persa; blu = completato; grigio = autopilot non avviato.
   - Aperto, il tile mostra le chat in ordine cronologico (caricate solo all'apertura), il prompt di continuazione, il limite di invii, **Porta avanti il lavoro**, **Salva impostazioni** (attivo solo con modifiche), l'avanzamento live e l'attività recente. Ogni azione mostra un messaggio di esito o l'errore.
4. **Registro** elenca tutte le azioni (accessi, endpoint, sessione, project, autopilot, richieste OpenAI) con filtri per livello, categoria e project e aggiornamento ogni 3 secondi. L'autopilot registra solo i passaggi (risposta terminata e prompt inviato, errore al primo tentativo e poi ogni 10, connessione ripristinata, completamento), non ogni controllo. Non contiene cookie, token, chiavi, testo dei prompt o delle risposte; le voci più vecchie di `Logs__RetentionDays` (14) sono eliminate.

## Come funziona

### Browser collegato (consigliato per Cloudflare)

Il browser visibile del tuo PC resta il proprietario della sessione. Il backend si collega a Chrome/Edge con CDP e apre **solo nuove schede ChatGPT che gestisce lui**; non legge, naviga o chiude le altre schede. I cookie, lo storage, la lingua, il fuso, lo user agent e gli header restano quelli reali del browser: in questa modalità i valori copiati a mano non vengono sovrapposti. La richiesta parte da `window.fetch` della pagina ChatGPT o dal form del composer. Se ChatGPT mostra una verifica, la fai manualmente nel browser collegato; il backend non la risolve né la aggira.

```powershell
pwsh -File script/start-chatgpt-browser.ps1
# accedi una volta a ChatGPT nella finestra aperta
# in .env: BROWSER_CDP_URL=http://host.docker.internal:63401
docker compose up -d --force-recreate api
```

Lo script usa un profilo Chrome/Edge dedicato (visibile, non headless), con CDP ascolta solo sulla porta locale `127.0.0.1:63401`; non serve esporre quella porta nel `docker-compose.yml`. Premi **Rinnova sessione ora** dopo il login. Se `BROWSER_CDP_URL` è vuoto si usa la modalità predefinita: Chromium headless isolato nel container.

### Modalità headless predefinita

- Un solo Chromium headless per processo, un contesto (cookie jar) isolato per endpoint, con user agent, lingua, fuso e header configurati. Ogni endpoint mantiene fino a 24 schede project (`Browser__MaxTabsPerEndpoint`) per evitare blocchi quando ci sono più di otto project attivi.
- Ogni richiesta passa dalla pagina di ChatGPT: la web app invia il proprio POST, il backend ne sostituisce modello/thinking/prompt e ne copia lo stream SSE. Lettura di project e chat tramite `fetch` nella pagina con il token Bearer, come la web app.
- I cookie del browser sono ricavati dopo ogni uso e salvati cifrati se cambiati. Un cookie incollato come header `Cookie` e poi riemesso da ChatGPT sotto un altro scope sostituisce quello importato.
- Worker di rinnovo: ogni minuto controlla gli endpoint; rinnova via `/api/auth/session` all'intervallo configurato (5–1440 min), 15 minuti prima della scadenza del JWT, con pausa di 5 minuti dopo un errore.
- Autopilot: controlla la chat ogni 30 s; quando il target è "la chat più recente", aggiorna e persiste subito `CurrentConversationId` prima di leggere il dettaglio, così UI e DB non restano sulla chat precedente se il dettaglio tarda o fallisce. A risposta finale invia il prompt una sola volta (nodo in attesa + 5 minuti di tolleranza); se la connessione cade riprova ogni 60 s; si ferma al limite di invii. Lo stato arriva alla UI via SSE.
- Limite di lunghezza: se ChatGPT mostra "You've reached the maximum length for this conversation" (anche `context length exceeded`, `exceeded` o una toast visibile dopo un errore HTTP generico), l'autopilot apre una **nuova chat nello stesso project** dalla pagina del project (`ChatGpt__ProjectPagePath`, predefinito `/g/{projectId}/project`), invia il *primo messaggio della nuova chat* (campo del project; se vuoto: spiegazione automatica + ultimo messaggio della chat precedente + prompt di continuazione) e imposta la nuova chat come quella da portare avanti. Il passaggio conta come confermato quando il browser mostra il nuovo URL `/c/{id}`, anche se il callback SSE iniziale è mancato, e viene registrato. Riusa inoltre la rotta canonica del project già osservata nel browser (incluso lo slug). Se l'ID della nuova chat non è leggibile dallo stream si usa l'indirizzo della pagina; se non c'è modo di capirlo si riconosce la chat più recente del project per non crearne due. Il testo cercato è configurabile con `ChatGpt__ConversationFullPattern` (regex, senza distinzione di maiuscole).
- Pannello in basso a destra (sempre visibile, comprimibile): quanti thread lavorano insieme (project con autopilot che sta scrivendo o inviando, più le richieste in corso sull'API OpenAI) e, per ogni endpoint, quando il token è stato rinnovato, quando verrà rinnovato (intervallo configurato o 15 minuti prima della scadenza del JWT, con pausa di 5 minuti dopo un errore) e fra quanto scade. Aggiornamento ogni 5 secondi.

## Configurazione

Variabili d'ambiente (doppio underscore): `ConnectionStrings__Postgres`, `DataProtection__KeysPath`, `Admin__*`, `Workers__*`, `Autopilot__*`, `Browser__*`, `ChatGpt__*` (percorsi e selettori della web app). Per il browser collegato Compose usa `BROWSER_CDP_URL` e la trasforma in `Browser__ConnectOverCdpUrl`; sono accettati solo endpoint locali.

## Test

```bash
dotnet run --project tests/backend/LllmFacade.Business.Tests/LllmFacade.Business.Tests.csproj
dotnet run --project tests/backend/LllmFacade.Browser.Tests/LllmFacade.Browser.Tests.csproj   # scarica Chromium
dotnet run --project tests/backend/LllmFacade.Api.Tests/LllmFacade.Api.Tests.csproj           # richiede Docker
cd tests/admin && npm install && npm run test:unit && npx playwright install chromium && npm run test:e2e
```
