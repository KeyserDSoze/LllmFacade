# F01 — Dashboard + estensione Chrome

## Criteri di accettazione

- La dashboard su Pages comunica con una sola estensione Chrome nota tramite il suo ID e mostra errore se assente.
- L'estensione respinge origine sconosciuta, comando sconosciuto, URL non ChatGPT e tab ID errato.
- L'utente può elencare, aprire, portare in primo piano le chat.
- La POC offre invio di prompt e lettura dell'ultima risposta su click, senza persistenza; errori espliciti se il DOM non è compatibile.
- Progetti creati/rinominabili (per la POC aggiunti e rimovibili); associazione a una scheda locale e ripristino della lista dopo refresh.
- I test coprono validazione dei messaggi e regole dei progetti; la UI ChatGPT reale è un test manuale non sostituibile con unit test.


## Associazione progetti (issue #3)

- I link delle conversazioni `/c/{id}` mantengono l'associazione dopo riavvio o apertura su un nuovo tab ID.
- La home e i link non riconosciuti usano un'associazione temporanea, promossa dopo un aggiornamento della scheda con URL di conversazione.
- Il progetto mostra link riapribili anche quando la conversazione non è fra le tab aperte.
- Nessuna risposta viene archiviata automaticamente; vedi ADR 0003.


## Onboarding progetti (issue #10)

- Quando il progetto selezionato non contiene ancora chat, distinguere il caso di nessuna scheda Chrome da schede presenti ma non assegnate.
- Se esistono schede ChatGPT aperte, mostrare un'azione esplicita per aprire la vista generale e assegnarne una.
- Il comportamento è coperto dal test browser Playwright su base path GitHub Pages; non richiede estensione reale.
