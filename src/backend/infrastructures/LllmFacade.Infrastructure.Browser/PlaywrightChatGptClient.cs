using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Sessions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LllmFacade.Infrastructure.Browser;

/// <summary>
/// ChatGPT driven through a real Chromium: cookies, user agent and headers come from the endpoint identity,
/// the web app runs its own scripts (including anti-abuse checks), reads use in-page fetch like the app does,
/// and prompts are typed into the composer and sent with the send button.
/// </summary>
public sealed partial class PlaywrightChatGptClient(
    BrowserSessionManager sessions,
    IOptions<ChatGptWebOptions> webOptions,
    IOptions<BrowserOptions> browserOptions) : IChatGptClient
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly ChatGptWebOptions _web = webOptions.Value;
    private readonly BrowserOptions _browser = browserOptions.Value;

    private const string FetchScript = """
        async ([url, headers]) => {
          try {
            const response = await fetch(url, { method: 'GET', headers, credentials: 'include', cache: 'no-store' });
            return JSON.stringify({
              status: response.status,
              body: await response.text(),
              contentType: response.headers.get('content-type') || '',
              mitigated: response.headers.get('cf-mitigated') || ''
            });
          } catch (error) {
            return JSON.stringify({ status: 0, body: String((error && error.message) || error), contentType: '', mitigated: '' });
          }
        }
        """;

    public async Task<SessionSnapshot> RefreshSessionAsync(ChatGptIdentity identity, CancellationToken cancellationToken)
    {
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var body = await GetJsonAsync(session, identity, _web.SessionPath, authorized: false, cancellationToken);
        var info = ChatGptResponses.ParseSession(body);
        if (info.AccessToken is null)
        {
            throw new ChatGptException(ChatGptFailure.SessionExpired,
                "La sessione ChatGPT non è più valida (nessun token da " + _web.SessionPath + "): reinserire i cookie dell'endpoint.");
        }

        session.AccessToken = info.AccessToken;
        var storageState = await Guard(session.CaptureStorageStateAsync);
        return new SessionSnapshot(info.AccessToken, AccessTokens.ReadExpiry(info.AccessToken) ?? info.Expires, info.Email, storageState);
    }

    public async Task<IReadOnlyList<ChatGptProject>> ListProjectsAsync(ChatGptIdentity identity, CancellationToken cancellationToken)
    {
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var projects = new List<ChatGptProject>();
        string? cursor = null;
        for (var page = 0; page < _web.MaxListPages; page++)
        {
            var path = cursor is null
                ? _web.ProjectsPath
                : _web.ProjectsPath + (_web.ProjectsPath.Contains('?') ? "&" : "?") + "cursor=" + Uri.EscapeDataString(cursor);
            var (items, next) = ChatGptResponses.ParseProjects(await GetJsonAsync(session, identity, path, authorized: true, cancellationToken));
            projects.AddRange(items);
            if (next is null || items.Count == 0 || next == cursor)
                break;
            cursor = next;
        }
        return projects.DistinctBy(project => project.Id).ToList();
    }

    public async Task<IReadOnlyList<ChatGptConversation>> ListProjectConversationsAsync(
        ChatGptIdentity identity, string projectId, CancellationToken cancellationToken)
    {
        var id = ChatGptIds.EnsureProjectId(projectId);
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var conversations = new List<ChatGptConversation>();
        var cursor = "0";
        for (var page = 0; page < _web.MaxListPages; page++)
        {
            var path = _web.ProjectConversationsPath
                .Replace("{projectId}", Uri.EscapeDataString(id))
                .Replace("{cursor}", Uri.EscapeDataString(cursor));
            var (items, next) = ChatGptResponses.ParseConversations(await GetJsonAsync(session, identity, path, authorized: true, cancellationToken));
            conversations.AddRange(items);
            if (next is null || items.Count == 0 || next == cursor || !_web.ProjectConversationsPath.Contains("{cursor}"))
                break;
            cursor = next;
        }
        return conversations.DistinctBy(conversation => conversation.Id).ToList();
    }

    public async Task<ConversationState> GetConversationStateAsync(
        ChatGptIdentity identity, string conversationId, CancellationToken cancellationToken)
    {
        var id = ChatGptIds.EnsureConversationId(conversationId);
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var path = _web.ConversationApiPath.Replace("{conversationId}", Uri.EscapeDataString(id));
        return ChatGptResponses.ParseConversationState(id, await GetJsonAsync(session, identity, path, authorized: true, cancellationToken));
    }

    public async Task<SubmissionOutcome> SubmitPromptAsync(
        ChatGptIdentity identity, PromptTarget target, string prompt, IProgress<string>? liveText, CancellationToken cancellationToken)
    {
        var conversationId = ChatGptIds.EnsureConversationId(target.ConversationId);
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var tab = await Guard(() => session.GetOrOpenTabAsync(target.TabKey, cancellationToken));
        await tab.Gate.WaitAsync(cancellationToken);
        var sendAttempted = false;
        try
        {
            await OpenComposerAsync(tab.Page, _web.Url(_web.ConversationPagePath.Replace("{conversationId}", Uri.EscapeDataString(conversationId))));
            tab.BeginSubmission(liveText);
            await tab.Page.Locator(_web.PromptSelector).First.FillAsync(prompt);
            await tab.Page.EvaluateAsync("next => { window.__lfNext = next; }", new { prompt });
            sendAttempted = true;
            await ClickSendAsync(tab.Page);

            var completed = await Task.WhenAny(tab.Started, Task.Delay(TimeSpan.FromSeconds(_browser.ConfirmationTimeoutSeconds), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != tab.Started)
                return SubmissionOutcome.Unconfirmed;
            var status = await tab.Started;
            if (status >= 400)
                throw await RejectedSendAsync(tab, status, cancellationToken);
            return SubmissionOutcome.Confirmed;
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            throw new ChatGptException(
                sendAttempted ? ChatGptFailure.Interface : ChatGptFailure.Upstream,
                (sendAttempted ? "Invio del prompt non verificabile: " : "Chat non disponibile nel browser: ") + Short(error.Message),
                promptMayHaveBeenSent: sendAttempted,
                innerException: error);
        }
        finally
        {
            tab.Gate.Release();
        }
    }

    public async Task<StartedChat> StartProjectChatAsync(
        ChatGptIdentity identity, NewChatTarget target, string prompt, IProgress<string>? liveText, CancellationToken cancellationToken)
    {
        var projectId = ChatGptIds.EnsureProjectId(target.ProjectId);
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var tab = await Guard(() => session.GetOrOpenTabAsync(target.TabKey, cancellationToken));
        await tab.Gate.WaitAsync(cancellationToken);
        var sendAttempted = false;
        try
        {
            await OpenComposerAsync(tab.Page, _web.Url(_web.ProjectPagePath.Replace("{projectId}", Uri.EscapeDataString(projectId))));
            tab.BeginSubmission(liveText);
            await tab.Page.Locator(_web.PromptSelector).First.FillAsync(prompt);
            await tab.Page.EvaluateAsync("next => { window.__lfNext = next; }", new { prompt });
            sendAttempted = true;
            await ClickSendAsync(tab.Page);

            var completed = await Task.WhenAny(tab.Started, Task.Delay(TimeSpan.FromSeconds(_browser.ConfirmationTimeoutSeconds), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            var confirmed = completed == tab.Started;
            if (confirmed && await tab.Started >= 400)
                throw await RejectedSendAsync(tab, await tab.Started, cancellationToken);

            var conversationId = await WaitForNewConversationIdAsync(tab, cancellationToken)
                ?? throw new ChatGptException(ChatGptFailure.Interface,
                    "Prompt inviato in una nuova chat, ma l'ID della chat non è stato rilevato: controllare il project su ChatGPT.",
                    promptMayHaveBeenSent: true);
            return new StartedChat(conversationId, confirmed ? SubmissionOutcome.Confirmed : SubmissionOutcome.Unconfirmed);
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            throw new ChatGptException(
                sendAttempted ? ChatGptFailure.Interface : ChatGptFailure.Upstream,
                (sendAttempted ? "Invio nella nuova chat non verificabile: " : "Pagina del project non disponibile nel browser: ") + Short(error.Message),
                promptMayHaveBeenSent: sendAttempted,
                innerException: error);
        }
        finally
        {
            tab.Gate.Release();
        }
    }

    /// <summary>The send was refused by ChatGPT: a length-limit refusal is reported as such, anything else as an upstream error.</summary>
    private async Task<ChatGptException> RejectedSendAsync(ProjectTab tab, int status, CancellationToken cancellationToken)
    {
        await Task.WhenAny(tab.Ended, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        return IsConversationFull(tab.ErrorBody) is { } full
            ? full
            : StatusException(status, tab.ErrorBody, "ChatGPT ha rifiutato il prompt");
    }

    private async Task<string?> WaitForNewConversationIdAsync(ProjectTab tab, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if (tab.ConversationId is { } fromStream && ChatGptIds.IsConversationId(fromStream))
                return fromStream;
            var fromUrl = ConversationUrl().Match(tab.Page.Url);
            if (fromUrl.Success && ChatGptIds.IsConversationId(fromUrl.Groups[1].Value))
                return fromUrl.Groups[1].Value;
            if (DateTime.UtcNow > deadline)
                return null;
            await Task.Delay(200, cancellationToken);
        }
    }

    private ChatGptException? IsConversationFull(string text) =>
        Regex.IsMatch(text, _web.ConversationFullPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? new ChatGptException(ChatGptFailure.ConversationFull, "La chat ha raggiunto il limite di lunghezza di ChatGPT.")
            : null;

    [GeneratedRegex("/c/([A-Za-z0-9-]{1,64})(?:[/?#]|$)")]
    private static partial Regex ConversationUrl();

    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(
        ChatGptIdentity identity, CompletionCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = Channel.CreateUnbounded<CompletionChunk>(new UnboundedChannelOptions { SingleReader = true });
        var producer = ProduceCompletionAsync(identity, command, output.Writer, stop.Token);
        try
        {
            await foreach (var chunk in output.Reader.ReadAllAsync(cancellationToken))
                yield return chunk;
        }
        finally
        {
            await stop.CancelAsync();
            await producer;
        }
    }

    public async Task<string?> CaptureStorageStateAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        if (sessions.Find(endpointId) is not { } session)
            return null;
        try
        {
            return await session.CaptureStorageStateAsync();
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    public Task CloseTabAsync(Guid endpointId, string tabKey) =>
        sessions.Find(endpointId)?.CloseTabAsync(tabKey) ?? Task.CompletedTask;

    public Task ReleaseAsync(Guid endpointId) => sessions.ReleaseAsync(endpointId);

    private async Task ProduceCompletionAsync(
        ChatGptIdentity identity, CompletionCommand command, ChannelWriter<CompletionChunk> output, CancellationToken cancellationToken)
    {
        EndpointBrowserSession? session = null;
        IPage? page = null;
        var hasSlot = false;
        try
        {
            session = await sessions.GetSessionAsync(identity, cancellationToken);
            hasSlot = await session.PageSlots.WaitAsync(TimeSpan.FromSeconds(_browser.QueueTimeoutSeconds), cancellationToken);
            if (!hasSlot)
                throw new ChatGptException(ChatGptFailure.Busy, "Troppe richieste contemporanee per questo endpoint: riprovare più tardi.");

            var signals = Channel.CreateUnbounded<(string Kind, string Data)>();
            page = await session.Context.NewPageAsync();
            await page.ExposeFunctionAsync<string, string, bool>("__lfStream", (kind, data) => signals.Writer.TryWrite((kind, data)));
            await OpenComposerAsync(page, NewChatUrl(command));
            await page.Locator(_web.PromptSelector).First.FillAsync(command.Prompt);
            await page.EvaluateAsync("next => { window.__lfNext = next; }", new
            {
                model = command.UpstreamModel,
                thinkingField = _web.ThinkingEffortField,
                thinking = command.ThinkingEffort,
                prompt = command.Prompt,
                temporary = command.TemporaryChat
            });
            await ClickSendAsync(page);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(_browser.CompletionTimeoutMinutes));
            await PumpStreamAsync(page, signals.Reader, output, deadline.Token);
            output.TryComplete();
        }
        catch (Exception error)
        {
            output.TryComplete(Translate(error, cancellationToken));
        }
        finally
        {
            if (page is not null)
            {
                try
                {
                    await page.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
            }
            if (hasSlot)
                session!.PageSlots.Release();
        }
    }

    private async Task PumpStreamAsync(
        IPage page, ChannelReader<(string Kind, string Data)> signals, ChannelWriter<CompletionChunk> output, CancellationToken deadline)
    {
        var parser = new ChatGptStreamParser();
        var errorBody = new StringBuilder();
        int? status = null;
        using var startWindow = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        startWindow.CancelAfter(TimeSpan.FromSeconds(_browser.ConfirmationTimeoutSeconds));
        while (true)
        {
            (string Kind, string Data) signal;
            try
            {
                signal = await signals.ReadAsync(status is null ? startWindow.Token : deadline);
            }
            catch (OperationCanceledException) when (status is null && !deadline.IsCancellationRequested)
            {
                // The request did not go through window.fetch: fall back to the rendered answer.
                output.TryWrite(new CompletionChunk(await ReadAnswerFromPageAsync(page)));
                output.TryWrite(new CompletionChunk(string.Empty, IsFinal: true));
                return;
            }

            switch (signal.Kind)
            {
                case "start":
                    status = int.TryParse(signal.Data, out var code) ? code : 0;
                    break;
                case "chunk" when status >= 400:
                    errorBody.Append(signal.Data);
                    break;
                case "chunk":
                    foreach (var delta in parser.Feed(signal.Data))
                        output.TryWrite(new CompletionChunk(delta));
                    if (parser.Error is { } error)
                        throw new ChatGptException(ChatGptFailure.Upstream, "ChatGPT ha restituito un errore: " + Short(error));
                    break;
                case "error":
                    throw new ChatGptException(ChatGptFailure.Upstream, "Connessione con ChatGPT interrotta: " + Short(signal.Data));
                case "end":
                    if (status >= 400)
                        throw StatusException(status.Value, errorBody.ToString(), "ChatGPT ha rifiutato la richiesta");
                    if (parser.Text.Length == 0)
                        output.TryWrite(new CompletionChunk(await ReadAnswerFromPageAsync(page)));
                    output.TryWrite(new CompletionChunk(string.Empty, IsFinal: true, parser.ConversationId));
                    return;
            }
        }
    }

    private async Task<string> ReadAnswerFromPageAsync(IPage page)
    {
        var timeout = (float)TimeSpan.FromMinutes(_browser.CompletionTimeoutMinutes).TotalMilliseconds;
        await page.Locator(_web.StopButtonSelector).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = timeout });
        var answers = page.Locator(_web.AssistantMessageSelector);
        var count = await answers.CountAsync();
        if (count == 0)
            throw new ChatGptException(ChatGptFailure.Interface, "Nessuna risposta trovata nella pagina ChatGPT: il layout potrebbe essere cambiato.");
        return (await answers.Nth(count - 1).InnerTextAsync()).Trim();
    }

    private async Task OpenComposerAsync(IPage page, string url)
    {
        var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (response is { Status: 403 } && await response.HeaderValueAsync("cf-mitigated") is not null)
            throw Blocked();
        // A short prefix is allowed ("You've reached the ..."), but not a long message that merely quotes the sentence.
        // Playwright only maps IgnoreCase, Multiline and Singleline to JavaScript regex flags.
        var banner = page.GetByText(new Regex(
            $"^.{{0,60}}?(?:{_web.ConversationFullPattern})", RegexOptions.IgnoreCase | RegexOptions.Singleline)).First;
        try
        {
            await page.Locator(_web.PromptSelector).First.Or(banner).First
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        }
        catch (TimeoutException)
        {
            if (await page.Locator(_web.LoginSelector).CountAsync() > 0 || page.Url.Contains("/auth/", StringComparison.OrdinalIgnoreCase))
                throw new ChatGptException(ChatGptFailure.SessionExpired, "ChatGPT richiede di nuovo il login: aggiornare i cookie dell'endpoint.");
            if (response is { Status: 403 } || (await page.TitleAsync()).Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                throw Blocked();
            throw new ChatGptException(ChatGptFailure.Interface,
                $"Casella del prompt non trovata ({_web.PromptSelector}): il layout di ChatGPT potrebbe essere cambiato.");
        }

        if (await banner.IsVisibleAsync())
            throw new ChatGptException(ChatGptFailure.ConversationFull, "La chat ha raggiunto il limite di lunghezza di ChatGPT.");
    }

    private async Task ClickSendAsync(IPage page)
    {
        try
        {
            await page.Locator(_web.SendButtonSelector).First.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            await page.Locator(_web.PromptSelector).First.PressAsync("Enter");
        }
    }

    private string NewChatUrl(CompletionCommand command)
    {
        var path = _web.NewChatPath.Replace("{model}", Uri.EscapeDataString(command.UpstreamModel));
        if (command.TemporaryChat && !string.IsNullOrEmpty(_web.TemporaryChatQuery))
            path += (path.Contains('?') ? "&" : "?") + _web.TemporaryChatQuery;
        return _web.Url(path);
    }

    private async Task<string> GetJsonAsync(
        EndpointBrowserSession session, ChatGptIdentity identity, string pathAndQuery, bool authorized, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["accept"] = "application/json" };
        if (authorized)
        {
            var token = session.AccessToken
                ?? throw new ChatGptException(ChatGptFailure.SessionExpired, "Token di accesso assente: rinnovare la sessione dell'endpoint.");
            headers["authorization"] = "Bearer " + token;
            headers["oai-language"] = identity.Locale;
            var cookies = await Guard(() => session.Context.CookiesAsync([_web.Origin]));
            if (cookies.FirstOrDefault(cookie => cookie.Name == "oai-did") is { } deviceId)
                headers["oai-device-id"] = deviceId.Value;
        }

        var json = await Guard(() => session.WithControlPageAsync(
            page => page.EvaluateAsync<string>(FetchScript, new object[] { _web.Url(pathAndQuery), headers }), cancellationToken));
        var result = JsonSerializer.Deserialize<FetchResult>(json, WebJson)
            ?? throw new ChatGptException(ChatGptFailure.Interface, "Risposta del browser non leggibile.");

        if (result.Status == 0)
            throw new ChatGptException(ChatGptFailure.Upstream, "ChatGPT non raggiungibile: " + Short(result.Body));
        var isJson = result.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || result.Body.TrimStart().StartsWith('{') || result.Body.TrimStart().StartsWith('[');
        if (result.Mitigated.Length > 0 || (result.Status == 403 && !isJson))
            throw Blocked();
        if (result.Status >= 400)
            throw StatusException(result.Status, result.Body, "ChatGPT ha rifiutato la richiesta");
        if (!isJson)
            throw new ChatGptException(ChatGptFailure.Interface, "Risposta inattesa da ChatGPT (non JSON).");
        return result.Body;
    }

    private static ChatGptException StatusException(int status, string body, string prefix)
    {
        var failure = status switch
        {
            401 or 403 => ChatGptFailure.SessionExpired,
            429 => ChatGptFailure.RateLimited,
            _ => ChatGptFailure.Upstream
        };
        var detail = Detail(body);
        return new ChatGptException(failure, $"{prefix} ({status}){(detail.Length > 0 ? ": " + detail : ".")}");
    }

    private static string Detail(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
            return "pagina HTML";
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return Short(detail.GetString() ?? string.Empty);
                if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("message", out var message))
                    return Short(message.ToString());
            }
        }
        catch (JsonException)
        {
        }
        return Short(trimmed);
    }

    private static ChatGptException Blocked() => new(ChatGptFailure.Blocked,
        "ChatGPT ha richiesto una verifica anti-bot (Cloudflare): il browser automatico è stato bloccato.");

    private static Exception Translate(Exception error, CancellationToken cancellationToken) => error switch
    {
        ChatGptException => error,
        OperationCanceledException when cancellationToken.IsCancellationRequested => error,
        OperationCanceledException => new ChatGptException(ChatGptFailure.Timeout, "Risposta di ChatGPT non completata entro il tempo massimo."),
        TimeoutException => new ChatGptException(ChatGptFailure.Timeout, "Timeout del browser: " + Short(error.Message), innerException: error),
        PlaywrightException => new ChatGptException(ChatGptFailure.Upstream, "Errore del browser: " + Short(error.Message), innerException: error),
        _ => error
    };

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (TimeoutException error)
        {
            throw new ChatGptException(ChatGptFailure.Timeout, "Timeout del browser: " + Short(error.Message), innerException: error);
        }
        catch (PlaywrightException error)
        {
            throw new ChatGptException(ChatGptFailure.Upstream, "Errore del browser: " + Short(error.Message), innerException: error);
        }
    }

    private static string Short(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private sealed record FetchResult(int Status, string Body, string ContentType, string Mitigated);
}
