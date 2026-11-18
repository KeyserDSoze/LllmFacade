'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('desktop', Object.freeze({
  workspace: () => ipcRenderer.invoke('desktop:workspace'),
  setLayout: layout => ipcRenderer.invoke('desktop:set-layout', layout),
  layout: rectangles => ipcRenderer.invoke('desktop:layout', rectangles),
  navigate: (id, url) => ipcRenderer.invoke('desktop:navigate', id, url),
  reload: id => ipcRenderer.invoke('desktop:reload', id),
  external: id => ipcRenderer.invoke('desktop:external', id),
  onUrlChanged: handler => {
    const listener = (_event, detail) => handler(detail);
    ipcRenderer.on('desktop:url-changed', listener);
    return () => ipcRenderer.removeListener('desktop:url-changed', listener);
  }
}));
