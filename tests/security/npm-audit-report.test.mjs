import test from 'node:test';
import assert from 'node:assert/strict';
import { summarizeAudit, securityBlockers } from '../../script/npm-audit-report.mjs';

test('empty audit report has zero vulnerabilities', () => {
  assert.deepEqual(summarizeAudit({
    metadata: { vulnerabilities: { critical: 0, high: 0, moderate: 0, low: 0 } },
    vulnerabilities: {}
  }), { counts: { critical: 0, high: 0, moderate: 0, low: 0 }, findings: [] });
});

test('captures direct, transitive and suggested fixes without overstating remediation', () => {
  const audit = {
    metadata: { vulnerabilities: { critical: 1, high: 1, moderate: 0, low: 0 } },
    vulnerabilities: {
      'vitest': { name: 'vitest', severity: 'critical', isDirect: true,
        range: '<4.0.0', fixAvailable: { name: 'vitest', version: '4.0.0', isSemVerMajor: true },
        via: [{ name: 'vite', url: 'https://github.com/advisories/GHSA-example' }] },
      'vite': { name: 'vite', severity: 'high', isDirect: false, range: '<7.0.0',
        fixAvailable: true, via: ['another-advisory'] }
    }
  };
  const report = summarizeAudit(audit);
  assert.equal(report.counts.critical, 1);
  assert.equal(report.findings.length, 2);
  assert.deepEqual(report.findings.find(x => x.package === 'vitest'), {
    package: 'vitest', severity: 'critical', direct: true, affected: '<4.0.0',
    fix: 'vitest@4.0.0 (major)', advisories: ['https://github.com/advisories/GHSA-example']
  });
  assert.equal(report.findings.find(x => x.package === 'vite')?.fix, 'available');
});

test('fails closed on invalid audit output', () => {
  assert.throws(() => summarizeAudit({ error: { code: 'ENOAUDIT' } }), /audit failed/i);
  assert.throws(() => summarizeAudit({}), /invalid npm audit/i);
  assert.throws(() => summarizeAudit(null), /invalid npm audit/i);
});

test('security gate blocks production high severity and Electron devDependency vulnerabilities', () => {
  const empty = { findings: [] };
  const high = { findings: [{ package: 'x', severity: 'high' }] };
  const critical = { findings: [{ package: 'electron', severity: 'critical' }] };
  const input = {
    'src/frontend': { all: high, production: empty },
    'tests/frontend': { all: high, production: empty },
    'src/desktop': { all: critical, production: empty },
    'src/admin': { all: high, production: empty },
    'tests/admin': { all: high, production: empty }
  };
  assert.deepEqual(securityBlockers(input), [{
    workspace: 'src/desktop', scope: 'all', package: 'electron', severity: 'critical'
  }]);
  input['src/frontend'].production = high;
  assert.equal(securityBlockers(input).length, 2);
});

test('security gate covers the production dependencies of the admin SPA served by the API', () => {
  const empty = { findings: [] };
  const input = Object.fromEntries(['src/frontend', 'tests/frontend', 'src/desktop', 'src/admin', 'tests/admin']
    .map(workspace => [workspace, { all: empty, production: empty }]));
  input['src/admin'] = { all: empty, production: { findings: [{ package: 'react-dom', severity: 'high' }] } };
  assert.deepEqual(securityBlockers(input), [{
    workspace: 'src/admin', scope: 'production', package: 'react-dom', severity: 'high'
  }]);
  delete input['tests/admin'];
  assert.throws(() => securityBlockers(input), /tests\/admin/);
});

test('security gate refuses missing or malformed workspace audit data', () => {
  assert.throws(() => securityBlockers({}), /missing/i);
});
