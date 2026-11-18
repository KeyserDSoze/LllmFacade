using System.Text.RegularExpressions;
using LllmFacade.Business.Chat;
using LllmFacade.Domain;

namespace LllmFacade.Business.Endpoints;

public static partial class EndpointRules
{
    public const int MaxModels = 30;
    public const int MaxHeaders = 30;
    public const string DefaultLocale = "it-IT";
    public const string DefaultTimeZone = "Europe/Rome";
    public const int DefaultRefreshIntervalMinutes = 30;

    // Headers owned by the browser or by dedicated fields (cookies, token, user agent).
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "cookie", "authorization", "connection", "transfer-encoding", "upgrade",
        "te", "trailer", "keep-alive", "expect", "user-agent"
    };

    public static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 80 || normalized.Any(char.IsControl))
            throw new ArgumentException("Il nome dell'endpoint deve contenere da 1 a 80 caratteri.", nameof(name));
        return normalized;
    }

    public static IReadOnlyList<ModelMapping> NormalizeModels(IEnumerable<ModelMapping>? models)
    {
        var result = new List<ModelMapping>();
        foreach (var model in models ?? [])
        {
            var name = model.Name?.Trim() ?? string.Empty;
            var upstream = model.UpstreamModel?.Trim() ?? string.Empty;
            if (!ModelNamePattern().IsMatch(name))
                throw new ArgumentException($"Nome modello non valido: '{name}'. Usare lettere, numeri e . _ : / -", nameof(models));
            if (!UpstreamModelPattern().IsMatch(upstream))
                throw new ArgumentException($"Modello ChatGPT non valido per '{name}'.", nameof(models));
            if (result.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Il modello '{name}' è duplicato.", nameof(models));
            result.Add(new ModelMapping(name, upstream, ReasoningEfforts.Normalize(model.DefaultReasoningEffort)));
        }

        if (result.Count is 0 or > MaxModels)
            throw new ArgumentException($"Configurare da 1 a {MaxModels} modelli.", nameof(models));
        return result;
    }

    public static Dictionary<string, string> NormalizeThinking(IDictionary<string, string>? map)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? new Dictionary<string, string>())
        {
            var effort = ReasoningEfforts.Normalize(key)
                ?? throw new ArgumentException("Livello di reasoning vuoto.", nameof(map));
            var thinking = value?.Trim() ?? string.Empty;
            if (!ThinkingValuePattern().IsMatch(thinking))
                throw new ArgumentException($"Valore di thinking non valido per '{effort}'.", nameof(map));
            result[effort] = thinking;
        }
        return result;
    }

    public static Dictionary<string, string> NormalizeHeaders(IDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, rawValue) in headers ?? new Dictionary<string, string>())
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (!HeaderNamePattern().IsMatch(name))
                throw new ArgumentException($"Nome header non valido: '{name}'.", nameof(headers));
            if (ReservedHeaders.Contains(name) || name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"L'header '{name}' è gestito dal sistema e non può essere impostato.", nameof(headers));
            var value = rawValue?.Trim() ?? string.Empty;
            if (value.Length > 2048 || value.Any(c => c is '\r' or '\n' or '\0'))
                throw new ArgumentException($"Valore non valido per l'header '{name}'.", nameof(headers));
            if (!result.TryAdd(name, value))
                throw new ArgumentException($"L'header '{name}' è duplicato.", nameof(headers));
        }

        if (result.Count > MaxHeaders)
            throw new ArgumentException($"Sono ammessi al massimo {MaxHeaders} header.", nameof(headers));
        return result;
    }

    public static string NormalizeUserAgent(string? userAgent)
    {
        var normalized = userAgent?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 512 || normalized.Any(char.IsControl))
            throw new ArgumentException("Lo user agent è obbligatorio, su una riga, massimo 512 caratteri.", nameof(userAgent));
        return normalized;
    }

    public static string NormalizeLocale(string? locale)
    {
        var normalized = string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale.Trim();
        if (!LocalePattern().IsMatch(normalized))
            throw new ArgumentException("Lingua non valida (esempio: it-IT).", nameof(locale));
        return normalized;
    }

    public static string NormalizeTimeZone(string? timeZone)
    {
        var normalized = string.IsNullOrWhiteSpace(timeZone) ? DefaultTimeZone : timeZone.Trim();
        if (normalized.Length > 64 || !TimeZonePattern().IsMatch(normalized))
            throw new ArgumentException("Fuso orario non valido (esempio: Europe/Rome).", nameof(timeZone));
        return normalized;
    }

    public static int NormalizeRefreshInterval(int? minutes)
    {
        var value = minutes ?? DefaultRefreshIntervalMinutes;
        if (value is < 5 or > 1440)
            throw new ArgumentException("L'intervallo di rinnovo deve essere tra 5 e 1440 minuti.", nameof(minutes));
        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,79}$")]
    private static partial Regex ModelNamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,79}$")]
    private static partial Regex UpstreamModelPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,40}$")]
    private static partial Regex ThinkingValuePattern();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$")]
    private static partial Regex HeaderNamePattern();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LocalePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)*$")]
    private static partial Regex TimeZonePattern();
}
