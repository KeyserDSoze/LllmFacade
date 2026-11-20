import { expect, test } from '@playwright/test';
import { FakeBackend, ISSUED_KEY } from './fake-backend';

test('first login forces the password change before any other screen', async ({ page }) => {
  const backend = new FakeBackend();
  await backend.install(page);

  await page.goto('/');
  await page.getByLabel('Nome utente').fill('admin');
  await page.getByLabel('Password', { exact: true }).fill('admin');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Cambia la password' })).toBeVisible();

  await page.getByLabel('Password attuale').fill('admin');
  await page.getByLabel('Nuova password', { exact: true }).fill('corta');
  await page.getByLabel('Conferma nuova password').fill('corta');
  await page.getByRole('button', { name: 'Salva password' }).click();
  await expect(page.getByRole('alert')).toContainText('12 caratteri');

  await page.getByLabel('Nuova password', { exact: true }).fill('Password-Sicura-2026');
  await page.getByLabel('Conferma nuova password').fill('Password-Sicura-2026');
  await page.getByRole('button', { name: 'Salva password' }).click();
  await expect(page.getByRole('heading', { name: 'Endpoint OpenAI compatibili' })).toBeVisible();

  const unsafe = backend.requests.filter(request => request.method !== 'GET');
  expect(unsafe.length).toBeGreaterThan(0);
  expect(unsafe.every(request => request.headers['x-requested-with'] === 'LllmFacade')).toBe(true);
  expect(await page.evaluate(() => Object.keys(window.localStorage))).toEqual([]);
});

test('creates an endpoint from copied browser headers and shows the API key only once', async ({ page }) => {
  const backend = FakeBackend.signedIn();
  await backend.install(page);

  await page.goto('/#/endpoint');
  await page.getByRole('button', { name: 'Nuovo endpoint' }).click();
  await page.getByLabel('Nome endpoint').fill('Account principale');
  await page.getByLabel('Nome esposto 1').fill('chatgpt-thinking');
  await page.getByLabel('Modello ChatGPT 1').fill('gpt-5-thinking');
  await page.getByLabel('Reasoning predefinito 1').selectOption('high');
  await page.getByText('Importa dagli header copiati dal browser').click();
  await page.getByLabel('Header copiati').fill([
    ':authority: chatgpt.com',
    'cookie: __Secure-next-auth.session-token=segreto; oai-did=d1',
    'authorization: Bearer eyJ.abc.def',
    'user-agent: Mozilla/5.0 Copiato',
    'accept-language: it-IT,it;q=0.9',
    'sec-fetch-mode: cors'
  ].join('\n'));
  await page.getByRole('button', { name: 'Importa', exact: true }).click();
  await expect(page.getByText('2 cookie rilevati')).toBeVisible();
  await page.getByRole('button', { name: 'Crea endpoint' }).click();

  const notice = page.getByRole('status', { name: 'Chiave API generata' });
  await expect(notice.getByTestId('api-key')).toHaveText(ISSUED_KEY);
  await expect(notice).toContainText('/v1/chat/completions');
  const created = backend.requests.find(request => request.method === 'POST' && request.path === '/api/endpoints')!.body;
  expect(created).toMatchObject({
    name: 'Account principale',
    userAgent: 'Mozilla/5.0 Copiato',
    accessToken: 'eyJ.abc.def',
    cookies: '__Secure-next-auth.session-token=segreto; oai-did=d1',
    extraHeaders: { 'accept-language': 'it-IT,it;q=0.9' },
    models: [{ name: 'chatgpt-thinking', upstreamModel: 'gpt-5-thinking', defaultReasoningEffort: 'high' }]
  });

  await page.getByRole('button', { name: 'Ho salvato la chiave' }).click();
  await expect(page.getByTestId('api-key')).toHaveCount(0);
  await expect(page.getByRole('listitem', { name: 'Endpoint Account principale' })).toContainText(`${ISSUED_KEY.slice(0, 11)}…`);
  await expect(page.locator('body')).not.toContainText('segreto');
});

