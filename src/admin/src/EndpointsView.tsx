import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, errorMessage, type Endpoint } from './api';
import {
  applyImported, countCookies, curlExample, emptyEndpointForm, formatDateTime, fromEndpoint, importBrowserHeaders,
  reasoningEfforts, relativeExpiry, toEndpointInput, type EndpointForm
} from './logic';

type IssuedKey = { endpointName: string; apiKey: string; model: string };
type Editing = { mode: 'create' } | { mode: 'edit'; endpoint: Endpoint } | null;

export function EndpointsView() {
  const [endpoints, setEndpoints] = useState<Endpoint[]>([]);
  const [editing, setEditing] = useState<Editing>(null);
  const [issued, setIssued] = useState<IssuedKey | null>(null);
  const [error, setError] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setEndpoints(await api.endpoints());
    } catch (failure) {
      setError(errorMessage(failure));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (endpointId: string, action: () => Promise<unknown>) => {
    setBusyId(endpointId);
    setError('');
    try {
      await action();
      await load();
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      setBusyId(null);
    }
  };

  const saved = async (endpoint: Endpoint, apiKey?: string) => {
    setEditing(null);
    if (apiKey) setIssued({ endpointName: endpoint.name, apiKey, model: endpoint.models[0]?.name ?? 'chatgpt' });
    await load();
  };

  return (
    <section>
      <div className="section-head">
        <h1>Endpoint OpenAI compatibili</h1>
        <button type="button" className="primary" onClick={() => { setIssued(null); setEditing({ mode: 'create' }); }}>
          Nuovo endpoint
        </button>
      </div>
      <p className="hint">
        Ogni endpoint espone <code>{window.location.origin}/v1</code> con una propria chiave API e usa un Chromium headless
        con i cookie, il token e gli header del tuo browser: i cookie aggiornati da ChatGPT vengono salvati e il token
        viene rinnovato in automatico.
      </p>
      {error && <p role="alert" className="error">{error}</p>}
      {issued && <KeyNotice issued={issued} onClose={() => setIssued(null)} />}
      {editing && (
        <EndpointEditor
          key={editing.mode === 'edit' ? editing.endpoint.id : 'new'}
          endpoint={editing.mode === 'edit' ? editing.endpoint : undefined}
          onSaved={saved}
          onCancel={() => setEditing(null)}
        />
      )}
      {endpoints.length === 0 && !editing && <p className="empty">Nessun endpoint configurato.</p>}
      <ul className="cards">
        {endpoints.map(endpoint => (
          <li key={endpoint.id} className="card" aria-label={`Endpoint ${endpoint.name}`}>
            <h2>
              {endpoint.name} {!endpoint.isEnabled && <span className="badge muted">Disabilitato</span>}
            </h2>
            <dl className="facts">
              <dt>Account</dt><dd>{endpoint.accountEmail ?? 'non ancora verificato'}</dd>
              <dt>Chiave API</dt><dd><code>{endpoint.apiKeyPrefix}…</code></dd>
              <dt>Modelli</dt><dd>{endpoint.models.map(m => `${m.name} → ${m.upstreamModel}`).join(', ')}</dd>
              <dt>Cookie</dt><dd>{endpoint.cookieCount}</dd>
              <dt>Token</dt><dd>{endpoint.hasAccessToken ? relativeExpiry(endpoint.accessTokenExpiresAt, new Date()) : 'assente'}</dd>
              <dt>Ultimo rinnovo</dt><dd>{formatDateTime(endpoint.lastRefreshAt)} (ogni {endpoint.refreshIntervalMinutes} min)</dd>
            </dl>
            {endpoint.lastError && (
              <p className="error">Ultimo errore ({formatDateTime(endpoint.lastErrorAt)}): {endpoint.lastError}</p>
            )}
            <div className="actions">
              <button type="button" onClick={() => { setIssued(null); setEditing({ mode: 'edit', endpoint }); }}>Modifica</button>
              <button type="button" disabled={busyId === endpoint.id} onClick={() => act(endpoint.id, () => api.refreshEndpoint(endpoint.id))}>
                Rinnova sessione ora
              </button>
              <button type="button" disabled={busyId === endpoint.id} onClick={() => act(endpoint.id, async () => {
                const { apiKey } = await api.rotateKey(endpoint.id);
                setIssued({ endpointName: endpoint.name, apiKey, model: endpoint.models[0]?.name ?? 'chatgpt' });
              })}>
                Rigenera chiave API
              </button>
              <button type="button" className="danger" disabled={busyId === endpoint.id} onClick={() => {
                if (window.confirm(`Eliminare l'endpoint ${endpoint.name} e i suoi project?`)) void act(endpoint.id, () => api.deleteEndpoint(endpoint.id));
              }}>
                Elimina
              </button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

function KeyNotice({ issued, onClose }: { issued: IssuedKey; onClose: () => void }) {
  return (
    <div className="card notice" role="status" aria-label="Chiave API generata">
      <h2>Chiave API di {issued.endpointName}</h2>
      <p>Copiala adesso: per sicurezza non verrà mostrata di nuovo.</p>
      <code className="secret" data-testid="api-key">{issued.apiKey}</code>
      <p>Base URL OpenAI: <code>{window.location.origin}/v1</code></p>
      <pre>{curlExample(window.location.origin, issued.apiKey, issued.model)}</pre>
      <div className="actions">
        <button type="button" onClick={() => void navigator.clipboard?.writeText(issued.apiKey)}>Copia chiave</button>
        <button type="button" className="primary" onClick={onClose}>Ho salvato la chiave</button>
      </div>
    </div>
  );
}

function EndpointEditor({ endpoint, onSaved, onCancel }: {
  endpoint?: Endpoint;
  onSaved: (endpoint: Endpoint, apiKey?: string) => void;
  onCancel: () => void;
}) {
  const creating = !endpoint;
  const [form, setForm] = useState<EndpointForm>(() => endpoint
    ? fromEndpoint(endpoint)
    : emptyEndpointForm(navigator.userAgent, Intl.DateTimeFormat().resolvedOptions().timeZone));
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState('');
  const [importNote, setImportNote] = useState('');

  const set = <K extends keyof EndpointForm>(key: K, value: EndpointForm[K]) => setForm(current => ({ ...current, [key]: value }));
  const setModel = (index: number, change: Partial<EndpointForm['models'][number]>) =>
    set('models', form.models.map((model, i) => (i === index ? { ...model, ...change } : model)));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const { input, errors: problems } = toEndpointInput(form, creating);
    setErrors(problems);
    if (!input) return;
    setBusy(true);
    try {
      if (endpoint) onSaved(await api.updateEndpoint(endpoint.id, input));
      else {
        const created = await api.createEndpoint(input);
        onSaved(created.endpoint, created.apiKey);
      }
    } catch (failure) {
      setErrors([errorMessage(failure)]);
    } finally {
      setBusy(false);
    }
  };

  const importHeaders = () => {
    const imported = importBrowserHeaders(copied);
    setForm(current => applyImported(current, imported));
    const found = [imported.cookies && 'cookie', imported.accessToken && 'token', imported.userAgent && 'user agent']
      .filter(Boolean).join(', ');
    setImportNote(`Importati: ${found || 'solo header'}; header aggiunti: ${Object.keys(imported.headers).length}; ignorati: ${imported.ignored.join(', ') || 'nessuno'}.`);
    setCopied('');
  };

  return (
    <form className="card editor" onSubmit={submit} aria-label={creating ? 'Nuovo endpoint' : `Modifica ${endpoint.name}`}>
      <h2>{creating ? 'Nuovo endpoint' : `Modifica ${endpoint.name}`}</h2>
      <label>Nome endpoint
        <input value={form.name} onChange={e => set('name', e.target.value)} />
      </label>

      <fieldset>
        <legend>Modelli esposti (OpenAI → ChatGPT)</legend>
        {form.models.map((model, index) => (
          <div className="row" key={index}>
            <input aria-label={`Nome esposto ${index + 1}`} placeholder="es. chatgpt-thinking" value={model.name}
              onChange={e => setModel(index, { name: e.target.value })} />
            <input aria-label={`Modello ChatGPT ${index + 1}`} placeholder="es. gpt-5-thinking" value={model.upstreamModel}
              onChange={e => setModel(index, { upstreamModel: e.target.value })} />
            <select aria-label={`Reasoning predefinito ${index + 1}`} value={model.defaultReasoningEffort ?? ''}
              onChange={e => setModel(index, { defaultReasoningEffort: e.target.value || null })}>
              <option value="">predefinito ChatGPT</option>
              {reasoningEfforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
            </select>
            <button type="button" onClick={() => set('models', form.models.filter((_, i) => i !== index))}>Rimuovi</button>
          </div>
        ))}
        <button type="button" onClick={() => set('models', [...form.models, { name: '', upstreamModel: '', defaultReasoningEffort: null }])}>
          Aggiungi modello
        </button>
      </fieldset>

      <fieldset>
        <legend>Valori di thinking ChatGPT per reasoning_effort</legend>
        <div className="grid">
          {reasoningEfforts.map(effort => (
            <label key={effort}>{effort}
              <input aria-label={`Thinking per ${effort}`} placeholder="predefinito" value={form.thinking[effort] ?? ''}
                onChange={e => set('thinking', { ...form.thinking, [effort]: e.target.value })} />
            </label>
          ))}
        </div>
      </fieldset>

      <label className="check">
        <input type="checkbox" checked={form.useTemporaryChat} onChange={e => set('useTemporaryChat', e.target.checked)} />
        Usa chat temporanee per le richieste API (non restano nella cronologia ChatGPT)
      </label>

      <details>
        <summary>Importa dagli header copiati dal browser</summary>
        <p className="hint">
          In DevTools → Rete, scegli una richiesta a chatgpt.com e copia gli header (o “Copy as cURL (bash)”):
          cookie, token e user agent vanno nei rispettivi campi, gli header stabili del browser vengono aggiunti.
        </p>
        <textarea aria-label="Header copiati" rows={5} spellCheck={false} value={copied} onChange={e => setCopied(e.target.value)} />
        <button type="button" onClick={importHeaders} disabled={!copied.trim()}>Importa</button>
        {importNote && <p role="status" className="hint">{importNote}</p>}
      </details>

      <label>User agent
        <textarea rows={2} spellCheck={false} value={form.userAgent} onChange={e => set('userAgent', e.target.value)} />
      </label>
      <button type="button" onClick={() => set('userAgent', navigator.userAgent)}>Usa lo user agent di questo browser</button>
      <div className="grid">
        <label>Lingua
          <input value={form.locale} onChange={e => set('locale', e.target.value)} />
        </label>
        <label>Fuso orario
          <input value={form.timeZone} onChange={e => set('timeZone', e.target.value)} />
        </label>
        <label>Rinnovo token ogni (minuti)
          <input type="number" min={5} max={1440} value={form.refreshIntervalMinutes}
            onChange={e => set('refreshIntervalMinutes', Number(e.target.value))} />
        </label>
      </div>
      <label>Header aggiuntivi (uno per riga, “Nome: valore”)
        <textarea rows={3} spellCheck={false} value={form.headersText} onChange={e => set('headersText', e.target.value)} />
      </label>
      <label>Cookie di ChatGPT {creating ? '' : '(lascia vuoto per mantenere quelli salvati)'}
        <textarea rows={4} spellCheck={false} autoComplete="off" value={form.cookies} onChange={e => set('cookies', e.target.value)}
          placeholder="__Secure-next-auth.session-token=...; oai-did=... oppure export JSON" />
      </label>
      <p className="hint">{countCookies(form.cookies)} cookie rilevati. Vengono cifrati sul server e non sono più mostrati.</p>
      <label>Token di accesso (facoltativo{creating ? '' : ', vuoto = invariato'})
        <textarea rows={2} spellCheck={false} autoComplete="off" value={form.accessToken} onChange={e => set('accessToken', e.target.value)}
          placeholder="eyJ... (header Authorization: Bearer)" />
      </label>
      <label className="check">
        <input type="checkbox" checked={form.isEnabled} onChange={e => set('isEnabled', e.target.checked)} />
        Endpoint abilitato
      </label>

      {errors.length > 0 && (
        <ul role="alert" className="error">{errors.map(problem => <li key={problem}>{problem}</li>)}</ul>
      )}
      <div className="actions">
        <button type="submit" className="primary" disabled={busy}>{creating ? 'Crea endpoint' : 'Salva modifiche'}</button>
        <button type="button" onClick={onCancel}>Annulla</button>
      </div>
    </form>
  );
}
