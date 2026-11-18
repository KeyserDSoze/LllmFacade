import { describe, expect, it } from 'vitest';
import {
  dotKind, dotLabel, gridColumns, isDirty, mergeLogs, parseGrid, parseIdList, tailSnippet, validateStart
} from '../../../src/admin/src/logic';
import type { LogEntry, TrackedProject } from '../../../src/admin/src/api';

const project: TrackedProject = {
  id: 'p1', endpointId: 'e1', endpointName: 'Account', projectId: 'g-p-a', name: 'Alpha', isActive: true,
  continuationPrompt: 'Continua', targetConversationId: null, autopilotEnabled: true, maxContinuations: null,
  continuationsSent: 0, status: 'Monitoring', currentConversationId: null, lastCheckAt: null, nextCheckAt: null,
  lastContinuationAt: null, consecutiveFailures: 0, lastError: null, newChatPrompt: '', chatsCreated: 0, updatedAt: '2026-11-18T10:00:00Z'
};

describe('status dot', () => {
  it('is green and animated only while ChatGPT is working', () => {
    expect(dotKind({ ...project, status: 'Generating' })).toBe('working');
    expect(dotLabel('working')).toBe('Sta lavorando');
  });

  it('is amber while waiting for the next check or for the send confirmation', () => {
    expect(dotKind({ ...project, status: 'Monitoring' })).toBe('pending');
    expect(dotKind({ ...project, status: 'Submitting' })).toBe('pending');
    expect(dotLabel('pending')).toBe('In attesa');
  });

  it('is red when the connection dropped or an error is pending', () => {
    expect(dotKind({ ...project, status: 'WaitingRetry', lastError: 'x' })).toBe('error');
    expect(dotKind({ ...project, status: 'Submitting', lastError: 'Invio non confermato' })).toBe('error');
  });

  it('is blue when completed and grey when not running', () => {
    expect(dotKind({ ...project, status: 'Completed', autopilotEnabled: false })).toBe('done');
    expect(dotKind({ ...project, status: 'Idle', autopilotEnabled: false })).toBe('idle');
    expect(dotKind({ ...project, status: 'Stopped', autopilotEnabled: false })).toBe('idle');
  });
});

describe('live text snippet', () => {
  it('keeps only the last piece, on one line', () => {
    const text = `${'a'.repeat(300)}\n\nfine   della   risposta`;

    const snippet = tailSnippet(text, 40);

    expect(snippet.startsWith('…')).toBe(true);
    expect(snippet).toHaveLength(41);
    expect(snippet.endsWith('fine della risposta')).toBe(true);
  });

  it('returns short text unchanged and empty text as empty', () => {
    expect(tailSnippet('ciao\nmondo', 40)).toBe('ciao mondo');
    expect(tailSnippet(undefined, 40)).toBe('');
  });
});

describe('grid choice', () => {
  it('accepts 1, 2 or 4 per row and falls back to 2', () => {
    expect(parseGrid('1')).toBe(1);
    expect(parseGrid('4')).toBe(4);
    expect(parseGrid('3')).toBe(2);
    expect(parseGrid(null)).toBe(2);
  });

  it('maps the choice to CSS columns', () => {
    expect(gridColumns(4)).toBe('repeat(4, minmax(0, 1fr))');
  });

  it('reads stored id lists defensively', () => {
    expect(parseIdList('["a","b"]')).toEqual(['a', 'b']);
    expect(parseIdList('{rotto')).toEqual([]);
    expect(parseIdList('[1,"x"]')).toEqual(['x']);
    expect(parseIdList(null)).toEqual([]);
  });
});

describe('start validation and dirty state', () => {
  it('asks for the prompt before starting', () => {
    expect(validateStart('  ', '')).toBe('Scrivi il prompt di continuazione prima di avviare.');
  });

  it('checks the send limit', () => {
    expect(validateStart('Continua', '0')).toBe('Il numero massimo di invii deve essere un intero tra 1 e 10000.');
    expect(validateStart('Continua', '2.5')).toBe('Il numero massimo di invii deve essere un intero tra 1 e 10000.');
    expect(validateStart('Continua', '')).toBeNull();
    expect(validateStart('Continua', '5')).toBeNull();
  });

  it('detects unsaved changes', () => {
    expect(isDirty(project, { prompt: 'Continua', target: '', max: '', newChat: '' })).toBe(false);
    expect(isDirty(project, { prompt: 'Altro', target: '', max: '', newChat: '' })).toBe(true);
    expect(isDirty(project, { prompt: 'Continua', target: 'c-1', max: '', newChat: '' })).toBe(true);
    expect(isDirty({ ...project, maxContinuations: 3 }, { prompt: 'Continua', target: '', max: '3', newChat: '' })).toBe(false);
    expect(isDirty(project, { prompt: 'Continua', target: '', max: '7', newChat: '' })).toBe(true);
    expect(isDirty(project, { prompt: 'Continua', target: '', max: '', newChat: 'Riparti da qui' })).toBe(true);
    expect(isDirty({ ...project, newChatPrompt: 'Riparti da qui' }, { prompt: 'Continua', target: '', max: '', newChat: ' Riparti da qui ' })).toBe(false);
  });
});

describe('log merging', () => {
  const entry = (id: number): LogEntry => ({
    id, at: '2026-11-18T10:00:00Z', level: 'Info', category: 'project', message: `m${id}`, endpointId: null, projectId: null, subject: null
  });

  it('merges newest first without duplicates', () => {
    expect(mergeLogs([entry(5), entry(3)], [entry(6), entry(5), entry(1)]).map(e => e.id)).toEqual([6, 5, 3, 1]);
  });
});
