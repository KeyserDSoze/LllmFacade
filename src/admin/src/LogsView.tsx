import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage, type LogEntry, type TrackedProject } from './api';
import { categoryLabels, levelLabels, mergeLogs } from './logic';

const PAGE = 100;
const REFRESH_MS = 3000;

export function LogsView() {
  const [entries, setEntries] = useState<LogEntry[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [level, setLevel] = useState('');
  const [category, setCategory] = useState('');
  const [projectId, setProjectId] = useState('');
  const [text, setText] = useState('');
  const [auto, setAuto] = useState(true);
  const [projects, setProjects] = useState<TrackedProject[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    Promise.all([api.projects('active'), api.projects('inactive')])
      .then(([active, inactive]) => setProjects([...active, ...inactive].sort((a, b) => a.name.localeCompare(b.name))))
      .catch(() => setProjects([]));
  }, []);

  const query = useCallback(() => ({ level, category, projectId, take: PAGE }), [level, category, projectId]);

  const reload = useCallback(async (merge: boolean) => {
    try {
      const page = await api.logs(query());
      setEntries(current => (merge ? mergeLogs(current, page.items) : page.items));
      if (!merge) setHasMore(page.hasMore);
      setError('');
    } catch (failure) {
      setError(errorMessage(failure));
    }
  }, [query]);

  useEffect(() => {
    setLoading(true);
    void reload(false).finally(() => setLoading(false));
  }, [reload]);

  useEffect(() => {
    if (!auto) return;
    const timer = window.setInterval(() => void reload(true), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [auto, reload]);

  const loadMore = async () => {
    const oldest = entries[entries.length - 1]?.id;
    try {
      const page = await api.logs({ ...query(), before: oldest });
      setEntries(current => mergeLogs(current, page.items));
      setHasMore(page.hasMore);
    } catch (failure) {
      setError(errorMessage(failure));
    }
  };

  const needle = text.trim().toLowerCase();
  const shown = needle
    ? entries.filter(entry => `${entry.message} ${entry.subject ?? ''}`.toLowerCase().includes(needle))
    : entries;

  return (
    <section>
      <div className="section-head"><h1>Registro attività</h1></div>
      <p className="hint">
        Ogni azione e ogni passaggio dell'autopilot, in ordine dal più recente. Non contiene cookie, token né il testo delle risposte;
        le voci più vecchie di 14 giorni vengono eliminate.
      </p>
      <div className="row filters">
        <label>Livello minimo
          <select value={level} onChange={e => setLevel(e.target.value)}>
            <option value="">Tutti</option>
            <option value="warning">Avvisi ed errori</option>
            <option value="error">Solo errori</option>
          </select>
        </label>
        <label>Categoria
          <select value={category} onChange={e => setCategory(e.target.value)}>
            <option value="">Tutte</option>
            {Object.entries(categoryLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </label>
        <label>Project
          <select aria-label="Filtra per project" value={projectId} onChange={e => setProjectId(e.target.value)}>
            <option value="">Tutti</option>
            {projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}
          </select>
        </label>
        <label>Cerca nel testo
          <input value={text} onChange={e => setText(e.target.value)} placeholder="filtra le voci caricate" />
        </label>
        <label className="check">
          <input type="checkbox" checked={auto} onChange={e => setAuto(e.target.checked)} />
          Aggiorna ogni 3 secondi
        </label>
        <button type="button" onClick={() => void reload(true)}>Aggiorna ora</button>
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {loading && entries.length === 0 && <p>Caricamento…</p>}
      {!loading && shown.length === 0 && <p className="empty">Nessuna voce.</p>}
      {shown.length > 0 && (
        <table className="log-table" aria-label="Registro attività">
          <thead><tr><th>Quando</th><th>Livello</th><th>Categoria</th><th>Oggetto</th><th>Messaggio</th></tr></thead>
          <tbody>
            {shown.map(entry => (
              <tr key={entry.id} className={`level-${entry.level}`}>
                <td><time dateTime={entry.at}>{new Date(entry.at).toLocaleString('it-IT')}</time></td>
                <td><span className={`badge level-${entry.level}`}>{levelLabels[entry.level]}</span></td>
                <td>{categoryLabels[entry.category] ?? entry.category}</td>
                <td>{entry.subject ?? '—'}</td>
                <td>{entry.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {hasMore && !needle && <button type="button" onClick={loadMore}>Carica voci precedenti</button>}
    </section>
  );
}
