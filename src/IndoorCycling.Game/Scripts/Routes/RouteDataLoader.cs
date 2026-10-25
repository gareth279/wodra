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
		double ActualOsmDistanceMeters, IReadOnlyList<RoutePointData>? Points,
		string? RouteType, string? Difficulty, int? RouteDataVersion, string? GeometryFingerprint,
		string? SourceAttribution, double? TotalAscentMeters, string? Environment, string? Discipline,
		IReadOnlyList<RouteSurfaceSegmentData>? SurfaceSegments);
	private sealed record RoutePointData(double DistanceMeters, double Latitude, double Longitude,
		double? ElevationMeters, double? RawElevationMeters, double? GradientPercent);
	private sealed record RouteSurfaceSegmentData(double StartMeters, double EndMeters, string Surface, long SourceOsmWayId);

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
		using var stream = RoutePackageFiles.Open(assembly, resourceName, id)
			?? throw new InvalidDataException($"Embedded route data resource '{resourceName}' was not found.");
		var source = JsonSerializer.Deserialize<RoutePointsFile>(stream, JsonOptions)
			?? throw new InvalidDataException($"Embedded route data resource '{resourceName}' is empty.");
		return Load(source, id, name, description, segments, resourceName,
			source.RouteType ?? "Loop", source.Difficulty ?? "Unrated",
			source.RouteDataVersion ?? 1, source.SourceAttribution ?? "", source.TotalAscentMeters);
	}

	/// <summary>Loads a reviewed route package installed in the per-user Wodra Routes directory.</summary>
	public static RouteDefinition LoadPackage(string routePointsPath)
	{
		using var stream = File.OpenRead(routePointsPath);
		var source = JsonSerializer.Deserialize<RoutePointsFile>(stream, JsonOptions)
			?? throw new InvalidDataException($"Route package '{routePointsPath}' is empty.");
		if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name))
			throw new InvalidDataException($"Route package '{routePointsPath}' is missing its ID or name.");
		if (source.RouteType is not ("loop" or "outAndBack"))
			throw new InvalidDataException($"Route package '{routePointsPath}' is not a repeated-lap loop or out-and-back route.");
		if (source.RouteDataVersion is not > 0 || source.Difficulty is not ("Unrated" or "Easy" or "Moderate" or "Challenging")
			|| string.IsNullOrWhiteSpace(source.SourceAttribution))
			throw new InvalidDataException($"Route package '{routePointsPath}' has incomplete difficulty, version, or attribution metadata.");
		var description = source.SourceAttribution ?? "OpenStreetMap route package";
		return Load(source, source.Id, source.Name, description, null, routePointsPath,
			source.RouteType, source.Difficulty ?? "Unrated", source.RouteDataVersion ?? 1,
			source.SourceAttribution ?? "", source.TotalAscentMeters);
	}

	private static RouteDefinition Load(RoutePointsFile source, string id, string name, string description,
		IReadOnlyList<RouteSegment>? authoredSegments, string sourceName, string routeType,
		string difficulty, int dataVersion, string attribution, double? publishedAscentMeters = null)
	{
		if (!string.Equals(source.Id, id, StringComparison.Ordinal))
			throw new InvalidDataException($"Route data '{sourceName}' does not match route id '{id}'.");
		if (!source.IsValid || source.Points is not { Count: >= 2 } points)
			throw new InvalidDataException($"Route data '{sourceName}' is not a valid route.");

		var routeLengthMeters = points[^1].DistanceMeters;
		if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0
			|| !double.IsFinite(source.ActualOsmDistanceMeters)
			|| Math.Abs(source.ActualOsmDistanceMeters - routeLengthMeters) > 0.001)
			throw new InvalidDataException($"Route data '{sourceName}' has inconsistent route length metadata.");
		for (var i = 0; i < points.Count; i++)
		{
			var point = points[i];
			if (!double.IsFinite(point.DistanceMeters) || !double.IsFinite(point.Latitude)
				|| !double.IsFinite(point.Longitude) || point.Latitude is < -90 or > 90
				|| point.Longitude is < -180 or > 180
				|| (i > 0 && point.DistanceMeters <= points[i - 1].DistanceMeters))
				throw new InvalidDataException($"Route data '{sourceName}' contains invalid or unordered points.");
		}
		if (points[0].DistanceMeters != 0 || Math.Abs(points[0].Latitude - points[^1].Latitude) > 1e-9
			|| Math.Abs(points[0].Longitude - points[^1].Longitude) > 1e-9)
			throw new InvalidDataException($"Route data '{sourceName}' does not close at the lap seam.");
		if (points.Any(point => point.ElevationMeters is not { } elevation || !double.IsFinite(elevation)
			|| point.RawElevationMeters is not { } raw || !double.IsFinite(raw)
			|| point.GradientPercent is not { } gradient || !double.IsFinite(gradient)))
			throw new InvalidDataException($"Route data '{sourceName}' has no complete generated elevation and gradient profile.");
		var environment = ParseEnvironment(source.Environment);
		var discipline = ParseDiscipline(source.Discipline);
		var surfaces = (source.SurfaceSegments ?? []).Select(item => new RouteSurfaceSegment(
			item.StartMeters, item.EndMeters, item.Surface, item.SourceOsmWayId)).ToArray();
		if (environment == RouteEnvironment.Trail
			&& discipline is not (RouteDiscipline.Gravel or RouteDiscipline.MountainBike))
			throw new InvalidDataException($"Trail environment '{id}' must declare its riding discipline separately as Gravel or MountainBike.");
		if (environment == RouteEnvironment.Trail && surfaces.Length == 0)
			throw new InvalidDataException($"Trail route '{id}' must declare route-relative source surface sections.");
		if (surfaces.Length > 0)
		{
			if (Math.Abs(surfaces[0].StartMeters) > 0.001
				|| Math.Abs(surfaces[^1].EndMeters - routeLengthMeters) > 0.05)
				throw new InvalidDataException($"Surface sections for '{id}' must cover the measured route from 0 through its end.");
			for (var i = 0; i < surfaces.Length; i++)
			{
				var surface = surfaces[i];
				if (!double.IsFinite(surface.StartMeters + surface.EndMeters) || surface.StartMeters < 0
					|| surface.EndMeters <= surface.StartMeters || surface.EndMeters > routeLengthMeters + 0.05
					|| string.IsNullOrWhiteSpace(surface.Surface) || surface.SourceOsmWayId <= 0
					|| (i > 0 && Math.Abs(surface.StartMeters - surfaces[i - 1].EndMeters) > 0.05))
					throw new InvalidDataException($"Surface sections for '{id}' contain invalid or gapped source chainage.");
			}
		}

		var segments = authoredSegments is { Count: > 0 }
			? authoredSegments : [new RouteSegment(0, routeLengthMeters, 0, name)];
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
		var route = new RouteDefinition(id, name, description, routeLengthMeters, segments, routePoints)
		{
			RouteType = routeType is "loop" ? "Loop" : routeType is "outAndBack" ? "Out-and-Back" : routeType,
			Difficulty = difficulty,
			SourceAttribution = attribution,
			RouteDataVersion = dataVersion,
			SupportsVerifiedCompletionAchievement = string.Equals(routeType, "loop", StringComparison.OrdinalIgnoreCase),
			PublishedTotalAscentMeters = publishedAscentMeters,
			Environment = environment,
			Discipline = discipline,
			SurfaceSegments = surfaces
		};
		if (!string.IsNullOrWhiteSpace(source.GeometryFingerprint)
			&& !string.Equals(route.GeometryFingerprint, source.GeometryFingerprint, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"Route package '{sourceName}' has a geometry fingerprint mismatch.");
		return route;
	}

	private static RouteEnvironment ParseEnvironment(string? value) => value switch
	{
		null or "Road" => RouteEnvironment.Road,
		"Trail" => RouteEnvironment.Trail,
		"Velodrome" => RouteEnvironment.Velodrome,
		"MountainBike" => RouteEnvironment.MountainBike,
		_ => throw new InvalidDataException($"Unknown route environment '{value}'.")
	};

	private static RouteDiscipline ParseDiscipline(string? value) => value switch
	{
		null or "Road" => RouteDiscipline.Road,
		"Gravel" => RouteDiscipline.Gravel,
		"MountainBike" => RouteDiscipline.MountainBike,
		"Track" => RouteDiscipline.Track,
		_ => throw new InvalidDataException($"Unknown route discipline '{value}'.")
	};
}
