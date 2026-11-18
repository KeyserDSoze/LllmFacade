using System.Buffers.Text;
using System.Text.Json;

namespace LllmFacade.Business.Sessions;

public static class AccessTokens
{
    public const int MaxLength = 16 * 1024;

    /// <summary>Accepts the raw JWT or a copied "Bearer ..." value; null when empty.</summary>
    public static string? Normalize(string? input)
    {
        var token = input?.Trim();
        if (string.IsNullOrEmpty(token))
            return null;
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        if (token.Length is 0 or > MaxLength || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Token di accesso non valido.", nameof(input));
        return token;
    }

    /// <summary>Reads the unverified "exp" claim: only used to schedule refreshes, never to trust the token.</summary>
    public static DateTimeOffset? ReadExpiry(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
            return null;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number)
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)exp.GetDouble());
            }
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }
        return null;
    }
}

public static class SessionRefreshPolicy
{
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

    public static bool IsDue(
        DateTimeOffset now,
        TimeSpan interval,
        DateTimeOffset? lastRefreshAt,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? tokenExpiresAt)
    {
        var lastAttemptFailed = lastAttemptAt is not null && lastAttemptAt != lastRefreshAt;
        if (lastAttemptFailed && now - lastAttemptAt!.Value < FailureBackoff)
            return false;
        if (lastRefreshAt is null || now - lastRefreshAt.Value >= interval)
            return true;
        return tokenExpiresAt is { } expiry && expiry - now <= ExpiryMargin;
    }
}
