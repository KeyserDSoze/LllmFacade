# ADR 0003 — Identità stabile delle conversazioni, senza riutilizzare tab ID

- Stato: Accepted (POC, issue #3)
- Problema: gli ID di `chrome.tabs` cambiano quando una scheda viene chiusa e possono essere riutilizzati. Salvare un'associazione permanente con quell'ID può assegnare una chat sbagliata a un progetto.
- Decisione: identificare in modo persistente solo le conversazioni con URL canonico `https://chatgpt.com/c/{id}` o l'alias `https://chat.openai.com/c/{id}`. La chiave logica è `chatgpt:c:{id}`, indipendente dal tab ID e dai parametri URL.
- Home `/`, link condivisi `/share/`, pagine di custom GPT `/g/` e URL sconosciuti non sono considerati identificativi stabili. L'eventuale associazione iniziale è soltanto temporanea (`tab:{id}`) e vive in memoria.
- Quando la home diventa una chat `/c/{id}`, al successivo aggiornamento della lista tab l'associazione temporanea viene convertita in chiave stabile. Una chiave già associata ha precedenza sul dato temporaneo.
- Persistence: `localStorage` `lllmfacade.assignments.v2` conserva solo chiavi canoniche e ID dei progetti. `v1` con i vecchi ID numerici **non viene migrata**: dopo un riavvio non è possibile verificare a quale conversazione appartenesse e riutilizzarla creerebbe associazioni sbagliate.
- La UI offre link per riaprire le conversazioni persistite, anche a schede chiuse. La prima POC non sincronizza questi link tra dispositivi e **non** archivia risposte, cookie, prompt o credenziali.
- Conseguenze: i collegamenti ad alcune conversazioni personalizzate restano temporanei finché non è definito un adapter appropriato. Aggiornare test e documentazione quando si amplia il riconoscimento.

