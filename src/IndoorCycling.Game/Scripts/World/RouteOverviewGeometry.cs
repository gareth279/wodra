using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

/// <summary>Cached route overview projection. Uses the same VisualRoute pose as the rider.</summary>
public sealed class RouteOverviewGeometry
{
	private const float Padding = 12f;
	private readonly VisualRoute _visualRoute;
	private readonly Vector2[] _mapWorldPoints;
	private readonly (double Distance, double Elevation)[] _profile;
	private readonly float _minX, _minY;
	private readonly float _spanX, _spanY;
	private readonly double _minElevation, _maxElevation;

	public double LengthMeters => _visualRoute.LengthMeters;
	public double MinimumElevationMeters => _minElevation;
	public double MaximumElevationMeters => _maxElevation;
	public IReadOnlyList<(double Distance, double Elevation)> Profile => _profile;

	/// <summary>Projects the visual world's +X east, -Z north axes into north-up screen coordinates.</summary>
	public static Vector2 ProjectNorthUp(Vector3 worldPosition) => new(worldPosition.X, worldPosition.Z);

	public RouteOverviewGeometry(RouteDefinition route, VisualRoute visualRoute)
	{
		_visualRoute = visualRoute ?? throw new ArgumentNullException(nameof(visualRoute));
		if (route.Points is not { Count: >= 2 } points)
			throw new ArgumentException("Route overview requires the route's sampled geographic elevation points.", nameof(route));
		_mapWorldPoints = visualRoute.Samples.Select(sample => ProjectNorthUp(sample.Position)).ToArray();
		_minX = _mapWorldPoints.Min(p => p.X);
		_minY = _mapWorldPoints.Min(p => p.Y);
		_spanX = Math.Max(.001f, _mapWorldPoints.Max(p => p.X) - _minX);
		_spanY = Math.Max(.001f, _mapWorldPoints.Max(p => p.Y) - _minY);
		_profile = points.OrderBy(p => p.DistanceMeters)
			.Select(p => (p.DistanceMeters, p.ElevationMeters)).ToArray();
		_minElevation = _profile.Min(p => p.Elevation);
		_maxElevation = _profile.Max(p => p.Elevation);
	}

	public Vector2[] BuildMapPolyline(Vector2 size)
	{
		var scale = Math.Min(Math.Max(1, size.X - Padding * 2) / _spanX,
			Math.Max(1, size.Y - Padding * 2) / _spanY);
		var spanX = _spanX * scale;
		var spanY = _spanY * scale;
		var origin = new Vector2((size.X - spanX) / 2f, (size.Y - spanY) / 2f);
		return _mapWorldPoints.Select(p => origin + new Vector2((p.X - _minX) * scale, (p.Y - _minY) * scale)).ToArray();
	}

	public (Vector2 Position, Vector2 Direction, double LapDistanceMeters, int Lap) GetMapPose(double rideDistanceMeters, Vector2 size)
	{
		var lap = (int)Math.Floor(Math.Max(0, rideDistanceMeters) / LengthMeters) + 1;
		var distance = ((rideDistanceMeters % LengthMeters) + LengthMeters) % LengthMeters;
		var pose = _visualRoute.GetPoseAtDistance(distance);
		var scale = Math.Min(Math.Max(1, size.X - Padding * 2) / _spanX,
			Math.Max(1, size.Y - Padding * 2) / _spanY);
		var spanX = _spanX * scale;
		var spanY = _spanY * scale;
		var origin = new Vector2((size.X - spanX) / 2f, (size.Y - spanY) / 2f);
		var mapPosition = ProjectNorthUp(pose.Position);
		var direction = Vector2.Normalize(ProjectNorthUp(pose.Forward));
		if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y)) direction = Vector2.UnitY;
		return (origin + new Vector2((mapPosition.X - _minX) * scale, (mapPosition.Y - _minY) * scale), direction, distance, lap);
	}

	public Vector2[] BuildProfilePolyline(Vector2 origin, Vector2 size)
	{
		var elevationSpan = Math.Max(1, _maxElevation - _minElevation);
		var distanceSpan = LengthMeters;
		return _profile.Select(p => new Vector2(
			origin.X + (float)(Math.Clamp(p.Distance, 0, distanceSpan) / distanceSpan * size.X),
			origin.Y + size.Y - (float)((p.Elevation - _minElevation) / elevationSpan * size.Y))).ToArray();
	}

	public Vector2 GetProfilePosition(double rideDistanceMeters, Vector2 origin, Vector2 size)
	{
		var sample = _profile;
		var distance = ((rideDistanceMeters % LengthMeters) + LengthMeters) % LengthMeters;
		var low = 0;
		var high = sample.Length - 1;
		while (high - low > 1)
		{
			var middle = (low + high) / 2;
			if (sample[middle].Distance <= distance) low = middle;
			else high = middle;
		}
		var beforeIndex = low;
		var afterIndex = high;
		var before = sample[beforeIndex]; var after = sample[afterIndex];
		var t = after.Distance > before.Distance ? (distance - before.Distance) / (after.Distance - before.Distance) : 0;
		var elevation = before.Elevation + (after.Elevation - before.Elevation) * t;
		var elevationSpan = Math.Max(1, _maxElevation - _minElevation);
		return new Vector2(origin.X + (float)(distance / LengthMeters * size.X),
			origin.Y + size.Y - (float)((elevation - _minElevation) / elevationSpan * size.Y));
	}
}
