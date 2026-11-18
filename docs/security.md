# Sicurezza della POC

- La dashboard gira su un'origine pubblica e salva solo nomi progetti, ID estensione e associazioni locali a conversazioni `/c/{id}` in `localStorage`. Non salvare credenziali o risposte ChatGPT.
- Il background verifica `sender.url` e confronta l'origin con un insieme chiuso, rifiutando qualunque mittente sconosciuto. Gli origin ammessi sono visibili nel manifesto e nel codice.
- I metodi che interagiscono con contenuti web limitano l'accesso a URL di ChatGPT, validano l'ID della scheda e la dimensione del prompt.
- La lettura è puntuale e avviene solo su click dell'utente: niente polling o esportazione massiva; visualizzazione in memoria, senza invio a terzi.
- Attenzione: qualsiasi codice JavaScript presente su una Pages pubblica potrebbe inviare messaggi all'estensione se compromesso. Tenere l'origine sotto controllo e ridurre al minimo le permission. Per una release aggiungere ulteriori conferme e revisione della superficie di attacco.
- Electron: sessione locale persistente, niente Node nelle pagine esterne, niente esecuzione di codice remoto privilegiato. Non caricare URL non attendibili senza ulteriori protezioni.
- Il backend F07 protegge le API admin con login a cookie (HttpOnly, SameSite=Strict), cambio password obbligatorio al primo accesso, header anti-CSRF sulle richieste non sicure e rate limiting sul login. Le API `/v1` usano chiavi per endpoint (hash SHA-256, mostrate una volta).
- Cookie e token ChatGPT sono cifrati con Data Protection (chiavi nel volume `api-keys`), non sono mai restituiti dalle API né registrati nei log; il testo delle risposte resta solo in memoria. Pubblicare solo dietro HTTPS e cambiare `POSTGRES_PASSWORD`.
- Nessuna tecnica per aggirare o risolvere challenge anti-bot (Cloudflare, Sentinel, Turnstile): se ChatGPT blocca il browser l'errore è mostrato.
- Le regole dei siti terzi non cambiano in base al fatto che un testo sia salvato o mostrato solo sullo schermo. Valutare il rispetto delle condizioni d'uso prima dell'utilizzo continuativo.


- Le assegnazioni temporanee `tab:{id}` non vengono salvate; la storage key `lllmfacade.assignments.v2` conserva esclusivamente ID conversazione e progetto. Il vecchio storage v1 viene ignorato per evitare collisioni di tab ID.


## Controllo delle dipendenze JavaScript (issue #9)

Il workflow `dependency-audit.yml` registra i risultati di `npm audit` per `src/frontend`, `tests/frontend` e `src/desktop`, distinguendo dipendenze di produzione e sviluppo. Il workflow applica ora un **gate bloccante** sulle dipendenze di produzione della dashboard e dei test e su **tutte** le dipendenze del desktop Electron (che distribuisce l'eseguibile pur dichiarandolo come `devDependency`). Il comando locale è `node script/npm-audit-report.mjs --gate`. Gli advisory residui nelle dipendenze di sviluppo sono segnalati ma non bloccano la CI: restano da correggere o motivare nella issue #9. Mancano ancora lockfile versionati; il solo manifest non rende riproducibile la risoluzione completa delle dipendenze. Gli advisory indicano pacchetti potenzialmente vulnerabili, da analizzare per l'esposizione reale prima di decidere una remediation o eccezione.


Il Vite di sviluppo ascolta solo `127.0.0.1`: non esporre il dev server su reti pubbliche. Il deployment Pages contiene asset compilati e non esegue Vite/Vitest. La versione Electron è stata aggiornata a `45.3.4` in base agli advisory rilevati; resta necessario collaudare l'app reale per sicurezza e compatibilità (issue #5).

## Persistenza locale desktop (issue #11)

Il modulo `src/desktop/workspace.cjs` salva solo `version`, `layout` e 12 URL sanitizzati, in un file locale `workspace.json` sotto `app.getPath('userData')`. La scrittura usa un file temporaneo e rinomina atomica, permessi `0600` sui sistemi POSIX. All'avvio i dati errati, non riconosciuti o oltre 16 KiB sono ignorati con fallback. Sono accettati solo HTTPS e HTTP locale, senza username/password; query e fragment non vengono memorizzati. Non salvare cookie, token, prompt o risposte nel workspace. La partizione Chromium `persist:lllmfacade` resta separata. L'IPC desktop verifica il mittente della finestra locale prima di applicare modifiche. Questa è una misura di riduzione del rischio, non una garanzia di cifratura dei dati: gli URL delle conversazioni possono contenere identificativi privati e chi usa il dispositivo può leggerli. Le prove reali di login e isolamento restano nella issue #5.
