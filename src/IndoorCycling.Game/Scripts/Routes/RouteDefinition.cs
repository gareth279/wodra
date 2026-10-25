using System.Collections.Generic;
using System.Linq;
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IndoorCycling.Core.Simulation;

public sealed record RouteDefinition(
	string Id,
	string Name,
	string Description,
	double DistanceMeters,
	IReadOnlyList<RouteSegment> Segments,
	IReadOnlyList<RoutePoint>? Points = null
)
{
	/// <summary>Route environment is independent of the activity mode selected for a ride.</summary>
	public RouteEnvironment Environment { get; init; } = RouteEnvironment.Road;
	/// <summary>Riding discipline is independent of environment and per-section surface.</summary>
	public RouteDiscipline Discipline { get; init; } = RouteDiscipline.Road;
	/// <summary>Source-authored route-relative surface classifications; empty retains legacy route behavior.</summary>
	public IReadOnlyList<RouteSurfaceSegment> SurfaceSegments { get; init; } = [];
	/// <summary>Authored, route-relative optional trail line choices. These affect presentation and challenge scoring only.</summary>
	public IReadOnlyList<TrailFeatureDefinition> TrailFeatures { get; init; } = [];
	public IReadOnlyList<TrailBermDefinition> TrailBerms { get; init; } = [];
	public IReadOnlyList<TrailRollerDefinition> TrailRollers { get; init; } = [];
	/// <summary>Modes explicitly supported by this environment. An empty value preserves legacy route behavior.</summary>
	public IReadOnlyList<RideActivityMode> SupportedModes { get; init; } = [];
	/// <summary>Optional feature support. Legacy routes retain the existing road feature set.</summary>
	public RouteCapabilities Capabilities { get; init; } = RouteCapabilities.LegacyRoad;
	/// <summary>True only when the route has a continuous reference loop and may award a verified full-route badge.</summary>
	public bool SupportsVerifiedCompletionAchievement { get; init; }
	/// <summary>Route geometry type shown in the route catalogue.</summary>
	public string RouteType { get; init; } = "Loop";
	public string Difficulty { get; init; } = "Unrated";
	public string SourceAttribution { get; init; } = "";
	public int RouteDataVersion { get; init; } = 1;
	public double? PublishedTotalAscentMeters { get; init; }
	public double TotalAscentMeters => PublishedTotalAscentMeters ?? (Points is not { Count: >= 2 } points ? 0
		: points.Zip(points.Skip(1), (a, b) => Math.Max(0, b.ElevationMeters - a.ElevationMeters)).Sum());
	/// <summary>Stable fingerprint of the ordered route geometry, independent of session state.</summary>
	public string GeometryFingerprint
	{
		get
		{
			if (Points is not { Count: >= 2 } points) return "";
			var canonical = string.Join("\n", points.Select(p => string.Join(",",
				p.DistanceMeters.ToString("F3", CultureInfo.InvariantCulture),
				p.LocalXMeters is { } x && p.LocalZMeters is { } z
					? $"local:{x.ToString("F3", CultureInfo.InvariantCulture)},{z.ToString("F3", CultureInfo.InvariantCulture)}"
					: $"{p.Latitude?.ToString("F7", CultureInfo.InvariantCulture)},{p.Longitude?.ToString("F7", CultureInfo.InvariantCulture)}",
				p.ElevationMeters.ToString("F3", CultureInfo.InvariantCulture))));
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
		}
	}

	public double GetLoopDistance(double rideDistanceMeters)
	{
		if (DistanceMeters <= 0)
			return 0;
		if (RouteType == "Point-to-Point")
			return Math.Clamp(rideDistanceMeters, 0, DistanceMeters);

		return rideDistanceMeters % DistanceMeters;
	}

	public double GetGradientAt(double rideDistanceMeters)
	{
		if (Points is { Count: >= 2 } points && points[0].GradientPercent.HasValue)
			return InterpolatePoint(GetLoopDistance(rideDistanceMeters))?.GradientPercent ?? 0;
		if (DistanceMeters <= 0 || Segments.Count == 0)
			return 0;

		var d = GetLoopDistance(rideDistanceMeters);

		var segment = Segments.FirstOrDefault(s =>
			d >= s.StartMeters && d < s.EndMeters
		);

		return segment?.GradientPercent ?? 0;
	}

	public RouteSegment? GetSegmentAt(double rideDistanceMeters)
	{
		if (DistanceMeters <= 0 || Segments.Count == 0)
			return null;

		var d = GetLoopDistance(rideDistanceMeters);

		return Segments.FirstOrDefault(s =>
			d >= s.StartMeters && d < s.EndMeters
		);
	}

	public RouteSample GetSampleAt(double rideDistanceMeters)
	{
		var loopDistance = GetLoopDistance(rideDistanceMeters);
		var gradient = GetGradientAt(rideDistanceMeters);
		var segment = GetSegmentAt(rideDistanceMeters);

		var routePoint = InterpolatePoint(loopDistance);

		return new RouteSample(
			LoopDistanceMeters: loopDistance,
			GradientPercent: gradient,
			Latitude: routePoint?.Latitude,
			Longitude: routePoint?.Longitude,
			ElevationMeters: routePoint?.ElevationMeters,
			SegmentLabel: segment?.Label ?? "-",
			RawElevationMeters: routePoint?.RawElevationMeters
		);
	}

	private RoutePoint? InterpolatePoint(double loopDistanceMeters)
	{
		if (Points is null || Points.Count == 0)
			return null;

		if (Points.Count == 1)
			return Points[0];

		var orderedPoints = Points.OrderBy(p => p.DistanceMeters).ToList();

		var before = orderedPoints.LastOrDefault(p => p.DistanceMeters <= loopDistanceMeters);
		var after = orderedPoints.FirstOrDefault(p => p.DistanceMeters >= loopDistanceMeters);

		if (before is null)
			return orderedPoints[0];

		if (after is null)
			return orderedPoints[^1];

		if (before.DistanceMeters == after.DistanceMeters)
			return before;

		var span = after.DistanceMeters - before.DistanceMeters;
		var ratio = (loopDistanceMeters - before.DistanceMeters) / span;

		return new RoutePoint(
			DistanceMeters: loopDistanceMeters,
			Latitude: LerpNullable(before.Latitude, after.Latitude, ratio),
			Longitude: LerpNullable(before.Longitude, after.Longitude, ratio),
			ElevationMeters: Lerp(before.ElevationMeters, after.ElevationMeters, ratio),
			RawElevationMeters: before.RawElevationMeters.HasValue && after.RawElevationMeters.HasValue
				? Lerp(before.RawElevationMeters.Value, after.RawElevationMeters.Value, ratio) : null,
			GradientPercent: before.GradientPercent.HasValue && after.GradientPercent.HasValue
				? Lerp(before.GradientPercent.Value, after.GradientPercent.Value, ratio) : null,
			LocalXMeters: before.LocalXMeters.HasValue && after.LocalXMeters.HasValue
				? Lerp(before.LocalXMeters.Value, after.LocalXMeters.Value, ratio) : null,
			LocalZMeters: before.LocalZMeters.HasValue && after.LocalZMeters.HasValue
				? Lerp(before.LocalZMeters.Value, after.LocalZMeters.Value, ratio) : null
		);
	}

	private static double Lerp(double a, double b, double t)
	{
		return a + ((b - a) * t);
	}

	private static double? LerpNullable(double? a, double? b, double t) => a.HasValue && b.HasValue
		? Lerp(a.Value, b.Value, t) : null;
}

