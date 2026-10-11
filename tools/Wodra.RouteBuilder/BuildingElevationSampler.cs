using System.Diagnostics;
using System.Text.Json;

namespace Wodra.RouteBuilder;

public static class BuildingElevationSampler
{
    private sealed record SamplePoint(double Longitude, double Latitude);
    private sealed record Sample(string Key, double Longitude, double Latitude, IReadOnlyList<SamplePoint> Footprint);
    private sealed record Elevation(string Key, double? ElevationMeters, IReadOnlyList<double?> FootprintElevationMeters);
    private sealed record ElevationOutput(IReadOnlyList<Elevation> Elevations);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<BuildingExtractionResult> SampleAsync(BuildingExtractionResult input, string cacheDirectory)
    {
        if (input.Buildings.Count == 0) return input;
        var timer = Stopwatch.StartNew();
        var rasterDirectory = Path.Combine(cacheDirectory, "rasters");
        if (!Directory.Exists(rasterDirectory) || !Directory.EnumerateFiles(rasterDirectory).Any())
            throw new InvalidDataException($"No cached LINZ DEM rasters found in '{rasterDirectory}'. Building elevations are required and will not be invented.");
        var inputPath = Path.Combine(Path.GetTempPath(), $"wodra-building-elevation-{Guid.NewGuid():N}.json");
        try
        {
            var samples = input.Buildings.Select(item =>
            {
                var ring = item.Footprint.Take(item.Footprint.Count - 1).ToArray();
                return new Sample(Key(item), ring.Average(point => point.Longitude), ring.Average(point => point.Latitude),
                    item.Footprint.Select(point => new SamplePoint(point.Longitude, point.Latitude)).ToArray());
            }).ToArray();
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new { samples }, JsonOptions));
            var script = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Wodra.RouteBuilder", "elevation", "sample_buildings.py");
            script = Path.GetFullPath(script);
            if (!File.Exists(script)) script = Path.GetFullPath(Path.Combine("tools", "Wodra.RouteBuilder", "elevation", "sample_buildings.py"));
            var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(script); start.ArgumentList.Add(inputPath); start.ArgumentList.Add(rasterDirectory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python for LINZ building sampling.");
            var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidDataException((await stderr).Trim());
            var sampled = JsonSerializer.Deserialize<ElevationOutput>(await File.ReadAllTextAsync(inputPath), JsonOptions)
                ?? throw new InvalidDataException("LINZ building sampler produced invalid output.");
            var values = sampled.Elevations.ToDictionary(item => item.Key, item => item, StringComparer.Ordinal);
            var buildings = input.Buildings.Where(item => values[Key(item)].ElevationMeters.HasValue)
                .Select(item =>
                {
                    var elevation = values[Key(item)];
                    var footprintValues = elevation.FootprintElevationMeters;
                    var validFootprintValues = footprintValues.Take(Math.Max(0, item.Footprint.Count - 1))
                        .Where(value => value.HasValue).Select(value => value!.Value).ToArray();
                    var baseElevation = validFootprintValues.Length > 0 ? validFootprintValues.Average() : elevation.ElevationMeters;
                    return item with { BaseElevationMeters = baseElevation, FootprintElevationMeters = footprintValues };
                }).ToArray();
            var skipped = input.Buildings.Count - buildings.Length;
            Console.WriteLine($"LINZ building elevations sampled: {buildings.Length}, omitted {skipped} without DEM coverage ({string.Join("; ", (await stdout).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries))})");
            timer.Stop();
            return input with { Buildings = buildings, SelectedCount = buildings.Length, ElevationSkippedCount = skipped,
                ExtractionSeconds = input.ExtractionSeconds + timer.Elapsed.TotalSeconds };
        }
        finally { if (File.Exists(inputPath)) File.Delete(inputPath); }
    }

    private static string Key(RouteBuilding item) => $"{item.OsmType}/{item.OsmId}";
}
