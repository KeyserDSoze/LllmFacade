const test = require('node:test');
const assert = require('node:assert/strict');
const security = require('../../src/extension/security.js');

test('accepts only known origins', () => {
  assert.equal(security.isAllowedSender('https://keyserdsoze.github.io/LllmFacade/'), true);
  assert.equal(security.isAllowedSender('http://localhost:5173/'), true);
  assert.equal(security.isAllowedSender('https://evil.example/'), false);
  assert.equal(security.isAllowedSender('https://keyserdsoze.github.io.evil.example/'), false);
  assert.equal(security.isAllowedSender('http://localhost:4444/'), false);
  assert.equal(security.isAllowedSender('not-a-url'), false);
});

test('only permits exact ChatGPT hosts over HTTPS', () => {
  assert.equal(security.isChatUrl('https://chatgpt.com/c/abc'), true);
  assert.equal(security.isChatUrl('https://chat.openai.com/'), true);
  assert.equal(security.isChatUrl('https://chatgpt.com.attacker.net'), false);
  assert.equal(security.isChatUrl('http://chatgpt.com/'), false);
  assert.equal(security.isChatUrl('https://example.com/'), false);
});

test('rejects unknown commands and invalid tab references', () => {
  assert.match(security.validateRequest({action: 'deleteAll'}), /Comando/);
  assert.match(security.validateRequest({action: 'activateTab',tabId: 0}), /ID/);
  assert.match(security.validateRequest({action: 'readLastReply',tabId: -2}), /ID/);
  assert.equal(security.validateRequest({action: 'activateTab',tabId: 12}), null);
});

test('validates prompt length and content', () => {
  assert.match(security.validateRequest({action: 'sendPrompt',tabId: 7,prompt: '  '}), /prompt/);
  assert.match(security.validateRequest({action: 'sendPrompt',tabId: 7,prompt: 'x'.repeat(4001)}), /prompt/);
  assert.equal(security.validateRequest({action: 'sendPrompt',tabId: 7,prompt: 'ciao'}), null);
});

