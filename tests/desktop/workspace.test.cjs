'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  DEFAULT_URL, WORKSPACE_VERSION, createDefaultWorkspace,
  sanitizeUrl, normalizeWorkspace, updateLayout, updateUrl,
  readWorkspace, writeWorkspace
} = require('../../src/desktop/workspace.cjs');

test('default has 12 named URLs, even with a four-pane layout', () => {
  const initial = createDefaultWorkspace();
  assert.deepEqual(Object.keys(initial).sort(), ['layout', 'urls', 'version']);
  assert.equal(initial.version, WORKSPACE_VERSION);
  assert.equal(initial.layout, 4);
  assert.deepEqual(initial.urls, Array(12).fill(DEFAULT_URL));
});

test('accepts HTTPS and local HTTP, removes URL query and hash before storage', () => {
  assert.equal(
    sanitizeUrl('https://chatgpt.com/c/my-chat?token=SECRET#part'),
    'https://chatgpt.com/c/my-chat'
  );
  assert.equal(sanitizeUrl('http://localhost:5173/demo?code=secret'), 'http://localhost:5173/demo');
  assert.equal(sanitizeUrl('http://127.0.0.1:5000/'), 'http://127.0.0.1:5000/');
});

test('rejects remote plaintext, credential-bearing, dangerous and excessive URLs', () => {
  for (const url of [
    'http://chatgpt.com/c/abc',
    'file:///etc/passwd',
    'javascript:alert(1)',
    'data:text/html,hello',
    'https://user:secret@chatgpt.com/',
    'http://localhost.evil.example/',
    'http://[::1]:1234/',
    'not a URL',
    'https://chatgpt.com/' + 'a'.repeat(4096)
  ]) {
    assert.throws(() => sanitizeUrl(url), /URL/i, url);
  }
});

test('refuses invalid layouts, invalid IDs and preserves hidden panes', () => {
  const starting = createDefaultWorkspace();
  assert.throws(() => updateLayout(starting, 3), /layout/i);
  assert.throws(() => updateLayout(starting, '12'), /layout/i);
  assert.throws(() => updateUrl(starting, 12, DEFAULT_URL), /riquadro/i);
  const customized = updateUrl(starting, 11, 'https://chatgpt.com/c/hidden');
  const single = updateLayout(customized, 1);
  assert.equal(single.urls[11], 'https://chatgpt.com/c/hidden');
  assert.equal(starting.urls[11], DEFAULT_URL);
  assert.equal(updateLayout(single, 12).urls[11], 'https://chatgpt.com/c/hidden');
});

test('restores safe valid URLs; unknown schema resets safely', () => {
  assert.deepEqual(normalizeWorkspace(null), createDefaultWorkspace());
  assert.deepEqual(normalizeWorkspace({version:99,layout:12,urls:[]}), createDefaultWorkspace());
  const valid = normalizeWorkspace({
    version: 1, layout: 12,
    urls: ['https://chatgpt.com/c/abc?secret=yes', 'file:///tmp/test', 4]
  });
  assert.equal(valid.layout, 12);
  assert.equal(valid.urls.length, 12);
  assert.equal(valid.urls[0], 'https://chatgpt.com/c/abc');
  assert.equal(valid.urls[1], DEFAULT_URL);
  assert.equal(valid.urls[2], DEFAULT_URL);
  assert.equal(normalizeWorkspace({version:1,layout:9,urls:[]}).layout, 4);
});

test('atomic file persistence round-trips, discards secrets and ignores malformed/huge files', (t) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'lllmfacade-workspace-'));
  t.after(() => fs.rmSync(dir, {recursive:true,force:true}));
  const file = path.join(dir, 'workspace.json');
  assert.deepEqual(readWorkspace(file), createDefaultWorkspace());

  const desired = updateUrl(
    updateLayout(createDefaultWorkspace(), 6),
    9, 'https://chatgpt.com/c/abc?authorization=DO_NOT_STORE#secret'
  );
  writeWorkspace(file, desired);
  const raw = fs.readFileSync(file, 'utf8');
  assert.ok(!raw.includes('DO_NOT_STORE'));
  assert.ok(!raw.includes('#secret'));
  assert.deepEqual(readWorkspace(file), desired);
  assert.deepEqual(fs.readdirSync(dir), ['workspace.json']);
  if (process.platform !== 'win32') {
    assert.equal(fs.statSync(file).mode & 0o777, 0o600);
  }

  fs.writeFileSync(file, '{');
  assert.deepEqual(readWorkspace(file), createDefaultWorkspace());
  fs.writeFileSync(file, 'x'.repeat(64000));
  assert.deepEqual(readWorkspace(file), createDefaultWorkspace());
});
