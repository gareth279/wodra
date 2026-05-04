public sealed record RouteSample(
	double LoopDistanceMeters,
	double GradientPercent,
	double? Latitude,
	double? Longitude,
	double? ElevationMeters,
	string SegmentLabel
);