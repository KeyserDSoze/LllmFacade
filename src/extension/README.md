# Estensione Chrome LllmFacade

Caricare la cartella con `chrome://extensions` → Modalità sviluppatore → Carica estensione non pacchettizzata. Copiare l'ID nella dashboard Pages.

Il bridge riceve messaggi solo da `https://keyserdsoze.github.io` o dalle origini locali di sviluppo (porta 5173). Le operazioni di scripting riguardano esclusivamente tab su `chatgpt.com` o `chat.openai.com`.

Comandi: `ping`, `listTabs`, `openChat`, `activateTab`, `sendPrompt`, `readLastReply`.

I due ultimi comandi sono **sperimentali** e dipendono dai selettori DOM attuali. Non interrogano un'API ufficiale né archiviano le risposte. Dopo una modifica alla UI ChatGPT può essere necessario aggiornarli; il test su ChatGPT reale è manuale.

Non pubblicare estensioni con origin aggiuntivi o permessi nuovi senza revisione di sicurezza.

