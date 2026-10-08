using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

/// <summary>Loads a bundled, authoring-generated route centreline without runtime authoring dependencies.</summary>
public static class RouteDataLoader
{
	private sealed record RoutePointsFile(string? Id, string? Name, bool IsValid,
		double ActualOsmDistanceMeters, IReadOnlyList<RoutePointData>? Points);
	private sealed record RoutePointData(double DistanceMeters, double Latitude, double Longitude,
		double? ElevationMeters, double? RawElevationMeters, double? GradientPercent);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	/// <summary>
	/// Loads OSM positions and generated LINZ elevation/gradient values with chainages authored against that geometry.
	/// </summary>
	public static RouteDefinition LoadEmbedded(Assembly assembly, string resourceName, string id, string name,
		string description, IReadOnlyList<RouteSegment> segments)
	{
		using var stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidDataException($"Embedded route data resource '{resourceName}' was not found.");
		var source = JsonSerializer.Deserialize<RoutePointsFile>(stream, JsonOptions)
			?? throw new InvalidDataException($"Embedded route data resource '{resourceName}' is empty.");
		if (!string.Equals(source.Id, id, StringComparison.Ordinal))
			throw new InvalidDataException($"Embedded route data resource '{resourceName}' does not match route id '{id}'.");
		if (!source.IsValid || source.Points is not { Count: >= 2 } points)
			throw new InvalidDataException($"Embedded route data resource '{resourceName}' is not a valid route.");

		var routeLengthMeters = points[^1].DistanceMeters;
		if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0
			|| !double.IsFinite(source.ActualOsmDistanceMeters)
			|| Math.Abs(source.ActualOsmDistanceMeters - routeLengthMeters) > 0.001)
			throw new InvalidDataException($"Embedded route data resource '{resourceName}' has inconsistent route length metadata.");
		for (var i = 0; i < points.Count; i++)
		{
			var point = points[i];
			if (!double.IsFinite(point.DistanceMeters) || !double.IsFinite(point.Latitude)
				|| !double.IsFinite(point.Longitude) || point.Latitude is < -90 or > 90
				|| point.Longitude is < -180 or > 180
				|| (i > 0 && point.DistanceMeters <= points[i - 1].DistanceMeters))
				throw new InvalidDataException($"Embedded route data resource '{resourceName}' contains invalid or unordered points.");
		}
		if (points[0].DistanceMeters != 0 || Math.Abs(points[0].Latitude - points[^1].Latitude) > 1e-9
			|| Math.Abs(points[0].Longitude - points[^1].Longitude) > 1e-9)
			throw new InvalidDataException($"Embedded route data resource '{resourceName}' does not close at the loop seam.");
		if (points.Any(point => point.ElevationMeters is not { } elevation || !double.IsFinite(elevation)
			|| point.RawElevationMeters is not { } raw || !double.IsFinite(raw)
			|| point.GradientPercent is not { } gradient || !double.IsFinite(gradient)))
			throw new InvalidDataException($"Embedded route data resource '{resourceName}' has no complete generated elevation and gradient profile.");

		if (segments.Count == 0 || Math.Abs(segments[0].StartMeters) > 0.001
			|| Math.Abs(segments[^1].EndMeters - routeLengthMeters) > 0.001)
			throw new InvalidDataException($"Route segments for '{id}' must cover the generated route chainage.");
		for (var i = 0; i < segments.Count; i++)
		{
			var segment = segments[i];
			if (!double.IsFinite(segment.StartMeters) || !double.IsFinite(segment.EndMeters)
				|| segment.EndMeters <= segment.StartMeters || string.IsNullOrWhiteSpace(segment.Label)
				|| segment.EndMeters > routeLengthMeters + 0.001
				|| (i > 0 && Math.Abs(segment.StartMeters - segments[i - 1].EndMeters) > 0.001))
				throw new InvalidDataException($"Route segments for '{id}' contain invalid or gapped chainage.");
		}
		var routePoints = points.Select(point => new RoutePoint(point.DistanceMeters,
			point.Latitude, point.Longitude, point.ElevationMeters!.Value, point.RawElevationMeters, point.GradientPercent)).ToArray();
		return new RouteDefinition(id, name, description, routeLengthMeters, segments, routePoints);
	}
}
