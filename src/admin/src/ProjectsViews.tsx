import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage, type ChatGptProject, type Endpoint, type TrackedProject } from './api';

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
