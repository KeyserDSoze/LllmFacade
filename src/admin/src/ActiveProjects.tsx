import { useCallback, useEffect, useId, useState } from 'react';
import {
  api, errorMessage, type Conversation, type LogEntry, type ProjectSettings, type TrackedProject
} from './api';
import {
  dotKind, dotLabel, formatDateTime, gridColumns, isDirty, levelLabels, parseGrid, parseIdList,
  sortConversations, statusLabel, tailSnippet, validateStart, type GridChoice
} from './logic';

const GRID_KEY = 'lllmfacade.admin.grid';
const COLLAPSED_KEY = 'lllmfacade.admin.collapsed';
const gridChoices: GridChoice[] = [1, 2, 4];

type Notice = { kind: 'ok' | 'error'; text: string } | null;

function store(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Layout preferences only: ignore storage being unavailable.
  }
}

export function ActiveProjectsView() {
  const [projects, setProjects] = useState<TrackedProject[] | null>(null);
  const [live, setLive] = useState<Record<string, string>>({});
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState('');
  const [grid, setGrid] = useState<GridChoice>(() => parseGrid(window.localStorage.getItem(GRID_KEY)));
  const [collapsed, setCollapsed] = useState<string[]>(() => parseIdList(window.localStorage.getItem(COLLAPSED_KEY)));

  useEffect(() => {
    api.projects('active').then(setProjects).catch(failure => setError(errorMessage(failure)));
  }, []);

  // One stream for every tile: browsers allow only a few concurrent SSE connections per origin.
  useEffect(() => {
    const source = new EventSource(api.eventsUrl);
    source.onopen = () => setConnected(true);
    source.onerror = () => setConnected(false);
    source.addEventListener('status', event => {
      const next = JSON.parse((event as MessageEvent<string>).data) as TrackedProject;
      setProjects(current => {
        const rest = (current ?? []).filter(project => project.id !== next.id);
        return next.isActive ? [...rest, next].sort((a, b) => a.name.localeCompare(b.name)) : rest;
      });
    });
    source.addEventListener('live', event => {
      const data = JSON.parse((event as MessageEvent<string>).data) as { projectId: string; text: string; reset: boolean };
      setLive(current => ({ ...current, [data.projectId]: data.reset ? data.text : (current[data.projectId] ?? '') + data.text }));
    });
    return () => source.close();
  }, []);

  const chooseGrid = (choice: GridChoice) => {
    setGrid(choice);
    store(GRID_KEY, String(choice));
  };

  const updateCollapsed = (next: string[]) => {
    setCollapsed(next);
    store(COLLAPSED_KEY, JSON.stringify(next));
  };

  const toggle = (id: string) => updateCollapsed(collapsed.includes(id) ? collapsed.filter(item => item !== id) : [...collapsed, id]);
  const working = projects?.filter(project => dotKind(project) === 'working').length ?? 0;

  return (
    <section>
      <div className="section-head">
        <h1>Project attivi</h1>
        <div className="toolbar">
          <span className={connected ? 'live-state online' : 'live-state'} role="status">
            {connected ? 'Aggiornamenti live attivi' : 'Riconnessione in corso…'}
          </span>
          <div role="radiogroup" aria-label="Griglia dei project" className="segmented">
            {gridChoices.map(choice => (
              <button key={choice} type="button" role="radio" aria-checked={grid === choice}
                className={grid === choice ? 'selected' : undefined} onClick={() => chooseGrid(choice)}>
                {choice} per riga
              </button>
            ))}
          </div>
          <button type="button" onClick={() => updateCollapsed((projects ?? []).map(project => project.id))}>Comprimi tutti</button>
          <button type="button" onClick={() => updateCollapsed([])}>Espandi tutti</button>
        </div>
      </div>
      {projects && projects.length > 0 && (
        <p className="hint">{projects.length} project attivi, {working} {working === 1 ? 'sta lavorando' : 'stanno lavorando'}.</p>
      )}
      {error && <p role="alert" className="error">{error}</p>}
      {projects?.length === 0 && <p className="empty">Nessun project attivo: attivane uno da “Tutti i project”.</p>}
      <div className="mosaic" data-testid="mosaic" style={{ gridTemplateColumns: gridColumns(grid) }}>
        {projects?.map(project => (
          <ProjectTile key={project.id} project={project} liveText={live[project.id] ?? ''}
            collapsed={collapsed.includes(project.id)} onToggle={() => toggle(project.id)}
            onChange={next => setProjects(current => (current ?? [])
              .map(item => (item.id === next.id ? next : item))
              .filter(item => item.isActive))} />
        ))}
      </div>
    </section>
  );
}

