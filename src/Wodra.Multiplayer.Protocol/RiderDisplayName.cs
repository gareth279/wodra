namespace Wodra.Multiplayer.Protocol;

/// <summary>Resolves a human readable multiplayer name from the selected local profile.</summary>
public static class RiderDisplayName
{
    public static string Resolve(string? selectedProfileName, string? legacyName = null)
    {
        if (IsValid(selectedProfileName, out var profileName)) return profileName;
        if (IsValid(legacyName, out var fallbackName)) return fallbackName;
        return "Rider";
    }

    private static bool IsValid(string? candidate, out string value)
    {
        value = candidate?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 32 && !value.Any(char.IsControl);
    }
}
