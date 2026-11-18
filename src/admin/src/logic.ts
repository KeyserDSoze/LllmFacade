import type { AutopilotStatus, Conversation, Endpoint, EndpointInput, ModelMapping } from './api';

export const reasoningEfforts = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh'] as const;

/** Starting point for the ChatGPT thinking values: editable, ChatGPT may rename them. */
export const suggestedThinking: Record<string, string> = { low: 'min', medium: 'standard', high: 'extended', xhigh: 'max' };

export type EndpointForm = {
  name: string;
  models: ModelMapping[];
  thinking: Record<string, string>;
  useTemporaryChat: boolean;
  userAgent: string;
  locale: string;
  timeZone: string;
  headersText: string;
  cookies: string;
  accessToken: string;
  refreshIntervalMinutes: number;
  isEnabled: boolean;
};

export type ImportedHeaders = {
  userAgent?: string;
  cookies?: string;
  accessToken?: string;
  headers: Record<string, string>;
  ignored: string[];
};

// Request-specific headers (sec-fetch-*, referer, accept...) would be wrong if sent on every request.
const stableBrowserHeader = /^(accept-language|dnt|sec-gpc|sec-ch-[a-z0-9-]+)$/;

export function emptyEndpointForm(userAgent: string, timeZone: string): EndpointForm {
  return {
    name: '',
    models: [{ name: 'chatgpt', upstreamModel: 'auto', defaultReasoningEffort: null }],
    thinking: { ...suggestedThinking },
    useTemporaryChat: true,
    userAgent,
    locale: 'it-IT',
    timeZone: timeZone || 'Europe/Rome',
    headersText: '',
    cookies: '',
    accessToken: '',
    refreshIntervalMinutes: 30,
    isEnabled: true
  };
}

export function fromEndpoint(endpoint: Endpoint): EndpointForm {
  return {
    name: endpoint.name,
    models: endpoint.models.map(model => ({ ...model })),
    thinking: { ...endpoint.thinkingEfforts },
    useTemporaryChat: endpoint.useTemporaryChat,
    userAgent: endpoint.userAgent,
    locale: endpoint.locale,
    timeZone: endpoint.timeZone,
    headersText: formatHeaderLines(endpoint.extraHeaders),
    cookies: '',
    accessToken: '',
    refreshIntervalMinutes: endpoint.refreshIntervalMinutes,
    isEnabled: endpoint.isEnabled
  };
}

export function parseHeaderLines(text: string): { headers: Record<string, string>; errors: string[] } {
  const headers: Record<string, string> = {};
  const errors: string[] = [];
  text.split(/\r?\n/).forEach((raw, index) => {
    const line = raw.trim();
    if (!line) return;
    const colon = line.indexOf(':');
    if (colon <= 0) {
      errors.push(`Riga ${index + 1}: usare il formato "Nome: valore".`);
      return;
    }
    headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  });
  return { headers, errors };
}

export function formatHeaderLines(headers: Record<string, string>): string {
  return Object.entries(headers).map(([name, value]) => `${name}: ${value}`).join('\n');
}

