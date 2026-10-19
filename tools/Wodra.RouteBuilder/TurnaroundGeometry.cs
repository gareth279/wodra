namespace Wodra.RouteBuilder;

public sealed record GeneratedTurnaround(double OriginalDistanceMeters, double NewDistanceMeters,
    double WidthMeters, double RadiusMeters, double MergeLengthMeters, double AddedDistanceMeters,
    string WidthSource);

public sealed record TurnaroundGeometryResult(IReadOnlyList<GeoPoint> Points,
    IReadOnlyList<GeneratedTurnaround> Turnarounds);

/// <summary>Replaces exact short road retraces with a continuous U-turn inside the authored road corridor.</summary>
public static class TurnaroundGeometry
{
    private const double EarthRadiusMeters = 6_371_000;
    private const double ReverseMatchToleranceMeters = 1.5;
    private const double MinimumReverseRunMeters = 8;
    private const double MaximumBlendStepMeters = 4;

    public static TurnaroundGeometryResult Repair(IReadOnlyList<GeoPoint> source,
        double roadWidthMeters, string widthSource)
    {
        if (!double.IsFinite(roadWidthMeters) || roadWidthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(roadWidthMeters));
        if (string.IsNullOrWhiteSpace(widthSource))
            throw new ArgumentException("Road-width provenance is required.", nameof(widthSource));
        if (source.Count < 4) return new TurnaroundGeometryResult(source.ToArray(), []);

        var originalChainage = Cumulative(source);
        var output = new List<GeoPoint>(source.Count + 32) { source[0] };
        var turnarounds = new List<GeneratedTurnaround>();
        var index = 1;
        for (; index < source.Count - 1;)
        {
            if (!TryGetReversal(source, index, roadWidthMeters, out var reverseRunMeters,
                    out var tangent, out var lateral, out var radius, out var mergeLength))
            {
                AppendDistinct(output, source[index]);
                index++;
                continue;
            }

            var apex = source[index];
            AppendDistinct(output, apex);
            var turnChainage = Length(output);
            const int arcSteps = 16;
            for (var step = 1; step <= arcSteps; step++)
            {
                var angle = Math.PI * step / arcSteps;
                var east = radius * lateral.East * (1 - Math.Cos(angle))
                    + radius * tangent.East * Math.Sin(angle);
                var north = radius * lateral.North * (1 - Math.Cos(angle))
                    + radius * tangent.North * Math.Sin(angle);
                AppendDistinct(output, Offset(apex, east, north));
            }

            var returnPrefix = BuildOffsetReturn(source, index, lateral,
                radius * 2, mergeLength, out var resumeIndex);
            var oldPrefixLength = mergeLength;
            var arcEnd = Offset(apex, lateral.East * radius * 2, lateral.North * radius * 2);
            var newPrefixLength = returnPrefix.Count == 0 ? 0
                : Distance(arcEnd, returnPrefix[0]) + Length(returnPrefix);
            foreach (var point in returnPrefix) AppendDistinct(output, point);
            turnarounds.Add(new GeneratedTurnaround(originalChainage[index], turnChainage,
                roadWidthMeters, radius, mergeLength, Math.PI * radius + newPrefixLength - oldPrefixLength,
                widthSource));
            index = Math.Max(index + 1, resumeIndex + 1);
        }
        for (; index < source.Count; index++) AppendDistinct(output, source[index]);
        return new TurnaroundGeometryResult(output, turnarounds);
    }

    private static bool TryGetReversal(IReadOnlyList<GeoPoint> points, int index, double width,
        out double reverseRun, out (double East, double North) tangent,
        out (double East, double North) lateral, out double radius, out double mergeLength)
    {
        reverseRun = radius = mergeLength = 0;
        tangent = lateral = default;
        var incoming = LocalVector(points[index - 1], points[index]);
        var outgoing = LocalVector(points[index], points[index + 1]);
        var incomingLength = Length(incoming);
        var outgoingLength = Length(outgoing);
        if (incomingLength < 1 || outgoingLength < 1) return false;
        tangent = (incoming.East / incomingLength, incoming.North / incomingLength);
        var departure = (outgoing.East / outgoingLength, outgoing.North / outgoingLength);
        if (tangent.East * departure.Item1 + tangent.North * departure.Item2 > -0.985
            || Distance(points[index - 1], points[index + 1]) > ReverseMatchToleranceMeters)
            return false;

        lateral = (-tangent.North, tangent.East);
        for (var step = 1; index + step < points.Count && index - step >= 0; step++)
        {
            if (Distance(points[index + step], points[index - step]) > ReverseMatchToleranceMeters) break;
            reverseRun += Distance(points[index + step - 1], points[index + step]);
        }
        if (reverseRun < MinimumReverseRunMeters) return false;
        radius = Math.Min(width * 0.22, reverseRun * 0.1);
        mergeLength = Math.Min(width * 2, reverseRun * 0.5);
        return radius >= 0.5 && mergeLength >= 4;
    }

