using System.Text.Json;

namespace LllmFacade.Infrastructure.Browser;

/// <summary>
/// Init script for ChatGPT pages: wraps window.fetch only for the conversation POST sent by the web app itself.
/// It applies the overrides queued in window.__lfNext (model, thinking, exact prompt, temporary chat) and
/// copies the SSE response to the host through the exposed __lfStream binding, leaving the page stream intact.
/// </summary>
internal static class FetchHookScript
{
    public static string Build(string origin, string streamPattern) => Template
        .Replace("__ORIGIN__", JsonSerializer.Serialize(origin))
        .Replace("__PATTERN__", JsonSerializer.Serialize(streamPattern));

    private const string Template = """
        (() => {
          if (window.top !== window || location.origin !== __ORIGIN__ || window.__lfHooked) return;
          window.__lfHooked = true;
          const pattern = new RegExp(__PATTERN__);
          const originalFetch = window.fetch.bind(window);
          const notify = (kind, data) => {
            try { if (typeof window.__lfStream === 'function') window.__lfStream(kind, data); } catch (_) { }
          };
          const rewrite = (init, next) => {
            if (!next || !init || typeof init.body !== 'string') return init;
            try {
              const body = JSON.parse(init.body);
              if (next.model) body.model = next.model;
              if (next.thinkingField && next.thinking) body[next.thinkingField] = next.thinking;
              if (typeof next.prompt === 'string' && Array.isArray(body.messages) && body.messages.length > 0) {
                const last = body.messages[body.messages.length - 1];
                if (last && last.content && Array.isArray(last.content.parts)) last.content.parts = [next.prompt];
              }
              if (next.temporary) body.history_and_training_disabled = true;
              return Object.assign({}, init, { body: JSON.stringify(body) });
            } catch (_) {
              notify('warning', 'request-rewrite-failed');
              return init;
            }
          };
          window.fetch = async function (input, init) {
            let url;
            let method;
            try {
              url = new URL(typeof input === 'string' ? input : (input instanceof URL ? input.href : input.url), location.href);
              method = String((init && init.method) || (input instanceof Request ? input.method : 'GET')).toUpperCase();
            } catch (_) {
              return originalFetch(input, init);
            }
            if (method !== 'POST' || url.origin !== location.origin || !pattern.test(url.pathname)) {
              return originalFetch(input, init);
            }
            const next = window.__lfNext;
            window.__lfNext = undefined;
            init = rewrite(init, next);
            let response;
            try {
              response = await originalFetch(input, init);
            } catch (error) {
              notify('error', String((error && error.message) || error));
              throw error;
            }
            notify('start', String(response.status));
            if (!response.body) {
              notify('end', '');
              return response;
            }
            const [forPage, forHost] = response.body.tee();
            (async () => {
              const reader = forHost.getReader();
              const decoder = new TextDecoder();
              try {
                for (;;) {
                  const { done, value } = await reader.read();
                  if (done) break;
                  notify('chunk', decoder.decode(value, { stream: true }));
                }
                const tail = decoder.decode();
                if (tail) notify('chunk', tail);
                notify('end', '');
              } catch (error) {
                notify('error', String((error && error.message) || error));
              }
            })();
            return new Response(forPage, { status: response.status, statusText: response.statusText, headers: response.headers });
          };
        })();
        """;
}
