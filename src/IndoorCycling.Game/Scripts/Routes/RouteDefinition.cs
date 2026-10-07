using System.Collections.Generic;
using System.Linq;

public sealed record RouteDefinition(
	string Id,
	string Name,
	string Description,
	double DistanceMeters,
	IReadOnlyList<RouteSegment> Segments,
	IReadOnlyList<RoutePoint>? Points = null
)
{
	public double GetLoopDistance(double rideDistanceMeters)
	{
		if (DistanceMeters <= 0)
			return 0;

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
			Latitude: Lerp(before.Latitude, after.Latitude, ratio),
			Longitude: Lerp(before.Longitude, after.Longitude, ratio),
			ElevationMeters: Lerp(before.ElevationMeters, after.ElevationMeters, ratio),
			RawElevationMeters: before.RawElevationMeters.HasValue && after.RawElevationMeters.HasValue
				? Lerp(before.RawElevationMeters.Value, after.RawElevationMeters.Value, ratio) : null,
			GradientPercent: before.GradientPercent.HasValue && after.GradientPercent.HasValue
				? Lerp(before.GradientPercent.Value, after.GradientPercent.Value, ratio) : null
		);
	}

	private static double Lerp(double a, double b, double t)
	{
		return a + ((b - a) * t);
	}
}
