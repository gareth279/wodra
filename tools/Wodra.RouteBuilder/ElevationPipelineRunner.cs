using System.Diagnostics;

namespace Wodra.RouteBuilder;

internal static class ElevationPipelineRunner
{
    public static async Task RunAsync(string outputDirectory, string id, ElevationBuildOptions options)
    {
        var script = Path.GetFullPath(Path.Combine("tools", "Wodra.RouteBuilder", "elevation", "generate_elevation.py"));
        if (!File.Exists(script)) throw new FileNotFoundException("Elevation helper is missing.", script);
        var stem = Path.Combine(Path.GetFullPath(outputDirectory), id);
        var cache = Path.GetFullPath(Path.Combine("routes", "cache", "elevation"));
        var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(script);
        foreach (var (key, value) in new[]
        {
            ("--route", stem + ".route-points.json"), ("--report", stem + ".report.json"), ("--cache", cache),
            ("--primary", options.PrimaryCollectionUrl), ("--fallback", options.FallbackCollectionUrl ?? ""),
            ("--smooth", options.SmoothingWindowMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("--gradient", options.GradientWindowMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("--interval", options.ResampleIntervalMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("--bridge-max", options.MaximumBridgeInterpolationMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("--review-gradient", options.GradientReviewThresholdPercent.ToString(System.Globalization.CultureInfo.InvariantCulture))
        }) { start.ArgumentList.Add(key); start.ArgumentList.Add(value); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python elevation helper.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var outText = await stdout; var errText = await stderr;
        if (!string.IsNullOrWhiteSpace(outText)) Console.Write(outText);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Elevation generation failed. Ensure Python dependencies are installed with tools/Wodra.RouteBuilder/elevation/requirements.txt. " + errText.Trim());
        if (!string.IsNullOrWhiteSpace(errText)) Console.Error.Write(errText);
    }
}
