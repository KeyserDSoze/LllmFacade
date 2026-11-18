import { expect, test } from '@playwright/test';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const desktopRenderer = pathToFileURL(
  resolve(process.cwd(), '../../src/desktop/renderer/index.html')
).href;

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    const urls = Array(12).fill('https://chatgpt.com/');
    urls[5] = 'https://chatgpt.com/c/remember-me';
    const state = window as unknown as {
      desktop: Record<string, unknown>;
      __layoutChanges: number[];
      __urlChange: ((event: { id: number; url: string }) => void) | undefined;
      __navigations: string[];
    };
    state.__layoutChanges = [];
    state.__navigations = [];
    state.desktop = {
      workspace: async () => ({ version: 1, layout: 6, urls }),
      setLayout: async (layout: number) => {
        state.__layoutChanges.push(layout);
        return layout;
      },
      layout: async () => undefined,
      navigate: async (_id: number, raw: string) => {
        state.__navigations.push(raw);
        const url = new URL(raw);
        url.search = '';
        url.hash = '';
        return url.href;
      },
      reload: async () => undefined,
      external: async () => undefined,
      onUrlChanged: (handler: (event: { id: number; url: string }) => void) => {
        state.__urlChange = handler;
        return () => { state.__urlChange = undefined; };
      }
    };
  });
});

test('desktop restores its six-pane layout and retains hidden pane URLs', async ({ page }) => {
  await page.goto(desktopRenderer);
  await expect(page.locator('#count')).toHaveValue('6');
  await expect(page.locator('.tile')).toHaveCount(6);
  await expect(page.locator('.tile[data-id="5"] input')).toHaveValue(
    'https://chatgpt.com/c/remember-me'
  );
  await page.locator('#count').selectOption('1');
  await expect(page.locator('.tile')).toHaveCount(1);
  await page.locator('#count').selectOption('12');
  await expect(page.locator('.tile')).toHaveCount(12);
  await expect(page.locator('.tile[data-id="5"] input')).toHaveValue(
    'https://chatgpt.com/c/remember-me'
  );
  const changes = await page.evaluate(() =>
    (window as unknown as { __layoutChanges: number[] }).__layoutChanges
  );
  expect(changes).toEqual([1, 12]);
});

test('desktop navigation updates the saved input and receives URL updates', async ({ page }) => {
  await page.goto(desktopRenderer);
  const first = page.locator('.tile[data-id="0"] input');
  await first.fill('https://chatgpt.com/c/one?temporary=secret#fragment');
  await first.press('Enter');
  await expect(first).toHaveValue('https://chatgpt.com/c/one');
  const navigations = await page.evaluate(() =>
    (window as unknown as { __navigations: string[] }).__navigations
  );
  expect(navigations).toEqual(['https://chatgpt.com/c/one?temporary=secret#fragment']);
  await page.evaluate(() => {
    const { __urlChange } = window as unknown as {
      __urlChange?: (event: { id: number; url: string }) => void
    };
    __urlChange?.({ id: 0, url: 'https://chatgpt.com/c/two' });
  });
  await expect(first).toHaveValue('https://chatgpt.com/c/two');
});
