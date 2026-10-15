using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndoorCycling.Trainer.Models;

/// <summary>Per-Windows-account ANT+ network key configuration stored outside the install directory.</summary>
public static partial class AntNetworkKeyMaterial
{
    public const string GarminNetworkKeysUrl = "https://developer.garmin.com/ant-program/ant-ant-plus/network-keys/";
    public static string ConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wodra", "ant-config.json");

    public static string GetStatus(string? path = null)
    {
        path ??= ConfigurationPath;
        try { var key = Load(path); Array.Clear(key, 0, key.Length); return "Configured"; }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ANT+ is not configured", StringComparison.Ordinal)) { return "Not configured"; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "Not configured"; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        { return "Cannot read ANT+ setup file. Check access to Local AppData."; }
        catch (Exception ex) when (ex is IOException)
        { return "Cannot read ANT+ setup file. Check access to Local AppData."; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        { return "ANT+ setup file is invalid. Clear it and save the key again."; }
    }

    public static byte[] Load(string? path = null)
    {
        path ??= ConfigurationPath;
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            if (!document.RootElement.TryGetProperty("networkKey", out var property) || property.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("ANT+ setup file must contain a 16-digit networkKey.");
            return Parse(property.GetString() ?? string.Empty);
        }
        catch (FileNotFoundException) { throw new InvalidOperationException("ANT+ is not configured. Open Settings and choose Set up ANT+."); }
        catch (DirectoryNotFoundException) { throw new InvalidOperationException("ANT+ is not configured. Open Settings and choose Set up ANT+."); }
        catch (JsonException) { throw new InvalidDataException("ANT+ setup file is invalid. Open Settings and save the key again."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("ANT+ setup file cannot be read. Check access to Local AppData."); }
        catch (SecurityException) { throw new InvalidOperationException("ANT+ setup file cannot be read. Check access to Local AppData."); }
    }

    public static void Save(string value, string? path = null)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!NetworkKeyPattern().IsMatch(normalized))
            throw new InvalidDataException("Enter the ANT+ network key as exactly 16 hexadecimal digits.");
        path ??= ConfigurationPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".ant-config-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { networkKey = normalized });
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
            if (File.Exists(path)) File.Move(temporary, path, true); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Clear(string? path = null)
    {
        path ??= ConfigurationPath;
        try { File.Delete(path); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("ANT+ setup file cannot be cleared. Check access to Local AppData."); }
        catch (SecurityException) { throw new InvalidOperationException("ANT+ setup file cannot be cleared. Check access to Local AppData."); }
    }

    public static byte[] Parse(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!NetworkKeyPattern().IsMatch(normalized)) throw new InvalidDataException("ANT+ network key must contain exactly 16 hexadecimal digits.");
        return Convert.FromHexString(normalized);
    }

    [GeneratedRegex("^[0-9a-fA-F]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkKeyPattern();
}
