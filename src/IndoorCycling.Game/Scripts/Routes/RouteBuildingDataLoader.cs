using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public sealed record RouteBuildingPoint(double Longitude, double Latitude);
public sealed record RouteBuildingData(string OsmType, long OsmId, string? Name, string BuildingType,
    double HeightMeters, string HeightSource, double? BaseElevationMeters,
    IReadOnlyList<RouteBuildingPoint> Footprint, double DistanceFromRouteMeters,
    double NearestRouteDistanceMeters, IReadOnlyDictionary<string, string> SourceTags)
{
    public string VisualCategory { get; init; } = "generic";
    public int MaterialVariant { get; init; }
}

/// <summary>Loads and validates the compact generated OSM building bundle.</summary>
public static class RouteBuildingDataLoader
{
    private sealed record BuildingFile(string? Id, IReadOnlyList<RouteBuildingData>? Buildings);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<RouteBuildingData> LoadEmbedded(Assembly assembly, string resourceName, string routeId)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded building resource '{resourceName}' was not found.");
        var file = JsonSerializer.Deserialize<BuildingFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Embedded building resource '{resourceName}' is empty.");
        if (!string.Equals(file.Id, routeId, StringComparison.Ordinal) || file.Buildings is null)
            throw new InvalidDataException($"Embedded building resource '{resourceName}' does not match route '{routeId}'.");
        foreach (var item in file.Buildings)
        {
            if (item.OsmId <= 0 || item.Footprint.Count < 4 || item.BaseElevationMeters is not { } elevation
                || !double.IsFinite(elevation) || !double.IsFinite(item.HeightMeters) || item.HeightMeters <= 0
                || item.Footprint.Any(point => !double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude)
                    || point.Latitude is < -90 or > 90 || point.Longitude is < -180 or > 180))
                throw new InvalidDataException($"Embedded building resource '{resourceName}' contains invalid building data.");
        }
        return file.Buildings;
    }
}
