using System.Diagnostics;
using System.Text.Json;

namespace GWift.RouteBuilder;

public static class BuildingElevationSampler
{
    private sealed record Sample(string Key, double Longitude, double Latitude);
    private sealed record Elevation(string Key, double ElevationMeters);
    private sealed record ElevationOutput(IReadOnlyList<Elevation> Elevations);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<BuildingExtractionResult> SampleAsync(BuildingExtractionResult input, string cacheDirectory)
    {
        if (input.Buildings.Count == 0) return input;
        var timer = Stopwatch.StartNew();
        var rasterDirectory = Path.Combine(cacheDirectory, "rasters");
        if (!Directory.Exists(rasterDirectory) || !Directory.EnumerateFiles(rasterDirectory).Any())
            throw new InvalidDataException($"No cached LINZ DEM rasters found in '{rasterDirectory}'. Building elevations are required and will not be invented.");
        var inputPath = Path.Combine(Path.GetTempPath(), $"gwift-building-elevation-{Guid.NewGuid():N}.json");
        try
        {
            var samples = input.Buildings.Select(item =>
            {
                var ring = item.Footprint.Take(item.Footprint.Count - 1).ToArray();
                return new Sample(Key(item), ring.Average(point => point.Longitude), ring.Average(point => point.Latitude));
            }).ToArray();
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new { samples }, JsonOptions));
            var script = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "GWift.RouteBuilder", "elevation", "sample_buildings.py");
            script = Path.GetFullPath(script);
            if (!File.Exists(script)) script = Path.GetFullPath(Path.Combine("tools", "GWift.RouteBuilder", "elevation", "sample_buildings.py"));
            var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(script); start.ArgumentList.Add(inputPath); start.ArgumentList.Add(rasterDirectory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python for LINZ building sampling.");
            var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidDataException((await stderr).Trim());
            var sampled = JsonSerializer.Deserialize<ElevationOutput>(await File.ReadAllTextAsync(inputPath), JsonOptions)
                ?? throw new InvalidDataException("LINZ building sampler produced invalid output.");
            var values = sampled.Elevations.ToDictionary(item => item.Key, item => item.ElevationMeters, StringComparer.Ordinal);
            var buildings = input.Buildings.Select(item => item with { BaseElevationMeters = values[Key(item)] }).ToArray();
            Console.WriteLine($"LINZ building elevations sampled: {buildings.Length} ({string.Join("; ", (await stdout).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries))})");
            timer.Stop();
            return input with { Buildings = buildings, ExtractionSeconds = input.ExtractionSeconds + timer.Elapsed.TotalSeconds };
        }
        finally { if (File.Exists(inputPath)) File.Delete(inputPath); }
    }

    private static string Key(RouteBuilding item) => $"{item.OsmType}/{item.OsmId}";
}
