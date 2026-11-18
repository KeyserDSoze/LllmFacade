import { useCallback, useEffect, useState } from 'react';
import {
  api, errorMessage, type ChatGptProject, type Conversation, type Endpoint, type ProjectSettings, type TrackedProject
} from './api';
import { formatDateTime, sortConversations, statusLabel } from './logic';

export function AllProjectsView() {
  const [endpoints, setEndpoints] = useState<Endpoint[] | null>(null);
  const [endpointId, setEndpointId] = useState('');
  const [projects, setProjects] = useState<ChatGptProject[] | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    api.endpoints()
      .then(list => {
        setEndpoints(list);
        setEndpointId(current => current || list[0]?.id || '');
      })
      .catch(failure => setError(errorMessage(failure)));
  }, []);

  const load = useCallback(async (id: string) => {
    if (!id) return;
    setLoading(true);
    setError('');
    try {
      setProjects(await api.chatGptProjects(id));
    } catch (failure) {
      setProjects(null);
      setError(errorMessage(failure));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load(endpointId);
  }, [endpointId, load]);

  const change = async (action: () => Promise<unknown>) => {
    setError('');
    try {
      await action();
      await load(endpointId);
    } catch (failure) {
      setError(errorMessage(failure));
    }
  };

  return (
    <section>
      <div className="section-head"><h1>Tutti i project ChatGPT</h1></div>
      <p className="hint">L'elenco viene letto da ChatGPT con il browser dell'endpoint. Attiva i project da seguire.</p>
      {endpoints?.length === 0 && <p className="empty">Configura prima un endpoint.</p>}
      {endpoints && endpoints.length > 0 && (
        <div className="row">
          <label>Endpoint
            <select value={endpointId} onChange={e => setEndpointId(e.target.value)}>
              {endpoints.map(endpoint => <option key={endpoint.id} value={endpoint.id}>{endpoint.name}</option>)}
            </select>
          </label>
          <button type="button" onClick={() => load(endpointId)} disabled={loading}>Aggiorna da ChatGPT</button>
        </div>
      )}
      {error && <p role="alert" className="error">{error}</p>}
      {loading && <p>Lettura dei project da ChatGPT…</p>}
      {projects && projects.length === 0 && <p className="empty">Nessun project trovato su ChatGPT.</p>}
      {projects && projects.length > 0 && (
        <table>
          <thead><tr><th>Project</th><th>Stato</th><th /></tr></thead>
          <tbody>
            {projects.map(project => (
              <tr key={project.projectId}>
                <td>{project.name} <small className="muted">{project.projectId}</small></td>
                <td>{project.isActive ? 'Attivo' : project.trackedId ? 'Disattivato' : 'Non seguito'}</td>
                <td>
                  {project.isActive
                    ? <button type="button" aria-label={`Disattiva ${project.name}`} onClick={() => change(() => api.deactivate(project.trackedId!))}>Disattiva</button>
                    : <button type="button" className="primary" aria-label={`Attiva ${project.name}`} onClick={() => change(() => project.trackedId
                      ? api.activate(project.trackedId)
                      : api.trackProject(endpointId, project.projectId, project.name))}>
                      {project.trackedId ? 'Riattiva' : 'Attiva'}
                    </button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export function ActiveProjectsView() {
  const [projects, setProjects] = useState<TrackedProject[] | null>(null);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    try {
      setProjects(await api.projects('active'));
    } catch (failure) {
      setError(errorMessage(failure));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <section>
      <div className="section-head"><h1>Project attivi</h1></div>
      {error && <p role="alert" className="error">{error}</p>}
      {projects?.length === 0 && <p className="empty">Nessun project attivo: attivane uno da “Tutti i project”.</p>}
      {projects?.map(project => <ProjectCard key={project.id} initial={project} onDeactivated={load} />)}
    </section>
  );
}

export function InactiveProjectsView() {
  const [projects, setProjects] = useState<TrackedProject[] | null>(null);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    try {
      setProjects(await api.projects('inactive'));
    } catch (failure) {
      setError(errorMessage(failure));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const reactivate = async (id: string) => {
    setError('');
    try {
      await api.activate(id);
      await load();
    } catch (failure) {
      setError(errorMessage(failure));
    }
  };

  return (
    <section>
      <div className="section-head"><h1>Project disattivati</h1></div>
      {error && <p role="alert" className="error">{error}</p>}
      {projects?.length === 0 && <p className="empty">Nessun project disattivato.</p>}
      <ul className="cards">
        {projects?.map(project => (
          <li key={project.id} className="card" aria-label={`Project ${project.name}`}>
            <h2>{project.name}</h2>
            <p className="muted">{project.endpointName} · {project.projectId}</p>
            <button type="button" className="primary" onClick={() => reactivate(project.id)}>Riattiva</button>
          </li>
        ))}
      </ul>
    </section>
  );
}

function ProjectCard({ initial, onDeactivated }: { initial: TrackedProject; onDeactivated: () => void }) {
  const [project, setProject] = useState(initial);
  const [conversations, setConversations] = useState<Conversation[] | null>(null);
  const [prompt, setPrompt] = useState(initial.continuationPrompt);
  const [target, setTarget] = useState(initial.targetConversationId ?? '');
  const [max, setMax] = useState(initial.maxContinuations?.toString() ?? '');
  const [live, setLive] = useState('');
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    api.conversations(initial.id)
      .then(list => setConversations(sortConversations(list)))
      .catch(failure => {
        setConversations([]);
        setError(errorMessage(failure));
      });
  }, [initial.id]);

  // Live status: EventSource reconnects by itself when the connection drops.
  useEffect(() => {
    const source = new EventSource(api.eventsUrl(initial.id));
    source.onopen = () => setConnected(true);
    source.onerror = () => setConnected(false);
    source.addEventListener('status', event => setProject(JSON.parse((event as MessageEvent<string>).data) as TrackedProject));
    source.addEventListener('live', event => {
      const data = JSON.parse((event as MessageEvent<string>).data) as { text: string; reset: boolean };
      setLive(current => (data.reset ? data.text : current + data.text));
    });
    return () => source.close();
  }, [initial.id]);

  const settings = (): ProjectSettings => ({
    continuationPrompt: prompt,
    targetConversationId: target || null,
    maxContinuations: max ? Number(max) : null
  });

  const run = async (action: () => Promise<TrackedProject>) => {
    setError('');
    try {
      setProject(await action());
    } catch (failure) {
      setError(errorMessage(failure));
    }
  };

  return (
    <article className="card project-card" aria-label={`Project ${project.name}`}>
      <header className="project-head">
        <h2>{project.name}</h2>
        <span className={`badge status-${project.status}`} data-testid="autopilot-status">{statusLabel(project.status)}</span>
        <span className="muted">{project.endpointName}</span>
        <span className={connected ? 'dot online' : 'dot'} title={connected ? 'Aggiornamenti in tempo reale' : 'Riconnessione in corso'} />
      </header>
      <dl className="facts">
        <dt>Prompt inviati</dt><dd>{project.continuationsSent}{project.maxContinuations ? ` / ${project.maxContinuations}` : ''}</dd>
        <dt>Ultimo controllo</dt><dd>{formatDateTime(project.lastCheckAt)}</dd>
        <dt>Prossimo controllo</dt><dd>{project.autopilotEnabled ? formatDateTime(project.nextCheckAt) : '—'}</dd>
        <dt>Ultimo invio</dt><dd>{formatDateTime(project.lastContinuationAt)}</dd>
      </dl>
      {project.lastError && <p className="error">{project.lastError}{project.consecutiveFailures > 1 ? ` (tentativi falliti: ${project.consecutiveFailures})` : ''}</p>}
      {error && <p role="alert" className="error">{error}</p>}

      <h3>Chat in ordine cronologico</h3>
      {conversations === null && <p>Caricamento delle chat…</p>}
      {conversations?.length === 0 && <p className="empty">Nessuna chat nel project.</p>}
      {conversations && conversations.length > 0 && (
        <ol className="chats" aria-label="Chat del project">
          {conversations.map(conversation => (
            <li key={conversation.id} className={conversation.id === project.currentConversationId ? 'current' : undefined}>
              <span>{conversation.title}</span> <small className="muted">{formatDateTime(conversation.createdAt)}</small>
            </li>
          ))}
        </ol>
      )}

      <label>Chat da portare avanti
        <select value={target} onChange={e => setTarget(e.target.value)}>
          <option value="">La chat più recente</option>
          {conversations?.map(conversation => <option key={conversation.id} value={conversation.id}>{conversation.title}</option>)}
        </select>
      </label>
      <label>Prompt di continuazione
        <textarea rows={3} value={prompt} onChange={e => setPrompt(e.target.value)} placeholder="es. Continua il lavoro dal punto in cui ti sei fermato." />
      </label>
      <label>Numero massimo di invii (vuoto = senza limite)
        <input type="number" min={1} value={max} onChange={e => setMax(e.target.value)} />
      </label>
      <div className="actions">
        {project.autopilotEnabled
          ? <button type="button" className="danger" onClick={() => run(() => api.stopAutopilot(project.id))}>Ferma</button>
          : <button type="button" className="primary" onClick={() => run(() => api.startAutopilot(project.id, settings()))}>Porta avanti il lavoro</button>}
        <button type="button" onClick={() => run(() => api.saveSettings(project.id, settings()))}>Salva impostazioni</button>
        <button type="button" onClick={async () => {
          await run(() => api.deactivate(project.id));
          onDeactivated();
        }}>Disattiva</button>
      </div>

      <section aria-label="Avanzamento">
        <h3>Avanzamento</h3>
        <pre className="live">{live || 'Nessun testo ricevuto finora.'}</pre>
      </section>
    </article>
  );
}