function ProjectTile({ project, liveText, collapsed, onToggle, onChange }: {
  project: TrackedProject;
  liveText: string;
  collapsed: boolean;
  onToggle: () => void;
  onChange: (project: TrackedProject) => void;
}) {
  const bodyId = useId();
  const kind = dotKind(project);
  const snippet = tailSnippet(liveText, 160);
  const [prompt, setPrompt] = useState(project.continuationPrompt);
  const [target, setTarget] = useState(project.targetConversationId ?? '');
  const [max, setMax] = useState(project.maxContinuations?.toString() ?? '');
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice>(null);
  const form = { prompt, target, max };
  const dirty = isDirty(project, form);

  const settings = (): ProjectSettings => ({
    continuationPrompt: prompt,
    targetConversationId: target || null,
    maxContinuations: max.trim() ? Number(max) : null
  });

  const run = async (label: string, action: () => Promise<TrackedProject>, success: (next: TrackedProject) => string, validate?: string | null) => {
    if (validate) {
      setNotice({ kind: 'error', text: validate });
      return;
    }
    setBusy(label);
    setNotice(null);
    try {
      const next = await action();
      onChange(next);
      setNotice({ kind: 'ok', text: success(next) });
    } catch (failure) {
      setNotice({ kind: 'error', text: errorMessage(failure) });
    } finally {
      setBusy(null);
    }
  };

  const time = () => new Date().toLocaleTimeString('it-IT');

  return (
    <article className={`tile ${kind}`} aria-label={`Project ${project.name}`}>
      <h2 className="tile-head">
        <button type="button" aria-expanded={!collapsed} aria-controls={bodyId} onClick={onToggle}>
          <span className={`dot ${kind}`} role="img" aria-label={dotLabel(kind)} title={dotLabel(kind)} />
          <span className="tile-name">{project.name}</span>
          <span className="tile-state" data-testid="autopilot-status">{statusLabel(project.status)}</span>
          <span className="chevron" aria-hidden="true">{collapsed ? '▸' : '▾'}</span>
        </button>
      </h2>
      <p className="snippet" data-testid="live-snippet" title={snippet}>
        {snippet || (project.autopilotEnabled ? 'In attesa di testo da ChatGPT…' : 'Autopilot non avviato.')}
      </p>
      {project.lastError && project.autopilotEnabled && <p className="tile-error">{project.lastError}</p>}
      {!collapsed && (
        <div id={bodyId} className="tile-body">
          <dl className="facts">
            <dt>Prompt inviati</dt><dd>{project.continuationsSent}{project.maxContinuations ? ` / ${project.maxContinuations}` : ''}</dd>
            <dt>Ultimo controllo</dt><dd>{formatDateTime(project.lastCheckAt)}</dd>
            <dt>Prossimo controllo</dt><dd>{project.autopilotEnabled ? formatDateTime(project.nextCheckAt) : '—'}</dd>
            <dt>Ultimo invio</dt><dd>{formatDateTime(project.lastContinuationAt)}</dd>
            <dt>Endpoint</dt><dd>{project.endpointName}</dd>
          </dl>
          {project.lastError && <p className="error">{project.lastError}{project.consecutiveFailures > 1 ? ` (tentativi falliti: ${project.consecutiveFailures})` : ''}</p>}

          <ChatList projectId={project.id} target={target} onTarget={setTarget} currentId={project.currentConversationId} />

          <label>Prompt di continuazione
            <textarea rows={3} value={prompt} onChange={e => setPrompt(e.target.value)}
              placeholder="es. Continua il lavoro dal punto in cui ti sei fermato." />
          </label>
          <label>Numero massimo di invii (vuoto = senza limite)
            <input type="number" min={1} value={max} onChange={e => setMax(e.target.value)} />
          </label>
          <div className="actions">
            {project.autopilotEnabled
              ? <button type="button" className="danger" disabled={busy !== null}
                onClick={() => run('stop', () => api.stopAutopilot(project.id), () => `Autopilot fermato alle ${time()}.`)}>
                {busy === 'stop' ? 'Arresto…' : 'Ferma'}
              </button>
              : <button type="button" className="primary" disabled={busy !== null}
                onClick={() => run('start', () => api.startAutopilot(project.id, settings()),
                  () => `Autopilot avviato alle ${time()}: sto controllando la chat. Lo stato si aggiorna qui sopra.`, validateStart(prompt, max))}>
                {busy === 'start' ? 'Avvio…' : 'Porta avanti il lavoro'}
              </button>}
            <button type="button" disabled={busy !== null || !dirty}
              onClick={() => run('save', () => api.saveSettings(project.id, settings()), () => `Impostazioni salvate alle ${time()}.`)}>
              {busy === 'save' ? 'Salvataggio…' : 'Salva impostazioni'}
            </button>
            <button type="button" disabled={busy !== null}
              onClick={() => run('deactivate', () => api.deactivate(project.id), () => 'Project disattivato.')}>
              Disattiva
            </button>
          </div>
          {dirty && <p className="hint" role="status">Modifiche non ancora salvate.</p>}
          {notice && <p role={notice.kind === 'error' ? 'alert' : 'status'} className={notice.kind === 'error' ? 'error' : 'ok'}>{notice.text}</p>}

          <section aria-label="Avanzamento">
            <h3>Avanzamento</h3>
            <pre className="live">{liveText || 'Nessun testo ricevuto finora.'}</pre>
          </section>
          <RecentActivity project={project} />
        </div>
      )}
    </article>
  );
}

