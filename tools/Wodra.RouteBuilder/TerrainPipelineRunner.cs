using System.Diagnostics;
using System.Globalization;

namespace Wodra.RouteBuilder;

internal static class TerrainPipelineRunner
{
    public static async Task RunAsync(string outputDirectory, string id, TerrainBuildOptions options)
    {
        var script = Path.GetFullPath(Path.Combine("tools", "Wodra.RouteBuilder", "elevation", "generate_terrain.py"));
        if (!File.Exists(script)) throw new FileNotFoundException("Terrain authoring helper is missing.", script);
        var stem = Path.Combine(Path.GetFullPath(outputDirectory), id);
        var start = new ProcessStartInfo("python")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(script);
        foreach (var (key, value) in new[]
        {
            ("--route", stem + ".route-points.json"), ("--report", stem + ".report.json"),
            ("--cache", Path.GetFullPath(Path.Combine(options.ElevationCacheDirectory, "rasters"))),
            ("--output", stem + ".terrain.json"),
            ("--near-radius", options.NearRadiusMeters.ToString(CultureInfo.InvariantCulture)),
            ("--far-radius", options.FarRadiusMeters.ToString(CultureInfo.InvariantCulture)),
            ("--spacing", options.SpacingMeters.ToString(CultureInfo.InvariantCulture)),
            ("--far-spacing", options.FarSpacingMeters.ToString(CultureInfo.InvariantCulture)),
            ("--corridor-radius", options.CorridorRadiusMeters.ToString(CultureInfo.InvariantCulture)),
            ("--chunk-size", options.ChunkSizeMeters.ToString(CultureInfo.InvariantCulture)),
            ("--far-chunk-size", options.FarChunkSizeMeters.ToString(CultureInfo.InvariantCulture)),
            ("--transition-overlap", options.TransitionOverlapMeters.ToString(CultureInfo.InvariantCulture)),
            ("--far-vertical-bias", options.FarVerticalBiasMeters.ToString(CultureInfo.InvariantCulture)),
            ("--vertical-bias", options.VerticalBiasMeters.ToString(CultureInfo.InvariantCulture)),
            ("--road-clearance", options.RoadClearanceMeters.ToString(CultureInfo.InvariantCulture)),
            ("--road-corridor-radius", options.RoadCorridorRadiusMeters.ToString(CultureInfo.InvariantCulture))
        }) { start.ArgumentList.Add(key); start.ArgumentList.Add(value); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python terrain authoring helper.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var outText = await stdout;
        var errText = await stderr;
        if (!string.IsNullOrWhiteSpace(outText)) Console.Write(outText);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Terrain generation failed. Ensure Python dependencies are installed and cached LINZ DEM tiles exist. " + errText.Trim());
        if (!string.IsNullOrWhiteSpace(errText)) Console.Error.Write(errText);
    }
}