test('activates ChatGPT projects, shows chats chronologically and keeps deactivated ones apart', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  await backend.install(page);

  await page.goto('/#/project');
  await page.getByRole('button', { name: 'Attiva Alpha' }).click();
  await expect(page.getByRole('button', { name: 'Disattiva Alpha' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Attiva Beta' })).toBeVisible();

  await page.getByRole('link', { name: 'Project attivi' }).click();
  const card = page.getByRole('article', { name: 'Project Alpha' });
  await expect(card.getByRole('list', { name: 'Chat del project' }).getByRole('listitem')).toHaveText([/Prima chat/, /Seconda chat/]);
  await card.getByRole('button', { name: 'Disattiva' }).click();
  await expect(page.getByRole('article', { name: 'Project Alpha' })).toHaveCount(0);

  await page.getByRole('link', { name: 'Project disattivati' }).click();
  await page.getByRole('listitem', { name: 'Project Alpha' }).getByRole('button', { name: 'Riattiva' }).click();
  await expect(page.getByText('Nessun project disattivato.')).toBeVisible();
  await page.getByRole('link', { name: 'Project attivi' }).click();
  await expect(page.getByRole('article', { name: 'Project Alpha' })).toBeVisible();
});

test('carries on the work with the saved prompt and follows the live progress', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  backend.track('g-p-alpha', 'Alpha');
  await backend.install(page);

  await page.goto('/#/attivi');
  const card = page.getByRole('article', { name: 'Project Alpha' });
  await card.getByRole('button', { name: 'Porta avanti il lavoro' }).click();
  await expect(card.getByRole('alert')).toContainText('prompt di continuazione');

  await card.getByLabel('Prompt di continuazione').fill('Continua dal punto in cui ti sei fermato.');
  await card.getByLabel('Numero massimo di invii (vuoto = senza limite)').fill('5');
  await card.getByLabel('Chat da portare avanti').selectOption({ label: 'Prima chat' });
  await card.getByRole('button', { name: 'Porta avanti il lavoro' }).click();
  await expect(card.getByText(/Autopilot avviato alle/)).toBeVisible();

  await expect(card.getByTestId('autopilot-status')).toHaveText('ChatGPT sta lavorando');
  await expect(card.getByRole('region', { name: 'Avanzamento' })).toContainText('Sto scrivendo il capitolo 3');
  await expect(card.getByRole('region', { name: 'Attività recente' })).toContainText('Autopilot avviato');
  const start = backend.requests.filter(request => request.path.endsWith('/autopilot/start')).at(-1)!;
  expect(start.body).toEqual({
    continuationPrompt: 'Continua dal punto in cui ti sei fermato.', targetConversationId: 'c-1', maxContinuations: 5, newChatPrompt: ''
  });

  await card.getByRole('button', { name: 'Ferma' }).click();
  await expect(card.getByText(/Autopilot fermato alle/)).toBeVisible();
  await expect(card.getByTestId('autopilot-status')).toHaveText('Fermato');
});

test('stops and resumes all only the autopilots selected by the global command', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  const beta = backend.track('g-p-beta', 'Beta');
  backend.track('g-p-gamma', 'Gamma');
  Object.assign(alpha, { autopilotEnabled: true, status: 'Monitoring', continuationPrompt: 'Continua' });
  Object.assign(beta, { autopilotEnabled: true, status: 'Generating', continuationPrompt: 'Vai avanti' });
  await backend.install(page);

  await page.goto('/#/attivi');
  await page.getByRole('button', { name: 'Ferma tutti' }).click();
  await expect(page.getByText('2 autopilot fermati.')).toBeVisible();
  await expect(page.getByRole('article', { name: 'Project Alpha' }).getByTestId('autopilot-status'))
    .toHaveText('In pausa dal comando globale');
  await expect(page.getByRole('button', { name: 'Ferma tutti' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Riprendi tutti' })).toBeEnabled();
  await expect(page.getByRole('article', { name: 'Project Gamma' }).getByRole('button', { name: 'Porta avanti il lavoro' })).toBeVisible();

  await page.getByRole('button', { name: 'Riprendi tutti' }).click();
  await expect(page.getByText('2 autopilot riavviati.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Riprendi tutti' })).toBeDisabled();
  await expect.poll(() => backend.tracked.filter(project => project.autopilotEnabled).map(project => project.name)).toEqual(['Alpha', 'Beta']);
  expect(backend.tracked.find(project => project.name === 'Gamma')?.autopilotEnabled).toBe(false);
  expect(backend.requests.some(request => request.path === '/api/projects/autopilot/stop-all')).toBe(true);
  expect(backend.requests.some(request => request.path === '/api/projects/autopilot/resume-all')).toBe(true);
});

test('resumes stopped projects even when another autopilot is already running', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  const beta = backend.track('g-p-beta', 'Beta');
  const gamma = backend.track('g-p-gamma', 'Gamma');
  Object.assign(alpha, { autopilotEnabled: true, status: 'Generating', continuationPrompt: 'Continua' });
  Object.assign(beta, { autopilotEnabled: false, status: 'Stopped', continuationPrompt: 'Riparti' });
  Object.assign(gamma, { autopilotEnabled: false, status: 'Idle', continuationPrompt: '' });
  await backend.install(page);

  await page.goto('/#/attivi');
  const resume = page.getByRole('button', { name: 'Riprendi tutti' });
  await expect(resume).toBeEnabled();
  await resume.click();

  await expect(page.getByText('1 autopilot riavviato.')).toBeVisible();
  expect(backend.tracked.find(project => project.id === alpha.id)?.autopilotEnabled).toBe(true);
  expect(backend.tracked.find(project => project.id === beta.id)?.autopilotEnabled).toBe(true);
  expect(backend.tracked.find(project => project.id === gamma.id)?.autopilotEnabled).toBe(false);
  await expect(resume).toBeDisabled();
});

test('shows the global ChatGPT rate-limit cooldown on affected project tiles', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  const until = new Date(Date.now() + 15 * 60_000).toISOString();
  Object.assign(alpha, {
    autopilotEnabled: true, status: 'WaitingRetry', rateLimitUntil: until, nextCheckAt: until,
    lastError: 'Too many requests: pausa globale per 15 minuti.'
  });
  await backend.install(page);

  await page.goto('/#/attivi');
  const card = page.getByRole('article', { name: 'Project Alpha' });
  await expect(card.getByTestId('autopilot-status')).toHaveText('Pausa ChatGPT per troppe richieste');
  await expect(card.getByText(/Pausa globale ChatGPT fino alle/)).toBeVisible();
  await expect(card.getByRole('img', { name: 'In attesa' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Riprendi tutti' })).toBeDisabled();
});

test('shows a manual Cloudflare verification instead of a connection error', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  Object.assign(alpha, {
    autopilotEnabled: true,
    continuationPrompt: 'Continua',
    status: 'VerificationRequired',
    rateLimitUntil: null,
    nextCheckAt: new Date(Date.now() + 30 * 60_000).toISOString(),
    lastError: 'ChatGPT richiede una verifica di sicurezza nella pagina.'
  });
  await backend.install(page);

  await page.goto('/#/attivi');
  const card = page.getByRole('article', { name: 'Project Alpha' });
  await expect(card.getByTestId('autopilot-status')).toHaveText('Completa la verifica ChatGPT nel browser');
  await expect(card.getByRole('img', { name: 'In attesa' })).toBeVisible();
  await expect(card.getByText(/Connessione persa/)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Riprendi tutti' })).toBeEnabled();
});

test('shows a completed rate-limit cooldown as queued rather than a connection error', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  Object.assign(alpha, {
    autopilotEnabled: true,
    status: 'WaitingRetry',
    rateLimitUntil: new Date(Date.now() - 60_000).toISOString(),
    nextCheckAt: new Date(Date.now() + 3 * 60_000).toISOString(),
    lastError: 'ChatGPT ha ricevuto un 429: pausa globale per 30 minuti.'
  });
  await backend.install(page);

  await page.goto('/#/attivi');
  const card = page.getByRole('article', { name: 'Project Alpha' });
  await expect(card.getByTestId('autopilot-status')).toHaveText('In coda dopo la pausa ChatGPT');
  await expect(card.getByText(/Pausa terminata: nuovo controllo alle/)).toBeVisible();
  await expect(card.getByRole('img', { name: 'In attesa' })).toBeVisible();
  await expect(card.getByText(/Connessione persa/)).toHaveCount(0);
});

test('gives visible feedback when saving settings succeeds or fails', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  backend.track('g-p-alpha', 'Alpha');
  await backend.install(page);

  await page.goto('/#/attivi');
  const card = page.getByRole('article', { name: 'Project Alpha' });
  const save = card.getByRole('button', { name: 'Salva impostazioni' });
  await expect(save).toBeDisabled();

  await card.getByLabel('Prompt di continuazione').fill('Nuovo prompt');
  await expect(card.getByText('Modifiche non ancora salvate.')).toBeVisible();
  await save.click();
  await expect(card.getByText(/Impostazioni salvate alle/)).toBeVisible();
  await expect(card.getByText('Modifiche non ancora salvate.')).toHaveCount(0);
  await expect(card.getByRole('region', { name: 'Attività recente' })).toContainText('Impostazioni salvate.');
  expect(backend.tracked[0].continuationPrompt).toBe('Nuovo prompt');

  backend.settingsError = 'Database non raggiungibile';
  await card.getByLabel('Prompt di continuazione').fill('Ancora un altro prompt');
  await save.click();
  await expect(card.getByRole('alert')).toContainText('Database non raggiungibile');
  await expect(card.getByText('Modifiche non ancora salvate.')).toBeVisible();
});

test('shows active projects as a mosaic and remembers the chosen grid', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  for (const name of ['Alpha', 'Beta', 'Gamma', 'Delta']) backend.track(`g-p-${name.toLowerCase()}`, name);
  Object.assign(backend.tracked[0], {
    autopilotEnabled: true,
    status: 'WaitingRetry',
    lastError: 'ChatGPT non disponibile: il browser mostra la verifica di sicurezza e il messaggio resta visibile finché non la completi manualmente.'
  });
  await backend.install(page);

  await page.goto('/#/attivi');
  const mosaic = page.getByTestId('mosaic');
  const columns = () => mosaic.evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length);
  await expect(page.getByRole('article')).toHaveCount(4);
  await expect.poll(columns).toBe(2);

  await page.getByRole('radio', { name: '4 per riga' }).click();
  await expect(page.getByRole('radio', { name: '4 per riga' })).toHaveAttribute('aria-checked', 'true');
  await expect.poll(columns).toBe(4);
  const horizontalOverflow = await page.getByRole('article').evaluateAll(tiles => tiles.flatMap(tile => {
    const elements = [tile, ...tile.querySelectorAll<HTMLElement>('*')];
    return elements.filter(element => element.scrollWidth > element.clientWidth + 2
      && (element === tile || getComputedStyle(element).overflowX === 'visible'))
      .map(element => `${element.tagName}.${element.className || ''}:${element.clientWidth}/${element.scrollWidth}`);
  }));
  expect(horizontalOverflow).toEqual([]);
  await page.getByRole('radio', { name: '1 per riga' }).click();
  await expect.poll(columns).toBe(1);

  await page.reload();
  await expect(page.getByRole('radio', { name: '1 per riga' })).toHaveAttribute('aria-checked', 'true');
  await expect.poll(columns).toBe(1);
});