function ChatList({ projectId, target, onTarget, currentId }: {
  projectId: string; target: string; onTarget: (id: string) => void; currentId: string | null;
}) {
  const [conversations, setConversations] = useState<Conversation[] | null>(null);
  const [error, setError] = useState('');

  // Loaded only when the tile is expanded: each call goes through the ChatGPT browser.
  useEffect(() => {
    api.conversations(projectId)
      .then(list => setConversations(sortConversations(list)))
      .catch(failure => {
        setConversations([]);
        setError(errorMessage(failure));
      });
  }, [projectId]);

  return (
    <div>
      <h3>Chat in ordine cronologico</h3>
      {error && <p className="error">{error}</p>}
      {conversations === null && <p className="hint">Caricamento delle chat…</p>}
      {conversations?.length === 0 && !error && <p className="empty">Nessuna chat nel project.</p>}
      {conversations && conversations.length > 0 && (
        <ol className="chats" aria-label="Chat del project">
          {conversations.map(conversation => (
            <li key={conversation.id} className={conversation.id === currentId ? 'current' : undefined}>
              <span>{conversation.title}</span> <small className="muted">{formatDateTime(conversation.createdAt)}</small>
            </li>
          ))}
        </ol>
      )}
      <label>Chat da portare avanti
        <select value={target} onChange={e => onTarget(e.target.value)}>
          <option value="">La chat più recente</option>
          {conversations?.map(conversation => <option key={conversation.id} value={conversation.id}>{conversation.title}</option>)}
        </select>
      </label>
    </div>
  );
}

function RecentActivity({ project }: { project: TrackedProject }) {
  const [entries, setEntries] = useState<LogEntry[]>([]);

  const load = useCallback(() => {
    api.logs({ projectId: project.id, take: 6 }).then(page => setEntries(page.items)).catch(() => setEntries([]));
  }, [project.id]);

  useEffect(load, [load, project.updatedAt, project.status, project.lastError]);

  return (
    <section aria-label="Attività recente">
      <h3>Attività recente</h3>
      {entries.length === 0 && <p className="empty">Nessuna attività registrata.</p>}
      <ul className="recent">
        {entries.map(entry => (
          <li key={entry.id} className={`level-${entry.level}`}>
            <time dateTime={entry.at}>{new Date(entry.at).toLocaleTimeString('it-IT')}</time>
            <span className="badge">{levelLabels[entry.level]}</span> {entry.message}
          </li>
        ))}
      </ul>
      <a href="#/log">Apri il registro completo</a>
    </section>
  );
}
