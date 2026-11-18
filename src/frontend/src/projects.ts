export interface Project {
  id: string;
  name: string;
}

export type Assignments = Record<string, string>;

const PROJECT_KEY = 'lllmfacade.projects.v1';
const ASSIGNMENT_KEY = 'lllmfacade.assignments.v1';

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

export function loadAssignments(): Assignments {
  try {
    const data: unknown = JSON.parse(localStorage.getItem(ASSIGNMENT_KEY) || '{}');
    if (!data || typeof data !== 'object' || Array.isArray(data)) return {};
    return Object.fromEntries(Object.entries(data).filter(
      ([key, value]) => key.length > 0 && typeof value === 'string'
    )) as Assignments;
  } catch {
    return {};
  }
}

export function saveAssignments(assignments: Assignments): void {
  localStorage.setItem(ASSIGNMENT_KEY, JSON.stringify(assignments));
}

export function projectForTab(assignments: Assignments, tab: { id: number }): string {
  return assignments[String(tab.id)] || '';
}

