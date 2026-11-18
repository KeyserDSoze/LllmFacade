'use strict';
const path = require('node:path');
const {
  app, BrowserWindow, WebContentsView, ipcMain, session, shell
} = require('electron');

let window;
const views = new Map();
const partition = 'persist:lllmfacade';
const defaultUrl = 'https://chatgpt.com/';

function parseUrl(raw) {
  try {
    const parsed = new URL(raw);
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('unsupported protocol');
    return parsed.toString();
  } catch {
    throw new Error('Inserisci un URL http(s) valido.');
  }
}

function ensureView(id) {
  if (!Number.isInteger(id) || id < 0 || id >= 12) throw new Error('Riquadro non valido');
  if (views.has(id)) return views.get(id);
  const view = new WebContentsView({
    webPreferences: {
      partition,
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true
    }
  });
  view.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) {
      void shell.openExternal(url);
    }
    return { action: 'deny' };
  });
  view.webContents.on('will-navigate', event => {
    // Remote pages are browser content; never allow navigation to privileged schemes.
    const url = event.url || '';
    if (!url.startsWith('https://') && !url.startsWith('http://')) event.preventDefault();
  });
  window.contentView.addChildView(view);
  views.set(id, view);
  void view.webContents.loadURL(defaultUrl).catch(() => {});
  return view;
}

function registerIpc() {
  ipcMain.handle('desktop:layout', (_event, rectangles) => {
    if (!Array.isArray(rectangles) || rectangles.length > 12) {
      throw new Error('Layout non valido');
    }
    const enabled = new Set();
    for (const rectangle of rectangles) {
      const { id, x, y, width, height } = rectangle;
      if (![id, x, y, width, height].every(Number.isFinite) ||
          !Number.isInteger(id) || !Number.isInteger(x) || !Number.isInteger(y) ||
          !Number.isInteger(width) || !Number.isInteger(height) ||
          width < 40 || height < 40 || width > 10000 || height > 10000) {
        throw new Error('Coordinate layout non valide');
      }
      const view = ensureView(id);
      view.setBounds({ x, y, width, height });
      view.setVisible(true);
      enabled.add(id);
    }
    for (const [id, view] of views) {
      if (!enabled.has(id)) view.setVisible(false);
    }
  });

  ipcMain.handle('desktop:navigate', async (_event, id, raw) => {
    const view = ensureView(id);
    await view.webContents.loadURL(parseUrl(raw));
    return view.webContents.getURL();
  });
  ipcMain.handle('desktop:reload', (_event, id) => {
    ensureView(id).webContents.reload();
  });
  ipcMain.handle('desktop:external', async (_event, id) => {
    const url = ensureView(id).webContents.getURL();
    if (url.startsWith('https://') || url.startsWith('http://')) {
      await shell.openExternal(url);
    }
  });
}

app.whenReady().then(() => {
  const browserSession = session.fromPartition(partition);
  browserSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  window = new BrowserWindow({
    width: 1600,
    height: 1000,
    minWidth: 700,
    minHeight: 450,
    backgroundColor: '#0b0e18',
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  registerIpc();
  void window.loadFile(path.join(__dirname, 'renderer', 'index.html'));
});

app.on('window-all-closed', () => app.quit());