/** Reads request headers copied from the browser DevTools (raw lines or bash cURL). */
export function importBrowserHeaders(text: string): ImportedHeaders {
  const result: ImportedHeaders = { headers: {}, ignored: [] };
  const lines: string[] = [];
  if (/^\s*curl\s/i.test(text)) {
    for (const match of text.matchAll(/(-H|--header|-b|--cookie)\s+(['"])(.*?)\2/g)) {
      lines.push(match[1] === '-b' || match[1] === '--cookie' ? `cookie: ${match[3]}` : match[3]);
    }
  } else {
    lines.push(...text.split(/\r?\n/));
  }

  for (const raw of lines) {
    const match = /^(:?[^:\s][^:]*?)\s*:\s*(.*)$/.exec(raw.trim());
    if (!match) continue;
    const name = match[1].trim().toLowerCase();
    const value = match[2].trim();
    if (name === 'cookie') result.cookies = value;
    else if (name === 'authorization') result.accessToken = value.replace(/^bearer\s+/i, '');
    else if (name === 'user-agent') result.userAgent = value;
    else if (stableBrowserHeader.test(name)) result.headers[name] = value;
    else result.ignored.push(name);
  }
  return result;
}

export function applyImported(form: EndpointForm, imported: ImportedHeaders): EndpointForm {
  return {
    ...form,
    userAgent: imported.userAgent ?? form.userAgent,
    cookies: imported.cookies ?? form.cookies,
    accessToken: imported.accessToken ?? form.accessToken,
    headersText: formatHeaderLines({ ...parseHeaderLines(form.headersText).headers, ...imported.headers })
  };
}

export function countCookies(input: string): number {
  const text = input.trim();
  if (!text) return 0;
  if (text.startsWith('[') || text.startsWith('{')) {
    try {
      const parsed = JSON.parse(text) as unknown;
      const list = Array.isArray(parsed) ? parsed : (parsed as { cookies?: unknown } | null)?.cookies;
      return Array.isArray(list) ? list.length : 0;
    } catch {
      return 0;
    }
  }
  return text.replace(/^cookie:/i, '').split(';').filter(part => part.includes('=')).length;
}

export function toEndpointInput(form: EndpointForm, creating: boolean): { input?: EndpointInput; errors: string[] } {
  const errors: string[] = [];
  const name = form.name.trim();
  if (!name) errors.push('Il nome è obbligatorio.');
  const models = form.models
    .map(model => ({
      name: model.name.trim(),
      upstreamModel: model.upstreamModel.trim(),
      defaultReasoningEffort: model.defaultReasoningEffort || null
    }))
    .filter(model => model.name || model.upstreamModel);
  if (models.length === 0) errors.push('Configurare almeno un modello.');
  if (models.some(model => !model.name || !model.upstreamModel)) errors.push('Ogni modello richiede nome esposto e modello ChatGPT.');
  const { headers, errors: headerErrors } = parseHeaderLines(form.headersText);
  errors.push(...headerErrors);
  const userAgent = form.userAgent.trim();
  if (!userAgent) errors.push('Lo user agent è obbligatorio.');
  const cookies = form.cookies.trim();
  if (creating && !cookies) errors.push('Incollare i cookie di ChatGPT.');
  if (errors.length > 0) return { errors };

  const thinkingEfforts = Object.fromEntries(
    Object.entries(form.thinking).map(([effort, value]) => [effort, value.trim()]).filter(([, value]) => value));
  return {
    errors,
    input: {
      name,
      models,
      thinkingEfforts,
      useTemporaryChat: form.useTemporaryChat,
      userAgent,
      locale: form.locale.trim(),
      timeZone: form.timeZone.trim(),
      extraHeaders: headers,
      cookies: cookies || null,
      accessToken: form.accessToken.trim() || null,
      refreshIntervalMinutes: form.refreshIntervalMinutes,
      isEnabled: form.isEnabled
    }
  };
}

export function sortConversations(conversations: Conversation[]): Conversation[] {
  const time = (value: string | null) => (value ? Date.parse(value) : Number.POSITIVE_INFINITY);
  return [...conversations].sort((a, b) => time(a.createdAt) - time(b.createdAt) || a.title.localeCompare(b.title));
}

const statusLabels: Record<AutopilotStatus, string> = {
  Idle: 'Inattivo',
  Monitoring: 'In osservazione',
  Generating: 'ChatGPT sta lavorando',
  Submitting: 'Invio del prompt in corso',
  WaitingRetry: 'Connessione persa: nuovo tentativo tra poco',
  Completed: 'Completato',
  Stopped: 'Fermato'
};

export function statusLabel(status: AutopilotStatus): string {
  return statusLabels[status] ?? status;
}

export function formatDateTime(value: string | null): string {
  return value ? new Date(value).toLocaleString('it-IT') : '—';
}

export function relativeExpiry(value: string | null, now: Date): string {
  if (!value) return 'scadenza sconosciuta';
  const milliseconds = Date.parse(value) - now.getTime();
  if (milliseconds <= 0) return 'scaduto';
  const minutes = Math.floor(milliseconds / 60_000);
  if (minutes < 60) return `scade tra ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `scade tra ${hours} ore`;
  return `scade tra ${Math.floor(hours / 24)} giorni`;
}

export function curlExample(origin: string, apiKey: string, model: string): string {
  return [
    `curl ${origin}/v1/chat/completions \\`,
    `  -H "Authorization: Bearer ${apiKey}" \\`,
    '  -H "Content-Type: application/json" \\',
    `  -d '${JSON.stringify({ model, messages: [{ role: 'user', content: 'Ciao' }] })}'`
  ].join('\n');
}
