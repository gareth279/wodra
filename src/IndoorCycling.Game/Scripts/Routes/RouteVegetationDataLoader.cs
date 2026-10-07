using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public sealed record RouteVegetationAreaData(string OsmType, long OsmId, string? Name, string Category,
    double[][] Polygon, double? BaseElevationMeters, double ElevationVariationMeters, bool RenderGround,
    double AreaSquareMeters, double DistanceFromRouteMeters, double NearestRouteDistanceMeters,
    int GeneratedTreeCount, int OriginalVertexCount, int ElevationSampleCount);
public sealed record RouteVegetationTreeData(string Source, long OsmId, double Latitude, double Longitude,
    double? ElevationMeters, string TreeType, double HeightMeters, double CanopyRadiusMeters,
    double NearestRouteDistanceMeters);
public sealed record RouteVegetationData(string Id, IReadOnlyList<RouteVegetationAreaData> Areas,
    IReadOnlyList<RouteVegetationTreeData> Trees);

/// <summary>Loads the compact generated OSM vegetation bundle for the matching route.</summary>
public static class RouteVegetationDataLoader
{
    private sealed record VegetationFile(string? Id, IReadOnlyList<RouteVegetationAreaData>? Areas,
        IReadOnlyList<RouteVegetationTreeData>? Trees);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static RouteVegetationData LoadEmbedded(Assembly assembly, string resourceName, string routeId)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded vegetation resource '{resourceName}' was not found.");
        var file = JsonSerializer.Deserialize<VegetationFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Embedded vegetation resource '{resourceName}' is empty.");
        if (!string.Equals(file.Id, routeId, StringComparison.Ordinal) || file.Areas is null || file.Trees is null)
            throw new InvalidDataException($"Embedded vegetation resource '{resourceName}' does not match route '{routeId}'.");
        if (file.Areas.Any(area => area.OsmId <= 0 || area.Polygon.Length < 4
                || area.BaseElevationMeters is not { } elevation || !double.IsFinite(elevation)
                || area.Polygon.Any(point => point.Length != 2 || !double.IsFinite(point[0]) || !double.IsFinite(point[1])
                    || point[0] is < -180 or > 180 || point[1] is < -90 or > 90))
            || file.Trees.Any(tree => !double.IsFinite(tree.Latitude) || !double.IsFinite(tree.Longitude)
                || tree.Latitude is < -90 or > 90 || tree.Longitude is < -180 or > 180
                || tree.ElevationMeters is not { } treeElevation || !double.IsFinite(treeElevation)
                || !double.IsFinite(tree.HeightMeters) || tree.HeightMeters <= 0))
            throw new InvalidDataException($"Embedded vegetation resource '{resourceName}' contains invalid feature data.");
        return new RouteVegetationData(routeId, file.Areas, file.Trees);
    }
}
