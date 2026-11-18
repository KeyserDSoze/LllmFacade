# Architettura

## Flusso web ↔ estensione

```text
GitHub Pages (React)
   | chrome.runtime.sendMessage(extensionId, request)
   v
Estensione Chrome MV3 (onMessageExternal)
   | controllo origin/azione/tab/URL
   +---- chrome.tabs (elenco, apertura, attivazione)
   +---- chrome.scripting (POC: prompt/ultima risposta su ChatGPT)
```

L'estensione non è un server HTTP, non espone porte ed evita CORS tra la Pages e ChatGPT. La web app deve conoscere l'ID dell'estensione locale (diverso per installazione). Non esiste una sessione condivisa fra browser desktop Electron e Chrome.

## Desktop

Electron ospita un controllo locale e fino a dodici WebContentsView, ciascuna una vera pagina navigabile. Tutte le view usano la stessa partizione persistente Electron: login e cookie possono essere condivisi **tra riquadri Electron**, se il provider consente l'autenticazione in quel browser. Non importiamo cookie del browser Chrome. Le preferenze desktop (layout e indirizzi dei 12 riquadri) sono un file JSON versionato e validato in `app.getPath('userData')/workspace.json`, scritto atomicamente con permessi locali; non contengono cookie, prompt o risposte (ADR 0005). La sessione persistente Chromium è separata da questo file.

## Backend

ASP.NET Core separa Applications (API, worker, SPA admin statica), Business (regole pure e porta `IChatGptClient`), Domain (entità) e Infrastructures (`Postgres` con EF Core, `Browser` con Playwright/Chromium, `Memory` del vecchio esempio non più usato dall'API). Il backend F07 espone API OpenAI compatibili su ChatGPT tramite un browser headless reale e automatizza i project: vedi `docs/backend-chatgpt.md`. Non sincronizza i dati della Pages.

## Roadmap

1. Convalidare messaggistica e UI su Chrome reale, inclusa la pagina ChatGPT.
2. Verificare e rifinire griglia Electron e autenticazione.
3. Associazione stabile delle chat tramite URL `/c/{id}` (ADR 0003, issue #3): le altre pagine restano temporanee; i link salvati sono riapribili anche senza scheda aperta.
4. Progettare backend autenticato, sincronizzazione tra device e adapter per provider diversi.


