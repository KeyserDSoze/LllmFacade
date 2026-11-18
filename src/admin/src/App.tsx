import { useEffect, useState } from 'react';
import { api, UNAUTHORIZED_EVENT, type Session } from './api';
import { ActiveProjectsView } from './ActiveProjects';
import { ChangePasswordView, LoginView } from './AuthViews';
import { EndpointsView } from './EndpointsView';
import { LogsView } from './LogsView';
import { AllProjectsView, InactiveProjectsView } from './ProjectsViews';
import { StatusDock } from './StatusDock';

const routes = [
  { hash: '#/endpoint', label: 'Endpoint OpenAI' },
  { hash: '#/project', label: 'Tutti i project' },
  { hash: '#/attivi', label: 'Project attivi' },
  { hash: '#/disattivati', label: 'Project disattivati' },
  { hash: '#/log', label: 'Registro' }
];

function useHash(): string {
  const [hash, setHash] = useState(() => window.location.hash || routes[0].hash);
  useEffect(() => {
    const update = () => setHash(window.location.hash || routes[0].hash);
    window.addEventListener('hashchange', update);
    return () => window.removeEventListener('hashchange', update);
  }, []);
  return hash;
}

export default function App() {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  const hash = useHash();

  useEffect(() => {
    api.me().then(setSession).catch(() => setSession(null));
    const expired = () => setSession(null);
    window.addEventListener(UNAUTHORIZED_EVENT, expired);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, expired);
  }, []);

  const logout = async () => {
    try {
      await api.logout();
    } finally {
      setSession(null);
    }
  };

  if (session === undefined) return <main className="center"><p>Caricamento…</p></main>;
  if (session === null) return <LoginView onLogin={setSession} />;
  if (session.mustChangePassword) return <ChangePasswordView onChanged={setSession} onLogout={logout} />;

  return (
    <div className="layout">
      <header className="topbar">
        <strong className="brand">LllmFacade</strong>
        <nav aria-label="Sezioni">
          {routes.map(route => (
            <a key={route.hash} href={route.hash} aria-current={hash === route.hash ? 'page' : undefined}>{route.label}</a>
          ))}
        </nav>
        <span className="user">{session.userName}</span>
        <button type="button" onClick={logout}>Esci</button>
      </header>
      <main className="content">
        {hash === '#/project' ? <AllProjectsView />
          : hash === '#/attivi' ? <ActiveProjectsView />
            : hash === '#/disattivati' ? <InactiveProjectsView />
              : hash === '#/log' ? <LogsView />
                : <EndpointsView />}
      </main>
      <StatusDock />
    </div>
  );
}
