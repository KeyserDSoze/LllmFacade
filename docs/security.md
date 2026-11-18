# Sicurezza della POC

- La dashboard gira su un'origine pubblica e salva solo nomi progetti, ID estensione e associazioni locali a conversazioni `/c/{id}` in `localStorage`. Non salvare credenziali o risposte ChatGPT.
- Il background verifica `sender.url` e confronta l'origin con un insieme chiuso, rifiutando qualunque mittente sconosciuto. Gli origin ammessi sono visibili nel manifesto e nel codice.
- I metodi che interagiscono con contenuti web limitano l'accesso a URL di ChatGPT, validano l'ID della scheda e la dimensione del prompt.
- La lettura è puntuale e avviene solo su click dell'utente: niente polling o esportazione massiva; visualizzazione in memoria, senza invio a terzi.
- Attenzione: qualsiasi codice JavaScript presente su una Pages pubblica potrebbe inviare messaggi all'estensione se compromesso. Tenere l'origine sotto controllo e ridurre al minimo le permission. Per una release aggiungere ulteriori conferme e revisione della superficie di attacco.
- Electron: sessione locale persistente, niente Node nelle pagine esterne, niente esecuzione di codice remoto privilegiato. Non caricare URL non attendibili senza ulteriori protezioni.
- Il backend demo non implementa autenticazione e non deve essere pubblicato su Internet.
- Le regole dei siti terzi non cambiano in base al fatto che un testo sia salvato o mostrato solo sullo schermo. Valutare il rispetto delle condizioni d'uso prima dell'utilizzo continuativo.


- Le assegnazioni temporanee `tab:{id}` non vengono salvate; la storage key `lllmfacade.assignments.v2` conserva esclusivamente ID conversazione e progetto. Il vecchio storage v1 viene ignorato per evitare collisioni di tab ID.


## Controllo delle dipendenze JavaScript (issue #9)

Il workflow `dependency-audit.yml` registra i risultati di `npm audit` per `src/frontend`, `tests/frontend` e `src/desktop`, distinguendo dipendenze di produzione e sviluppo. Il workflow applica ora un **gate bloccante** sulle dipendenze di produzione della dashboard e dei test e su **tutte** le dipendenze del desktop Electron (che distribuisce l'eseguibile pur dichiarandolo come `devDependency`). Il comando locale è `node script/npm-audit-report.mjs --gate`. Gli advisory residui nelle dipendenze di sviluppo sono segnalati ma non bloccano la CI: restano da correggere o motivare nella issue #9. Mancano ancora lockfile versionati; il solo manifest non rende riproducibile la risoluzione completa delle dipendenze. Gli advisory indicano pacchetti potenzialmente vulnerabili, da analizzare per l'esposizione reale prima di decidere una remediation o eccezione.


Il Vite di sviluppo ascolta solo `127.0.0.1`: non esporre il dev server su reti pubbliche. Il deployment Pages contiene asset compilati e non esegue Vite/Vitest. La versione Electron è stata aggiornata a `45.3.4` in base agli advisory rilevati; resta necessario collaudare l'app reale per sicurezza e compatibilità (issue #5).