test('collapses tiles into an accordion that keeps the status dot and the last text visible', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  Object.assign(alpha, { autopilotEnabled: true, status: 'Generating', continuationsSent: 1 });
  backend.liveText[alpha.id] = `${'x'.repeat(400)} ...e poi il capitolo 3 è pronto`;
  backend.track('g-p-beta', 'Beta');
  await backend.install(page);

  await page.goto('/#/attivi');
  const working = page.getByRole('article', { name: 'Project Alpha' });
  const idle = page.getByRole('article', { name: 'Project Beta' });
  await expect(working.getByRole('img', { name: 'Sta lavorando' })).toBeVisible();
  await expect(working.getByTestId('live-snippet')).toContainText('il capitolo 3 è pronto');
  await expect(idle.getByRole('img', { name: 'Non in esecuzione' })).toBeVisible();
  await expect(page.getByText('2 project attivi, 1 sta lavorando.')).toBeVisible();

  const header = working.locator('.tile-head button');
  await expect(header).toHaveAttribute('aria-expanded', 'true');
  await header.click();
  await expect(header).toHaveAttribute('aria-expanded', 'false');
  await expect(working.getByRole('button', { name: 'Ferma' })).toHaveCount(0);
  await expect(working.getByRole('img', { name: 'Sta lavorando' })).toBeVisible();
  await expect(working.getByTestId('live-snippet')).toContainText('il capitolo 3 è pronto');
  await expect(idle.getByRole('button', { name: 'Porta avanti il lavoro' })).toBeVisible();

  await page.reload();
  await expect(working.locator('.tile-head button')).toHaveAttribute('aria-expanded', 'false');

  await page.getByRole('button', { name: 'Espandi tutti' }).click();
  await expect(working.getByRole('button', { name: 'Ferma' })).toBeVisible();
  await page.getByRole('button', { name: 'Comprimi tutti' }).click();
  await expect(idle.getByRole('button', { name: 'Porta avanti il lavoro' })).toHaveCount(0);
});

