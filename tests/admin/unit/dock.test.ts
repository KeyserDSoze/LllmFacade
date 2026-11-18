import { describe, expect, it } from 'vitest';
import { dockHeadline, elapsedLabel, relativeTime, workingCount } from '../../../src/admin/src/logic';
import type { ActivitySummary } from '../../../src/admin/src/api';

const now = new Date('2026-11-18T12:00:00Z');

describe('relative time', () => {
  it('describes the future', () => {
    expect(relativeTime('2026-11-18T12:18:00Z', now)).toBe('tra 18 min');
    expect(relativeTime('2026-11-18T15:00:00Z', now)).toBe('tra 3 ore');
    expect(relativeTime('2026-11-20T12:00:00Z', now)).toBe('tra 2 giorni');
  });

  it('describes the past', () => {
    expect(relativeTime('2026-11-18T11:48:00Z', now)).toBe('12 min fa');
    expect(relativeTime('2026-11-18T09:00:00Z', now)).toBe('3 ore fa');
    expect(relativeTime('2026-11-16T12:00:00Z', now)).toBe('2 giorni fa');
  });

  it('says "adesso" around the present and handles missing values', () => {
    expect(relativeTime('2026-11-18T12:00:20Z', now)).toBe('adesso');
    expect(relativeTime('2026-11-18T11:59:40Z', now)).toBe('adesso');
    expect(relativeTime(null, now)).toBe('mai');
  });
});

describe('elapsed time', () => {
  it('formats seconds, minutes and hours', () => {
    expect(elapsedLabel('2026-11-18T11:59:30Z', now)).toBe('da 30 s');
    expect(elapsedLabel('2026-11-18T11:47:00Z', now)).toBe('da 13 min');
    expect(elapsedLabel('2026-11-18T09:30:00Z', now)).toBe('da 2 h 30 min');
    expect(elapsedLabel(null, now)).toBe('');
  });
});

describe('dock headline', () => {
  const summary = (working: number): ActivitySummary => ({
    working: Array.from({ length: working }, (_, index) => ({
      kind: 'project' as const, id: `p${index}`, name: `P${index}`, endpointName: 'E', detail: 'ChatGPT sta scrivendo', since: null
    })),
    watching: 2, endpoints: [], serverTime: '2026-11-18T12:00:00Z'
  });

  it('counts the threads working together', () => {
    expect(dockHeadline(summary(0))).toBe('Nessun thread al lavoro');
    expect(dockHeadline(summary(1))).toBe('1 thread al lavoro');
    expect(dockHeadline(summary(3))).toBe('3 thread al lavoro');
    expect(workingCount(summary(3))).toBe(3);
  });

  it('shows a placeholder before the first answer', () => {
    expect(dockHeadline(null)).toBe('Stato in caricamento…');
    expect(workingCount(null)).toBe(0);
  });
});
