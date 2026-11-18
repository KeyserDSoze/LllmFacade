export interface ChatTab {
  id: number;
  title: string;
  url: string;
  active: boolean;
  windowId: number;
}

export type BridgeRequest =
  | { action: 'ping' }
  | { action: 'listTabs' }
  | { action: 'openChat' }
  | { action: 'activateTab'; tabId: number }
  | { action: 'sendPrompt'; tabId: number; prompt: string }
  | { action: 'readLastReply'; tabId: number };

interface BridgeResponse<T> {
  ok: boolean;
  data?: T;
  error?: string;
}

interface ChromeRuntime {
  sendMessage: <T>(
    extensionId: string,
    message: BridgeRequest,
    callback: (response: BridgeResponse<T> | undefined) => void
  ) => void;
  lastError?: { message: string };
}

export function validExtensionId(id: string): boolean {
  return /^[a-p]{32}$/.test(id.trim());
}

export function sendCommand<T>(extensionId: string, request: BridgeRequest): Promise<T> {
  const runtime = (globalThis as typeof globalThis & {
    chrome?: { runtime?: ChromeRuntime }
  }).chrome?.runtime;

  if (!runtime?.sendMessage) {
    return Promise.reject(new Error('Apri questa pagina in Chrome con l’estensione installata.'));
  }
  if (!validExtensionId(extensionId)) {
    return Promise.reject(new Error('Inserisci un ID estensione Chrome valido (32 caratteri).'));
  }

  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(
      new Error('L’estensione non risponde. Verifica che sia installata e ricaricata.')
    ), 8000);
    try {
      runtime.sendMessage<T>(extensionId.trim(), request, response => {
        clearTimeout(timer);
        if (runtime.lastError) {
          reject(new Error(runtime.lastError.message));
        } else if (!response?.ok || response.data === undefined) {
          reject(new Error(response?.error || 'Nessuna risposta dall’estensione'));
        } else {
          resolve(response.data);
        }
      });
    } catch (error) {
      clearTimeout(timer);
      reject(error);
    }
  });
}

