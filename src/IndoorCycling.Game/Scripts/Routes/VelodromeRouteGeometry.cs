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
	public const double BankingTransitionMeters = 20;
	public const double StraightBankDegrees = 13.5;
	public const double CornerBankDegrees = 43.6;
	private const double SampleStepMeters = .25;
	private const double CurveIntegralMeters = CornerLengthMeters - BankingTransitionMeters;
	public static double TurnRadiusMeters => CurveIntegralMeters / Math.PI;

	public static IReadOnlyList<RoutePoint> CreateCenterline()
	{
		const double halfStraight = StraightLengthMeters / 2;
		var points = new List<RoutePoint>(1001);
		var x = -halfStraight;
		var z = -TurnRadiusMeters;
		var heading = 0d;
		var startX = x;
		var startZ = z;
		points.Add(Point(0, x, z));

		// Each 80 m turn is a symmetric clothoid-like transition: curvature rises
		// and falls with a zero-slope smoothstep over 20 m, with constant curvature
		// through the middle. Integrating by canonical chainage keeps the route at
		// exactly 250 m while removing the straight-to-circular curvature corners.
		for (var index = 1; index <= LapLengthMeters / SampleStepMeters; index++)
		{
			var distance = index * SampleStepMeters;
			var midpoint = distance - SampleStepMeters / 2;
			var curvature = CurvatureAt(midpoint);
			heading += curvature * SampleStepMeters;
			x += Math.Cos(heading - curvature * SampleStepMeters / 2) * SampleStepMeters;
			z += Math.Sin(heading - curvature * SampleStepMeters / 2) * SampleStepMeters;
			if (index == LapLengthMeters / SampleStepMeters)
			{
				if (Math.Sqrt(Math.Pow(x - startX, 2) + Math.Pow(z - startZ, 2)) > .001 || Math.Abs(heading - Math.Tau) > .001)
					throw new InvalidOperationException("Smooth 250 m velodrome integration did not close at its start/finish line.");
				x = startX;
				z = startZ;
			}
			points.Add(Point(distance, x, z));
		}
		return points;
	}

	public static double BankAngleDegreesAt(double distanceMeters)
	{
		if (!double.IsFinite(distanceMeters)) throw new ArgumentOutOfRangeException(nameof(distanceMeters));
		var distance = ((distanceMeters % LapLengthMeters) + LapLengthMeters) % LapLengthMeters;
		var cornerFactor = Math.Max(
			BankFactor(distance, CornerStartMeters, FirstCornerEndMeters),
			BankFactor(distance, BackStraightEndMeters, LapLengthMeters));
		return StraightBankDegrees + (CornerBankDegrees - StraightBankDegrees) * cornerFactor;
	}

	public static float BankSlopeAt(double distanceMeters) =>
		(float)Math.Tan(BankAngleDegreesAt(distanceMeters) * Math.PI / 180);

	private static double CurvatureAt(double distance)
	{
		var factor = Math.Max(
			BankFactor(distance, CornerStartMeters, FirstCornerEndMeters),
			BankFactor(distance, BackStraightEndMeters, LapLengthMeters));
		return factor * Math.PI / CurveIntegralMeters;
	}

	private static double BankFactor(double distance, double start, double end)
	{
		if (distance < start || distance > end) return 0;
		var entry = SmoothStep(Math.Clamp((distance - start) / BankingTransitionMeters, 0, 1));
		var exit = SmoothStep(Math.Clamp((end - distance) / BankingTransitionMeters, 0, 1));
		return Math.Min(entry, exit);
	}

	private static double SmoothStep(double value) => value * value * (3 - 2 * value);

	private static RoutePoint Point(double distance, double x, double z) => new(
		distance, null, null, 0, 0, 0, x, z);
}
