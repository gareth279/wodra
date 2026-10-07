using System.Numerics;

var tests = new (string Name, Action Run)[]
{
    ("60 rpm advances one crank revolution per second", () =>
    {
        Near((float)Math.Tau,(float)CyclistAnimationMath.CrankAngularSpeedRadiansPerSecond(60));
        Near((float)Math.PI,(float)CyclistAnimationMath.AdvanceCrankAngle(0,60,.5));
    }),
    ("left and right pedals remain half a revolution apart", () =>
    {
        for(var i=0;i<360;i++)
        {
            var angle=i*Math.PI/180;
            var left=CyclistAnimationMath.PedalOffset(angle,.17);
            var right=CyclistAnimationMath.PedalOffset(angle+Math.PI,.17);
            if(Vector2.Distance(left,-right)>.00001f) throw new Exception("Pedal phases are not opposed.");
        }
    }),
    ("zero cadence freezes crank and pause freezes crank and wheel", () =>
    {
        Near(1.2f,(float)CyclistAnimationMath.AdvanceCrankAngle(1.2,0,1));
        Near(1.2f,(float)CyclistAnimationMath.AdvanceCrankAngle(1.2,90,1,true));
        Near(2.4f,(float)CyclistAnimationMath.AdvanceWheelAngle(2.4,0,1,.35));
        Near(2.4f,(float)CyclistAnimationMath.AdvanceWheelAngle(2.4,30,1,.35,true));
    }),
    ("virtual speed produces wheel angular speed from wheel radius", () =>
    {
        Near((float)(10/.35),(float)CyclistAnimationMath.WheelAngularSpeedRadiansPerSecond(36,.35));
        Near(0,(float)CyclistAnimationMath.WheelAngularSpeedRadiansPerSecond(0,.35));
    }),
    ("leg solver remains finite through a complete crank revolution", () =>
    {
        var hip=new Vector2(.2f,1.0f);
        for(var i=0;i<360;i++)
        {
            var angle=i*Math.PI/180;
            var offset=CyclistAnimationMath.PedalOffset(angle,.17);
            var knee=CyclistAnimationMath.SolveKnee(hip,new Vector2(.1f+offset.X,.39f+offset.Y),.43,.43,1);
            if(!float.IsFinite(knee.X+knee.Y)) throw new Exception($"Invalid knee at {i} degrees.");
        }
    }),
    ("corner lean remains within ten degree visual clamp", () =>
    {
        var lean=CyclistAnimationMath.CornerLeanDegrees(-Vector3.UnitZ,Vector3.UnitX,.001,70);
        if(Math.Abs(lean)>CyclistAnimationMath.MaximumLeanDegrees) throw new Exception($"Lean exceeded clamp: {lean}.");
        Near(0,(float)CyclistAnimationMath.CornerLeanDegrees(-Vector3.UnitZ,Vector3.UnitX,0,30));
    }),
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
    ("landmark WGS84 conversion uses the route's east-up-south local frame", () =>
    {
        var geometry = new VisualRoute(GpsRoute());
        var landmark = geometry.GetPositionForCoordinate(0.001, 0.001, 25);
        Near(111.195f, landmark.X, 0.02f);
        Near(5, landmark.Y, 0.001f);
        Near(-111.195f, landmark.Z, 0.02f);
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
    ("Dunedin loads the generated OSM centreline and exact route length", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var points = route.Points!;
        if (points.Count <= 16) throw new Exception($"Expected dense generated OSM points, found {points.Count}.");
        Near(5411.183741640602f, (float)route.DistanceMeters, 0.001f);
        Near((float)points[^1].DistanceMeters, (float)route.DistanceMeters, 0.0001f);
        if (route.DistanceMeters == 5850) throw new Exception("Legacy route distance remains authoritative.");
        for (var i = 1; i < points.Count; i++)
            if (points[i].DistanceMeters <= points[i - 1].DistanceMeters)
                throw new Exception($"Generated route distances are not increasing at point {i}.");
        if (points[^1].Latitude != points[0].Latitude || points[^1].Longitude != points[0].Longitude)
            throw new Exception("Generated route endpoints do not close.");
    }),
    ("Dunedin landmark bundle contains named nearby orientation features", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var landmarks = RouteLandmarkDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.landmarks.json", route.Id);
        if (landmarks.Count is < 10 or > 20) throw new Exception($"Unexpected landmark count {landmarks.Count}.");
        if (landmarks.Max(item => item.DistanceFromRouteMeters) > 150)
            throw new Exception("Landmark bundle contains a feature outside the search radius.");
        if (landmarks.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.OsmId <= 0))
            throw new Exception("Landmark bundle contains an unnamed/invalid feature.");
        if (landmarks.Any(item => item.Name == "Physio Pool"))
            throw new Exception("A non-distinctive hospital building was included as a landmark.");
        if (!landmarks.Any(item => item.Category == "railway/station"))
            throw new Exception("Expected a distinct Dunedin railway station landmark.");
    }),
    ("bundled building elevations and polygon metadata load correctly", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var buildings = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id);
        if (buildings.Count < 100 || buildings.Any(item => item.BaseElevationMeters is null || item.Footprint.Count < 4))
            throw new Exception($"Building bundle is incomplete ({buildings.Count} buildings).");
        if (buildings.Any(item => !double.IsFinite(item.BaseElevationMeters!.Value)))
            throw new Exception("Building base elevation is not finite.");
    }),
    ("building coordinates use exactly the VisualRoute geographic local frame", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var building = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id)[0];
        var point = building.Footprint[0];
        var converted = geometry.GetPositionForCoordinate(point.Latitude, point.Longitude, building.BaseElevationMeters!.Value);
        var origin = route.Points![0]; const double radians = Math.PI / 180; const double earth = 6371000;
        var expected = new Vector3((float)((point.Longitude-origin.Longitude)*radians*earth*Math.Cos(origin.Latitude*radians)),
            (float)(building.BaseElevationMeters.Value-origin.ElevationMeters),
            (float)(-(point.Latitude-origin.Latitude)*radians*earth));
        NearVector(expected, converted);
    }),
    ("building extrusion creates wall and flat roof triangles at configured height", () =>
    {
        var ring = new[] { new Vector3(0,0,0), new Vector3(4,0,0), new Vector3(4,0,3),
            new Vector3(0,0,3), new Vector3(0,0,0) };
        var mesh = BuildingMeshGeometry.Extrude(ring, 5);
        if (mesh.Length / 3 != 10) throw new Exception($"Expected 10 triangles, got {mesh.Length / 3}.");
        Near(0, mesh.Min(point => point.Y)); Near(5, mesh.Max(point => point.Y));
    }),
    ("bundled vegetation elevations and feature metadata load correctly", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var vegetation = RouteVegetationDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.vegetation.json", route.Id);
        if (vegetation.Areas.Count != 22 || vegetation.Trees.Count != 171)
            throw new Exception($"Unexpected vegetation bundle sizes: {vegetation.Areas.Count} areas, {vegetation.Trees.Count} trees.");
        if (vegetation.Areas.Any(area => area.BaseElevationMeters is null || !double.IsFinite(area.BaseElevationMeters.Value)
                || area.Polygon.Length < 4 || area.Polygon.Any(point => point.Length != 2)))
            throw new Exception("Vegetation area elevation or polygon metadata is incomplete.");
        if (vegetation.Trees.Any(tree => tree.ElevationMeters is null || !double.IsFinite(tree.ElevationMeters.Value)
                || tree.HeightMeters is < 4 or > 12))
            throw new Exception("Vegetation tree elevation or size metadata is incomplete.");
        if (!vegetation.Areas.Any(area => area.Name == "Market Reserve")
            || !vegetation.Areas.Any(area => area.Name == "Union Lawn"))
            throw new Exception("Expected current named Dunedin green areas in the bundle.");
    }),
    ("bundled street furniture elevations and route metadata load correctly", () =>
    {
        var route=RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var furniture=RouteStreetFurnitureDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.street-furniture.json",route.Id);
        if(furniture.Items.Count < 100 || furniture.Items.Any(item=>item.ElevationMeters is null || !double.IsFinite(item.ElevationMeters.Value)))
            throw new Exception($"Street furniture bundle has incomplete LINZ elevations ({furniture.Items.Count} items).");
        if(furniture.Items.Any(item=>item.DistanceFromRouteMeters < 0 || item.NearestRouteDistanceMeters < 0))
            throw new Exception("Street-furniture route relationship metadata is invalid.");
        if(!furniture.Items.Any(item=>item.Category=="traffic-signal") || !furniture.Items.Any(item=>item.Category=="crossing"))
            throw new Exception("Expected explicit mapped traffic signals and crossings in the corridor.");
    }),
    ("street-furniture coordinates use the VisualRoute geographic mapping", () =>
    {
        var route=RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry=new VisualRoute(route);
        var item=RouteStreetFurnitureDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.street-furniture.json",route.Id).Items[0];
        var converted=geometry.GetPositionForCoordinate(item.Latitude,item.Longitude,item.ElevationMeters!.Value);
        var origin=route.Points![0]; const double radians=Math.PI/180; const double earth=6371000;
        var expected=new Vector3((float)((item.Longitude-origin.Longitude)*radians*earth*Math.Cos(origin.Latitude*radians)),
            (float)(item.ElevationMeters.Value-origin.ElevationMeters),
            (float)(-(item.Latitude-origin.Latitude)*radians*earth));
        NearVector(expected,converted);
    }),
    ("vegetation coordinates use VisualRoute mapping and area surfaces triangulate", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var vegetation = RouteVegetationDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.vegetation.json", route.Id);
        var tree = vegetation.Trees[0];
        var localTree = geometry.GetPositionForCoordinate(tree.Latitude, tree.Longitude, tree.ElevationMeters!.Value);
        var origin = route.Points![0]; const double radians = Math.PI / 180; const double earth = 6371000;
        var expected = new Vector3((float)((tree.Longitude-origin.Longitude)*radians*earth*Math.Cos(origin.Latitude*radians)),
            (float)(tree.ElevationMeters.Value-origin.ElevationMeters),
            (float)(-(tree.Latitude-origin.Latitude)*radians*earth));
        NearVector(expected, localTree);
        var triangleCount = 0;
        foreach (var area in vegetation.Areas.Where(item => item.RenderGround))
        {
            var ring = area.Polygon.Select(point => geometry.GetPositionForCoordinate(point[1], point[0], area.BaseElevationMeters!.Value)).ToArray();
            var triangles = BuildingMeshGeometry.TriangulateSurface(ring);
            if (triangles.Length == 0 || triangles.Length % 3 != 0)
                throw new Exception($"OSM {area.OsmType} {area.OsmId} did not create a valid surface mesh.");
            triangleCount += triangles.Length / 3;
        }
        if (triangleCount != 188) throw new Exception($"Expected 188 vegetation triangles, got {triangleCount}.");
    }),
    ("all bundled OSM footprints can be batched into valid extrusion meshes", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var buildings = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id);
        foreach (var building in buildings)
        {
            var ring = building.Footprint.Select(point =>
            {
                var local = geometry.GetPositionForCoordinate(point.Latitude, point.Longitude, building.BaseElevationMeters!.Value);
                return local;
            }).ToArray();
            var mesh = BuildingMeshGeometry.Extrude(ring, (float)building.HeightMeters);
            if (mesh.Length == 0 || mesh.Length % 3 != 0) throw new Exception($"OSM {building.OsmType} {building.OsmId} made an invalid mesh.");
        }
    }),
    ("Dunedin uses generated LINZ elevations and one shared route gradient", () =>
    {
        const double oldLength = 5850;
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var scale = route.DistanceMeters / oldLength;
        var segments = route.Segments;
        Near((float)(277 * scale), (float)segments[0].EndMeters, 0.0001f);
        Near((float)(2882 * scale), (float)segments[6].EndMeters, 0.0001f);
        Near((float)(2942 * scale), (float)segments[8].StartMeters, 0.0001f);
        Near((float)route.DistanceMeters, (float)segments[^1].EndMeters, 0.0001f);
        foreach (var segment in segments)
        {
            Near(0, (float)segment.GradientPercent);
        }
        var generated = route.Points!;
        if (generated.Any(point => point.RawElevationMeters is null || point.GradientPercent is null))
            throw new Exception("Expected raw DEM and generated gradient values on every route point.");
        Near((float)generated[0].ElevationMeters, (float)generated[^1].ElevationMeters, 0.01f);
        if (generated.Min(point => point.ElevationMeters) < -10 || generated.Max(point => point.ElevationMeters) > 100)
            throw new Exception("Bundled elevations are outside the expected local Dunedin range.");
        foreach (var point in generated.Where((_, index) => index % 17 == 0))
        {
            var sample = route.GetSampleAt(point.DistanceMeters);
            Near((float)point.GradientPercent!.Value, (float)sample.GradientPercent, 0.0001f);
            Near((float)point.GradientPercent!.Value, (float)route.GetGradientAt(point.DistanceMeters), 0.0001f);
            Near((float)point.ElevationMeters, (float)sample.ElevationMeters!.Value, 0.0001f);
        }
        if (generated.Max(point => point.GradientPercent!.Value) - generated.Min(point => point.GradientPercent!.Value) < 0.5)
            throw new Exception("Generated route gradients do not vary with the measured elevation profile.");
        Near((float)route.GetGradientAt(1234.5), (float)route.GetSampleAt(1234.5).GradientPercent, 0.0001f);
        Near((float)route.GetGradientAt(1234.5), (float)route.GetSampleAt(1234.5 + route.DistanceMeters).GradientPercent, 0.0001f);
    }),
    ("Dunedin uses generic visual closure with canonical generated geometry", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var sameDataDifferentId = new VisualRoute(route with { Id = "ordinary-generated-route" });
        if (geometry.Samples.Count != sameDataDifferentId.Samples.Count)
            throw new Exception("Route identity changed visual sampling.");
        for (var i = 0; i < geometry.Samples.Count; i++)
            if (geometry.Samples[i] != sameDataDifferentId.Samples[i])
                throw new Exception("Route identity changed canonical visual geometry.");
        if (geometry.ClosureCorrection.Length() > 0.001f)
            throw new Exception($"Closed generated route received synthetic closure correction {geometry.ClosureCorrection.Length():F3} m.");
        var length = route.DistanceMeters;
        NearVector(geometry.GetPoseAtDistance(0).Position, geometry.GetPoseAtDistance(length).Position);
        var before = geometry.GetPoseAtDistance(length - 0.01);
        var after = geometry.GetPoseAtDistance(0.01);
        if (Vector3.Distance(before.Position, after.Position) > 0.1f)
            throw new Exception("Position discontinuity at the Dunedin lap seam.");
        if (Vector3.Dot(before.Forward, after.Forward) < 0.99f)
            throw new Exception("Tangent discontinuity at the Dunedin lap seam.");
    }),
    ("gentle route bends retain a normal bounded miter", () =>
    {
        var previous = new Vector3(0, 0, 10);
        var current = Vector3.Zero;
        var next = new Vector3(1, 0, -10);
        if (RoadRibbonMath.ClassifyJoin(previous, current, next) != RoadJoinType.Normal)
            throw new Exception("A gentle bend was not classified as normal.");
        var offset = RoadRibbonMath.OffsetAtJoin(previous, current, next, 3.5f);
        if (offset.Length() > 3.5f * RoadRibbonMath.MaximumMiterScale)
            throw new Exception("Gentle-bend miter exceeded its limit.");
    }),
    ("moderate route bends use a bounded bevel join", () =>
    {
        var previous = new Vector3(0, 0, 10);
        var current = Vector3.Zero;
        var next = new Vector3(10, 0, -10);
        if (RoadRibbonMath.ClassifyJoin(previous, current, next) != RoadJoinType.Bevel)
            throw new Exception("A moderate bend was not classified as bevel.");
        var offset = RoadRibbonMath.OffsetAtJoin(previous, current, next, 3.5f);
        if (offset.Length() > 3.5f * RoadRibbonMath.MaximumBevelMiterScale + 0.001f)
            throw new Exception("Bevel join exceeded its bounded offset.");
    }),
    ("sharp synthetic lap seam uses separate incoming and outgoing sections", () =>
    {
        var positions = SyntheticSharpLoop();
        var previous = positions[^2];
        var current = positions[0];
        var next = positions[1];
        if (RoadRibbonMath.ClassifyJoin(previous, current, next) != RoadJoinType.Discontinuity)
            throw new Exception("Synthetic seam did not classify as a discontinuity.");
        var incoming = RoadRibbonMath.OffsetAtJoin(previous, current, next, 3.5f, false);
        var outgoing = RoadRibbonMath.OffsetAtJoin(previous, current, next, 3.5f, true);
        Near(3.5f, incoming.Length()); Near(3.5f, outgoing.Length());
        if (Vector3.Distance(incoming, outgoing) < 4)
            throw new Exception("Sharp-seam cross-sections were joined through a miter.");
    }),
    ("Dunedin route endpoint uses discontinuity handling at its actual lap join", () =>
    {
        var geometry = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var samples = geometry.Samples;
        var join = RoadRibbonMath.ClassifyJoin(samples[^2].Position, samples[^1].Position, samples[1].Position);
        if (join != RoadJoinType.Discontinuity)
            throw new Exception($"Expected a sharp Dunedin lap join, got {join}.");
    }),
    ("sharp-seam footpaths terminate on each side without a diagonal bridge", () =>
    {
        var positions = SyntheticSharpLoop();
        var quads = RoadRibbonMath.BuildQuads(positions, 6f - 3.73f, (6f + 3.73f) / 2);
        if (quads.Count != positions.Length - 1) throw new Exception("Footpath emitted a duplicate closing segment.");
        var incoming = new[] { quads[^1].EndLeft, quads[^1].EndRight };
        var outgoing = new[] { quads[0].StartLeft, quads[0].StartRight };
        if (incoming.All(point => outgoing.Any(other => Vector3.Distance(point, other) < 0.01f)))
            throw new Exception("Footpath cross-sections were bridged at the sharp join.");
        AssertFiniteQuads(quads);
        AssertMaximumExtent(quads, positions, 6.01f);
        AssertNondegenerateTriangles(quads);
        var incomingCap = RoadRibbonMath.BuildSharpEndCap(quads[^1].EndLeft, quads[^1].EndRight,
            positions[^2] - positions[^3], 0.08f, false);
        var outgoingCap = RoadRibbonMath.BuildSharpEndCap(quads[0].StartLeft, quads[0].StartRight,
            positions[1] - positions[2], 0.08f, false);
        AssertNondegenerateTriangles([incomingCap, outgoingCap]);
    }),
    ("sharp-seam kerbs terminate safely on each side", () =>
    {
        var positions = SyntheticSharpLoop();
        var quads = RoadRibbonMath.BuildQuads(positions, 0.23f, 3.615f);
        if (quads.Count != positions.Length - 1) throw new Exception("Kerb emitted a duplicate closing segment.");
        if (Vector3.Distance(quads[^1].EndLeft, quads[0].StartLeft) < 0.1f)
            throw new Exception("Kerb ends were connected across the discontinuity.");
        AssertFiniteQuads(quads);
        AssertMaximumExtent(quads, positions, 3.74f);
        AssertNondegenerateTriangles(quads);
        var cap = RoadRibbonMath.BuildSharpEndCap(quads[^1].EndLeft, quads[^1].EndRight,
            positions[^2] - positions[^3], 0.08f, false);
        AssertNondegenerateTriangles([cap]);
    }),
    ("sharp-seam road triangles are finite, nondegenerate and bounded", () =>
    {
        var positions = SyntheticSharpLoop();
        var quads = RoadRibbonMath.BuildQuads(positions, 7, 0);
        if (quads.Count != positions.Length - 1) throw new Exception("Road closing segment was not emitted exactly once.");
        AssertFiniteQuads(quads);
        AssertNondegenerateTriangles(quads);
        AssertMaximumExtent(quads, positions, 3.51f);
    }),
    ("ordinary Dunedin sections retain their previous miter geometry", () =>
    {
        var geometry = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        foreach (var distance in new[] { 650d, 4050d })
        {
            var index = Enumerable.Range(1, geometry.Samples.Count - 2)
                .MinBy(i => Math.Abs(geometry.Samples[i].DistanceMeters - distance));
            var prev = geometry.Samples[index - 1].Position;
            var here = geometry.Samples[index].Position;
            var next = geometry.Samples[index + 1].Position;
            if (RoadRibbonMath.ClassifyJoin(prev, here, next) == RoadJoinType.Discontinuity)
                throw new Exception($"Expected ordinary section at {distance} m.");
            var actual = RoadRibbonMath.OffsetAtJoin(prev, here, next, 3.5f);
            var expected = LegacyBoundedMiter(prev, here, next, 3.5f,
                RoadRibbonMath.ClassifyJoin(prev, here, next) == RoadJoinType.Bevel
                    ? RoadRibbonMath.MaximumBevelMiterScale : RoadRibbonMath.MaximumMiterScale);
            NearVector(expected, actual);
        }
    }),
    ("street-furniture offsets stay in metres and primitive bounds reject runaway scale", () =>
    {
        var localOffset = new Vector3(0, 3.9f, 0);
        Near(3.9f, RoadsidePrimitiveMath.RotateLocalOffset(localOffset, 1.2f).Y);
        var signalSize = RoadsidePrimitiveMath.WorldAabbSize(new Vector3(1, 1, 1), new Vector3(.38f, 1.15f, .32f), 0.7f);
        if (!RoadsidePrimitiveMath.IsPlausibleStreetFurnitureSize(signalSize))
            throw new Exception("Expected traffic-signal primitive dimensions to be valid.");
        var runaway = RoadsidePrimitiveMath.WorldAabbSize(Vector3.One, new Vector3(.4f, 24, .4f), 0);
        if (RoadsidePrimitiveMath.IsPlausibleStreetFurnitureSize(runaway))
            throw new Exception("Accepted street-furniture primitive tens of metres high.");
        var cap = RoadRibbonMath.BuildSharpEndCap(Vector3.Zero, new Vector3(0, 0, 2), -Vector3.UnitZ, .08f, false);
        Near(.08f, Math.Abs(cap.StartLeft.Y - cap.StartRight.Y));
        AssertNondegenerateTriangles([cap]);
    }),
    ("Generated ground remains well below every route road sample", () =>
    {
        foreach (var route in RouteCatalog.Routes)
        {
            var geometry = new VisualRoute(route);
            var minimumRoadY = geometry.Samples.Min(sample => sample.Position.Y);
            Near(minimumRoadY - VisualRoute.GroundClearanceMeters, geometry.GroundPlaneY);
            foreach (var sample in geometry.Samples)
            {
                // Asphalt is built at the sample centreline height; edge vertices
                // have the same Y because the ribbon's lateral offset is horizontal.
                if (sample.Position.Y - geometry.GroundPlaneY < VisualRoute.GroundClearanceMeters - 0.001f)
                    throw new Exception($"Ground could cover road at {route.Id}, {sample.DistanceMeters:F1} m.");
            }
        }
    }),
    ("background street coordinates use the canonical VisualRoute map", () =>
    {
        var route=new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var origin=RouteCatalog.GetById("dunedin-one-way-oval-gardens")!.Points![0];
        var mapped=BackgroundStreetGeometry.ConvertPoints(route,[new RouteBackgroundStreetPoint(origin.Latitude,origin.Longitude,origin.ElevationMeters)]);
        if(mapped.Length!=1||mapped[0].Length()>0.001f)throw new Exception("Street coordinates do not share the route geographic origin.");
    }),
    ("background street ribbons remain bounded through a sharp curve", () =>
    {
        var points=new[]{new Vector3(0,0,0),new Vector3(15,0,0),new Vector3(15,0,-15),new Vector3(0,0,-15)};
        var quads=BackgroundStreetGeometry.BuildRibbon(points,8);
        if(quads.Count!=points.Length-1)throw new Exception("Expected one bounded ribbon quad per road segment.");
        foreach(var q in quads)foreach(var p in new[]{q.StartLeft,q.EndLeft,q.StartRight,q.EndRight})
            if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)||Math.Abs(p.X)>25||Math.Abs(p.Z)>25)throw new Exception("Street ribbon has an unbounded or non-finite vertex.");
    }),
    ("background street ribbon has no NaN or degenerate triangles", () =>
    {
        var quads=BackgroundStreetGeometry.BuildRibbon(new[]{new Vector3(0,0,0),new Vector3(10,0,0),new Vector3(10,0,-10)},6);
        if(quads.Count!=2)throw new Exception("Unexpected degenerate street sections.");
        foreach(var q in quads){var area=Vector3.Cross(q.EndLeft-q.StartLeft,q.StartRight-q.StartLeft).LengthSquared();if(!float.IsFinite(area)||area<=1e-8f)throw new Exception("Street ribbon triangle is invalid.");}
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

static Vector3[] SyntheticSharpLoop() =>
[
    new(0, 0, 0), new(0, 0, -10), new(0, 0, -20), new(10, 0, -20),
    new(20, 0, -20), new(20, 0, -10), new(20, 0, 0), new(10, 0, 0), new(0, 0, 0)
];

static Vector3 LegacyBoundedMiter(Vector3 previous, Vector3 current, Vector3 next, float distance, float maximumScale)
{
    static Vector3 H(Vector3 value) => new(value.X, 0, value.Z);
    var incoming = Vector3.Normalize(H(current - previous));
    var outgoing = Vector3.Normalize(H(next - current));
    var incomingSide = Vector3.Normalize(Vector3.Cross(incoming, Vector3.UnitY));
    var outgoingSide = Vector3.Normalize(Vector3.Cross(outgoing, Vector3.UnitY));
    var miter = Vector3.Normalize(incomingSide + outgoingSide);
    var scale = Math.Min(1 / Vector3.Dot(miter, outgoingSide), maximumScale);
    return miter * (distance * scale);
}

static void AssertFiniteQuads(IReadOnlyList<RoadRibbonQuad> quads)
{
    foreach (var point in quads.SelectMany(quad => new[] { quad.StartLeft, quad.EndLeft, quad.StartRight, quad.EndRight }))
        if (!float.IsFinite(point.X + point.Y + point.Z)) throw new Exception("Ribbon emitted a non-finite vertex.");
}

static void AssertNondegenerateTriangles(IReadOnlyList<RoadRibbonQuad> quads)
{
    foreach (var quad in quads)
    {
        Check(quad.StartLeft, quad.EndLeft, quad.StartRight);
        Check(quad.StartRight, quad.EndLeft, quad.EndRight);
    }
    static void Check(Vector3 a, Vector3 b, Vector3 c)
    {
        var twiceArea = Vector3.Cross(b - a, c - a).Length();
        if (!float.IsFinite(twiceArea) || twiceArea < 1e-5f)
            throw new Exception("Ribbon emitted a zero-area or non-finite triangle.");
    }
}

static void AssertMaximumExtent(IReadOnlyList<RoadRibbonQuad> quads, IReadOnlyList<Vector3> centerline, float maximum)
{
    foreach (var point in quads.SelectMany(quad => new[] { quad.StartLeft, quad.EndLeft, quad.StartRight, quad.EndRight }))
    {
        var nearest = centerline.Min(center => Vector3.Distance(center, point));
        if (nearest > maximum) throw new Exception($"Ribbon vertex exceeded corridor ({nearest:F2} m > {maximum:F2} m).");
    }
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
