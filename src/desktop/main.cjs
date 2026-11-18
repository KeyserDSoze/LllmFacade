'use strict';
const path = require('node:path');
const {
  app, BrowserWindow, WebContentsView, ipcMain, session, shell
} = require('electron');
const {
  createDefaultWorkspace, readWorkspace, writeWorkspace,
  sanitizeUrl, updateLayout, updateUrl
} = require('./workspace.cjs');

let window;
let workspace = createDefaultWorkspace();
let workspaceFile;
const views = new Map();
const partition = 'persist:lllmfacade';

function saveWorkspace(next) {
  writeWorkspace(workspaceFile, next);
  workspace = next;
}

function validateSender(event) {
  if (!window || event.sender !== window.webContents ||
      event.senderFrame !== window.webContents.mainFrame) {
    throw new Error('Richiesta desktop non autorizzata.');
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
    try {
      sanitizeUrl(url);
      void shell.openExternal(url);
    } catch {
      // Never delegate privileged or unsafe protocols to the OS.
    }
    return { action: 'deny' };
  });

  view.webContents.on('will-navigate', (event, navigationInfo) => {
    try {
      sanitizeUrl(navigationInfo?.url || event.url || '');
    } catch {
      event.preventDefault();
    }
  });

  view.webContents.on('did-navigate', (_event, navigationInfo) => {
    const url = navigationInfo?.url;
    if (typeof url !== 'string') return;
    try {
      const stored = sanitizeUrl(url);
      if (workspace.urls[id] !== stored) {
        saveWorkspace(updateUrl(workspace, id, stored));
      }
      if (window && !window.isDestroyed()) {
        window.webContents.send('desktop:url-changed', { id, url: stored });
      }
    } catch {
      // Redirects with unsafe URLs must not be written into workspace storage.
    }
  });

  window.contentView.addChildView(view);
  views.set(id, view);
  void view.webContents.loadURL(workspace.urls[id]).catch(() => {
    // Surface a navigation error when the user explicitly navigates.
    // A startup failure (e.g. offline) must not prevent opening the workspace.
  });
  return view;
}

function registerIpc() {
  ipcMain.handle('desktop:workspace', event => {
    validateSender(event);
    return { ...workspace, urls: [...workspace.urls] };
  });

  ipcMain.handle('desktop:set-layout', (event, layout) => {
    validateSender(event);
    saveWorkspace(updateLayout(workspace, layout));
    return workspace.layout;
  });

  ipcMain.handle('desktop:layout', (event, rectangles) => {
    validateSender(event);
    if (!Array.isArray(rectangles) || rectangles.length > 12) {
      throw new Error('Layout non valido');
    }
    const enabled = new Set();
    // Validate every rectangle before instantiating any remote view.
    for (const rectangle of rectangles) {
      if (!rectangle || typeof rectangle !== 'object') {
        throw new Error('Coordinate layout non valide');
      }
      const { id, x, y, width, height } = rectangle;
      if (![id, x, y, width, height].every(Number.isFinite) ||
          !Number.isInteger(id) || id < 0 || id >= 12 || enabled.has(id) ||
          !Number.isInteger(x) || !Number.isInteger(y) ||
          !Number.isInteger(width) || !Number.isInteger(height) ||
          width < 40 || height < 40 || width > 10000 || height > 10000) {
        throw new Error('Coordinate layout non valide');
      }
      enabled.add(id);
    }
    for (const rectangle of rectangles) {
      const view = ensureView(rectangle.id);
      view.setBounds({
        x: rectangle.x, y: rectangle.y,
        width: rectangle.width, height: rectangle.height
      });
      view.setVisible(true);
    }
    for (const [id, view] of views) {
      if (!enabled.has(id)) view.setVisible(false);
    }
  });

  ipcMain.handle('desktop:navigate', async (event, id, raw) => {
    validateSender(event);
    const stored = sanitizeUrl(raw);
    if (!Number.isInteger(id) || id < 0 || id >= 12) throw new Error('Riquadro non valido');
    const view = ensureView(id);
    // User-requested query strings can be used for navigation, but are never persisted.
    await view.webContents.loadURL(new URL(raw).toString());
    saveWorkspace(updateUrl(workspace, id, stored));
    return stored;
  });

  ipcMain.handle('desktop:reload', (event, id) => {
    validateSender(event);
    ensureView(id).webContents.reload();
  });

  ipcMain.handle('desktop:external', async (event, id) => {
    validateSender(event);
    const url = ensureView(id).webContents.getURL();
    sanitizeUrl(url);
    await shell.openExternal(url);
  });
}

app.whenReady().then(() => {
  workspaceFile = path.join(app.getPath('userData'), 'workspace.json');
  workspace = readWorkspace(workspaceFile);

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
