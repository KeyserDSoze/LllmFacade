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
        async ([url, headers, timeoutMs]) => {
          const controller = new AbortController();
          const timer = setTimeout(() => controller.abort(), timeoutMs);
          try {
            const response = await fetch(url, {
              method: 'GET', headers, credentials: 'include', cache: 'no-store', signal: controller.signal
            });
            return JSON.stringify({
              status: response.status,
              body: await response.text(),
              contentType: response.headers.get('content-type') || '',
              mitigated: response.headers.get('cf-mitigated') || ''
            });
          } catch (error) {
            return JSON.stringify({ status: 0, body: String((error && error.message) || error), contentType: '', mitigated: '' });
          } finally {
            clearTimeout(timer);
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

    public async Task EnsureConversationTabAsync(
        ChatGptIdentity identity, PromptTarget target, CancellationToken cancellationToken)
    {
        var conversationId = ChatGptIds.EnsureConversationId(target.ConversationId);
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var tab = await Guard(() => session.GetOrOpenTabAsync(target.TabKey, conversationId, cancellationToken));
        await tab.Gate.WaitAsync(cancellationToken);
        try
        {
            await OpenComposerAsync(
                tab.Page,
                _web.Url(_web.ConversationPagePath.Replace("{conversationId}", Uri.EscapeDataString(conversationId))),
                allowFullConversation: true);
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            throw new ChatGptException(
                ChatGptFailure.Upstream,
                "Tab della chat non disponibile nel browser: " + Short(error.Message),
                innerException: error);
        }
        finally
        {
            tab.Gate.Release();
        }
    }

    public async Task<SubmissionOutcome> SubmitPromptAsync(
        ChatGptIdentity identity, PromptTarget target, string prompt, IProgress<string>? liveText, CancellationToken cancellationToken)
    {
        var conversationId = ChatGptIds.EnsureConversationId(target.ConversationId);
        var session = await Guard(() => sessions.GetSessionAsync(identity, cancellationToken));
        var tab = await Guard(() => session.GetOrOpenTabAsync(target.TabKey, conversationId, cancellationToken));
        await tab.Gate.WaitAsync(cancellationToken);
        var sendAttempted = false;
        try
        {
            await OpenComposerAsync(tab.Page, _web.Url(_web.ConversationPagePath.Replace("{conversationId}", Uri.EscapeDataString(conversationId))));
            await session.WaitForPageToSettleAsync(tab.Page, cancellationToken);
            tab.BeginSubmission(liveText);
            await TypePromptAsync(tab.Page, prompt, cancellationToken);
            await tab.Page.EvaluateAsync("next => { window.__lfNext = next; }", new { prompt });
            await PauseAfterTypingAsync(cancellationToken);
            await session.PacePromptSendAsync(async () =>
            {
                sendAttempted = true;
                await ClickSendAsync(tab.Page, tab.Started);
            }, cancellationToken);

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
        var previous = session.FindTab(target.TabKey);
        var projectUrl = ProjectPageUrl(previous?.Page.Url ?? string.Empty, projectId);
        var tab = previous is null
            ? await Guard(() => session.GetOrOpenTabAsync(target.TabKey, cancellationToken))
            : await Guard(() => session.BeginTabReplacementAsync(target.TabKey, cancellationToken));
        await tab.Gate.WaitAsync(cancellationToken);
        var sendAttempted = false;
        var promoted = false;
        try
        {
            await OpenComposerAsync(tab.Page, projectUrl);
            await session.WaitForPageToSettleAsync(tab.Page, cancellationToken);
            tab.BeginSubmission(liveText);
            await TypePromptAsync(tab.Page, prompt, cancellationToken);
            await tab.Page.EvaluateAsync("next => { window.__lfNext = next; }", new { prompt });
            await PauseAfterTypingAsync(cancellationToken);
            var preferKeyboard = await tab.Page.Locator(
                "form[data-chatgpt-composer][data-composer-placement='home']:visible").CountAsync() > 0;
            await session.PacePromptSendAsync(async () =>
            {
                sendAttempted = true;
                await ClickSendAsync(tab.Page, tab.Started, preferKeyboard: preferKeyboard);
            }, cancellationToken);

            var completed = await Task.WhenAny(tab.Started, Task.Delay(TimeSpan.FromSeconds(_browser.ConfirmationTimeoutSeconds), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            var confirmed = completed == tab.Started;
            if (confirmed && await tab.Started >= 400)
                throw await RejectedSendAsync(tab, await tab.Started, cancellationToken);

            var conversationId = await WaitForNewConversationIdAsync(tab, cancellationToken)
                ?? throw new ChatGptException(ChatGptFailure.Interface,
                    "Prompt inviato in una nuova chat, ma l'ID della chat non è stato rilevato: controllare il project su ChatGPT.",
                    promptMayHaveBeenSent: true);
            if (previous is not null)
            {
                await session.PromoteTabReplacementAsync(target.TabKey, previous, tab);
                promoted = true;
            }
            // A conversation URL can only appear after the project page accepted the new chat; it is also a confirmation
            // when the fetch-hook's initial callback was missed.
            return new StartedChat(conversationId, SubmissionOutcome.Confirmed);
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
            if (previous is not null && !promoted)
                await EndpointBrowserSession.DiscardTabReplacementAsync(tab);
        }
    }

    /// <summary>The send was refused by ChatGPT: a length-limit refusal is reported as such, anything else as an upstream error.</summary>
    private async Task<ChatGptException> RejectedSendAsync(ProjectTab tab, int status, CancellationToken cancellationToken)
    {
        await Task.WhenAny(tab.Ended, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        if (IsConversationFull(tab.ErrorBody) is { } responseFull)
            return responseFull;

        var visibleMessages = await tab.Page.Locator(
                "[role='alert']:visible, [role='status']:visible, [data-radix-toast-viewport] :visible, " +
                "[data-above-composer-portal]:visible, form[data-chatgpt-composer] [data-state='error']:visible")
            .AllInnerTextsAsync();
        var visible = string.Join('\n', visibleMessages.Where(text => text.Length is > 0 and <= 500));
        return IsConversationFull(visible) is { } visibleFull
            ? visibleFull
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

    public async Task CloseTabAsync(Guid endpointId, string tabKey)
    {
        try
        {
            await (sessions.Find(endpointId)?.CloseTabAsync(tabKey) ?? Task.CompletedTask);
        }
        catch (PlaywrightException)
        {
        }
    }


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
            page = await session.NewOwnedPageAsync();
            await page.ExposeFunctionAsync<string, string, bool>("__lfStream", (kind, data) => signals.Writer.TryWrite((kind, data)));
            await OpenComposerAsync(page, NewChatUrl(command));
            await session.WaitForPageToSettleAsync(page, cancellationToken);
            await TypePromptAsync(page, command.Prompt, cancellationToken);
            await page.EvaluateAsync("next => { window.__lfNext = next; }", new
            {
                model = command.UpstreamModel,
                thinkingField = _web.ThinkingEffortField,
                thinking = command.ThinkingEffort,
                prompt = command.Prompt,
                temporary = command.TemporaryChat
            });
            await PauseAfterTypingAsync(cancellationToken);
            await session.PacePromptSendAsync(() => ClickSendAsync(page), cancellationToken);

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

    private async Task OpenComposerAsync(IPage page, string url, bool allowFullConversation = false)
    {
        IResponse? response = null;
        if (IsSamePageTarget(page.Url, url))
        {
            await page.BringToFrontAsync();
            await page.WaitForLoadStateAsync(LoadState.Load);
        }
        else
        {
            response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        }
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

        if (await banner.IsVisibleAsync() && !allowFullConversation)
            throw new ChatGptException(ChatGptFailure.ConversationFull, "La chat ha raggiunto il limite di lunghezza di ChatGPT.");
    }

    private static bool IsSamePageTarget(string currentUrl, string requestedUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
            || !Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requested)
            || !string.Equals(current.GetLeftPart(UriPartial.Authority), requested.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            return false;

        var currentConversation = ConversationUrl().Match(currentUrl);
        var requestedConversation = ConversationUrl().Match(requestedUrl);
        if (currentConversation.Success && requestedConversation.Success)
            return string.Equals(currentConversation.Groups[1].Value, requestedConversation.Groups[1].Value, StringComparison.Ordinal);

        return string.Equals(current.PathAndQuery, requested.PathAndQuery, StringComparison.Ordinal);
    }

    private async Task ClickSendAsync(
        IPage page, Task<int>? requestStarted = null, bool preferKeyboard = false)
    {
        var box = page.Locator(_web.PromptSelector).First;
        var send = page.Locator(_web.SendButtonSelector).First;
        var previousUrl = page.Url;
        if (preferKeyboard)
        {
            // No click: in a long draft it could land on a link chip; Enter keeps the caret where the insertion ended.
            await box.PressAsync("Enter");
            if (await ComposerAcceptedAsync(page, box, previousUrl, requestStarted))
                return;
        }

        try
        {
            // ChatGPT replaces the voice control with a submit control after its editor state updates.
            await send.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            });
            if (await send.IsEnabledAsync())
                await send.ClickAsync();
            if (await ComposerAcceptedAsync(page, box, previousUrl, requestStarted))
                return;
        }
        catch (TimeoutException)
        {
        }

        await box.EvaluateAsync("element => { const form = element.closest('form'); if (form) { const button = form.querySelector('button[type=submit]:not(:disabled)'); form.requestSubmit(button || undefined); } }");
        if (await ComposerAcceptedAsync(page, box, previousUrl, requestStarted))
            return;

        if (!preferKeyboard)
        {
            await box.PressAsync("Enter");
            if (await ComposerAcceptedAsync(page, box, previousUrl, requestStarted))
                return;
        }
        throw new ChatGptException(ChatGptFailure.Interface,
            "Il composer contiene il testo, ma ChatGPT non ha accettato né Enter né il submit nativo: controllare il form.");
    }

    private static async Task<bool> ComposerAcceptedAsync(
        IPage page, ILocator box, string previousUrl, Task<int>? requestStarted)
    {
        var stop = page.Locator(
            "button[aria-label='Stop']:visible, button[aria-label='Stop generating']:visible, [data-testid='stop-button']:visible");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        do
        {
            if (requestStarted?.IsCompletedSuccessfully == true || await stop.IsVisibleAsync())
                return true;
            if (page.Url != previousUrl && ConversationUrl().IsMatch(page.Url))
                return true;
            if (!await box.IsVisibleAsync() || CompactComposerText(await ReadComposerTextAsync(box)).Length == 0)
                return true;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>Types in the visible composer so the web app receives the same editor events as a user.</summary>
    private Task PauseAfterTypingAsync(CancellationToken cancellationToken) =>
        Task.Delay(Math.Max(0, _browser.PromptToSendDelayMilliseconds), cancellationToken);

    private async Task TypePromptAsync(
        IPage page, string prompt, CancellationToken cancellationToken)
    {
        var box = page.Locator(_web.PromptSelector).First;
        await box.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        if (ComposerContains(await ReadComposerTextAsync(box), prompt))
            return;

        await box.ClickAsync();
        await box.PressAsync("ControlOrMeta+A");
        await box.PressAsync("Backspace");
        await page.Keyboard.InsertTextAsync(prompt);
        if (await WaitForComposerTextAsync(box, prompt, cancellationToken))
            return;

        await box.FillAsync(prompt);
        if (await WaitForComposerTextAsync(box, prompt, cancellationToken))
            return;

        throw new ChatGptException(ChatGptFailure.Interface,
            "Il testo non è stato acquisito dal composer di ChatGPT: non è stato inviato alcun prompt.");
    }

    private static Task<string> ReadComposerTextAsync(ILocator box) =>
        box.EvaluateAsync<string>("""
            element => {
              const values = [
                'value' in element ? element.value : '',
                element.innerText || '',
                element.textContent || ''
              ];
              return values.reduce((longest, current) => current.length > longest.length ? current : longest, '');
            }
            """);

    private static async Task<bool> WaitForComposerTextAsync(
        ILocator box, string prompt, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            if (ComposerContains(await ReadComposerTextAsync(box), prompt))
                return true;
            await Task.Delay(100, cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested);
        return false;
    }

    /// <summary>
    /// The rich composer renders the inserted text differently (line breaks around link chips, paragraphs, typographic
    /// input rules): compare without whitespace, then accept a near-complete word coverage. The fetch hook still sends
    /// the exact prompt.
    /// </summary>
    internal static bool ComposerContains(string current, string expected)
    {
        if (CompactComposerText(current).Contains(CompactComposerText(expected), StringComparison.Ordinal))
            return true;

        var wanted = ComposerWords(expected);
        if (wanted.Count == 0)
            return false;
        var available = ComposerWords(current).CountBy(word => word).ToDictionary();
        var found = 0;
        foreach (var word in wanted)
        {
            if (available.TryGetValue(word, out var count) && count > 0)
            {
                available[word] = count - 1;
                found++;
            }
        }
        return found >= wanted.Count * MinimumComposerWordCoverage;
    }

    private const double MinimumComposerWordCoverage = 0.9;

    private static string CompactComposerText(string? text) =>
        ComposerSpacing().Replace(text ?? string.Empty, string.Empty);

    private static List<string> ComposerWords(string? text) =>
        ComposerWord().Matches(text ?? string.Empty).Select(match => match.Value.ToLowerInvariant()).ToList();

    [GeneratedRegex("[\\s\\u200B-\\u200D\\u2060\\uFEFF]+")]
    private static partial Regex ComposerSpacing();

    [GeneratedRegex("[\\p{L}\\p{N}]+")]
    private static partial Regex ComposerWord();

    private string ProjectPageUrl(string currentUrl, string projectId)
    {
        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.Host.Equals(_web.Host, StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "g"
                && (parts[1].Equals(projectId, StringComparison.Ordinal)
                    || parts[1].StartsWith(projectId + "-", StringComparison.Ordinal)))
            {
                return _web.Url($"/g/{parts[1]}/project");
            }
        }

        return _web.Url(_web.ProjectPagePath.Replace("{projectId}", Uri.EscapeDataString(projectId)));
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

        var timeoutMs = Math.Max(1_000, _browser.NavigationTimeoutSeconds * 1_000);
        var json = await Guard(() => session.WithControlPageAsync(
            page => page.EvaluateAsync<string>(FetchScript,
                new object[] { _web.Url(pathAndQuery), headers, timeoutMs }), cancellationToken));
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
        "ChatGPT richiede una verifica di sicurezza nella pagina. Completarla manualmente nel browser collegato; il controllo non viene aggirato e l'autopilot riproverà successivamente.");

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
