using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LllmFacade.Business.Security;

public static class PasswordRules
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    public static IReadOnlyList<string> Validate(string userName, string? currentPassword, string? newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            return ["La nuova password è obbligatoria."];

        var errors = new List<string>();
        if (newPassword.Length < MinLength)
            errors.Add($"La password deve contenere almeno {MinLength} caratteri.");
        if (newPassword.Length > MaxLength)
            errors.Add($"La password può contenere al massimo {MaxLength} caratteri.");
        if (string.Equals(newPassword.Trim(), userName.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("La password non può coincidere con il nome utente.");
        if (currentPassword is not null && newPassword == currentPassword)
            errors.Add("La nuova password deve essere diversa da quella attuale.");
        return errors;
    }
}

public sealed record GeneratedApiKey(string Key, string LookupPrefix, string Hash);

/// <summary>High-entropy keys: a fast SHA-256 hash is enough and allows constant-time verification.</summary>
public static class ApiKeys
{
    public const string Prefix = "lf_";
    public const int LookupLength = 11;
    private const int SecretBytes = 32;
    private const int MinKeyLength = 35;
    private const int MaxKeyLength = 200;

    public static GeneratedApiKey Generate()
    {
        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        return new GeneratedApiKey(key, key[..LookupLength], Hash(key));
    }

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool Verify(string key, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(expectedHash));

    public static bool TryGetLookupPrefix(string? key, out string prefix)
    {
        prefix = string.Empty;
        if (key is null || key.Length is < MinKeyLength or > MaxKeyLength || !key.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        prefix = key[..LookupLength];
        return true;
    }
}
