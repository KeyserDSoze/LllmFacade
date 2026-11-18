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

  await expect(card.getByTestId('autopilot-status')).toHaveText('ChatGPT sta lavorando');
  await expect(card.getByRole('region', { name: 'Avanzamento' })).toContainText('Sto scrivendo il capitolo 3');
  const start = backend.requests.filter(request => request.path.endsWith('/autopilot/start')).at(-1)!;
  expect(start.body).toEqual({ continuationPrompt: 'Continua dal punto in cui ti sei fermato.', targetConversationId: 'c-1', maxContinuations: 5 });

  await card.getByRole('button', { name: 'Ferma' }).click();
  await expect(card.getByTestId('autopilot-status')).toHaveText('Fermato');
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
