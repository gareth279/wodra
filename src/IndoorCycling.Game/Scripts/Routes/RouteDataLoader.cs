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
		double? ActualOsmDistanceMeters, IReadOnlyList<RoutePointData>? Points,
		string? RouteType, string? Difficulty, int? RouteDataVersion, string? GeometryFingerprint,
		string? SourceAttribution, double? TotalAscentMeters, string? Environment, string? Discipline,
		IReadOnlyList<RouteSurfaceSegmentData>? SurfaceSegments, double? ActualRouteDistanceMeters);
	private sealed record RoutePointData(double DistanceMeters, double? Latitude, double? Longitude,
		double? ElevationMeters, double? RawElevationMeters, double? GradientPercent,
		double? LocalXMeters, double? LocalZMeters);
	private sealed record RouteSurfaceSegmentData(double StartMeters, double EndMeters, string Surface,
		long SourceOsmWayId, string? SourceName);

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
		var route = Load(source, id, name, description, segments, resourceName,
			source.RouteType ?? "Loop", source.Difficulty ?? "Unrated",
			source.RouteDataVersion ?? 1, source.SourceAttribution ?? "", source.TotalAscentMeters);
		var featureResource = $"Wodra.GameData.Routes.{id}.trail-features.json";
		if (RoutePackageFiles.Exists(assembly, featureResource, id))
		{
			var course = TrailFeatureDataLoader.Load(assembly, featureResource, id, route.DistanceMeters);
			return route with { TrailFeatures = course.Features, TrailBerms = course.Berms, TrailRollers = course.Rollers };
		}
		return route;
	}

	/// <summary>Loads a reviewed route package installed in the per-user Wodra Routes directory.</summary>
	public static RouteDefinition LoadPackage(string routePointsPath)
	{
		using var stream = File.OpenRead(routePointsPath);
		var source = JsonSerializer.Deserialize<RoutePointsFile>(stream, JsonOptions)
			?? throw new InvalidDataException($"Route package '{routePointsPath}' is empty.");
		if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name))
			throw new InvalidDataException($"Route package '{routePointsPath}' is missing its ID or name.");
		if (source.RouteType is not ("loop" or "outAndBack" or "pointToPoint"))
			throw new InvalidDataException($"Route package '{routePointsPath}' has an unsupported route geometry type.");
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
		var storedRouteLengthMeters = source.ActualRouteDistanceMeters ?? source.ActualOsmDistanceMeters ?? 0;
		if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0
			|| !double.IsFinite(storedRouteLengthMeters)
			|| Math.Abs(storedRouteLengthMeters - routeLengthMeters) > 0.001)
			throw new InvalidDataException($"Route data '{sourceName}' has inconsistent route length metadata.");
		for (var i = 0; i < points.Count; i++)
		{
			var point = points[i];
			var hasGps = point.Latitude is not null && point.Longitude is not null;
			var hasLocal = point.LocalXMeters is not null && point.LocalZMeters is not null;
			if (!double.IsFinite(point.DistanceMeters) || (!hasGps && !hasLocal)
				|| (hasGps && (!double.IsFinite(point.Latitude!.Value) || !double.IsFinite(point.Longitude!.Value)
					|| point.Latitude is < -90 or > 90 || point.Longitude is < -180 or > 180))
				|| (hasLocal && (!double.IsFinite(point.LocalXMeters!.Value) || !double.IsFinite(point.LocalZMeters!.Value)))
				|| (i > 0 && ((points[i - 1].Latitude is not null) != (point.Latitude is not null)
					|| (points[i - 1].LocalXMeters is not null) != (point.LocalXMeters is not null)))
				|| (i > 0 && point.DistanceMeters <= points[i - 1].DistanceMeters))
				throw new InvalidDataException($"Route data '{sourceName}' contains invalid or unordered points.");
		}
		if (points[0].DistanceMeters != 0)
			throw new InvalidDataException($"Route data '{sourceName}' must start at chainage zero.");
		if (points.Any(point => (point.Latitude is not null || point.Longitude is not null)
			&& (point.LocalXMeters is not null || point.LocalZMeters is not null)))
			throw new InvalidDataException($"Route data '{sourceName}' must use either geographic or local coordinates, not both.");
		if ((routeType is "loop" or "outAndBack") && points.All(point => point.Latitude is not null && point.Longitude is not null)
			&& (Math.Abs(points[0].Latitude!.Value - points[^1].Latitude!.Value) > 1e-9
				|| Math.Abs(points[0].Longitude!.Value - points[^1].Longitude!.Value) > 1e-9))
			throw new InvalidDataException($"Route data '{sourceName}' does not close at the lap seam.");
		if ((routeType is "loop" or "outAndBack") && points.All(point => point.LocalXMeters is not null && point.LocalZMeters is not null)
			&& (Math.Abs(points[0].LocalXMeters!.Value - points[^1].LocalXMeters!.Value) > 1e-6
				|| Math.Abs(points[0].LocalZMeters!.Value - points[^1].LocalZMeters!.Value) > 1e-6))
			throw new InvalidDataException($"Local route data '{sourceName}' does not close at the lap seam.");
		if (routeType == "pointToPoint"
			&& points.All(point => point.Latitude is not null && point.Longitude is not null)
			&& Math.Abs(points[0].Latitude!.Value - points[^1].Latitude!.Value) < 1e-9
			&& Math.Abs(points[0].Longitude!.Value - points[^1].Longitude!.Value) < 1e-9)
			throw new InvalidDataException($"Point-to-point route data '{sourceName}' must have distinct natural endpoints.");
		if (routeType == "pointToPoint" && points.All(point => point.LocalXMeters is not null && point.LocalZMeters is not null)
			&& Math.Abs(points[0].LocalXMeters!.Value - points[^1].LocalXMeters!.Value) < 1e-6
			&& Math.Abs(points[0].LocalZMeters!.Value - points[^1].LocalZMeters!.Value) < 1e-6)
			throw new InvalidDataException($"Local point-to-point route data '{sourceName}' must have distinct endpoints.");
		if (points.Any(point => point.ElevationMeters is not { } elevation || !double.IsFinite(elevation)
			|| point.RawElevationMeters is not { } raw || !double.IsFinite(raw)
			|| point.GradientPercent is not { } gradient || !double.IsFinite(gradient)))
			throw new InvalidDataException($"Route data '{sourceName}' has no complete generated elevation and gradient profile.");
		var environment = ParseEnvironment(source.Environment);
		var discipline = ParseDiscipline(source.Discipline);
		var surfaces = (source.SurfaceSegments ?? []).Select(item => new RouteSurfaceSegment(
			item.StartMeters, item.EndMeters, item.Surface, item.SourceOsmWayId, item.SourceName)).ToArray();
		if (environment == RouteEnvironment.Trail
			&& discipline is not (RouteDiscipline.Gravel or RouteDiscipline.MountainBike))
			throw new InvalidDataException($"Trail environment '{id}' must declare its riding discipline separately as Gravel or MountainBike.");
		if (environment == RouteEnvironment.Trail && surfaces.Length == 0)
			throw new InvalidDataException($"Trail route '{id}' must declare route-relative source surface sections.");
		if (environment == RouteEnvironment.MountainBike
			&& discipline != RouteDiscipline.MountainBike)
			throw new InvalidDataException($"Mountain-bike environment '{id}' must declare MountainBike discipline.");
		if (environment == RouteEnvironment.MountainBike && surfaces.Length == 0)
			throw new InvalidDataException($"Mountain-bike route '{id}' must declare route-relative source surface sections.");
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
					|| string.IsNullOrWhiteSpace(surface.Surface)
					|| (surface.SourceOsmWayId <= 0 && string.IsNullOrWhiteSpace(surface.SourceName))
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
			point.Latitude, point.Longitude, point.ElevationMeters!.Value, point.RawElevationMeters,
			point.GradientPercent, point.LocalXMeters, point.LocalZMeters)).ToArray();
		var route = new RouteDefinition(id, name, description, routeLengthMeters, segments, routePoints)
		{
			RouteType = routeType switch
			{
				"loop" => "Loop",
				"outAndBack" => "Out-and-Back",
				"pointToPoint" => "Point-to-Point",
				_ => routeType
			},
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
		"Raceway" => RouteEnvironment.Raceway,
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
