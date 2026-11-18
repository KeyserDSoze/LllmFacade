import { useState } from 'react';
import {
  ArrowUpRight, Check, CircleHelp, FolderPlus, Globe2, LoaderCircle,
  MessageSquareText, Monitor, Plus, RefreshCw, Send, Trash2, Unplug, Zap
} from 'lucide-react';
import { sendCommand, validExtensionId, type ChatTab } from './bridge';
import {
  createProject, loadAssignments, loadProjects, projectForTab,
  saveAssignments, saveProjects, type Assignments, type Project
} from './projects';

const EXTENSION_ID_KEY = 'lllmfacade.extensionId.v1';

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export default function App() {
  const [extensionId, setExtensionId] = useState(() => localStorage.getItem(EXTENSION_ID_KEY) || '');
  const [connected, setConnected] = useState(false);
  const [tabs, setTabs] = useState<ChatTab[]>([]);
  const [projects, setProjects] = useState<Project[]>(loadProjects);
  const [assignments, setAssignments] = useState<Assignments>(loadAssignments);
  const [name, setName] = useState('');
  const [selectedProject, setSelectedProject] = useState('all');
  const [drafts, setDrafts] = useState<Record<number, string>>({});
  const [answers, setAnswers] = useState<Record<number, string>>({});
  const [working, setWorking] = useState<Record<number, boolean>>({});
  const [notice, setNotice] = useState('');
  const [loading, setLoading] = useState(false);

  async function refresh(id = extensionId) {
    const result = await sendCommand<ChatTab[]>(id, { action: 'listTabs' });
    setTabs(result);
  }

  async function connect() {
    setLoading(true);
    try {
      if (!validExtensionId(extensionId)) throw new Error('L’ID estensione deve avere 32 caratteri da a a p.');
      await sendCommand<{ version: string }>(extensionId, { action: 'ping' });
      await refresh(extensionId);
      localStorage.setItem(EXTENSION_ID_KEY, extensionId.trim());
      setConnected(true);
      setNotice('Estensione collegata. Le conversazioni restano nelle tue schede Chrome.');
    } catch (error) {
      setConnected(false);
      setNotice(errorText(error));
    } finally {
      setLoading(false);
    }
  }

  async function reload() {
    setLoading(true);
    try {
      await refresh();
      setNotice('Elenco delle schede aggiornato.');
    } catch (error) {
      setConnected(false);
      setNotice(errorText(error));
    } finally {
      setLoading(false);
    }
  }

  function addProject() {
    try {
      const newEntry = createProject(name, crypto.randomUUID());
      const updated = [...projects, newEntry];
      setProjects(updated);
      saveProjects(updated);
      setName('');
      setSelectedProject(newEntry.id);
    } catch (error) {
      setNotice(errorText(error));
    }
  }

  function deleteProject(id: string) {
    const updated = projects.filter(project => project.id !== id);
    const nextAssignments = Object.fromEntries(
      Object.entries(assignments).filter(([, projectId]) => projectId !== id)
    );
    setProjects(updated);
    setAssignments(nextAssignments);
    saveProjects(updated);
    saveAssignments(nextAssignments);
    if (selectedProject === id) setSelectedProject('all');
  }

  function assignTab(tabId: number, projectId: string) {
    const next = { ...assignments, [String(tabId)]: projectId };
    setAssignments(next);
    saveAssignments(next);
  }

  async function openChat() {
    try {
      await sendCommand<ChatTab>(extensionId, { action: 'openChat' });
      await refresh();
      setNotice('Nuova scheda ChatGPT aperta in Chrome.');
    } catch (error) {
      setNotice(errorText(error));
    }
  }

  async function action(tabId: number, kind: 'activateTab' | 'sendPrompt' | 'readLastReply') {
    setWorking(previous => ({ ...previous, [tabId]: true }));
    try {
      if (kind === 'sendPrompt') {
        const prompt = drafts[tabId]?.trim();
        if (!prompt) throw new Error('Scrivi un prompt prima dell’invio.');
        const result = await sendCommand<{ status: string }>(extensionId, {
          action: 'sendPrompt', tabId, prompt
        });
        setDrafts(previous => ({ ...previous, [tabId]: '' }));
        setNotice(result.status + ' Quando ChatGPT ha finito, premi “Leggi risposta”.');
      } else if (kind === 'readLastReply') {
        const result = await sendCommand<{ text: string }>(extensionId, {
          action: 'readLastReply', tabId
        });
        setAnswers(previous => ({ ...previous, [tabId]: result.text }));
        setNotice('Risposta letta in memoria, non archiviata.');
      } else {
        await sendCommand<ChatTab>(extensionId, { action: 'activateTab', tabId });
        setNotice('Scheda attivata in Chrome.');
      }
    } catch (error) {
      setNotice(errorText(error));
    } finally {
      setWorking(previous => ({ ...previous, [tabId]: false }));
    }
  }

  const visibleTabs = selectedProject === 'all' ? tabs :
    tabs.filter(tab => projectForTab(assignments, tab) === selectedProject);

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand"><span className="logo">L</span><span><strong>LllmFacade</strong><small>Personal AI workspace</small></span></div>
        <div className="side-label">WORKSPACE</div>
        <button className={selectedProject === 'all' ? 'nav-item active' : 'nav-item'}
          onClick={() => setSelectedProject('all')}>
          <Globe2 size={17} /> Tutte le chat <span className="count">{tabs.length}</span>
        </button>
        <div className="side-label projects-title">PROGETTI <span>{projects.length}</span></div>
        {projects.map(project => (
          <div className="project-row" key={project.id}>
            <button className={selectedProject === project.id ? 'nav-item active' : 'nav-item'}
              onClick={() => setSelectedProject(project.id)}>
              <MessageSquareText size={16} /><span className="project-name">{project.name}</span>
            </button>
            <button className="icon-btn delete-project" aria-label={'Rimuovi ' + project.name}
              title="Rimuovi progetto" onClick={() => deleteProject(project.id)}><Trash2 size={14} /></button>
          </div>
        ))}
        <form className="add-project" onSubmit={event => { event.preventDefault(); addProject(); }}>
          <input placeholder="Nuovo progetto..." value={name}
            onChange={event => setName(event.target.value)} maxLength={60} />
          <button aria-label="Aggiungi progetto" title="Aggiungi progetto"><FolderPlus size={18} /></button>
        </form>
        <div className="sidebar-footer"><Monitor size={16} /> Browser desktop: <strong>Electron</strong><small>Avvio locale, fino a 12 finestre</small></div>
      </aside>

      <main className="main">
        <header className="topbar">
          <div className="eyebrow">WORKSPACE / <span>OVERVIEW</span></div>
          <div className="top-status"><span className={connected ? 'dot online' : 'dot'} />
            {connected ? 'Bridge connesso' : 'Bridge non connesso'}</div>
        </header>
        <div className="content">
          <div className="hero">
            <div><div className="kicker"><Zap size={14} /> MULTI-CHAT CONTROL</div>
              <h1>Un solo spazio.<br /><span>Tutte le tue chat.</span></h1>
              <p>Organizza i progetti, comanda le schede Chrome e visualizza le risposte in un unico posto.</p>
            </div>
            <div className="hero-count"><strong>{tabs.length}</strong><span>CHAT APERTE</span></div>
          </div>

          <section className="connect-panel">
            <div className="panel-title"><span className="panel-icon"><Zap size={18} /></span>
              <div><h2>Collegamento estensione Chrome</h2>
                <p>Copia l’ID da chrome://extensions e collegalo alla dashboard.</p></div>
            </div>
            <div className="connect-controls">
              <input aria-label="ID estensione Chrome" spellCheck={false} placeholder="ID estensione (32 caratteri)"
                value={extensionId} onChange={event => setExtensionId(event.target.value)}
                maxLength={32} />
              <button className="btn primary" disabled={loading} onClick={connect}>
                {loading ? <LoaderCircle size={16} className="spin" /> : connected ? <Check size={16} /> : <Zap size={16} />}
                {connected ? 'Riconnetti' : 'Connetti'}
              </button>
            </div>
            <div className="hint"><CircleHelp size={14} /> Serve Chrome con l’estensione caricata. Nessuna API key richiesta.</div>
          </section>

          {notice && <div className="notice" role="status">{notice}</div>}

          <div className="section-heading">
            <div><div className="section-eyebrow">LE TUE CONVERSAZIONI</div>
              <h2>{selectedProject === 'all' ? 'Schede ChatGPT' :
                projects.find(project => project.id === selectedProject)?.name || 'Progetto'}</h2>
              <p>Una scheda = una conversazione. L’invio e la lettura sono funzioni sperimentali.</p>
            </div>
            <div className="toolbar">
              <button className="btn secondary" disabled={!connected || loading} onClick={reload}>
                <RefreshCw size={16} /> Aggiorna
              </button>
              <button className="btn primary" disabled={!connected} onClick={openChat}>
                <Plus size={16} /> Apri chat
              </button>
            </div>
          </div>

          {!connected ? (
            <div className="empty"><Unplug size={32} /><h3>Collega la tua estensione</h3>
              <p>La dashboard potrà visualizzare e gestire le schede ChatGPT già aperte nel tuo Chrome.</p></div>
          ) : visibleTabs.length === 0 ? (
            <div className="empty"><MessageSquareText size={32} /><h3>Nessuna scheda trovata</h3>
              <p>Apri ChatGPT in Chrome o scegli un altro progetto, poi aggiorna l’elenco.</p></div>
          ) : (
            <div className="chat-grid">
              {visibleTabs.map((tab, index) => (
                <article className="chat-card" key={tab.id}>
                  <div className="chat-card-head">
                    <span className="tab-number">{String(index + 1).padStart(2, '0')}</span>
                    <div className="card-heading">
                      <strong title={tab.title}>{tab.title}</strong>
                      <span title={tab.url}>{tab.url}</span>
                    </div>
                    <button className="icon-btn" title="Vai alla scheda" aria-label="Vai alla scheda"
                      disabled={working[tab.id]} onClick={() => action(tab.id, 'activateTab')}>
                      <ArrowUpRight size={18} /></button>
                  </div>
                  <div className="select-line">
                    <span>Progetto</span>
                    <select aria-label={'Progetto della scheda ' + tab.id}
                      value={projectForTab(assignments, tab)}
                      onChange={event => assignTab(tab.id, event.target.value)}>
                      <option value="">Non assegnato</option>
                      {projects.map(project => <option value={project.id} key={project.id}>{project.name}</option>)}
                    </select>
                  </div>
                  <div className="reply">
                    <div className="reply-label"><MessageSquareText size={13} /> ULTIMA RISPOSTA
                      <button onClick={() => action(tab.id, 'readLastReply')} disabled={working[tab.id]}>
                        <RefreshCw size={13} /> Leggi risposta
                      </button>
                    </div>
                    <div className="reply-text">{answers[tab.id] ||
                      'Premi “Leggi risposta” per mostrare qui il testo della chat. Non viene salvato.'}</div>
                  </div>
                  <div className="composer">
                    <textarea aria-label={'Prompt scheda ' + tab.id} rows={2}
                      value={drafts[tab.id] || ''}
                      onChange={event => setDrafts(previous => ({ ...previous, [tab.id]: event.target.value }))}
                      placeholder="Scrivi un prompt per questa chat..." maxLength={4000} />
                    <button className="btn primary send" disabled={working[tab.id]}
                      onClick={() => action(tab.id, 'sendPrompt')}>
                      <Send size={14} /> Invia
                    </button>
                  </div>
                </article>
              ))}
            </div>
          )}
          <footer className="page-footer">LllmFacade v0.1.0 · POC locale · Nessuna risposta archiviata ·
            <span> Estensione sperimentale e non ufficiale</span></footer>
        </div>
      </main>
    </div>
  );
}