test('lists the activity log with filters and live refresh', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  backend.addLog('Info', 'endpoint', 'Endpoint creato: 1 modelli, 2 cookie.');
  backend.addLog('Warning', 'autopilot', 'Errore (tentativo 1): ChatGPT non raggiungibile. Nuovo tentativo programmato.', alpha);
  backend.addLog('Error', 'sessione', 'Rinnovo sessione non riuscito: Sessione scaduta');
  await backend.install(page);

  await page.goto('/#/log');
  const table = page.getByRole('table', { name: 'Registro attività' });
  await expect(table.getByRole('row')).toHaveCount(4);
  await expect(table.getByRole('row').nth(1)).toContainText('Rinnovo sessione non riuscito');

  await page.getByLabel('Livello minimo').selectOption('error');
  await expect(table.getByRole('row')).toHaveCount(2);
  await page.getByLabel('Livello minimo').selectOption('warning');
  await expect(table.getByRole('row')).toHaveCount(3);
  await page.getByLabel('Livello minimo').selectOption('');

  await page.getByLabel('Categoria').selectOption('endpoint');
  await expect(table.getByRole('row')).toHaveCount(2);
  await page.getByLabel('Categoria').selectOption('');
  await page.getByLabel('Filtra per project').selectOption({ label: 'Alpha' });
  await expect(table.getByRole('row')).toHaveCount(2);
  await expect(table).toContainText('ChatGPT non raggiungibile');
  await page.getByLabel('Filtra per project').selectOption('');

  await page.getByLabel('Cerca nel testo').fill('cookie');
  await expect(table.getByRole('row')).toHaveCount(2);
  await page.getByLabel('Cerca nel testo').fill('');

  backend.addLog('Info', 'autopilot', 'Connessione ripristinata dopo 1 tentativi falliti.', alpha);
  await expect(table).toContainText('Connessione ripristinata', { timeout: 8000 });
});

