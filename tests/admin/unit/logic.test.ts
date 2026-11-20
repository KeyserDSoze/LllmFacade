import { describe, expect, it } from 'vitest';
import {
  applyImported, countCookies, curlExample, emptyEndpointForm, formatHeaderLines, fromEndpoint,
  importBrowserHeaders, parseHeaderLines, projectStatusLabel, rateLimitState, relativeExpiry,
  sortConversations, statusLabel, toEndpointInput
} from '../../../src/admin/src/logic';
import type { Endpoint } from '../../../src/admin/src/api';

const endpoint: Endpoint = {
  id: 'e1', name: 'Account', models: [{ name: 'chatgpt', upstreamModel: 'auto', defaultReasoningEffort: 'high' }],
  thinkingEfforts: { high: 'extended' }, useTemporaryChat: false, userAgent: 'UA', locale: 'it-IT', timeZone: 'Europe/Rome',
  extraHeaders: { 'Accept-Language': 'it-IT' }, apiKeyPrefix: 'lf_12345678', hasAccessToken: true,
  accessTokenExpiresAt: null, cookieCount: 4, accountEmail: null, refreshIntervalMinutes: 15, lastRefreshAt: null,
  lastRefreshAttemptAt: null, lastError: null, lastErrorAt: null, nextRefreshAt: null, isEnabled: true, createdAt: '2026-11-18T10:00:00Z',
  updatedAt: '2026-11-18T10:00:00Z'
};

describe('header lines', () => {
  it('parses "Name: value" lines and keeps colons inside values', () => {
    expect(parseHeaderLines('Accept-Language: it-IT\n\nsec-ch-ua: "Chromium";v="142"\nX-Url: https://a.b')).toEqual({
      headers: { 'Accept-Language': 'it-IT', 'sec-ch-ua': '"Chromium";v="142"', 'X-Url': 'https://a.b' },
      errors: []
    });
  });

  it('reports malformed lines with their number', () => {
    expect(parseHeaderLines('A: 1\nsenza-due-punti').errors).toEqual(['Riga 2: usare il formato "Nome: valore".']);
  });

  it('formats headers back to lines', () => {
    expect(formatHeaderLines({ A: '1', B: '2' })).toBe('A: 1\nB: 2');
  });
});

describe('browser header import', () => {
  const copied = [
    ':authority: chatgpt.com',
    ':method: GET',
    'accept: */*',
    'accept-language: it-IT,it;q=0.9',
    'authorization: Bearer eyJ.token.firma',
    'cookie: __Secure-next-auth.session-token=abc; oai-did=xyz',
    'referer: https://chatgpt.com/',
    'sec-ch-ua: "Chromium";v="142"',
    'sec-ch-ua-platform: "Windows"',
    'sec-fetch-mode: cors',
    'user-agent: Mozilla/5.0 Test'
  ].join('\n');

  it('moves cookie, token and user agent to their fields and keeps stable browser headers', () => {
    const imported = importBrowserHeaders(copied);

    expect(imported.cookies).toBe('__Secure-next-auth.session-token=abc; oai-did=xyz');
    expect(imported.accessToken).toBe('eyJ.token.firma');
    expect(imported.userAgent).toBe('Mozilla/5.0 Test');
    expect(imported.headers).toEqual({
      'accept-language': 'it-IT,it;q=0.9', 'sec-ch-ua': '"Chromium";v="142"', 'sec-ch-ua-platform': '"Windows"'
    });
    expect(imported.ignored).toEqual([':authority', ':method', 'accept', 'referer', 'sec-fetch-mode']);
  });

  it('reads a bash cURL copy', () => {
    const imported = importBrowserHeaders(
      "curl 'https://chatgpt.com/backend-api/me' -H 'accept-language: en-US' -b 'a=1; b=2' -H 'user-agent: UA curl'");

    expect(imported).toMatchObject({ cookies: 'a=1; b=2', userAgent: 'UA curl', headers: { 'accept-language': 'en-US' } });
  });

  it('fills the form without dropping existing values', () => {
    const form = applyImported(
      { ...emptyEndpointForm('UA vecchio', 'Europe/Rome'), headersText: 'X-Keep: 1', accessToken: 'vecchio' },
      importBrowserHeaders('user-agent: UA nuovo\naccept-language: it-IT'));

    expect(form.userAgent).toBe('UA nuovo');
    expect(form.accessToken).toBe('vecchio');
    expect(form.headersText).toBe('X-Keep: 1\naccept-language: it-IT');
  });
});

describe('cookie preview', () => {
  it('counts header pairs and JSON exports', () => {
    expect(countCookies('Cookie: a=1; b=2;')).toBe(2);
    expect(countCookies('[{"name":"a"},{"name":"b"},{"name":"c"}]')).toBe(3);
    expect(countCookies('{"cookies":[{"name":"a"}],"origins":[]}')).toBe(1);
  });

  it('returns zero for empty or invalid input', () => {
    expect(countCookies('   ')).toBe(0);
    expect(countCookies('{rotto')).toBe(0);
  });
});

