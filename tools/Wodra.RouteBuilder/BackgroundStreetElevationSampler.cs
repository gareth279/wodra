using System.Diagnostics;
using System.Text.Json;

namespace Wodra.RouteBuilder;

public static class BackgroundStreetElevationSampler
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static async Task<BackgroundStreetExtractionResult> SampleAsync(BackgroundStreetExtractionResult input, string cacheDirectory)
    {
        if (input.Streets.Count == 0) return input;
        var rasterDirectory = Path.Combine(cacheDirectory, "rasters");
        if (!Directory.Exists(rasterDirectory) || !Directory.EnumerateFiles(rasterDirectory).Any())
            throw new InvalidDataException($"No cached LINZ DEM rasters found in '{rasterDirectory}'. Background street elevations will not be invented.");
        var path = Path.Combine(Path.GetTempPath(), $"wodra-street-elevation-{Guid.NewGuid():N}.json");
        var timer=Stopwatch.StartNew();
        try
        {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new { streets=input.Streets.Select((s,i)=>new { index=i,points=s.Points.Select(p=>new { latitude=p.Latitude,longitude=p.Longitude }) }) },Options));
            var script=Path.GetFullPath(Path.Combine("tools","Wodra.RouteBuilder","elevation","sample_streets.py"));
            var start=new ProcessStartInfo("python"){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true};start.ArgumentList.Add(script);start.ArgumentList.Add(path);start.ArgumentList.Add(rasterDirectory);
            using var process=Process.Start(start)??throw new InvalidOperationException("Could not start Python for LINZ street sampling.");
            var stderr=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();if(process.ExitCode!=0)throw new InvalidDataException((await stderr).Trim());
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var elevations=document.RootElement.GetProperty("elevations").EnumerateArray().Select(a=>a.EnumerateArray().Select(v=>v.GetDouble()).ToArray()).ToArray();
            if(elevations.Length!=input.Streets.Count)throw new InvalidDataException("LINZ street sampler returned a different number of street profiles.");
            var streets=input.Streets.Select((s,i)=>s with { Points=s.Points.Select((p,j)=>p with { ElevationMeters=elevations[i][j] }).ToArray() }).ToArray();
            timer.Stop();return input with { Streets=streets,GenerationSeconds=input.GenerationSeconds+timer.Elapsed.TotalSeconds };
        }
        finally { if(File.Exists(path))File.Delete(path); }
    }
}
