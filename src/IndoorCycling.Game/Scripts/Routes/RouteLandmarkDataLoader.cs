using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public sealed record RouteLandmarkData(string Name, string Category, double Latitude, double Longitude,
	double DistanceFromRouteMeters, double NearestRouteDistanceMeters, string OsmType, long OsmId);

/// <summary>Loads only the small generated landmark metadata bundled with the game.</summary>
public static class RouteLandmarkDataLoader
{
	private sealed record LandmarkFile(string? Id, IReadOnlyList<RouteLandmarkData>? Landmarks);
	private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

	public static IReadOnlyList<RouteLandmarkData> LoadEmbedded(Assembly assembly, string resourceName, string routeId)
	{
		using var stream = RoutePackageFiles.Open(assembly, resourceName, routeId)
			?? throw new InvalidDataException($"Route landmark resource '{resourceName}' was not found.");
		var file = JsonSerializer.Deserialize<LandmarkFile>(stream, JsonOptions)
			?? throw new InvalidDataException($"Embedded landmark resource '{resourceName}' is empty.");
		if (!string.Equals(file.Id, routeId, StringComparison.Ordinal) || file.Landmarks is null)
			throw new InvalidDataException($"Embedded landmark resource '{resourceName}' does not match route '{routeId}'.");
		if (file.Landmarks.Any(item => string.IsNullOrWhiteSpace(item.Name)
			|| !double.IsFinite(item.Latitude) || !double.IsFinite(item.Longitude)
			|| item.Latitude is < -90 or > 90 || item.Longitude is < -180 or > 180
			|| !double.IsFinite(item.NearestRouteDistanceMeters)))
			throw new InvalidDataException($"Embedded landmark resource '{resourceName}' contains invalid entries.");
		return file.Landmarks;
	}
}
