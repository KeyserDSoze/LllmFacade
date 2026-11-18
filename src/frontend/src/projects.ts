export interface Project {
  id: string;
  name: string;
}

export type Assignments = Record<string, string>;

type ChatTabRef = { id: number; url?: string };

const PROJECT_KEY = 'lllmfacade.projects.v1';
const ASSIGNMENT_KEY = 'lllmfacade.assignments.v2';
const STABLE_PREFIX = 'chatgpt:c:';
const CHAT_ID = /^[a-zA-Z0-9_-]+$/;

function isStableKey(key: string): boolean {
  return key.startsWith(STABLE_PREFIX) &&
    CHAT_ID.test(key.slice(STABLE_PREFIX.length));
}

export function createProject(name: string, id: string): Project {
  const normalized = name.trim();
  if (!normalized || normalized.length > 60) {
    throw new Error('Il nome del progetto deve contenere da 1 a 60 caratteri.');
  }
  if (!id) throw new Error('ID progetto mancante');
  return { id, name: normalized };
}

export function parseProjects(raw: string | null): Project[] {
  if (!raw) return [];
  try {
    const data: unknown = JSON.parse(raw);
    if (!Array.isArray(data)) return [];
    return data.filter((item): item is Project =>
      item !== null && typeof item === 'object' &&
      typeof item.id === 'string' &&
      typeof item.name === 'string' &&
      item.id.length > 0 && item.name.trim().length > 0);
  } catch {
    return [];
  }
}

export function loadProjects(): Project[] {
  return parseProjects(localStorage.getItem(PROJECT_KEY));
}

export function saveProjects(projects: Project[]): void {
  localStorage.setItem(PROJECT_KEY, JSON.stringify(projects));
}

// Only canonical /c/{id} URLs are treated as conversations.
// Home, shared links and custom GPT routes remain transient until explicitly supported.
export function conversationKey(url: string | undefined): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== 'https:' ||
      !['chatgpt.com', 'chat.openai.com'].includes(parsed.hostname)) {
      return null;
    }
    const match = /^\/c\/([a-zA-Z0-9_-]+)\/?$/.exec(parsed.pathname);
    return match ? STABLE_PREFIX + match[1] : null;
  } catch {
    return null;
  }
}

function tabKey(id: number): string {
  return 'tab:' + id;
}

export function projectForTab(assignments: Assignments, tab: ChatTabRef): string {
  const stable = conversationKey(tab.url);
  return (stable && assignments[stable]) || assignments[tabKey(tab.id)] || '';
}

export function assignProject(
  assignments: Assignments, tab: ChatTabRef, projectId: string
): Assignments {
  const stable = conversationKey(tab.url);
  const key = stable || tabKey(tab.id);
  const next = { ...assignments };
  if (projectId) next[key] = projectId;
  else delete next[key];
  if (stable) delete next[tabKey(tab.id)];
  return next;
}

export function reconcileAssignments(
  assignments: Assignments, tabs: ChatTabRef[]
): Assignments {
  const next = { ...assignments };
  for (const tab of tabs) {
    const stable = conversationKey(tab.url);
    if (!stable) continue;
    const temporary = tabKey(tab.id);
    if (!next[stable] && next[temporary]) next[stable] = next[temporary];
    delete next[temporary];
  }
  return next;
}

function onlyStableAssignments(source: unknown): Assignments {
  if (!source || typeof source !== 'object' || Array.isArray(source)) return {};
  return Object.fromEntries(
    Object.entries(source).filter(([key, value]) =>
      isStableKey(key) && typeof value === 'string' && value.trim().length > 0
    )
  ) as Assignments;
}

export function loadAssignments(): Assignments {
  try {
    return onlyStableAssignments(JSON.parse(
      localStorage.getItem(ASSIGNMENT_KEY) || '{}'
    ));
  } catch {
    return {};
  }
}

export function saveAssignments(assignments: Assignments): void {
  // A Chrome tab ID may be reused after restart: never persist tab:{id}.
  localStorage.setItem(ASSIGNMENT_KEY, JSON.stringify(onlyStableAssignments(assignments)));
}

export function listSavedLinks(
  assignments: Assignments, projectId: string
): Array<{ key: string; url: string }> {
  return Object.entries(assignments)
    .filter(([key, value]) => isStableKey(key) && value === projectId)
    .map(([key]) => ({
      key,
      url: 'https://chatgpt.com/c/' + key.slice(STABLE_PREFIX.length)
    }));
}
