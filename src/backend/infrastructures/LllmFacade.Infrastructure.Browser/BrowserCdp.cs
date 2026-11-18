using System.Net.Http.Json;
using System.Text.Json;

namespace LllmFacade.Infrastructure.Browser;

/// <summary>Limits the CDP bridge to the local machine and rewrites Chrome's loopback WebSocket URL for Docker.</summary>
internal static class BrowserCdp
{
    private static readonly HashSet<string> LocalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "[::1]", "host.docker.internal"
    };

    public static Uri ParseEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !LocalHosts.Contains(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("/" or ""))
        {
            throw new ArgumentException(
                "Il browser collegato deve usare un endpoint CDP HTTP locale (localhost, 127.0.0.1, ::1 o host.docker.internal) senza credenziali o path.",
                nameof(endpoint));
        }
        return uri;
    }

    public static string RewriteWebSocket(string webSocketUrl, Uri endpoint)
    {
        if (!Uri.TryCreate(webSocketUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !LocalHosts.Contains(uri.Host)
            || string.IsNullOrEmpty(uri.AbsolutePath))
        {
            throw new ArgumentException("L'endpoint CDP non ha restituito un indirizzo WebSocket locale valido.", nameof(webSocketUrl));
        }

        var builder = new UriBuilder(uri)
        {
            Host = endpoint.Host,
            Port = endpoint.Port
        };
        return builder.Uri.ToString();
    }

    public static string HostHeader(Uri endpoint) =>
        endpoint.Host is "[::1]" or "::1" ? $"[::1]:{endpoint.Port}" : $"127.0.0.1:{endpoint.Port}";

    public static async Task<string> ResolveWebSocketAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        var baseUri = ParseEndpoint(endpoint);
        var versionUri = new Uri(baseUri, "/json/version/");
        using var request = new HttpRequestMessage(HttpMethod.Get, versionUri);
        request.Headers.Host = HostHeader(baseUri);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
            ?? throw new InvalidOperationException("Il browser CDP non ha restituito informazioni.");
        if (!body.RootElement.TryGetProperty("webSocketDebuggerUrl", out var webSocket) || webSocket.GetString() is not { } url)
            throw new InvalidOperationException("Il browser CDP non ha esposto webSocketDebuggerUrl: avvia Chrome con remote debugging.");
        return RewriteWebSocket(url, baseUri);
    }
}
