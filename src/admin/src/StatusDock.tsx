import { useEffect, useState } from 'react';
import { api, type ActivitySummary } from './api';
import { dockHeadline, elapsedLabel, relativeExpiry, relativeTime, workingCount } from './logic';

const DOCK_KEY = 'lllmfacade.admin.dock';
const POLL_MS = 5000;

const clock = (value: string | null) =>
  value ? new Date(value).toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' }) : '—';

/** Fixed bottom-right panel: threads working together and when each ChatGPT token was renewed / will be renewed. */
export function StatusDock() {
  const [summary, setSummary] = useState<ActivitySummary | null>(null);
  const [failed, setFailed] = useState(false);
  const [now, setNow] = useState(() => new Date());
  const [open, setOpen] = useState(() => window.localStorage.getItem(DOCK_KEY) !== 'closed');

  useEffect(() => {
    let active = true;
    const load = () => api.summary()
      .then(next => {
        if (!active) return;
        setSummary(next);
        setFailed(false);
        setNow(new Date());
      })
      .catch(() => active && setFailed(true));
    void load();
    const timer = window.setInterval(() => void load(), POLL_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);

  const toggle = () => {
    const next = !open;
    setOpen(next);
    try {
      window.localStorage.setItem(DOCK_KEY, next ? 'open' : 'closed');
    } catch {
      // Layout preference only.
    }
  };

  const busy = workingCount(summary) > 0;

  return (
    <aside className="dock" aria-label="Stato in tempo reale">
      <button type="button" className="dock-head" aria-expanded={open} aria-controls="dock-body" onClick={toggle}>
        <span className={busy ? 'dot working' : 'dot idle'} aria-hidden="true" />
        <span className="dock-title" data-testid="dock-headline">{dockHeadline(summary)}</span>
        <span className="chevron" aria-hidden="true">{open ? '▾' : '▴'}</span>
      </button>
      {open && (
        <div id="dock-body" className="dock-body">
          {failed && <p className="error">Stato non aggiornabile: riprovo tra pochi secondi.</p>}
          {summary && (
            <>
              <section aria-label="Thread al lavoro">
                {summary.working.length === 0 && <p className="empty">Nessun project o richiesta API in corso.</p>}
                <ul className="dock-list">
                  {summary.working.map((item, index) => (
                    <li key={`${item.kind}-${item.id ?? index}`}>
                      <span className="dot working" aria-hidden="true" />
                      <span>
                        <strong>{item.name}</strong>{' '}
                        <span className="muted">{item.kind === 'api' ? 'API OpenAI' : item.endpointName}</span>
                        <small className="block muted">{item.detail} {elapsedLabel(item.since, now)}</small>
                      </span>
                    </li>
                  ))}
                </ul>
                {summary.watching > 0 && (
                  <p className="hint">{summary.watching} project con autopilot in attesa del prossimo controllo.</p>
                )}
              </section>
              <section aria-label="Token ChatGPT">
                <h3>Token ChatGPT</h3>
                {summary.endpoints.length === 0 && <p className="empty">Nessun endpoint configurato.</p>}
                <ul className="dock-list">
                  {summary.endpoints.map(endpoint => (
                    <li key={endpoint.id} className={endpoint.lastError ? 'has-error' : undefined}>
                      <span className={endpoint.lastError ? 'dot error' : endpoint.isEnabled ? 'dot done' : 'dot idle'} aria-hidden="true" />
                      <span>
                        <strong>{endpoint.name}</strong>
                        {!endpoint.isEnabled && <span className="muted"> (disabilitato)</span>}
                        <small className="block">
                          Rinnovato {clock(endpoint.lastRefreshAt)} ({relativeTime(endpoint.lastRefreshAt, now)})
                        </small>
                        <small className="block">
                          {endpoint.isEnabled
                            ? <>Prossimo rinnovo {clock(endpoint.nextRefreshAt)} ({relativeTime(endpoint.nextRefreshAt, now)})</>
                            : 'Rinnovo sospeso'}
                        </small>
                        {endpoint.hasAccessToken && <small className="block muted">Token {relativeExpiry(endpoint.accessTokenExpiresAt, now)}</small>}
                        {endpoint.lastError && <small className="block error">{endpoint.lastError}</small>}
                      </span>
                    </li>
                  ))}
                </ul>
              </section>
            </>
          )}
        </div>
      )}
    </aside>
  );
}
