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
- Quando una chat raggiunge il limite di lunghezza di ChatGPT l'autopilot crea una nuova chat nello stesso project, invia il primo messaggio configurabile e prosegue lì, senza duplicare chat o prompt.
- Pannello fisso in basso a destra con i thread al lavoro (project e richieste API) e, per endpoint, ultimo e prossimo rinnovo del token.
- Registro attività persistente e consultabile (filtri, aggiornamento live, retention) senza segreti né testo di prompt/risposte.
- Project attivi a mosaico (1/2/4 per riga), accordion per project, pallino di stato e ultimo testo ricevuto, un solo stream SSE per tutti i tile, feedback esplicito su salvataggio, avvio e arresto.
- Comandi globali "Ferma tutti" / "Riprendi tutti": lo stop sospende gli autopilot in esecuzione senza chiudere le tab; la ripresa avvia i project attivi ancora fermi che hanno un prompt salvato, distribuendo le partenze con lo stesso intervallo adattivo dei cooldown e lasciando invariati quelli già in esecuzione. Lo stesso distanziamento viene applicato quando il servizio riavvia.
- Ogni project mantiene una tab della sua chat corrente: i controlli ogni 5 minuti riusano la pagina già aperta, la riadottano dopo un riavvio del servizio e non la ricaricano se l'URL della conversation è già corretto. Se Chrome mostra già la conversation giusta, anche una pagina utente non ancora registrata viene adottata. Solo il rollover apre una nuova tab, verifica l'invio della nuova chat e chiude la precedente.
- Prima di usare il composer il browser attende il vero evento `load`. Se la tab è stata aperta, riaperta, ricaricata o riadottata, lascia passare altri 120 secondi dal load prima di scrivere (`PageLoadToPromptDelayMilliseconds`); una tab stabile non ripete la pausa. Dopo che il testo è acquisito dal composer aspetta 8 secondi prima dell'invio e due prompt dello stesso endpoint non partono a meno di 60 secondi di distanza (`PromptToSendDelayMilliseconds` e `MinimumPromptSendIntervalMilliseconds`). Sono pause di carico esplicite, non casuali né stealth.
- Un 403 Cloudflare/"Verify you are human" mette in `VerificationRequired` tutti i project dello stesso endpoint per 30 minuti (`ChallengeCooldownMinutes`). Durante questa pausa non vengono fatti tentativi di bypass o richieste ripetute. La verifica si completa manualmente nel browser collegato; dopo la verifica `Riprendi tutti` riattiva esplicitamente la coda scalata.
- Il composer del project può rendere il primo messaggio diversamente dal testo inserito (link GitHub come chip, Markdown in paragrafi): la verifica ignora queste differenze di resa, la nuova chat parte con Enter e il testo inviato resta esattamente quello composto.
- Quando una risposta termina, il singolo project entra in `CoolingDown` per 10 minuti prima di ricevere il prompt di continuazione successivo; gli altri thread non vengono fermati e la scadenza resta valida anche dopo un riavvio del servizio. Nel pannello la fine di un cooldown 429 viene mostrata come `In coda dopo la pausa ChatGPT`, non come perdita di connessione; `Connessione persa` resta riservato a retry senza un rate limit.
- Un 429 di ChatGPT, da autopilot o API OpenAI-compatibili, mette in cooldown globale per 15 minuti tutti gli autopilot attivi. Se un altro 429 arriva entro 30 minuti dal precedente, il nuovo cooldown globale dura direttamente 30 minuti. I 429 quasi simultanei dello stesso ciclo vengono unificati senza estendere il cooldown né incrementare due volte i fallimenti. Alla scadenza i project ripartono distribuiti; la distanza parte da 30 secondi (`RateLimitReleaseStaggerSeconds`) e raddoppia con i cicli 429 consecutivi fino a 5 minuti (`RateLimitReleaseStaggerMaxSeconds`). Le tab restano aperte e timestamp e ordine di ripresa sono persistiti.

## Limiti

- Le API web di ChatGPT non sono pubbliche né stabili: percorsi e selettori sono configurabili (`ChatGpt__*`).
- Nessuna risoluzione di challenge anti-bot e nessuna tecnica stealth: se ChatGPT blocca il browser l'errore è mostrato.
- L'automazione di ChatGPT può violare i termini d'uso: valutazione a carico del proprietario.
- Login reale e invio su un account ChatGPT vanno verificati manualmente: i test usano un ChatGPT simulato.
