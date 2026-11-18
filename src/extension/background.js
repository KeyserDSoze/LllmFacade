/* Chrome MV3 external messaging service worker. */
importScripts('security.js');

const security = globalThis.LllmSecurity;

function summary(tab) {
  return {
    id: tab.id,
    title: tab.title || 'ChatGPT',
    url: tab.url || '',
    active: Boolean(tab.active),
    windowId: tab.windowId
  };
}

async function requireChatTab(tabId) {
  const tab = await chrome.tabs.get(tabId);
  if (!security.isChatUrl(tab.url)) throw new Error('Questa scheda non è ChatGPT');
  return tab;
}

async function inject(tabId, func, args = []) {
  const result = await chrome.scripting.executeScript({
    target: { tabId },
    func,
    args
  });
  return result[0]?.result;
}

async function perform(message) {
  switch (message.action) {
    case 'ping':
      return { version: chrome.runtime.getManifest().version };
    case 'listTabs': {
      const tabs = await chrome.tabs.query({});
      return tabs.filter(tab => security.isChatUrl(tab.url)).map(summary);
    }
    case 'openChat': {
      const tab = await chrome.tabs.create({
        url: 'https://chatgpt.com/',
        active: true
      });
      return summary(tab);
    }
    case 'activateTab': {
      const tab = await requireChatTab(message.tabId);
      await chrome.windows.update(tab.windowId, { focused: true });
      const activated = await chrome.tabs.update(tab.id, { active: true });
      return summary(activated);
    }
    case 'sendPrompt': {
      await requireChatTab(message.tabId);
      const outcome = await inject(message.tabId, async (prompt) => {
        const editor = document.querySelector('#prompt-textarea') ||
          document.querySelector('[data-testid="composer-input"]');
        if (!editor) {
          return { ok: false, error: 'Compositore non trovato: ChatGPT potrebbe aver cambiato UI' };
        }
        editor.focus();
        if (editor instanceof HTMLTextAreaElement) {
          const setter = Object.getOwnPropertyDescriptor(
            HTMLTextAreaElement.prototype, 'value'
          )?.set;
          if (!setter) return { ok: false, error: 'Input non supportato' };
          setter.call(editor, prompt);
          editor.dispatchEvent(new Event('input', { bubbles: true }));
        } else if (editor.isContentEditable) {
          const selection = window.getSelection();
          const range = document.createRange();
          range.selectNodeContents(editor);
          selection?.removeAllRanges();
          selection?.addRange(range);
          if (!document.execCommand('insertText', false, prompt)) {
            return { ok: false, error: 'Inserimento testo non supportato' };
          }
        } else {
          return { ok: false, error: 'Compositore non riconosciuto' };
        }

        await new Promise(resolve => setTimeout(resolve, 250));
        const button = document.querySelector('button[data-testid="send-button"]');
        if (!(button instanceof HTMLButtonElement) || button.disabled) {
          return { ok: false, error: 'Tasto invio non pronto: controlla la scheda' };
        }
        button.click();
        return { ok: true, status: 'Prompt inviato (non equivale a risposta completata)' };
      }, [message.prompt]);
      if (!outcome?.ok) throw new Error(outcome?.error || 'Invio fallito');
      return outcome;
    }
    case 'readLastReply': {
      await requireChatTab(message.tabId);
      const outcome = await inject(message.tabId, () => {
        const replies = document.querySelectorAll('[data-message-author-role="assistant"]');
        const last = replies[replies.length - 1];
        if (!last) return { ok: false, error: 'Nessuna risposta visibile nella scheda' };
        const text = last.innerText?.trim() || last.textContent?.trim() || '';
        if (!text) return { ok: false, error: 'La risposta non contiene testo leggibile' };
        return { ok: true, text: text.slice(0, 12000) };
      });
      if (!outcome?.ok) throw new Error(outcome?.error || 'Lettura fallita');
      return { text: outcome.text };
    }
    default:
      throw new Error('Comando sconosciuto');
  }
}

chrome.runtime.onMessageExternal.addListener((message, sender, sendResponse) => {
  if (!security.isAllowedSender(sender.url)) {
    sendResponse({ ok: false, error: 'Origine non autorizzata' });
    return false;
  }
  const error = security.validateRequest(message);
  if (error) {
    sendResponse({ ok: false, error });
    return false;
  }

  perform(message)
    .then(data => sendResponse({ ok: true, data }))
    .catch(err => sendResponse({ ok: false, error: err.message || 'Errore estensione' }));
  return true;
});

