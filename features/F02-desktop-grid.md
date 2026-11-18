# F02 — Browser desktop Chromium multivista

## Criteri di accettazione

- Una finestra Electron con 1, 4, 6 o 12 view reali, ridisposte al resize.
- Ogni view può navigare in una conversazione distinta. Le view condividono la sessione Electron, non quella Chrome.
- `nodeIntegration` disattivato, `contextIsolation` e sandbox attivi.
- La UI permette di ricaricare e mettere in evidenza un riquadro.
- Login su ChatGPT da verificare manualmente; autenticazione di servizi terzi non garantita.


## Persistenza del workspace (issue #11)

- Il numero di riquadri (1/4/6/12) viene ripristinato dopo il riavvio.
- Gli URL di tutti i 12 riquadri vengono conservati anche quando alcuni sono nascosti.
- Il file di configurazione non contiene credenziali, token nei query string, cookie o messaggi.
- File malformato, versione non supportata e URL non sicuri non impediscono l'avvio e ricadono sui valori predefiniti.
- La lettura/scrittura del modello è testata automaticamente con Node; la prova Electron reale e login restano da eseguire e non sono implicate da questi test.
