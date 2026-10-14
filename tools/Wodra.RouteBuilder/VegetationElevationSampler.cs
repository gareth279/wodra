using System.Diagnostics;
using System.Text.Json;

namespace Wodra.RouteBuilder;

public static class VegetationElevationSampler
{
    private sealed record SamplePoint(double Latitude, double Longitude);
    private sealed record AreaInput(int Index, IReadOnlyList<SamplePoint> Samples);
    private sealed record TreeInput(int Index, double Latitude, double Longitude);
    private sealed record AreaOutput(int Index, IReadOnlyList<double?> Elevations);
    private sealed record TreeOutput(int Index, double? ElevationMeters);
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
        var path = Path.Combine(Path.GetTempPath(), $"wodra-vegetation-elevation-{Guid.NewGuid():N}.json");
        try
        {
            var areas = input.Areas.Select((area, index) => new AreaInput(index,
                VegetationExtractor.AreaElevationSamples(area.Polygon)
                    .Select(point => new SamplePoint(point.Latitude, point.Longitude)).ToArray())).ToArray();
            var trees = input.Trees.Select((tree, index) => new TreeInput(index, tree.Latitude, tree.Longitude)).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { areas, trees }, JsonOptions));
            var script = Path.GetFullPath(Path.Combine("tools", "Wodra.RouteBuilder", "elevation", "sample_vegetation.py"));
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
            var resultAreas = input.Areas.Select((area, index) => (Area:area, Samples:areaValues[index]))
                .Where(item => item.Samples.Count > 0 && item.Samples.All(value => value.HasValue))
                .Select(item =>
                {
                    var samples = item.Samples.Select(value => value!.Value).ToArray();
                    var variation = samples.Max() - samples.Min();
                    return item.Area with { BaseElevationMeters = samples.Average(), ElevationVariationMeters = variation,
                        RenderGround = VegetationGroundPolicy.ShouldRenderGround(
                            item.Area.DistanceFromRouteMeters, variation), ElevationSampleCount = samples.Length };
                }).ToArray();
            var resultTrees = input.Trees.Select((tree, index) => (Tree:tree, Elevation:treeValues[index]))
                .Where(item => item.Elevation.HasValue)
                .Select(item => item.Tree with { ElevationMeters = item.Elevation }).ToArray();
            var skippedAreas = input.Areas.Count - resultAreas.Length;
            var skippedTrees = input.Trees.Count - resultTrees.Length;
            timer.Stop();
            Console.WriteLine($"LINZ vegetation elevations sampled: {resultTrees.Length} trees, {sampled.AreaElevations.Sum(item => item.Elevations.Count(value => value.HasValue))} area samples; omitted {skippedTrees} trees and {skippedAreas} areas without complete DEM coverage; cached DEM tiles used: {sampled.CacheHits}");
            return input with { Areas = resultAreas, Trees = resultTrees,
                DemSamples = resultTrees.Length + sampled.AreaElevations.Sum(item => item.Elevations.Count(value => value.HasValue)),
                DemCacheHits = sampled.CacheHits, DemSkippedAreas = skippedAreas, DemSkippedTrees = skippedTrees,
                GenerationSeconds = input.GenerationSeconds + timer.Elapsed.TotalSeconds };
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

/// <summary>Retains route clearance when DEM sampling decides whether a flat area overlay is safe.</summary>
public static class VegetationGroundPolicy
{
    public const double MinimumRouteClearanceMeters = 5;
    public const double MaximumFlatElevationVariationMeters = 3;

    public static bool ShouldRenderGround(double distanceFromRouteMeters, double elevationVariationMeters) =>
        double.IsFinite(distanceFromRouteMeters) && double.IsFinite(elevationVariationMeters)
        && distanceFromRouteMeters >= MinimumRouteClearanceMeters
        && elevationVariationMeters <= MaximumFlatElevationVariationMeters;
}
