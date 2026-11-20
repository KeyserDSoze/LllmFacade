export type ModelMapping = { name: string; upstreamModel: string; defaultReasoningEffort: string | null };

export type Endpoint = {
  id: string;
  name: string;
  models: ModelMapping[];
  thinkingEfforts: Record<string, string>;
  useTemporaryChat: boolean;
  userAgent: string;
  locale: string;
  timeZone: string;
  extraHeaders: Record<string, string>;
  apiKeyPrefix: string;
  hasAccessToken: boolean;
  accessTokenExpiresAt: string | null;
  cookieCount: number;
  accountEmail: string | null;
  refreshIntervalMinutes: number;
  lastRefreshAt: string | null;
  lastRefreshAttemptAt: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
  nextRefreshAt: string | null;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
};

export type EndpointInput = {
  name: string;
  models: ModelMapping[];
  thinkingEfforts: Record<string, string>;
  useTemporaryChat: boolean;
  userAgent: string;
  locale: string;
  timeZone: string;
  extraHeaders: Record<string, string>;
  cookies: string | null;
  accessToken: string | null;
  refreshIntervalMinutes: number;
  isEnabled: boolean;
};

export type Session = { userName: string; mustChangePassword: boolean };

export type ChatGptProject = { projectId: string; name: string; trackedId: string | null; isActive: boolean };

export type AutopilotStatus = 'Idle' | 'Monitoring' | 'Generating' | 'CoolingDown' | 'Submitting' | 'WaitingRetry' | 'VerificationRequired' | 'Completed' | 'Stopped';

export type TrackedProject = {
  id: string;
  endpointId: string;
  endpointName: string;
  projectId: string;
  name: string;
  isActive: boolean;
  continuationPrompt: string;
  targetConversationId: string | null;
  autopilotEnabled: boolean;
  batchPaused: boolean;
  maxContinuations: number | null;
  continuationsSent: number;
  status: AutopilotStatus;
  currentConversationId: string | null;
  lastCheckAt: string | null;
  nextCheckAt: string | null;
  rateLimitUntil: string | null;
  lastContinuationAt: string | null;
  consecutiveFailures: number;
  lastError: string | null;
  newChatPrompt: string;
  chatsCreated: number;
  updatedAt: string;
};

export type Conversation = { id: string; title: string; createdAt: string | null; updatedAt: string | null };

export type BatchAutopilotResult = { changed: number; projects: TrackedProject[] };

export type ProjectSettings = {
  continuationPrompt: string;
  targetConversationId: string | null;
  maxContinuations: number | null;
  newChatPrompt: string;
};

export type WorkingItem = {
  kind: 'project' | 'api';
  id: string | null;
  name: string;
  endpointName: string;
  detail: string;
  since: string | null;
};

export type EndpointToken = {
  id: string;
  name: string;
  isEnabled: boolean;
  hasAccessToken: boolean;
  accessTokenExpiresAt: string | null;
  lastRefreshAt: string | null;
  nextRefreshAt: string | null;
  lastError: string | null;
};

export type ActivitySummary = { working: WorkingItem[]; watching: number; endpoints: EndpointToken[]; serverTime: string };

export type LogLevel = 'Info' | 'Warning' | 'Error';

export type LogEntry = {
  id: number;
  at: string;
  level: LogLevel;
  category: string;
  message: string;
  endpointId: string | null;
  projectId: string | null;
  subject: string | null;
};

export type LogPage = { items: LogEntry[]; hasMore: boolean };

export type LogQuery = { level?: string; category?: string; projectId?: string; before?: number; take?: number };

export const UNAUTHORIZED_EVENT = 'lllmfacade:unauthorized';

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

export function errorMessage(error: unknown): string {
  return error instanceof ApiError ? error.message : 'Server non raggiungibile.';
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: {
      // Required by the API on unsafe requests: cross-site forms cannot set it (CSRF protection).
      'X-Requested-With': 'LllmFacade',
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' })
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  let data: unknown;
  try {
    data = text ? JSON.parse(text) : undefined;
  } catch {
    data = undefined;
  }
  if (!response.ok) {
    if (response.status === 401 && !path.startsWith('/api/auth/')) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
    const serverMessage = (data as { error?: string } | undefined)?.error;
    throw new ApiError(response.status, serverMessage
      ?? (response.status === 429 ? 'Troppi tentativi: riprovare tra un minuto.' : `Errore ${response.status}.`));
  }
  return data as T;
}

const id = (value: string) => encodeURIComponent(value);

export const api = {
  me: () => request<Session>('GET', '/api/auth/me'),
  login: (userName: string, password: string) => request<Session>('POST', '/api/auth/login', { userName, password }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<Session>('POST', '/api/auth/change-password', { currentPassword, newPassword }),

  endpoints: () => request<Endpoint[]>('GET', '/api/endpoints'),
  createEndpoint: (input: EndpointInput) => request<{ endpoint: Endpoint; apiKey: string }>('POST', '/api/endpoints', input),
  updateEndpoint: (endpointId: string, input: EndpointInput) => request<Endpoint>('PUT', `/api/endpoints/${id(endpointId)}`, input),
  rotateKey: (endpointId: string) => request<{ apiKey: string }>('POST', `/api/endpoints/${id(endpointId)}/api-key`),
  refreshEndpoint: (endpointId: string) => request<Endpoint>('POST', `/api/endpoints/${id(endpointId)}/refresh`),
  deleteEndpoint: (endpointId: string) => request<void>('DELETE', `/api/endpoints/${id(endpointId)}`),

  chatGptProjects: (endpointId: string) => request<ChatGptProject[]>('GET', `/api/endpoints/${id(endpointId)}/chatgpt-projects`),
  projects: (state: 'active' | 'inactive') => request<TrackedProject[]>('GET', `/api/projects?state=${state}`),
  trackProject: (endpointId: string, projectId: string, name: string) =>
    request<TrackedProject>('POST', '/api/projects', { endpointId, projectId, name }),
  activate: (projectId: string) => request<TrackedProject>('POST', `/api/projects/${id(projectId)}/activate`),
  deactivate: (projectId: string) => request<TrackedProject>('POST', `/api/projects/${id(projectId)}/deactivate`),
  saveSettings: (projectId: string, settings: ProjectSettings) =>
    request<TrackedProject>('PUT', `/api/projects/${id(projectId)}/settings`, settings),
  startAutopilot: (projectId: string, settings: ProjectSettings) =>
    request<TrackedProject>('POST', `/api/projects/${id(projectId)}/autopilot/start`, settings),
  stopAutopilot: (projectId: string) => request<TrackedProject>('POST', `/api/projects/${id(projectId)}/autopilot/stop`),
  stopAllAutopilot: () => request<BatchAutopilotResult>('POST', '/api/projects/autopilot/stop-all'),
  resumeAllAutopilot: () => request<BatchAutopilotResult>('POST', '/api/projects/autopilot/resume-all'),
  conversations: (projectId: string) => request<Conversation[]>('GET', `/api/projects/${id(projectId)}/conversations`),
  eventsUrl: '/api/projects/events',
  summary: () => request<ActivitySummary>('GET', '/api/activity/summary'),
  logs: (query: LogQuery = {}) => {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query)) if (value !== undefined && value !== '') params.set(key, String(value));
    const text = params.toString();
    return request<LogPage>('GET', `/api/logs${text ? `?${text}` : ''}`);
  }
};
