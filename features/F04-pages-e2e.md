# F04 — Test browser della dashboard e GitHub Pages (issue #6)

## Criteri

- La dashboard React deve caricare sul path GitHub Pages `/LllmFacade/` senza risorse mancanti.
- Il progetto aggiunto dall'utente deve sopravvivere a un refresh del browser.
- I test E2E devono simulare un'estensione Chrome minimamente, non richiedere credenziali né ChatGPT reale.
- Il progetto associato a `/c/{id}` deve restare corretto dopo un cambio di tab ID e comparire fra le conversazioni riapribili alla chiusura della scheda.
- Playwright Chromium deve girare in CI e fallire se il comportamento regredisce.
- Il deploy reale Pages rimane aperto fino a quando `Settings → Pages → GitHub Actions` non viene attivato e l'URL pubblico verificato; vedi `docs/local-development.md`.

## Limiti

Gli E2E qui non verificano il DOM remoto, l'autenticazione a ChatGPT o l'estensione realmente installata. Questi scenari sono nella issue #4.
