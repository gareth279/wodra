using System.Diagnostics;
using System.Text.Json;

namespace Wodra.RouteBuilder;

public static class StreetFurnitureElevationSampler
{
    private sealed record TreeOutput(int Index, double? ElevationMeters);
    private sealed record SamplerOutput(IReadOnlyList<TreeOutput> TreeElevations, int CacheHits);
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static async Task<StreetFurnitureExtractionResult> SampleAsync(StreetFurnitureExtractionResult input, string cacheDirectory)
    {
        if (input.Items.Count == 0) return input;
        var timer = Stopwatch.StartNew();
        var rasterDirectory = Path.Combine(cacheDirectory, "rasters");
        if (!Directory.Exists(rasterDirectory) || !Directory.EnumerateFiles(rasterDirectory).Any())
            throw new InvalidDataException($"No cached LINZ DEM rasters found in '{rasterDirectory}'. Street-furniture elevations will not be invented.");
        var path = Path.Combine(Path.GetTempPath(), $"wodra-furniture-elevation-{Guid.NewGuid():N}.json");
        try
        {
            var trees = input.Items.Select((item, index) => new { index, latitude = item.Latitude, longitude = item.Longitude }).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { areas = Array.Empty<object>(), trees }, Options));
            var script = Path.GetFullPath(Path.Combine("tools", "Wodra.RouteBuilder", "elevation", "sample_vegetation.py"));
            var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(script); start.ArgumentList.Add(path); start.ArgumentList.Add(rasterDirectory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python for LINZ street-furniture sampling.");
            var stderr = process.StandardError.ReadToEndAsync(); _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidDataException((await stderr).Trim());
            var sampled = JsonSerializer.Deserialize<SamplerOutput>(await File.ReadAllTextAsync(path), Options)
                ?? throw new InvalidDataException("LINZ street-furniture sampler produced invalid output.");
            var heights = sampled.TreeElevations.ToDictionary(item => item.Index, item => item.ElevationMeters);
            if (heights.Count != input.Items.Count) throw new InvalidDataException("LINZ did not return every street-furniture elevation.");
            timer.Stop();
            var items = input.Items.Select((item, index) => (Item:item, Elevation:heights[index]))
                .Where(sample => sample.Elevation.HasValue)
                .Select(sample => sample.Item with { ElevationMeters = sample.Elevation }).ToArray();
            var skipped = input.Items.Count - items.Length;
            return input with { Items = items,
                ItemsInsideRoadRibbon = items.Count(item => StreetFurnitureExtractor.IsInsideRoadRibbon(item.NearestRouteDistanceMeters)),
                ElevationSamples = items.Length, ElevationSkippedCount = skipped,
                GenerationSeconds = input.GenerationSeconds + timer.Elapsed.TotalSeconds };
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
