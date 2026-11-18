'use strict';
const grid = document.getElementById('grid');
const countSelect = document.getElementById('count');
const status = document.getElementById('status');
let count = 4;
let focused = null;
let nextLayout = 0;

function columnsFor(length) {
  if (length <= 1) return 1;
  if (length <= 4) return 2;
  if (length <= 6) return 3;
  return 4;
}

function scheduleLayout() {
  cancelAnimationFrame(nextLayout);
  nextLayout = requestAnimationFrame(async () => {
    const rectangles = [...document.querySelectorAll('.tile')].map(tile => {
      const bounds = tile.querySelector('.viewport').getBoundingClientRect();
      return {
        id: Number(tile.dataset.id),
        x: Math.round(bounds.left),
        y: Math.round(bounds.top),
        width: Math.floor(bounds.width),
        height: Math.floor(bounds.height)
      };
    });
    try {
      await window.desktop.layout(rectangles);
      status.textContent = rectangles.length + ' pagine reali · sessione Electron condivisa';
    } catch (error) {
      status.textContent = 'Errore layout: ' + error.message;
    }
  });
}

function button(label, tooltip, handler) {
  const element = document.createElement('button');
  element.type = 'button';
  element.textContent = label;
  element.title = tooltip;
  element.addEventListener('click', handler);
  return element;
}

function createTile(id) {
  const tile = document.createElement('section');
  tile.className = 'tile';
  tile.dataset.id = String(id);
  const toolbar = document.createElement('div');
  toolbar.className = 'tile-toolbar';
  const label = document.createElement('span');
  label.className = 'tile-label';
  label.textContent = 'CHAT ' + String(id + 1).padStart(2, '0');
  const url = document.createElement('input');
  url.type = 'url';
  url.placeholder = 'https://chatgpt.com/c/...';
  url.value = 'https://chatgpt.com/';
  url.title = 'Incolla qui la URL della conversazione';
  url.addEventListener('keydown', async event => {
    if (event.key !== 'Enter') return;
    try {
      await window.desktop.navigate(id, url.value);
      url.blur();
    } catch (error) {
      status.textContent = error.message;
    }
  });
  toolbar.appendChild(label);
  toolbar.appendChild(url);
  toolbar.appendChild(button('Vai', 'Naviga all’URL inserito', async () => {
    try { await window.desktop.navigate(id, url.value); } catch (error) {
      status.textContent = error.message;
    }
  }));
  toolbar.appendChild(button('↻', 'Ricarica', () => window.desktop.reload(id)));
  toolbar.appendChild(button('↗', 'Apri nel browser predefinito', () => window.desktop.external(id)));
  toolbar.appendChild(button(focused === id ? '▦' : '□', 'Espandi o ripristina la griglia', () => {
    focused = focused === id ? null : id;
    render();
  }));
  const viewport = document.createElement('div');
  viewport.className = 'viewport';
  tile.appendChild(toolbar);
  tile.appendChild(viewport);
  return tile;
}

function render() {
  const ids = focused === null ? Array.from({ length: count }, (_, i) => i) : [focused];
  grid.style.setProperty('--columns', String(columnsFor(ids.length)));
  grid.replaceChildren(...ids.map(createTile));
  scheduleLayout();
}

countSelect.addEventListener('change', () => {
  count = Number(countSelect.value);
  focused = null;
  render();
});
document.getElementById('all').addEventListener('click', () => {
  focused = null;
  render();
});
window.addEventListener('resize', scheduleLayout);
new ResizeObserver(scheduleLayout).observe(grid);
render();

