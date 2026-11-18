import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  assignProject, conversationKey, createProject, listSavedLinks,
  loadAssignments, parseProjects, projectForTab,
  reconcileAssignments, saveAssignments
} from '../../../src/frontend/src/projects';

describe('project metadata rules', () => {
  it('trims project names', () => {
    expect(createProject('  Backend  ', 'a')).toEqual({ id: 'a', name: 'Backend' });
  });

  it('rejects blank and overlong names', () => {
    expect(() => createProject('   ', 'a')).toThrow();
    expect(() => createProject('x'.repeat(61), 'a')).toThrow();
  });

  it('ignores malformed persisted projects', () => {
    expect(parseProjects('not-json')).toEqual([]);
    expect(parseProjects('[{"name":"A"}, {"id":"x","name":"Valid"}]')).toEqual([
      { id: 'x', name: 'Valid' }
    ]);
  });
});

describe('stable ChatGPT conversation identity', () => {
  const key = 'chatgpt:c:abc-123';
  const chat = (id: number, url: string) => ({ id, url });

  it('normalizes ChatGPT conversation URLs across aliases, parameters and tabs', () => {
    expect(conversationKey('https://chatgpt.com/c/abc-123?model=gpt-6#x')).toBe(key);
    expect(conversationKey('https://chat.openai.com/c/abc-123/')).toBe(key);
    expect(projectForTab({ [key]: 'p1' }, chat(101, 'https://chatgpt.com/c/abc-123')))
      .toBe('p1');
    expect(projectForTab({ [key]: 'p1' }, chat(222, 'https://chatgpt.com/c/abc-123')))
      .toBe('p1');
  });

  it('does not persist arbitrary or ambiguous URLs as chat identities', () => {
    expect(conversationKey('https://chatgpt.com/')).toBeNull();
    expect(conversationKey('https://chatgpt.com/share/abc-123')).toBeNull();
    expect(conversationKey('https://chatgpt.com/g/g-foo/c/abc-123')).toBeNull();
    expect(conversationKey('http://chatgpt.com/c/abc-123')).toBeNull();
    expect(conversationKey('https://chatgpt.com.evil.example/c/abc-123')).toBeNull();
    expect(conversationKey('not-an-url')).toBeNull();
  });

  it('uses a transient tab key on home and upgrades when a conversation is created', () => {
    const pending = assignProject({}, chat(42, 'https://chatgpt.com/'), 'p1');
    expect(pending).toEqual({ 'tab:42': 'p1' });
    expect(projectForTab(pending, chat(42, 'https://chatgpt.com/'))).toBe('p1');

    const reconciled = reconcileAssignments(pending, [
      chat(42, 'https://chatgpt.com/c/abc-123')
    ]);
    expect(reconciled).toEqual({ [key]: 'p1' });
    expect(projectForTab(reconciled, chat(87, 'https://chatgpt.com/c/abc-123'))).toBe('p1');
  });

  it('does not overwrite an existing permanent association when a temporary tab changes URL', () => {
    expect(reconcileAssignments({ [key]: 'p1', 'tab:7': 'p2' }, [
      chat(7, 'https://chatgpt.com/c/abc-123')
    ])).toEqual({ [key]: 'p1' });
  });

  it('can assign and unassign persistent and temporary projects', () => {
    const stored = assignProject({}, chat(7, 'https://chatgpt.com/c/abc-123'), 'p1');
    expect(stored).toEqual({ [key]: 'p1' });
    expect(assignProject(stored, chat(7, 'https://chatgpt.com/c/abc-123'), ''))
      .toEqual({});
    expect(assignProject({ 'tab:7': 'p1' }, chat(7, 'https://chatgpt.com/'), ''))
      .toEqual({});
  });

  it('lists saved conversations even when their Chrome tabs are closed', () => {
    expect(listSavedLinks({
      [key]: 'p1',
      'chatgpt:c:other': 'p2',
      'tab:7': 'p1'
    }, 'p1')).toEqual([{ key, url: 'https://chatgpt.com/c/abc-123' }]);
  });
});

describe('local assignment storage', () => {
  const storage = new Map<string, string>();
  afterEach(() => {
    vi.unstubAllGlobals();
    storage.clear();
  });

  it('writes permanent conversation associations but never saves temporary tab IDs', () => {
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value)
    });
    saveAssignments({ 'chatgpt:c:abc-123': 'p1', 'tab:3': 'p2' });
    expect(loadAssignments()).toEqual({ 'chatgpt:c:abc-123': 'p1' });
    expect(JSON.stringify([...storage.values()])).not.toContain('tab:3');
  });

  it('ignores old numeric tab associations so they cannot attach to a reused Chrome tab ID', () => {
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => key === 'lllmfacade.assignments.v1'
        ? '{"7":"old-project"}'
        : null,
      setItem: () => undefined
    });
    expect(loadAssignments()).toEqual({});
  });
});
