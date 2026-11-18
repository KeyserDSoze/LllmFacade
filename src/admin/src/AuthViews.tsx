import { useState, type FormEvent } from 'react';
import { api, errorMessage, type Session } from './api';

export function LoginView({ onLogin }: { onLogin: (session: Session) => void }) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError('');
    try {
      onLogin(await api.login(userName, password));
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="center">
      <form className="card auth" onSubmit={submit}>
        <h1>Accesso amministratore</h1>
        <label>Nome utente
          <input autoComplete="username" value={userName} onChange={e => setUserName(e.target.value)} required />
        </label>
        <label>Password
          <input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required />
        </label>
        {error && <p role="alert" className="error">{error}</p>}
        <button type="submit" className="primary" disabled={busy}>Accedi</button>
      </form>
    </main>
  );
}

export function ChangePasswordView({ onChanged, onLogout }: { onChanged: (session: Session) => void; onLogout: () => void }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (next !== confirm) {
      setError('Le due password non coincidono.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      onChanged(await api.changePassword(current, next));
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="center">
      <form className="card auth" onSubmit={submit}>
        <h1>Cambia la password</h1>
        <p className="hint">Primo accesso: scegli una nuova password di almeno 12 caratteri prima di continuare.</p>
        <label>Password attuale
          <input type="password" autoComplete="current-password" value={current} onChange={e => setCurrent(e.target.value)} required />
        </label>
        <label>Nuova password
          <input type="password" autoComplete="new-password" value={next} onChange={e => setNext(e.target.value)} required />
        </label>
        <label>Conferma nuova password
          <input type="password" autoComplete="new-password" value={confirm} onChange={e => setConfirm(e.target.value)} required />
        </label>
        {error && <p role="alert" className="error">{error}</p>}
        <div className="actions">
          <button type="submit" className="primary" disabled={busy}>Salva password</button>
          <button type="button" onClick={onLogout}>Esci</button>
        </div>
      </form>
    </main>
  );
}
