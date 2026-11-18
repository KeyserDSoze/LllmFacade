/* Pure message validation: also loaded by Node tests. */
(function (root) {
  'use strict';
  const allowedOrigins = new Set([
    'https://keyserdsoze.github.io',
    'http://localhost:5173',
    'http://127.0.0.1:5173'
  ]);
  const allowedActions = new Set([
    'ping', 'listTabs', 'openChat', 'activateTab', 'sendPrompt', 'readLastReply'
  ]);

  function isAllowedSender(url) {
    if (typeof url !== 'string') return false;
    try {
      const parsed = new URL(url);
      return allowedOrigins.has(parsed.origin);
    } catch {
      return false;
    }
  }

  function isChatUrl(url) {
    if (typeof url !== 'string') return false;
    try {
      const parsed = new URL(url);
      return parsed.protocol === 'https:' &&
        (parsed.hostname === 'chatgpt.com' || parsed.hostname === 'chat.openai.com');
    } catch {
      return false;
    }
  }

  function validateRequest(message) {
    if (!message || typeof message !== 'object' ||
        !allowedActions.has(message.action)) {
      return 'Comando non riconosciuto';
    }
    if (['activateTab', 'sendPrompt', 'readLastReply'].includes(message.action) &&
        (!Number.isInteger(message.tabId) || message.tabId <= 0)) {
      return 'ID della scheda non valido';
    }
    if (message.action === 'sendPrompt' &&
        (typeof message.prompt !== 'string' ||
          message.prompt.trim().length === 0 || message.prompt.length > 4000)) {
      return 'Il prompt deve contenere da 1 a 4000 caratteri';
    }
    return null;
  }

  const api = { isAllowedSender, isChatUrl, validateRequest };
  root.LllmSecurity = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(globalThis);

