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

