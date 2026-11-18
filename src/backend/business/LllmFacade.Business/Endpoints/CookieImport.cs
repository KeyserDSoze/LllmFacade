using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LllmFacade.Business.Endpoints;

/// <summary>Cookie in the Playwright storage-state shape.</summary>
public sealed record BrowserCookie(
    string Name, string Value, string Domain, string Path, double Expires, bool HttpOnly, bool Secure, string SameSite);

/// <summary>Accepts what an admin can copy from a real browser: the Cookie header, a JSON export or a storage state.</summary>
public static class CookieImport
{
    public const int MaxInputLength = 256 * 1024;
    public const int MaxCookies = 300;
    private const string InvalidFormat = "Formato dei cookie non valido: usare l'header Cookie (nome=valore; ...) oppure un export JSON.";

    public static IReadOnlyList<BrowserCookie> Parse(string? input, string host)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Inserire i cookie del browser.", nameof(input));
        if (text.Length > MaxInputLength)
            throw new ArgumentException("I cookie superano la dimensione massima consentita.", nameof(input));

        var parsed = text[0] is '[' or '{' ? ParseJson(text, host) : ParseHeader(text, host);
        var result = new List<BrowserCookie>();
        var positions = new Dictionary<(string, string, string), int>();
        foreach (var cookie in parsed.Where(c => MatchesHost(c.Domain, host)))
        {
            var key = (cookie.Name, cookie.Domain.ToLowerInvariant(), cookie.Path);
            if (positions.TryGetValue(key, out var index))
                result[index] = cookie;
            else
            {
                positions[key] = result.Count;
                result.Add(cookie);
            }
        }

        if (result.Count == 0)
            throw new ArgumentException($"Nessun cookie valido per {host}.", nameof(input));
        if (result.Count > MaxCookies)
            throw new ArgumentException($"Sono ammessi al massimo {MaxCookies} cookie.", nameof(input));
        return result;
    }

    private static List<BrowserCookie> ParseHeader(string text, string host)
    {
        if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            text = text[7..];

        var cookies = new List<BrowserCookie>();
        foreach (var part in text.Split(';'))
        {
            var item = part.Trim();
            if (item.Length == 0)
                continue;
            var separator = item.IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException(InvalidFormat, "input");
            var name = item[..separator].Trim();
            var value = item[(separator + 1)..].Trim();
            Validate(name, value);
            var hostOnly = name.StartsWith("__Host-", StringComparison.Ordinal);
            cookies.Add(new BrowserCookie(name, value, hostOnly ? host : "." + host, "/", -1, false, true, "Lax"));
        }
        return cookies;
    }

    private static List<BrowserCookie> ParseJson(string text, string host)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new ArgumentException(InvalidFormat, "input");
        }

        using (document)
        {
            var root = document.RootElement;
            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("cookies", out var inner) && inner.ValueKind == JsonValueKind.Array => inner,
                _ => throw new ArgumentException(InvalidFormat, "input")
            };

            var cookies = new List<BrowserCookie>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException(InvalidFormat, "input");
                var name = ReadString(item, "name")?.Trim() ?? string.Empty;
                var value = ReadString(item, "value") ?? string.Empty;
                Validate(name, value);
                var expires = ReadBool(item, "session") == true
                    ? -1
                    : ReadNumber(item, "expirationDate") ?? ReadNumber(item, "expires") ?? -1;
                var sameSite = NormalizeSameSite(ReadString(item, "sameSite"));
                var secure = ReadBool(item, "secure")
                    ?? name.StartsWith("__Secure-", StringComparison.Ordinal) || name.StartsWith("__Host-", StringComparison.Ordinal);
                cookies.Add(new BrowserCookie(
                    name,
                    value,
                    ReadString(item, "domain")?.Trim() is { Length: > 0 } domain ? domain : "." + host,
                    ReadString(item, "path") is { Length: > 0 } path ? path : "/",
                    expires > 0 ? expires : -1,
                    ReadBool(item, "httpOnly") ?? false,
                    secure || sameSite == "None",
                    sameSite));
            }
            return cookies;
        }
    }

    private static void Validate(string name, string value)
    {
        if (name.Length is 0 or > 256 || name.Any(c => c <= 0x20 || c >= 0x7F || "()<>@,;:\\\"/[]?={}".Contains(c)))
            throw new ArgumentException(InvalidFormat, "input");
        if (value.Length > 8192 || value.Any(c => c < 0x20 || c == 0x7F || c == ';'))
            throw new ArgumentException(InvalidFormat, "input");
    }

    private static bool MatchesHost(string domain, string host)
    {
        var normalized = domain.TrimStart('.');
        return normalized.Equals(host, StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSameSite(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" or "no_restriction" => "None",
        "strict" => "Strict",
        _ => "Lax"
    };

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? ReadBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? ReadNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

public static class StorageStates
{
    public static string FromCookies(IEnumerable<BrowserCookie> cookies) => JsonSerializer.Serialize(new
    {
        cookies = cookies.Select(c => new
        {
            name = c.Name,
            value = c.Value,
            domain = c.Domain,
            path = c.Path,
            expires = c.Expires,
            httpOnly = c.HttpOnly,
            secure = c.Secure,
            sameSite = c.SameSite
        }),
        origins = Array.Empty<object>()
    });

    public static int CountCookies(string storageStateJson)
    {
        using var document = JsonDocument.Parse(storageStateJson);
        return document.RootElement.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array
            ? cookies.GetArrayLength()
            : 0;
    }

    /// <summary>Order-independent fingerprint used to skip writes when the browser did not change any cookie.</summary>
    public static string Hash(string storageStateJson)
    {
        using var document = JsonDocument.Parse(storageStateJson);
        var lines = new List<string>();
        if (document.RootElement.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
        {
            foreach (var cookie in cookies.EnumerateArray())
            {
                lines.Add(string.Join('\u001f',
                    Read(cookie, "domain"), Read(cookie, "path"), Read(cookie, "name"), Read(cookie, "value"), Read(cookie, "expires")));
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', lines))));

        static string Read(JsonElement cookie, string name) =>
            cookie.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
    }
}