public enum RouteEnvironment { Road, Trail, Velodrome, MountainBike, TrainingStudio }
public enum RouteDiscipline { Road, Gravel, MountainBike, Track, IndoorTraining }
public sealed record RouteSurfaceSegment(double StartMeters, double EndMeters, string Surface, long SourceOsmWayId,
	string? SourceName = null);

/// <summary>Explicit ambient-world policy, independent of local/AI/remote cyclist capabilities.</summary>
public sealed record RouteEnvironmentPolicy(bool SupportsAmbientCars, bool SupportsAmbientPedestrians,
	bool SupportsUrbanStreetScenery)
{
	public bool SupportsAmbientActors => SupportsAmbientCars || SupportsAmbientPedestrians;
	public static RouteEnvironmentPolicy For(RouteEnvironment environment) => environment switch
	{
		RouteEnvironment.Road => new(true, true, true),
		RouteEnvironment.Trail or RouteEnvironment.MountainBike or RouteEnvironment.Velodrome or RouteEnvironment.TrainingStudio => new(false, false, false),
		_ => throw new ArgumentOutOfRangeException(nameof(environment))
	};
}
public enum RideActivityMode { FreeRide, LapTiming, Flying200, Standing500, Standing1000 }
public sealed record RouteCapabilities(bool SupportsPacer, bool SupportsGhost, bool SupportsCollectibles,
	bool SupportsTraffic, bool SupportsMultiplayer)
{
	/// <summary>Optional maximum lateral steering extent for a narrow environment, in metres.</summary>
	public float? MaximumLateralOffsetMeters { get; init; }
	/// <summary>Default rider line when no steering input is active. Null retains the legacy route line.</summary>
	public float? DefaultLateralOffsetMeters { get; init; }
	/// <summary>Route-content-driven MTB line choices and visual-only jump presentation.</summary>
	public bool SupportsTrailFeatures { get; init; }
	public static RouteCapabilities LegacyRoad { get; } = new(true, true, true, true, true);
	public static RouteCapabilities SoloVelodrome { get; } = new(false, false, false, false, false);
	public static RouteCapabilities SoloTrail { get; } = new(false, true, true, false, false);
	public static RouteCapabilities SoloMountainBike { get; } = new(false, false, false, false, false)
	{ MaximumLateralOffsetMeters = 0.28f };
}
