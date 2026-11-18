# F01 — Dashboard + estensione Chrome

## Criteri di accettazione

- La dashboard su Pages comunica con una sola estensione Chrome nota tramite il suo ID e mostra errore se assente.
- L'estensione respinge origine sconosciuta, comando sconosciuto, URL non ChatGPT e tab ID errato.
- L'utente può elencare, aprire, portare in primo piano le chat.
- La POC offre invio di prompt e lettura dell'ultima risposta su click, senza persistenza; errori espliciti se il DOM non è compatibile.
- Progetti creati/rinominabili (per la POC aggiunti e rimovibili); associazione a una scheda locale e ripristino della lista dopo refresh.
- I test coprono validazione dei messaggi e regole dei progetti; la UI ChatGPT reale è un test manuale non sostituibile con unit test.

