namespace LllmFacade.Business;

/// <summary>Pure validation, independent of persistence and transport.</summary>
public static class ProjectRules
{
    public static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 60)
            throw new ArgumentException("Il nome del progetto deve contenere da 1 a 60 caratteri.", nameof(name));
        return normalized;
    }
}

