using System.Collections.Concurrent;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Endpoints;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LllmFacade.Infrastructure.Browser;

/// <summary>One headless Chromium for the process, one isolated browser context (cookie jar) per endpoint.</summary>
public sealed class BrowserSessionManager(
    IOptions<BrowserOptions> browserOptions,
    IOptions<ChatGptWebOptions> webOptions,
    IEnumerable<IBrowserContextConfigurator> configurators,
    ILogger<BrowserSessionManager> logger) : IAsyncDisposable
{
    private readonly BrowserOptions _browserOptions = browserOptions.Value;
    private readonly ChatGptWebOptions _webOptions = webOptions.Value;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, EndpointBrowserSession> _sessions = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    internal async Task<EndpointBrowserSession> GetSessionAsync(ChatGptIdentity identity, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(identity.EndpointId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var fingerprint = Fingerprint(identity);
            if (!_sessions.TryGetValue(identity.EndpointId, out var existing))
                return _sessions[identity.EndpointId] = await CreateAsync(identity, fingerprint);

            if (existing.IsUsable && existing.Fingerprint == fingerprint)
            {
                existing.AccessToken = identity.AccessToken ?? existing.AccessToken;
                return existing;
            }

            // Settings changed but credentials did not: keep the cookies the live browser has rotated meanwhile.
            var storageState = identity.StorageStateJson;
            if (existing.IsUsable && existing.CredentialsStamp == identity.CredentialsStamp)
            {
                try
                {
                    storageState = await existing.CaptureStorageStateAsync();
                }
                catch (PlaywrightException error)
                {
                    logger.LogWarning(error, "Cookie del contesto precedente non leggibili per l'endpoint {EndpointId}.", identity.EndpointId);
                }
            }

            _sessions.TryRemove(identity.EndpointId, out _);
            await existing.DisposeAsync();
            return _sessions[identity.EndpointId] = await CreateAsync(identity with { StorageStateJson = storageState }, fingerprint);
        }
        finally
        {
            gate.Release();
        }
    }

    internal EndpointBrowserSession? Find(Guid endpointId) =>
        _sessions.TryGetValue(endpointId, out var session) && session.IsUsable ? session : null;

    public async Task ReleaseAsync(Guid endpointId)
    {
        if (_sessions.TryRemove(endpointId, out var session))
            await session.DisposeAsync();
    }

    private async Task<EndpointBrowserSession> CreateAsync(ChatGptIdentity identity, string fingerprint)
    {
        var browser = await GetBrowserAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = identity.UserAgent,
            Locale = identity.Locale,
            TimezoneId = identity.TimeZone,
            ExtraHTTPHeaders = identity.ExtraHeaders,
            StorageState = identity.StorageStateJson,
            ViewportSize = new ViewportSize { Width = _browserOptions.ViewportWidth, Height = _browserOptions.ViewportHeight },
            ServiceWorkers = ServiceWorkerPolicy.Block
        });
        var timeout = (float)TimeSpan.FromSeconds(_browserOptions.NavigationTimeoutSeconds).TotalMilliseconds;
        context.SetDefaultTimeout(timeout);
        context.SetDefaultNavigationTimeout(timeout);
        await context.AddInitScriptAsync(FetchHookScript.Build(_webOptions.Origin, _webOptions.ConversationStreamPattern));
        foreach (var configurator in configurators)
            await configurator.ConfigureAsync(context);

        logger.LogInformation("Contesto browser creato per l'endpoint {EndpointId}.", identity.EndpointId);
        return new EndpointBrowserSession(identity, fingerprint, context, _webOptions, _browserOptions);
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        await _launchLock.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true })
                return _browser;
            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _browserOptions.Headless,
                Channel = string.IsNullOrWhiteSpace(_browserOptions.Channel) ? null : _browserOptions.Channel,
                ExecutablePath = string.IsNullOrWhiteSpace(_browserOptions.ExecutablePath) ? null : _browserOptions.ExecutablePath,
                Args = _browserOptions.Args
            });
            logger.LogInformation("Chromium avviato (headless: {Headless}, versione {Version}).", _browserOptions.Headless, _browser.Version);
            return _browser;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    private static string Fingerprint(ChatGptIdentity identity) => string.Join('\n',
        identity.CredentialsStamp,
        identity.UserAgent,
        identity.Locale,
        identity.TimeZone,
        string.Join('\u001f', identity.ExtraHeaders
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => header.Key + ":" + header.Value)));

    public async ValueTask DisposeAsync()
    {
        foreach (var endpointId in _sessions.Keys)
            await ReleaseAsync(endpointId);
        if (_browser is not null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}

internal sealed class EndpointBrowserSession : IAsyncDisposable
{
    private readonly ChatGptWebOptions _web;
    private readonly BrowserOptions _browser;
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly SemaphoreSlim _tabLock = new(1, 1);
    private readonly SemaphoreSlim _tabSlots;
    private readonly ConcurrentDictionary<string, ProjectTab> _tabs = new();
    private readonly Dictionary<(string Name, string Domain, string Path), string> _initialValues;
    private IPage? _controlPage;
    private volatile bool _closed;

    public EndpointBrowserSession(
        ChatGptIdentity identity, string fingerprint, IBrowserContext context, ChatGptWebOptions web, BrowserOptions browser)
    {
        EndpointId = identity.EndpointId;
        CredentialsStamp = identity.CredentialsStamp;
        AccessToken = identity.AccessToken;
        Fingerprint = fingerprint;
        Context = context;
        _web = web;
        _browser = browser;
        PageSlots = new SemaphoreSlim(browser.MaxPagesPerEndpoint);
        _tabSlots = new SemaphoreSlim(browser.MaxTabsPerEndpoint);
        _initialValues = ReadCookies(identity.StorageStateJson, web.Host);
        context.Close += (_, _) => _closed = true;
    }

    public Guid EndpointId { get; }
    public Guid CredentialsStamp { get; }
    public string Fingerprint { get; }
    public IBrowserContext Context { get; }
    public SemaphoreSlim PageSlots { get; }
    public string? AccessToken { get; set; }
    public bool IsUsable => !_closed;

    /// <summary>Runs <paramref name="action"/> on a same-origin page, the place where the web app itself calls its backend.</summary>
    public async Task<T> WithControlPageAsync<T>(Func<IPage, Task<T>> action, CancellationToken cancellationToken)
    {
        await _controlLock.WaitAsync(cancellationToken);
        try
        {
            if (_controlPage is null || _controlPage.IsClosed)
                _controlPage = await Context.NewPageAsync();
            if (!_controlPage.Url.StartsWith(_web.Origin + "/", StringComparison.Ordinal))
                await _controlPage.GotoAsync(_web.Url(_web.HomePath), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            return await action(_controlPage);
        }
        finally
        {
            _controlLock.Release();
        }
    }

    public async Task<ProjectTab> GetOrOpenTabAsync(string key, CancellationToken cancellationToken)
    {
        await _tabLock.WaitAsync(cancellationToken);
        try
        {
            if (_tabs.TryGetValue(key, out var existing) && !existing.Page.IsClosed)
                return existing;
            if (!await _tabSlots.WaitAsync(TimeSpan.FromSeconds(_browser.QueueTimeoutSeconds), cancellationToken))
                throw new ChatGptException(ChatGptFailure.Busy, "Troppe schede aperte per questo endpoint.");
            try
            {
                var page = await Context.NewPageAsync();
                var tab = new ProjectTab(page);
                await page.ExposeFunctionAsync<string, string, bool>("__lfStream", tab.OnSignal);
                page.Close += (_, _) =>
                {
                    if (_tabs.TryRemove(new KeyValuePair<string, ProjectTab>(key, tab)))
                        _tabSlots.Release();
                };
                _tabs[key] = tab;
                return tab;
            }
            catch
            {
                _tabSlots.Release();
                throw;
            }
        }
        finally
        {
            _tabLock.Release();
        }
    }

    public async Task CloseTabAsync(string key)
    {
        if (_tabs.TryGetValue(key, out var tab))
            await tab.Page.CloseAsync();
    }

    /// <summary>Cookies of the ChatGPT site as currently held by the browser, in storage-state format.</summary>
    public async Task<string> CaptureStorageStateAsync()
    {
        var cookies = (await Context.CookiesAsync())
            .Where(cookie => cookie.Domain.TrimStart('.').Equals(_web.Host, StringComparison.OrdinalIgnoreCase)
                || cookie.Domain.EndsWith("." + _web.Host, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A pasted Cookie header cannot tell host-only from domain cookies: when the site re-issues a cookie under
        // the other variant, the imported value is stale and would still be sent first, so it is removed.
        var sameCookie = cookies.GroupBy(c => (c.Name, c.Path, Host: c.Domain.TrimStart('.').ToLowerInvariant()));
        foreach (var variants in sameCookie.Where(group => group.Count() > 1).Select(group => group.ToList()))
        {
            var stale = variants
                .Where(c => _initialValues.TryGetValue((c.Name, c.Domain, c.Path), out var initial) && initial == c.Value)
                .ToList();
            if (stale.Count == 0 || stale.Count == variants.Count)
                continue;
            foreach (var cookie in stale)
            {
                await Context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Name = cookie.Name, Domain = cookie.Domain, Path = cookie.Path });
                cookies.Remove(cookie);
            }
        }

        return StorageStates.FromCookies(cookies.Select(cookie => new BrowserCookie(
            cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Expires,
            cookie.HttpOnly, cookie.Secure, cookie.SameSite.ToString())));
    }

    private static Dictionary<(string Name, string Domain, string Path), string> ReadCookies(string storageState, string host)
    {
        try
        {
            return CookieImport.Parse(storageState, host).ToDictionary(c => (c.Name, c.Domain, c.Path), c => c.Value);
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        try
        {
            await Context.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }
}

/// <summary>Long-lived tab on a tracked project's chat; streams what it receives to the latest listener.</summary>
internal sealed class ProjectTab(IPage page)
{
    private const int MaxErrorBody = 4000;
    private TaskCompletionSource<int> _started = NewSource();
    private TaskCompletionSource<int> _ended = NewSource();
    private readonly System.Text.StringBuilder _errorBody = new();
    private ChatGptStreamParser _parser = new();
    private IProgress<string>? _liveText;
    private int _status;

    public IPage Page { get; } = page;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public Task<int> Started => _started.Task;
    public Task Ended => _ended.Task;
    public string ErrorBody => _errorBody.ToString();
    public string? ConversationId => _parser.ConversationId;

    public void BeginSubmission(IProgress<string>? liveText)
    {
        _liveText = liveText;
        _parser = new ChatGptStreamParser();
        _started = NewSource();
        _ended = NewSource();
        _errorBody.Clear();
        _status = 0;
    }

    public bool OnSignal(string kind, string data)
    {
        switch (kind)
        {
            case "start":
                _parser = new ChatGptStreamParser();
                _status = int.TryParse(data, out var status) ? status : 0;
                _started.TrySetResult(_status);
                break;
            case "chunk" when _status >= 400:
                if (_errorBody.Length < MaxErrorBody)
                    _errorBody.Append(data);
                break;
            case "chunk":
                foreach (var delta in _parser.Feed(data))
                    _liveText?.Report(delta);
                break;
            case "end" or "error":
                _ended.TrySetResult(0);
                break;
        }
        return true;
    }

    private static TaskCompletionSource<int> NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
