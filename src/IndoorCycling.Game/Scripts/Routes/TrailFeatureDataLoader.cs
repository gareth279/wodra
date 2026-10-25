using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IndoorCycling.Core.Simulation;

/// <summary>Loads route-authored optional line geometry without adding route-ID branches to shared ride logic.</summary>
public static class TrailFeatureDataLoader
{
    private sealed record FeaturePackage(string? Id, int Version, TrailFeatureDefinition[]? Features,
        TrailBermDefinition[]? Berms, TrailRollerDefinition[]? Rollers);
    public sealed record TrailCoursePackage(TrailFeatureDefinition[] Features, TrailBermDefinition[] Berms,
        TrailRollerDefinition[] Rollers);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static TrailCoursePackage Load(Assembly assembly, string resourceName, string routeId, double routeLength)
    {
        using var stream = RoutePackageFiles.Open(assembly, resourceName, routeId)
            ?? throw new InvalidDataException($"Trail feature resource '{resourceName}' was not found.");
        var package = JsonSerializer.Deserialize<FeaturePackage>(stream, Options)
            ?? throw new InvalidDataException($"Trail feature resource '{resourceName}' is empty.");
        if (!string.Equals(package.Id, routeId, StringComparison.Ordinal) || package.Version != 1
            || package.Features is not { Length: > 0 })
            throw new InvalidDataException($"Trail feature resource '{resourceName}' has invalid identity or version.");
        if (package.Features.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != package.Features.Length)
            throw new InvalidDataException($"Trail feature resource '{resourceName}' has duplicate feature IDs.");
        foreach (var feature in package.Features)
        {
            if (string.IsNullOrWhiteSpace(feature.Id) || string.IsNullOrWhiteSpace(feature.Name)
                || !double.IsFinite(feature.StartMeters + feature.TakeoffMeters + feature.LandingMeters + feature.EndMeters)
                || feature.StartMeters < 0 || feature.TakeoffMeters <= feature.StartMeters
                || feature.LandingMeters <= feature.TakeoffMeters || feature.EndMeters <= feature.LandingMeters
                || feature.EndMeters > routeLength || feature.RequiredLateralMeters == 0
                || !double.IsFinite(feature.RequiredLateralMeters) || feature.HalfWidthMeters is < .25 or > 1.5
                || feature.MinimumSpeedKph is < 8 or > 60 || feature.MaximumAirHeightMeters is < 0 or > 1.2
                || feature.ScorePoints is < 0 or > 500 || feature.Path is not { Count: >= 4 })
                throw new InvalidDataException($"Trail feature '{feature.Id}' has invalid bounds or scoring metadata.");
            if (Math.Abs(feature.Path[0].RouteDistanceMeters - feature.StartMeters) > .01
                || Math.Abs(feature.Path[^1].RouteDistanceMeters - feature.EndMeters) > .01
                || Math.Abs(feature.Path[0].LateralMeters) > .01 || Math.Abs(feature.Path[^1].LateralMeters) > .01
                || Math.Abs(feature.Path[0].SurfaceLiftMeters) > .01 || Math.Abs(feature.Path[^1].SurfaceLiftMeters) > .01)
                throw new InvalidDataException($"Trail feature '{feature.Id}' line must begin and rejoin the reference line smoothly.");
            for (var index = 0; index < feature.Path.Count; index++)
            {
                var point = feature.Path[index];
                if (!double.IsFinite(point.RouteDistanceMeters + point.LateralMeters + point.SurfaceLiftMeters)
                    || point.RouteDistanceMeters < feature.StartMeters || point.RouteDistanceMeters > feature.EndMeters
                    || Math.Abs(point.LateralMeters) > 4 || Math.Abs(point.SurfaceLiftMeters) > 1.2
                    || (index > 0 && point.RouteDistanceMeters <= feature.Path[index - 1].RouteDistanceMeters))
                    throw new InvalidDataException($"Trail feature '{feature.Id}' has an invalid or unordered line point.");
            }
            if (Math.Abs(TrailFeatureChallenge.LateralAt(feature, feature.TakeoffMeters) - feature.RequiredLateralMeters)
                > feature.HalfWidthMeters)
                throw new InvalidDataException($"Trail feature '{feature.Id}' take-off does not align with its authored line.");
        }
        var berms = package.Berms ?? [];
        if (berms.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.StartMeters < 0 || item.EndMeters <= item.StartMeters
                || item.EndMeters > routeLength || item.Side is not (-1 or 1) || item.WidthMeters is < 0.5f or > 5
                || item.HeightMeters is < 0.1f or > 1.5f))
            throw new InvalidDataException($"Trail feature resource '{resourceName}' has invalid berm geometry.");
        var rollers = package.Rollers ?? [];
        if (rollers.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.StartMeters < 0
                || item.PeakMeters <= item.StartMeters || item.EndMeters <= item.PeakMeters
                || item.EndMeters > routeLength || item.HeightMeters is <= 0 or > 0.35))
            throw new InvalidDataException($"Trail feature resource '{resourceName}' has invalid roller geometry.");
        if (berms.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != berms.Length
            || rollers.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != rollers.Length)
            throw new InvalidDataException($"Trail feature resource '{resourceName}' has duplicate berm or roller IDs.");
        return new(package.Features, berms, rollers);
    }
}
