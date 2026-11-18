# ADR 0002 — Bridge esplicito e isolato fra dashboard ed estensione

- Stato: Accepted (POC sperimentale)
- Contesto: una pagina GitHub Pages non può usare direttamente `chrome.tabs` né può incorniciare ChatGPT in un normale iframe.
- Decisione: estensione Manifest V3 con `externally_connectable` limitato all'origine GitHub Pages e ai localhost di sviluppo; la web app invia messaggi JSON tramite ID dell'estensione. Il service worker convalida mittente, azione, parametri e destinazione.
- Funzioni: `ping`, `listTabs`, `openChat`, `activateTab`. `sendPrompt` e `readLastReply` sono un esperimento manuale limitato a `chatgpt.com`, non una API affidabile.
- Dati: i progetti e le associazioni temporanee si salvano nel browser; le risposte restano solo nello stato React. Nessuna estrazione automatica in background.
- Limiti: i selettori del compositore e dei messaggi possono cambiare; nuove sessioni o ricaricamenti possono cambiare gli ID delle schede; Chrome su altri browser richiede test dedicati; termini d'uso dei servizi terzi da rispettare.