test('shows in a fixed panel which threads are working and when the token is renewed', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  Object.assign(alpha, { autopilotEnabled: true, status: 'Generating' });
  const beta = backend.track('g-p-beta', 'Beta');
  Object.assign(beta, { autopilotEnabled: true, status: 'Monitoring' });
  backend.apiRequests.push({
    name: 'Account principale', detail: 'Richiesta API chatgpt-thinking (streaming)', since: new Date(Date.now() - 20_000).toISOString()
  });
  const minutes = (value: number) => new Date(Date.now() + value * 60_000).toISOString();
  Object.assign(backend.endpoints[0], {
    hasAccessToken: true, lastRefreshAt: minutes(-12), nextRefreshAt: minutes(18), accessTokenExpiresAt: minutes(60 * 24 * 2)
  });
  await backend.install(page);

  await page.goto('/#/endpoint');
  const dock = page.getByRole('complementary', { name: 'Stato in tempo reale' });
  await expect(dock.getByTestId('dock-headline')).toHaveText('2 thread al lavoro');

  const threads = dock.getByRole('region', { name: 'Thread al lavoro' });
  await expect(threads).toContainText('Alpha');
  await expect(threads).toContainText('ChatGPT sta scrivendo');
  await expect(threads).toContainText('Richiesta API chatgpt-thinking (streaming)');
  await expect(threads).not.toContainText('Beta');
  await expect(threads).toContainText('1 project con autopilot in attesa del prossimo controllo');

  const tokens = dock.getByRole('region', { name: 'Token ChatGPT' });
  await expect(tokens).toContainText('Account principale');
  await expect(tokens).toContainText(/Rinnovato \d{2}:\d{2} \(12 min fa\)/);
  await expect(tokens).toContainText(/Prossimo rinnovo \d{2}:\d{2} \(tra 18 min\)/);
  await expect(tokens).toContainText(/Token scade tra \d+ (ore|giorni)/);

  await dock.getByRole('button').click();
  await expect(dock.getByRole('button')).toHaveAttribute('aria-expanded', 'false');
  await expect(tokens).toHaveCount(0);
  await page.reload();
  await expect(dock.getByTestId('dock-headline')).toHaveText('2 thread al lavoro');
  await expect(dock.getByRole('region', { name: 'Token ChatGPT' })).toHaveCount(0);
});

