using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public sealed record RouteStreetFurnitureItemData(string OsmType, long OsmId, string Category,
    double Latitude, double Longitude, double? ElevationMeters, double? DirectionDegrees,
    double RouteTangentDegrees, double DistanceFromRouteMeters, double NearestRouteDistanceMeters,
    string? Name, Dictionary<string, string> Tags);
public sealed record RouteStreetFurnitureData(string Id, IReadOnlyList<RouteStreetFurnitureItemData> Items);

public static class RouteStreetFurnitureDataLoader
{
    private sealed record FurnitureFile(string? Id, IReadOnlyList<RouteStreetFurnitureItemData>? Items);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static RouteStreetFurnitureData LoadEmbedded(Assembly assembly, string resourceName, string routeId)
    {
        using var stream = RoutePackageFiles.Open(assembly, resourceName, routeId)
            ?? throw new InvalidDataException($"Route street-furniture resource '{resourceName}' was not found.");
        var file = JsonSerializer.Deserialize<FurnitureFile>(stream, Options)
            ?? throw new InvalidDataException($"Embedded street-furniture resource '{resourceName}' is empty.");
        if (!string.Equals(file.Id, routeId, StringComparison.Ordinal) || file.Items is null)
            throw new InvalidDataException($"Embedded street-furniture resource '{resourceName}' does not match route '{routeId}'.");
        if (file.Items.Any(item => item.OsmId <= 0 || string.IsNullOrWhiteSpace(item.Category)
            || !double.IsFinite(item.Latitude) || item.Latitude is < -90 or > 90
            || !double.IsFinite(item.Longitude) || item.Longitude is < -180 or > 180
            || item.ElevationMeters is not { } elevation || !double.IsFinite(elevation)
            || !double.IsFinite(item.NearestRouteDistanceMeters) || !double.IsFinite(item.DistanceFromRouteMeters)))
            throw new InvalidDataException($"Embedded street-furniture resource '{resourceName}' contains invalid feature data.");
        return new RouteStreetFurnitureData(routeId, file.Items);
    }
}
