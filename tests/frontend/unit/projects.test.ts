import { describe, expect, it } from 'vitest';
import {
  createProject, parseProjects, projectForTab
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

  it('finds associated project by tab ID', () => {
    expect(projectForTab({ '7': 'project-1' }, { id: 7 })).toBe('project-1');
    expect(projectForTab({}, { id: 7 })).toBe('');
  });
});

