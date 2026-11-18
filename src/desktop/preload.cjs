'use strict';
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('desktop', Object.freeze({
  layout: (rectangles) => ipcRenderer.invoke('desktop:layout', rectangles),
  navigate: (id, url) => ipcRenderer.invoke('desktop:navigate', id, url),
  reload: (id) => ipcRenderer.invoke('desktop:reload', id),
  external: (id) => ipcRenderer.invoke('desktop:external', id)
}));

