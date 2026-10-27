using System;
using System.Collections.Generic;

/// <summary>Measured local-coordinate geometry for Wodra's procedural 250 m oval.</summary>
public static class VelodromeRouteGeometry
{
	public const double LapLengthMeters = 250;
	public const double StraightLengthMeters = 45;
	public const double CornerLengthMeters = 80;
	public const int SamplesPerStraight = 180;
	public const int SamplesPerCorner = 320;
	public const double CornerStartMeters = StraightLengthMeters;
	public const double FirstCornerEndMeters = CornerStartMeters + CornerLengthMeters;
	public const double BackStraightEndMeters = FirstCornerEndMeters + StraightLengthMeters;
	public const double BankingTransitionMeters = 8;
	public const double StraightBankDegrees = 13.5;
	public const double CornerBankDegrees = 43.6;
	public static double TurnRadiusMeters => CornerLengthMeters /
		(2 * SamplesPerCorner * Math.Sin(Math.PI / (2 * SamplesPerCorner)));

	public static IReadOnlyList<RoutePoint> CreateCenterline()
	{
		const double halfStraight = StraightLengthMeters / 2;
		const double step = StraightLengthMeters / SamplesPerStraight;
		var radius = TurnRadiusMeters;
		var points = new List<RoutePoint>(2 * SamplesPerStraight + 2 * SamplesPerCorner + 1);

		void Add(double distance, double x, double z) => points.Add(new RoutePoint(
			distance, null, null, 0, 0, 0, x, z));

		// Start on the south straight. The reference line follows two 45 m
		// straights and two 80 m polygonal semicircles. Radius compensates only
		// for the sampled chords, so the authored polyline itself measures 250 m.
		for (var i = 0; i <= SamplesPerStraight; i++)
			Add(i * step, -halfStraight + i * step, -radius);

		for (var i = 1; i <= SamplesPerCorner; i++)
		{
			var angle = -Math.PI / 2 + Math.PI * i / SamplesPerCorner;
			var x = halfStraight + radius * Math.Cos(angle);
			var z = radius * Math.Sin(angle);
			if (i == SamplesPerCorner) { x = halfStraight; z = radius; }
			Add(CornerStartMeters + CornerLengthMeters * i / SamplesPerCorner, x, z);
		}

		for (var i = 1; i <= SamplesPerStraight; i++)
			Add(FirstCornerEndMeters + i * step, halfStraight - i * step, radius);

		for (var i = 1; i <= SamplesPerCorner; i++)
		{
			var angle = Math.PI / 2 + Math.PI * i / SamplesPerCorner;
			var x = -halfStraight + radius * Math.Cos(angle);
			var z = radius * Math.Sin(angle);
			if (i == SamplesPerCorner) { x = -halfStraight; z = -radius; }
			Add(BackStraightEndMeters + CornerLengthMeters * i / SamplesPerCorner, x, z);
		}
		return points;
	}

	public static double BankAngleDegreesAt(double distanceMeters)
	{
		if (!double.IsFinite(distanceMeters)) throw new ArgumentOutOfRangeException(nameof(distanceMeters));
		var distance = ((distanceMeters % LapLengthMeters) + LapLengthMeters) % LapLengthMeters;
		var cornerFactor = Math.Max(
			TransitionFactor(distance, CornerStartMeters, FirstCornerEndMeters),
			TransitionFactor(distance, BackStraightEndMeters, LapLengthMeters));
		return StraightBankDegrees + (CornerBankDegrees - StraightBankDegrees) * cornerFactor;
	}

	public static float BankSlopeAt(double distanceMeters) =>
		(float)Math.Tan(BankAngleDegreesAt(distanceMeters) * Math.PI / 180);

	private static double TransitionFactor(double distance, double start, double end)
	{
		if (distance < start || distance > end) return 0;
		var entry = SmoothStep(Math.Clamp((distance - start) / BankingTransitionMeters, 0, 1));
		var exit = SmoothStep(Math.Clamp((end - distance) / BankingTransitionMeters, 0, 1));
		return Math.Min(entry, exit);
	}

	private static double SmoothStep(double value) => value * value * (3 - 2 * value);
}
