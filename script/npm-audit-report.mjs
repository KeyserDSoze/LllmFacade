import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const counts = ['critical', 'high', 'moderate', 'low'];
const workspaces = ['src/frontend', 'tests/frontend', 'src/desktop'];

export function summarizeAudit(report) {
  if (!report || typeof report !== 'object' || report.error ||
    !report.vulnerabilities || typeof report.vulnerabilities !== 'object' ||
    !report.metadata?.vulnerabilities) {
    throw new Error('Invalid npm audit report or npm audit failed.');
  }

  const findings = Object.entries(report.vulnerabilities).map(([name, detail]) => {
    const via = Array.isArray(detail.via) ? detail.via : [];
    const advisories = [...new Set(via
      .map(item => typeof item === 'object' && item !== null ? item.url : null)
      .filter(value => typeof value === 'string' && value.startsWith('https://')))];
    let fix = 'unavailable';
    if (detail.fixAvailable === true) fix = 'available';
    else if (detail.fixAvailable && typeof detail.fixAvailable === 'object') {
      fix = detail.fixAvailable.name + '@' + detail.fixAvailable.version +
        (detail.fixAvailable.isSemVerMajor ? ' (major)' : '');
    }
    return {
      package: name,
      severity: detail.severity,
      direct: detail.isDirect === true,
      affected: detail.range || '',
      fix,
      advisories
    };
  }).sort((a, b) => a.package.localeCompare(b.package));

  return {
    counts: Object.fromEntries(counts.map(severity =>
      [severity, report.metadata.vulnerabilities[severity] || 0])),
    findings
  };
}

// Blocking policy: production dependency advisories affect the deployed SPA,
// while Electron ships its devDependency runtime and must be audited in full.
// Tooling-only advisories remain visible but do not silently block releases.
export function securityBlockers(reports) {
  const required = [
    ['src/frontend', 'production'],
    ['tests/frontend', 'production'],
    ['src/desktop', 'all']
  ];
  const blockers = [];
  for (const [workspace, scope] of required) {
    const findings = reports?.[workspace]?.[scope]?.findings;
    if (!Array.isArray(findings)) {
      throw new Error('Missing or invalid npm audit report: ' + workspace + '/' + scope);
    }
    for (const finding of findings) {
      if (finding.severity === 'high' || finding.severity === 'critical') {
        blockers.push({
          workspace, scope, package: finding.package, severity: finding.severity
        });
      }
    }
  }
  return blockers;
}

function npm(cwd, args, allowAuditFindings = false) {
  const result = spawnSync('npm', args, {
    cwd, encoding: 'utf8', maxBuffer: 10 * 1024 * 1024,
    env: { ...process.env, npm_config_fund: 'false' }
  });
  if (result.error) throw result.error;
  if (result.status !== 0 && !allowAuditFindings) {
    throw new Error('npm ' + args[0] + ' failed in ' + cwd + ': ' +
      (result.stderr || result.stdout).slice(-1000));
  }
  return result.stdout;
}

function main() {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  let criticalOrHigh = 0;
  const reports = {};
  for (const workspace of workspaces) {
    const cwd = join(root, workspace);
    reports[workspace] = {};
    console.log('\n## ' + workspace);
    npm(cwd, ['install', '--package-lock-only', '--ignore-scripts', '--no-audit']);
    for (const scope of ['all', 'production']) {
      const args = ['audit', '--json'];
      if (scope === 'production') args.push('--omit=dev');
      const result = summarizeAudit(JSON.parse(npm(cwd, args, true)));
      reports[workspace][scope] = result;
      if (scope === 'all') criticalOrHigh += result.counts.critical + result.counts.high;
      console.log(scope + ': ' + JSON.stringify(result.counts));
      for (const finding of result.findings) console.log('  ' + JSON.stringify(finding));
    }
  }
  if (process.argv.includes('--gate')) {
    const blockers = securityBlockers(reports);
    if (blockers.length) {
      console.error('Blocking security advisories: ' + JSON.stringify(blockers));
      process.exitCode = 1;
    }
  }
  if (process.argv.includes('--strict') && criticalOrHigh > 0) {
    console.error('Security gate failed: ' + criticalOrHigh + ' high or critical advisories.');
    process.exitCode = 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); }
  catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