describe('endpoint form', () => {
  it('builds the API payload with trimmed values', () => {
    const form = {
      ...emptyEndpointForm('UA', 'Europe/Rome'),
      name: '  Principale ',
      models: [
        { name: ' chatgpt-thinking ', upstreamModel: ' gpt-5-thinking ', defaultReasoningEffort: 'medium' },
        { name: '', upstreamModel: '', defaultReasoningEffort: null }
      ],
      thinking: { medium: ' standard ', high: '' },
      headersText: 'Accept-Language: it-IT',
      cookies: ' a=1 ',
      accessToken: ''
    };

    const { input, errors } = toEndpointInput(form, true);

    expect(errors).toEqual([]);
    expect(input).toMatchObject({
      name: 'Principale',
      models: [{ name: 'chatgpt-thinking', upstreamModel: 'gpt-5-thinking', defaultReasoningEffort: 'medium' }],
      thinkingEfforts: { medium: 'standard' },
      extraHeaders: { 'Accept-Language': 'it-IT' },
      cookies: 'a=1',
      accessToken: null
    });
  });

  it('requires cookies only when creating', () => {
    const form = { ...emptyEndpointForm('UA', 'Europe/Rome'), name: 'X' };

    expect(toEndpointInput(form, true).errors).toContain('Incollare i cookie di ChatGPT.');
    expect(toEndpointInput(form, false).errors).toEqual([]);
    expect(toEndpointInput(form, false).input?.cookies).toBeNull();
  });

  it('rejects missing models, user agent and malformed headers', () => {
    const form = { ...emptyEndpointForm('', 'Europe/Rome'), name: 'X', models: [], headersText: 'rotto', cookies: 'a=1' };

    expect(toEndpointInput(form, true).errors).toEqual([
      'Configurare almeno un modello.',
      'Riga 1: usare il formato "Nome: valore".',
      'Lo user agent è obbligatorio.'
    ]);
  });

  it('prefills the edit form without secrets', () => {
    const form = fromEndpoint(endpoint);

    expect(form).toMatchObject({ name: 'Account', cookies: '', accessToken: '', headersText: 'Accept-Language: it-IT', refreshIntervalMinutes: 15 });
  });
});

describe('project views', () => {
  it('sorts chats chronologically with undated chats last', () => {
    const sorted = sortConversations([
      { id: 'b', title: 'B', createdAt: '2026-11-02T00:00:00Z', updatedAt: null },
      { id: 'x', title: 'X', createdAt: null, updatedAt: null },
      { id: 'a', title: 'A', createdAt: '2026-11-01T00:00:00Z', updatedAt: null }
    ]);

    expect(sorted.map(c => c.id)).toEqual(['a', 'b', 'x']);
  });

  it('labels autopilot states in Italian', () => {
    expect(statusLabel('Generating')).toBe('ChatGPT sta lavorando');
    expect(statusLabel('CoolingDown')).toBe('Pausa del thread prima del prossimo invio');
    expect(statusLabel('WaitingRetry')).toBe('Connessione persa: nuovo tentativo tra poco');
    expect(statusLabel('VerificationRequired')).toBe('Completa la verifica ChatGPT nel browser');
  });

  it('distinguishes ChatGPT cooldown, queued retry and a real connection error', () => {
    const now = Date.parse('2026-11-20T14:00:00Z');
    const base = {
      autopilotEnabled: true,
      batchPaused: false,
      status: 'WaitingRetry' as const,
      rateLimitUntil: '2026-11-20T14:15:00Z',
      nextCheckAt: '2026-11-20T14:15:00Z'
    };

    expect(rateLimitState(base, now)).toBe('cooldown');
    expect(projectStatusLabel(base, now)).toBe('Pausa ChatGPT per troppe richieste');

    const queued = {
      ...base,
      rateLimitUntil: '2026-11-20T13:59:00Z',
      nextCheckAt: '2026-11-20T14:03:00Z'
    };
    expect(rateLimitState(queued, now)).toBe('queued');
    expect(projectStatusLabel(queued, now)).toBe('In coda dopo la pausa ChatGPT');

    const failed = { ...base, rateLimitUntil: null, nextCheckAt: '2026-11-20T14:01:00Z' };
    expect(rateLimitState(failed, now)).toBeNull();
    expect(projectStatusLabel(failed, now)).toBe('Connessione persa: nuovo tentativo tra poco');
  });

  it('describes token expiry', () => {
    const now = new Date('2026-11-18T12:00:00Z');
    expect(relativeExpiry('2026-11-20T12:00:00Z', now)).toBe('scade tra 2 giorni');
    expect(relativeExpiry('2026-11-18T15:00:00Z', now)).toBe('scade tra 3 ore');
    expect(relativeExpiry('2026-11-18T12:20:00Z', now)).toBe('scade tra 20 min');
    expect(relativeExpiry('2026-11-18T11:00:00Z', now)).toBe('scaduto');
    expect(relativeExpiry(null, now)).toBe('scadenza sconosciuta');
  });
});

it('builds a curl example for the OpenAI-compatible endpoint', () => {
  const example = curlExample('http://localhost:8080', 'lf_chiave', 'chatgpt-thinking');

  expect(example).toContain('http://localhost:8080/v1/chat/completions');
  expect(example).toContain('Authorization: Bearer lf_chiave');
  expect(example).toContain('"model":"chatgpt-thinking"');
});
