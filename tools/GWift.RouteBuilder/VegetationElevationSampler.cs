using System.Diagnostics;
using System.Text.Json;

namespace GWift.RouteBuilder;

public static class VegetationElevationSampler
{
    private sealed record SamplePoint(double Latitude, double Longitude);
    private sealed record AreaInput(int Index, IReadOnlyList<SamplePoint> Samples);
    private sealed record TreeInput(int Index, double Latitude, double Longitude);
    private sealed record AreaOutput(int Index, IReadOnlyList<double> Elevations);
    private sealed record TreeOutput(int Index, double ElevationMeters);
    private sealed record SamplerOutput(IReadOnlyList<AreaOutput> AreaElevations, IReadOnlyList<TreeOutput> TreeElevations, int CacheHits);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static async Task<VegetationExtractionResult> SampleAsync(VegetationExtractionResult input, string cacheDirectory)
    {
        if (input.Areas.Count == 0 && input.Trees.Count == 0) return input;
        var timer = Stopwatch.StartNew();
        var rasterDirectory = Path.Combine(cacheDirectory, "rasters");
        if (!Directory.Exists(rasterDirectory) || !Directory.EnumerateFiles(rasterDirectory).Any())
            throw new InvalidDataException($"No cached LINZ DEM rasters found in '{rasterDirectory}'. Vegetation elevations will not be invented.");
        var path = Path.Combine(Path.GetTempPath(), $"gwift-vegetation-elevation-{Guid.NewGuid():N}.json");
        try
        {
            var areas = input.Areas.Select((area, index) => new AreaInput(index,
                VegetationExtractor.AreaElevationSamples(area.Polygon)
                    .Select(point => new SamplePoint(point.Latitude, point.Longitude)).ToArray())).ToArray();
            var trees = input.Trees.Select((tree, index) => new TreeInput(index, tree.Latitude, tree.Longitude)).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { areas, trees }, JsonOptions));
            var script = Path.GetFullPath(Path.Combine("tools", "GWift.RouteBuilder", "elevation", "sample_vegetation.py"));
            var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(script); start.ArgumentList.Add(path); start.ArgumentList.Add(rasterDirectory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python for LINZ vegetation sampling.");
            var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidDataException((await stderr).Trim());
            var sampled = JsonSerializer.Deserialize<SamplerOutput>(await File.ReadAllTextAsync(path), JsonOptions)
                ?? throw new InvalidDataException("LINZ vegetation sampler produced invalid output.");
            var areaValues = sampled.AreaElevations.ToDictionary(item => item.Index, item => item.Elevations);
            var treeValues = sampled.TreeElevations.ToDictionary(item => item.Index, item => item.ElevationMeters);
            var resultAreas = input.Areas.Select((area, index) =>
            {
                var samples = areaValues[index];
                var variation = samples.Count == 0 ? 0 : samples.Max() - samples.Min();
                var baseElevation = samples.Count == 0 ? throw new InvalidDataException($"No elevation sample for vegetation area {area.OsmType}/{area.OsmId}.") : samples.Average();
                return area with { BaseElevationMeters = baseElevation, ElevationVariationMeters = variation,
                    RenderGround = variation <= 3, ElevationSampleCount = samples.Count };
            }).ToArray();
            var resultTrees = input.Trees.Select((tree, index) => tree with { ElevationMeters = treeValues[index] }).ToArray();
            timer.Stop();
            Console.WriteLine($"LINZ vegetation elevations sampled: {resultTrees.Length} trees, {sampled.AreaElevations.Sum(item => item.Elevations.Count)} area samples; cached DEM tiles used: {sampled.CacheHits}");
            return input with { Areas = resultAreas, Trees = resultTrees,
                DemSamples = resultTrees.Length + sampled.AreaElevations.Sum(item => item.Elevations.Count),
                DemCacheHits = sampled.CacheHits, GenerationSeconds = input.GenerationSeconds + timer.Elapsed.TotalSeconds };
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
