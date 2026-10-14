using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public sealed record StravaFitSegment(int Index, string Path, bool IsValid, string DisplayName);

public static class StravaFitSegmentReader
{
    public static IReadOnlyList<StravaFitSegment> Read(Guid activityId, string? fitPath)
    {
        if (string.IsNullOrWhiteSpace(fitPath)) return Array.Empty<StravaFitSegment>();
        var directory = Path.GetDirectoryName(fitPath);
        if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<StravaFitSegment>();
        var manifestPath = Path.Combine(directory, $"{activityId:N}.segments.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var segments = manifest.RootElement.GetProperty("segments");
                if (segments.ValueKind != JsonValueKind.Array) throw new JsonException();
                var result = new List<StravaFitSegment>();
                var index = 0;
                foreach (var segment in segments.EnumerateArray())
                {
                    var fileValue = segment.TryGetProperty("FileName", out var pascal)
                        ? pascal : segment.TryGetProperty("fileName", out var camel) ? camel : default;
                    var rawName = fileValue.ValueKind == JsonValueKind.String ? fileValue.GetString() : null;
                    var fileName = string.IsNullOrWhiteSpace(rawName) ? "" : Path.GetFileName(rawName);
                    var safeName = !string.IsNullOrWhiteSpace(rawName) && string.Equals(rawName, fileName, StringComparison.Ordinal);
                    var path = safeName ? Path.Combine(directory, fileName) : "";
                    var valid = safeName && string.Equals(Path.GetExtension(fileName), ".fit", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(path) && new FileInfo(path).Length > 0;
                    var display = !safeName ? "Unsafe segment file name" : string.IsNullOrWhiteSpace(fileName) ? "Unrecognized segment" : fileName;
                    result.Add(new StravaFitSegment(index, path, valid, display));
                    index++;
                }
                if (result.Count > 0) return result;
                return new[] { new StravaFitSegment(0, "", false, "Segment manifest contains no saved FIT segments") };
            }
            catch { return new[] { new StravaFitSegment(0, "", false, "Segment manifest could not be read") }; }
        }
        var validFallback = File.Exists(fitPath) && string.Equals(Path.GetExtension(fitPath), ".fit", StringComparison.OrdinalIgnoreCase) && new FileInfo(fitPath).Length > 0;
        return validFallback
            ? new[] { new StravaFitSegment(0, fitPath, true, Path.GetFileName(fitPath)) }
            : Array.Empty<StravaFitSegment>();
    }
}