    private static IReadOnlyList<GeoPoint> BuildOffsetReturn(IReadOnlyList<GeoPoint> source, int apexIndex,
        (double East, double North) lateral, double offsetMeters, double blendLengthMeters, out int resumeIndex)
    {
        var result = new List<GeoPoint>();
        var walked = 0d;
        resumeIndex = apexIndex;
        for (var segment = apexIndex + 1; segment < source.Count && walked < blendLengthMeters; segment++)
        {
            var a = source[segment - 1];
            var b = source[segment];
            var length = Distance(a, b);
            if (length <= 0.001) continue;
            var take = Math.Min(length, blendLengthMeters - walked);
            var steps = Math.Max(1, (int)Math.Ceiling(take / MaximumBlendStepMeters));
            for (var step = 1; step <= steps; step++)
            {
                var local = take * step / steps;
                var sourcePoint = Interpolate(a, b, local / length);
                var along = walked + local;
                var t = Math.Clamp(along / blendLengthMeters, 0, 1);
                var eased = t * t * (3 - 2 * t);
                var lateralOffset = offsetMeters * (1 - eased);
                result.Add(Offset(sourcePoint, lateral.East * lateralOffset,
                    lateral.North * lateralOffset));
            }
            walked += take;
            if (take >= length - 0.001) resumeIndex = segment;
            else
            {
                // The last generated return point is an interpolated location inside this source edge.
                resumeIndex = segment - 1;
                break;
            }
        }
        return result;
    }

    private static GeoPoint Offset(GeoPoint origin, double eastMeters, double northMeters)
    {
        var latitudeRadians = origin.Latitude * Math.PI / 180;
        var latitude = origin.Latitude + northMeters / EarthRadiusMeters * 180 / Math.PI;
        var longitudeScale = EarthRadiusMeters * Math.Cos(latitudeRadians);
        var longitude = Math.Abs(longitudeScale) < 1 ? origin.Longitude
            : origin.Longitude + eastMeters / longitudeScale * 180 / Math.PI;
        return new GeoPoint(latitude, longitude);
    }

    private static (double East, double North) LocalVector(GeoPoint from, GeoPoint to)
    {
        var latitudeRadians = (from.Latitude + to.Latitude) * 0.5 * Math.PI / 180;
        var east = (to.Longitude - from.Longitude) * Math.PI / 180
            * EarthRadiusMeters * Math.Cos(latitudeRadians);
        var north = (to.Latitude - from.Latitude) * Math.PI / 180 * EarthRadiusMeters;
        return (east, north);
    }

    private static GeoPoint Interpolate(GeoPoint a, GeoPoint b, double fraction) => new(
        a.Latitude + (b.Latitude - a.Latitude) * fraction,
        a.Longitude + (b.Longitude - a.Longitude) * fraction);

    private static double Distance(GeoPoint a, GeoPoint b) => Length(LocalVector(a, b));
    private static double Length((double East, double North) vector) => Math.Sqrt(vector.East * vector.East + vector.North * vector.North);
    private static double Length(IReadOnlyList<GeoPoint> points)
    {
        var total = 0d;
        for (var i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        return total;
    }
    private static double[] Cumulative(IReadOnlyList<GeoPoint> points)
    {
        var result = new double[points.Count];
        for (var i = 1; i < points.Count; i++) result[i] = result[i - 1] + Distance(points[i - 1], points[i]);
        return result;
    }
    private static void AppendDistinct(List<GeoPoint> points, GeoPoint point)
    {
        if (points.Count == 0 || Distance(points[^1], point) > 0.001) points.Add(point);
    }
}