test('highlights a failed token renewal in the panel', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  Object.assign(backend.endpoints[0], { lastError: 'Sessione scaduta: reinserire i cookie.', nextRefreshAt: new Date(Date.now() + 300_000).toISOString() });
  await backend.install(page);

  await page.goto('/#/attivi');

  const dock = page.getByRole('complementary', { name: 'Stato in tempo reale' });
  await expect(dock.getByTestId('dock-headline')).toHaveText('Nessun thread al lavoro');
  await expect(dock.getByRole('region', { name: 'Token ChatGPT' })).toContainText('Sessione scaduta: reinserire i cookie.');
});

test('lets you choose the first message of the new chat opened when the length limit is reached', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  const alpha = backend.track('g-p-alpha', 'Alpha');
  alpha.chatsCreated = 2;
  await backend.install(page);

  await page.goto('/#/attivi');
  const card = page.getByRole('article', { name: 'Project Alpha' });
  await expect(card.getByText('Nuove chat create')).toBeVisible();
  await expect(card.locator('dt', { hasText: 'Nuove chat create' }).locator('+ dd')).toHaveText('2');

  await card.getByLabel(/Primo messaggio della nuova chat/).fill('Riparti dal capitolo 4.');
  await expect(card.getByText('Modifiche non ancora salvate.')).toBeVisible();
  await card.getByRole('button', { name: 'Salva impostazioni' }).click();

  await expect(card.getByText(/Impostazioni salvate alle/)).toBeVisible();
  const saved = backend.requests.filter(request => request.path.endsWith('/settings')).at(-1)!;
  expect(saved.body).toMatchObject({ newChatPrompt: 'Riparti dal capitolo 4.' });
  expect(backend.tracked[0].newChatPrompt).toBe('Riparti dal capitolo 4.');
});

test('returns to the login when the session expires', async ({ page }) => {
  const backend = FakeBackend.withEndpoint();
  await backend.install(page);

  await page.goto('/#/endpoint');
  await expect(page.getByRole('listitem', { name: 'Endpoint Account principale' })).toBeVisible();
  backend.loggedIn = false;
  await page.getByRole('link', { name: 'Project attivi' }).click();

  await expect(page.getByRole('heading', { name: 'Accesso amministratore' })).toBeVisible();
});
