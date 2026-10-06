using System.Numerics;

var tests = new (string Name, Action Run)[]
{
    ("GPS uses first point as origin and east/north/up metre coordinates", () =>
    {
        var route = GpsRoute();
        var geometry = new VisualRoute(route);
        NearVector(Vector3.Zero, geometry.GetPoseAtDistance(0).Position);
        var p = geometry.GetPoseAtDistance(100).Position;
        Near(111.195f, p.X, 0.3f);
        Near(0, p.Z);
        Near(10, p.Y, 0.03f);
        var north = geometry.GetPoseAtDistance(200).Position;
        Near(-111.195f, north.Z, 0.01f);
    }),
    ("GPS interpolation uses normalized centreline arc length", () =>
    {
        var geometry = new VisualRoute(GpsRoute());
        var half = geometry.GetPoseAtDistance(50).Position;
        var full = geometry.GetPoseAtDistance(100).Position;
        if (Vector3.Distance(half, full) <= 0 || !float.IsFinite(half.X + half.Y + half.Z))
            throw new Exception("Arc-length samples were not finite and ordered.");
    }),
    ("Latitude adjusts longitude scale", () =>
    {
        var route = GpsRoute() with
        {
            Points = GpsRoute().Points!.Select(p => p with { Latitude = p.Latitude + 60 }).ToArray()
        };
        Near(55.5975f, new VisualRoute(route).GetPoseAtDistance(100).Position.X, 0.01f);
    }),
    ("Distances wrap over multiple laps and negative offsets", () =>
    {
        var geometry = new VisualRoute(GpsRoute());
        NearVector(geometry.GetPoseAtDistance(50).Position, geometry.GetPoseAtDistance(1250).Position);
        Near(350, (float)geometry.GetPoseAtDistance(-50).LoopDistanceMeters);
        NearVector(geometry.GetPoseAtDistance(350).Position, geometry.GetPoseAtDistance(-50).Position);
        NearVector(Vector3.Zero, geometry.GetPoseAtDistance(400).Position);
    }),
    ("Unchanged authoritative distance produces an unchanged pose", () =>
    {
        var geometry = new VisualRoute(GpsRoute());
        var distance = 123.4;
        var first = geometry.GetPoseAtDistance(distance);
        for (int i = 0; i < 100; i++)
            if (first != geometry.GetPoseAtDistance(distance)) throw new Exception("Pose changed without distance input.");
        if (distance != 123.4) throw new Exception("Input distance changed.");
    }),
    ("GPS-free routes integrate grade and retain segment boundaries", () =>
    {
        var route = new RouteDefinition("generated", "Generated", "", 400,
            [new RouteSegment(0, 155, 5, "Up"), new RouteSegment(155, 400, -2, "Down")]);
        var geometry = new VisualRoute(route);
        if (!float.IsFinite(geometry.GetPoseAtDistance(100).Position.Y)
            || !float.IsFinite(geometry.GetPoseAtDistance(155).Position.Y))
            throw new Exception("Invalid elevation samples.");
        if (geometry.Vertices.Count < 40) throw new Exception("Expected dense visual route samples.");
        NearVector(Vector3.Zero, geometry.Vertices[^1].Position);
    }),
    ("Synthetic Test Route closes its elevation seam without mutating domain data", () =>
    {
        var route = RouteCatalog.GetById("test-route")!;
        var geometry = new VisualRoute(route);
        Near(70, geometry.ClosureCorrection.Y);
        NearVector(Vector3.Zero, geometry.Vertices[^1].Position);
        Near(170, (float)route.Points![^1].ElevationMeters);
        Near(-2, (float)route.GetGradientAt(3000));
        if (!float.IsFinite(geometry.GetPoseAtDistance(2500).Position.Y)) throw new Exception("Invalid mapped elevation.");
    }),
    ("Every catalog route has finite poses and a continuous lap seam", () =>
    {
        foreach (var route in RouteCatalog.Routes)
        {
            var geometry = new VisualRoute(route);
            NearVector(geometry.Vertices[0].Position, geometry.Vertices[^1].Position);
            var before = geometry.GetPoseAtDistance(route.DistanceMeters - 0.01);
            var after = geometry.GetPoseAtDistance(0.01);
            if (Vector3.Distance(before.Position, after.Position) > 0.1f) throw new Exception($"Position seam: {route.Id}");
            if (Vector3.Dot(before.Forward, after.Forward) < 0.99f) throw new Exception($"Direction seam: {route.Id}");
            for (double d = 0; d < route.DistanceMeters; d += 3.7)
            {
                var pose = geometry.GetPoseAtDistance(d);
                if (!float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z)) throw new Exception("Invalid position.");
                Near(1, pose.Forward.Length());
            }
        }
    }),
    ("Dunedin rider poses interpolate the canonical samples through the closure", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var length = route.DistanceMeters;
        Near((float)length, (float)geometry.Samples[^1].DistanceMeters, 0.001f);
        NearVector(geometry.Samples[0].Position, geometry.Samples[^1].Position);
        foreach (var offset in new[] { 100.0, 20.0, 10.0, 5.0, 1.0 })
        {
            var d = length - offset;
            var pose = geometry.GetPoseAtDistance(d);
            Near((float)d, (float)pose.LoopDistanceMeters, 0.001f);
            var a = geometry.Samples[pose.SampleIndexBefore];
            var b = geometry.Samples[pose.SampleIndexAfter];
            var canonicalPosition = Vector3.Lerp(a.Position, b.Position, pose.Interpolation);
            NearVector(canonicalPosition, pose.Position);
            Near((float)d, (float)(a.DistanceMeters + (b.DistanceMeters - a.DistanceMeters) * pose.Interpolation), 0.001f);
            if (!float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z)
                || Math.Abs(pose.Forward.Length() - 1) > 0.001f)
                throw new Exception($"Invalid pose at {d} m.");
        }
        var atLength = geometry.GetPoseAtDistance(length);
        var atZero = geometry.GetPoseAtDistance(0);
        NearVector(atZero.Position, atLength.Position);
        NearVector(geometry.GetPoseAtDistance(1).Position, geometry.GetPoseAtDistance(length + 1).Position);
        NearVector(geometry.GetPoseAtDistance(10).Position, geometry.GetPoseAtDistance(length + 10).Position);
        var before = geometry.GetPoseAtDistance(length - 0.01);
        var after = geometry.GetPoseAtDistance(0.01);
        if (Vector3.Distance(before.Position, after.Position) > 0.1f)
            throw new Exception("Wrapped position jumped at the lap seam.");
        if (Vector3.Dot(before.Forward, after.Forward) < 0.99f)
            throw new Exception("Tangent changed abruptly at the lap seam.");
        Near((float)length, (float)geometry.Vertices[^1].DistanceMeters, 0.001f);
    }),
    ("Dunedin final route tail avoids a straight shortcut and remains continuous", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var length = route.DistanceMeters;
        foreach (var distance in new[] { 5700.0, 5720.0, 5740.0, 5780.0, 5820.0, 5849.0, 5850.0, 5851.0 })
        {
            var pose = geometry.GetPoseAtDistance(distance);
            var before = geometry.Samples[pose.SampleIndexBefore];
            var after = geometry.Samples[pose.SampleIndexAfter];
            NearVector(Vector3.Lerp(before.Position, after.Position, pose.Interpolation), pose.Position);
            if (!float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z))
                throw new Exception($"Non-finite pose at {distance} m.");
            Near(1, pose.Forward.Length());
        }

        var points = route.Points!;
        var endpoint = GpsWorld(points[^1], points[0]);
        var chord = endpoint - GpsWorld(points[^2], points[0]);
        var chordLengthSquared = chord.LengthSquared();
        var greatestDeviation = geometry.Samples
            .Where(s => s.DistanceMeters >= length - 115)
            .Select(s =>
            {
                var fromStart = s.Position - GpsWorld(points[^2], points[0]);
                var t = Math.Clamp(Vector3.Dot(fromStart, chord) / chordLengthSquared, 0, 1);
                return Vector3.Distance(s.Position, GpsWorld(points[^2], points[0]) + chord * t);
            })
            .Max();
        if (greatestDeviation < 15)
            throw new Exception($"Final tail still follows the straight GPS chord (deviation {greatestDeviation:F1} m).");

        NearVector(geometry.GetPoseAtDistance(0).Position, geometry.GetPoseAtDistance(length).Position);
        NearVector(geometry.GetPoseAtDistance(1).Position, geometry.GetPoseAtDistance(length + 1).Position);
        NearVector(geometry.GetPoseAtDistance(10).Position, geometry.GetPoseAtDistance(length + 10).Position);
        var beforeSeam = geometry.GetPoseAtDistance(length - 0.01);
        var afterSeam = geometry.GetPoseAtDistance(0.01);
        if (Vector3.Distance(beforeSeam.Position, afterSeam.Position) > 0.1f)
            throw new Exception("Position discontinuity at the Dunedin lap seam.");
        if (Vector3.Dot(beforeSeam.Forward, afterSeam.Forward) < 0.99f)
            throw new Exception("Tangent discontinuity at the Dunedin lap seam.");
    }),
    ("Zero length route is rejected", () =>
    {
        try { _ = new VisualRoute(GpsRoute() with { DistanceMeters = 0 }); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Accepted zero route length.");
    })
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS: {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL: {test.Name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;

static RouteDefinition GpsRoute() => new("gps", "GPS", "", 400,
    [new RouteSegment(0, 400, 0, "Test")],
    [new RoutePoint(0, 0, 0, 20), new RoutePoint(100, 0, 0.001, 30),
     new RoutePoint(200, 0.001, 0.001, 30), new RoutePoint(300, 0.001, 0, 20),
     new RoutePoint(400, 0, 0, 20)]);

static Vector3 GpsWorld(RoutePoint point, RoutePoint origin)
{
    const double earthRadius = 6371000;
    const double radians = Math.PI / 180;
    return new Vector3(
        (float)((point.Longitude - origin.Longitude) * radians * earthRadius * Math.Cos(origin.Latitude * radians)),
        (float)(point.ElevationMeters - origin.ElevationMeters),
        (float)(-(point.Latitude - origin.Latitude) * radians * earthRadius));
}

static void Near(float expected, float actual, float tolerance = 0.001f)
{
    if (!float.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        throw new Exception($"Expected {expected}, got {actual}.");
}

static void NearVector(Vector3 expected, Vector3 actual)
{
    if (Vector3.Distance(expected, actual) > 0.001f) throw new Exception($"Expected {expected}, got {actual}.");
}
