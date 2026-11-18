import type { Page, Route } from '@playwright/test';

export type Recorded = { method: string; path: string; body: unknown; headers: Record<string, string> };

type Endpoint = Record<string, unknown> & { id: string; name: string };
type Tracked = Record<string, unknown> & {
  id: string; endpointId: string; projectId: string; name: string; isActive: boolean; autopilotEnabled: boolean;
  status: string; continuationPrompt: string; targetConversationId: string | null; maxContinuations: number | null;
  continuationsSent: number;
};

export const ISSUED_KEY = 'lf_chiave-di-test-solo-una-volta';

/** In-memory imitation of the LllmFacade admin API, mounted with page.route (no real backend involved). */
export class FakeBackend {
  loggedIn = false;
  mustChangePassword = true;
  password = 'admin';
  endpoints: Endpoint[] = [];
  tracked: Tracked[] = [];
  requests: Recorded[] = [];
  chatGptProjects = [{ projectId: 'g-p-alpha', name: 'Alpha' }, { projectId: 'g-p-beta', name: 'Beta' }];
  conversations = [
    { id: 'c-2', title: 'Seconda chat', createdAt: '2026-11-10T10:00:00Z', updatedAt: '2026-11-18T10:00:00Z' },
    { id: 'c-1', title: 'Prima chat', createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-02T10:00:00Z' }
  ];

  static signedIn(): FakeBackend {
    const backend = new FakeBackend();
    backend.loggedIn = true;
    backend.mustChangePassword = false;
    return backend;
  }

  static withEndpoint(): FakeBackend {
    const backend = FakeBackend.signedIn();
    backend.endpoints.push(backend.endpointView('e-1', { name: 'Account principale', models: [], cookies: 'a=1' }));
    return backend;
  }

  track(projectId: string, name: string): Tracked {
    const project: Tracked = {
      id: `t-${projectId}`, endpointId: 'e-1', endpointName: 'Account principale', projectId, name, isActive: true,
      continuationPrompt: '', targetConversationId: null, autopilotEnabled: false, maxContinuations: null,
      continuationsSent: 0, status: 'Idle', currentConversationId: null, lastCheckAt: null, nextCheckAt: null,
      lastContinuationAt: null, consecutiveFailures: 0, lastError: null, updatedAt: '2026-11-18T10:00:00Z'
    };
    this.tracked.push(project);
    return project;
  }

  async install(page: Page) {
    await page.route('**/api/**', route => this.handle(route));
  }

  private endpointView(id: string, body: Record<string, unknown>): Endpoint {
    const cookies = typeof body.cookies === 'string' ? body.cookies : '';
    return {
      ...body, id, name: String(body.name), cookies: undefined, accessToken: undefined,
      apiKeyPrefix: ISSUED_KEY.slice(0, 11), hasAccessToken: Boolean(body.accessToken),
      accessTokenExpiresAt: null, cookieCount: cookies.split(';').filter(part => part.includes('=')).length,
      accountEmail: null, refreshIntervalMinutes: 30, lastRefreshAt: null, lastRefreshAttemptAt: null, lastError: null,
      lastErrorAt: null, isEnabled: true, createdAt: '2026-11-18T10:00:00Z', updatedAt: '2026-11-18T10:00:00Z'
    };
  }

  private async handle(route: Route) {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const raw = request.postData();
    const body = raw ? JSON.parse(raw) as Record<string, unknown> : {};
    this.requests.push({ method, path: url.pathname + url.search, body: raw ? body : undefined, headers: request.headers() });
    const json = (status: number, data?: unknown) =>
      route.fulfill({ status, contentType: 'application/json', body: data === undefined ? '' : JSON.stringify(data) });
    const session = () => ({ userName: 'admin', mustChangePassword: this.mustChangePassword });
    const path = url.pathname;

    if (method !== 'GET' && request.headers()['x-requested-with'] !== 'LllmFacade') return json(403, { error: 'CSRF' });
    if (path === '/api/auth/login') {
      if (body.userName !== 'admin' || body.password !== this.password) return json(401, { error: 'Nome utente o password non validi.' });
      this.loggedIn = true;
      return json(200, session());
    }
    if (!this.loggedIn) return json(401);
    if (path === '/api/auth/me') return json(200, session());
    if (path === '/api/auth/logout') {
      this.loggedIn = false;
      return json(204);
    }
    if (path === '/api/auth/change-password') {
      if (body.currentPassword !== this.password) return json(400, { error: 'La password attuale non è corretta.' });
      if (String(body.newPassword).length < 12) return json(400, { error: 'La password deve contenere almeno 12 caratteri.' });
      this.password = String(body.newPassword);
      this.mustChangePassword = false;
      return json(200, session());
    }
    if (this.mustChangePassword) return json(403);

    if (path === '/api/endpoints' && method === 'GET') return json(200, this.endpoints);
    if (path === '/api/endpoints' && method === 'POST') {
      const endpoint = this.endpointView(`e-${this.endpoints.length + 1}`, body);
      this.endpoints.push(endpoint);
      return json(201, { endpoint, apiKey: ISSUED_KEY });
    }
    const projectsOfEndpoint = /^\/api\/endpoints\/([^/]+)\/chatgpt-projects$/.exec(path);
    if (projectsOfEndpoint) {
      return json(200, this.chatGptProjects.map(project => {
        const tracked = this.tracked.find(t => t.projectId === project.projectId);
        return { ...project, trackedId: tracked?.id ?? null, isActive: tracked?.isActive ?? false };
      }));
    }

    if (path === '/api/projects' && method === 'GET') {
      const active = url.searchParams.get('state') === 'active';
      return json(200, this.tracked.filter(project => project.isActive === active));
    }
    if (path === '/api/projects' && method === 'POST') {
      return json(201, this.track(String(body.projectId), String(body.name)));
    }
    const projectAction = /^\/api\/projects\/([^/]+)\/(.+)$/.exec(path);
    const project = projectAction && this.tracked.find(t => t.id === decodeURIComponent(projectAction[1]));
    if (!projectAction || !project) return json(404, { error: 'Non trovato.' });

    switch (projectAction[2]) {
      case 'activate':
        project.isActive = true;
        return json(200, project);
      case 'deactivate':
        Object.assign(project, { isActive: false, autopilotEnabled: false, status: 'Stopped' });
        return json(200, project);
      case 'conversations':
        return json(200, this.conversations);
      case 'settings':
      case 'autopilot/start': {
        const prompt = String(body.continuationPrompt ?? '').trim();
        if (projectAction[2] === 'autopilot/start' && !prompt) return json(400, { error: 'Il prompt di continuazione è obbligatorio.' });
        Object.assign(project, {
          continuationPrompt: prompt, targetConversationId: body.targetConversationId ?? null, maxContinuations: body.maxContinuations ?? null
        });
        if (projectAction[2] === 'autopilot/start') Object.assign(project, { autopilotEnabled: true, status: 'Monitoring' });
        return json(200, project);
      }
      case 'autopilot/stop':
        Object.assign(project, { autopilotEnabled: false, status: 'Stopped' });
        return json(200, project);
      case 'events': {
        // Simulates the worker: once started, the chat is generating and streams text.
        if (project.autopilotEnabled) Object.assign(project, { status: 'Generating', continuationsSent: 1, currentConversationId: 'c-1' });
        const events = [`retry: 300\nevent: status\ndata: ${JSON.stringify(project)}\n\n`];
        if (project.autopilotEnabled) events.push(`event: live\ndata: ${JSON.stringify({ text: 'Sto scrivendo il capitolo 3', reset: true })}\n\n`);
        return route.fulfill({ status: 200, contentType: 'text/event-stream', body: events.join('') });
      }
      default:
        return json(404, { error: 'Non trovato.' });
    }
  }
}
