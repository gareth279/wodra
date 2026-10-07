public sealed record RoutePoint(
	double DistanceMeters,
	double Latitude,
	double Longitude,
	double ElevationMeters,
	double? RawElevationMeters = null,
	double? GradientPercent = null
);
