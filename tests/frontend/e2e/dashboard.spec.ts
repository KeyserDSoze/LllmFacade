import { expect, test } from '@playwright/test';

const extensionId = 'a'.repeat(32);

test('opens at GitHub Pages base path and preserves projects after refresh', async ({ page }) => {
  await page.goto('/LllmFacade/');
  await expect(page.getByRole('heading', { name: /Un solo spazio/i })).toBeVisible();
  await page.getByPlaceholder('Nuovo progetto...').fill('Project demo');
  await page.getByRole('button', { name: 'Aggiungi progetto' }).click();
  await expect(page.locator('.project-row .project-name', { hasText: 'Project demo' })).toBeVisible();
  await page.reload();
  await expect(page.locator('.project-row .project-name', { hasText: 'Project demo' })).toBeVisible();
  await expect(page.getByText('Collega la tua estensione')).toBeVisible();
});

test('keeps chat projects when tab IDs change and offers links to closed conversations', async ({ page }) => {
  await page.addInitScript(() => {
    const runtime = {
      sendMessage(_id: string, message: { action: string }, callback: (result: unknown) => void) {
        const tabs = window.localStorage.getItem('mock.closed') === 'true' ? [] : [{
          id: Number(window.localStorage.getItem('mock.tabId') || '17'),
          title: 'Test Chat',
          url: 'https://chatgpt.com/c/abc-123',
          active: false,
          windowId: 1
        }];
        if (message.action === 'ping') callback({ ok: true, data: { version: '0.1.0' } });
        else if (message.action === 'listTabs') callback({ ok: true, data: tabs });
        else callback({ ok: false, error: 'Comando non simulato nel test' });
      }
    };
    Object.defineProperty(window, 'chrome', {
      configurable: true, value: { runtime }
    });
  });
  await page.goto('/LllmFacade/');
  await page.getByPlaceholder('Nuovo progetto...').fill('Cliente Alfa');
  await page.getByRole('button', { name: 'Aggiungi progetto' }).click();
  // Starting from a freshly selected project, help the user assign an unassigned chat.
  await page.getByRole('textbox', { name: 'ID estensione Chrome' }).fill('a'.repeat(32));
  await page.getByRole('button', { name: 'Connetti', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Nessuna chat in questo progetto' })).toBeVisible();
  await page.getByRole('button', { name: 'Mostra tutte le chat per assegnarle' }).click();
  await expect(page.locator('.chat-card')).toHaveCount(1);
  await expect(page.getByText('Associazione permanente alla conversazione')).toBeVisible();
  await page.locator('.chat-card select').selectOption({ label: 'Cliente Alfa' });

  await page.evaluate(() => window.localStorage.setItem('mock.tabId', '992'));
  await page.reload();
  await page.getByRole('button', { name: 'Connetti', exact: true }).click();
  await expect(page.locator('.chat-card select option:checked')).toHaveText('Cliente Alfa');

  await page.evaluate(() => window.localStorage.setItem('mock.closed', 'true'));
  await page.getByRole('button', { name: 'Aggiorna', exact: true }).click();
  await page.locator('.project-row .nav-item', { hasText: 'Cliente Alfa' }).click();
  await expect(page.getByText('Conversazioni da riaprire')).toBeVisible();
  const saved = page.locator('.saved-links a');
  await expect(saved).toHaveCount(1);
  await expect(saved).toHaveAttribute('href', 'https://chatgpt.com/c/abc-123');
  const keys = await page.evaluate(() => Object.keys(window.localStorage));
  expect(keys).not.toContain('lllmfacade.responses');
});
