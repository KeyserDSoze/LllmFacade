'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { randomBytes } = require('node:crypto');

const WORKSPACE_VERSION = 1;
const DEFAULT_URL = 'https://chatgpt.com/';
const PANE_COUNT = 12;
const MAX_FILE_BYTES = 16 * 1024;
const MAX_URL_LENGTH = 2048;
const LAYOUTS = new Set([1, 4, 6, 12]);

function createDefaultWorkspace() {
  return {
    version: WORKSPACE_VERSION,
    layout: 4,
    urls: Array(PANE_COUNT).fill(DEFAULT_URL)
  };
}

function sanitizeUrl(input) {
  try {
    if (typeof input !== 'string' || !input.trim() || input.length > MAX_URL_LENGTH) {
      throw new Error('bad URL');
    }
    const url = new URL(input);
    const localHttp = url.protocol === 'http:' &&
      (url.hostname === 'localhost' || url.hostname === '127.0.0.1');
    if (!(url.protocol === 'https:' || localHttp) || url.username || url.password) {
      throw new Error('unsafe URL');
    }
    url.search = '';
    url.hash = '';
    return url.toString();
  } catch {
    throw new Error('URL non valido: usa HTTPS o HTTP locale senza credenziali.');
  }
}

function normalizeWorkspace(raw) {
  const fallback = createDefaultWorkspace();
  if (!raw || typeof raw !== 'object' || Array.isArray(raw) ||
      raw.version !== WORKSPACE_VERSION) {
    return fallback;
  }
  const layout = LAYOUTS.has(raw.layout) ? raw.layout : fallback.layout;
  const urls = fallback.urls.map((initial, index) => {
    const candidate = Array.isArray(raw.urls) ? raw.urls[index] : undefined;
    try {
      return sanitizeUrl(candidate);
    } catch {
      return initial;
    }
  });
  return { version: WORKSPACE_VERSION, layout, urls };
}

function updateLayout(workspace, layout) {
  if (!LAYOUTS.has(layout)) throw new Error('Layout non valido.');
  return { ...normalizeWorkspace(workspace), layout };
}

function updateUrl(workspace, id, url) {
  if (!Number.isInteger(id) || id < 0 || id >= PANE_COUNT) {
    throw new Error('ID del riquadro non valido.');
  }
  const next = normalizeWorkspace(workspace);
  next.urls[id] = sanitizeUrl(url);
  return next;
}

function readWorkspace(filename) {
  try {
    const stats = fs.lstatSync(filename);
    if (!stats.isFile() || stats.isSymbolicLink() || stats.size > MAX_FILE_BYTES) {
      return createDefaultWorkspace();
    }
    return normalizeWorkspace(JSON.parse(fs.readFileSync(filename, 'utf8')));
  } catch {
    return createDefaultWorkspace();
  }
}

function writeWorkspace(filename, workspace) {
  const data = JSON.stringify(normalizeWorkspace(workspace));
  if (Buffer.byteLength(data, 'utf8') > MAX_FILE_BYTES) {
    throw new Error('Configurazione workspace troppo grande.');
  }
  const dir = path.dirname(filename);
  fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  const temporary = filename + '.' + process.pid + '.' + randomBytes(6).toString('hex') + '.tmp';
  try {
    fs.writeFileSync(temporary, data, { encoding: 'utf8', mode: 0o600, flag: 'wx' });
    fs.renameSync(temporary, filename);
  } finally {
    try { fs.unlinkSync(temporary); } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
    }
  }
}

module.exports = {
  WORKSPACE_VERSION,
  DEFAULT_URL,
  createDefaultWorkspace,
  sanitizeUrl,
  normalizeWorkspace,
  updateLayout,
  updateUrl,
  readWorkspace,
  writeWorkspace
};
