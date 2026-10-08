using System;
using System.IO;

namespace IndoorCycling.Core.Persistence;

public static class ProductFilePaths
{
    public const string DefaultFitOutputFolderSetting = "%USERPROFILE%\\Documents\\Wodra";

    public static string UpgradeLegacyFitOutputFolder(string configuredPath, string userProfile, string documents)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;
        var normalized = configuredPath.Replace('/', '\\');
        if (string.Equals(normalized, "%USERPROFILE%\\Documents\\gwift", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "%DOCUMENTS%\\gwift", StringComparison.OrdinalIgnoreCase))
            return DefaultFitOutputFolderSetting;

        var oldDefault = Path.GetFullPath(Path.Combine(documents, "gwift"));
        try
        {
            var expanded = normalized
                .Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase)
                .Replace("%DOCUMENTS%", documents, StringComparison.OrdinalIgnoreCase);
            if (Path.IsPathFullyQualified(expanded)
                && string.Equals(Path.GetFullPath(expanded), oldDefault, StringComparison.OrdinalIgnoreCase))
                return DefaultFitOutputFolderSetting;
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (PathTooLongException) { }
        return configuredPath;
    }

    public static string ResolveFitOutputFolder(string configuredPath, string userProfile, string documents)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return Path.Combine(documents, "Wodra");
        var expanded = configuredPath
            .Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%DOCUMENTS%", documents, StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(expanded);
    }

    public static string CreateFitFileName(DateTime timestamp) => $"wodra-{timestamp:yyyyMMdd-HHmmss}.fit";
}
