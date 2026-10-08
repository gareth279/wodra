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
    ("building visual categories follow OSM building and use tags", () =>
    {
        Equal("residential", BuildingVisualStyle.Classify(new Dictionary<string,string>{{"building","house"}}));
        Equal("commercial/retail", BuildingVisualStyle.Classify(new Dictionary<string,string>{{"shop","bakery"}}));
        Equal("office", BuildingVisualStyle.Classify(new Dictionary<string,string>{{"office","company"}}));
        Equal("university/education", BuildingVisualStyle.Classify(new Dictionary<string,string>{{"amenity","university"}}));
        Equal("historic", BuildingVisualStyle.Classify(new Dictionary<string,string>{{"historic","yes"},{"building","house"}}));
        Equal("generic", BuildingVisualStyle.Classify(new Dictionary<string,string>{{"building","yes"}}));
    }),
    ("building facade palette variant is deterministic and category-aware", () =>
    {
        var first=BuildingVisualStyle.MaterialVariant(123456,"residential");
        Equal(first,BuildingVisualStyle.MaterialVariant(123456,"residential"));
        if(first is <0 or >9 || BuildingVisualStyle.PaletteCount != 10)throw new Exception("Unexpected shared facade palette size/index.");
        if(BuildingVisualStyle.MaterialVariant(123456,"industrial/service") is <8 or >9)
            throw new Exception("Industrial category did not select its muted cooler palette family.");
    }),
    ("building explicit colour parser accepts compact hex and rejects malformed input", () =>
    {
        if(!BuildingVisualStyle.TryParseColour("#abc",out var colour))throw new Exception("Valid OSM colour was rejected.");
        Near(170/255f,colour.R);Near(187/255f,colour.G);Near(204/255f,colour.B);
        if(BuildingVisualStyle.TryParseColour("#12xz00",out _))throw new Exception("Malformed OSM colour was accepted.");
    }),
    ("rectangular building receives a gabled roof within its total height", () =>
    {
        var roof=BuildingMeshGeometry.BuildStyled(StyledRectangle(12,8),8,"gabled",2,2,false);
        Equal("gabled",roof.EffectiveRoofShape);Equal(4,roof.RoofTriangleCount);
        Near(6,roof.WallTopHeightMeters);Near(8,roof.Facade.Concat(roof.Roof).Max(p=>p.Y));
        Near(8,roof.Facade.Concat(roof.Roof).Max(p=>p.Y)-roof.Facade.Concat(roof.Roof).Min(p=>p.Y));
    }),
    ("rectangular building receives a valid hipped roof", () =>
    {
        var roof=BuildingMeshGeometry.BuildStyled(StyledRectangle(16,8),10,"hipped",2.5f,3,false);
        Equal("hipped",roof.EffectiveRoofShape);Equal(6,roof.RoofTriangleCount);
        AssertFiniteBuildingMesh(roof);
    }),
    ("irregular footprint safely falls back to a flat roof", () =>
    {
        var irregular=new[]{new Vector3(0,0,0),new Vector3(8,0,0),new Vector3(7,0,3),
            new Vector3(4,0,5),new Vector3(1,0,4),new Vector3(0,0,0)};
        var roof=BuildingMeshGeometry.BuildStyled(irregular,7,"gabled",2,2,false);
        Equal("flat",roof.EffectiveRoofShape);Equal(3,roof.RoofTriangleCount);
    }),
    ("gabled and hipped roofs contain only finite nondegenerate triangles", () =>
    {
        foreach(var shape in new[]{"gabled","hipped"})
            AssertFiniteBuildingMesh(BuildingMeshGeometry.BuildStyled(StyledRectangle(14,9),9,shape,2,3,false));
    }),
    ("roof height metadata remains inside existing building height envelope", () =>
    {
        var roofHeight=BuildingVisualStyle.ResolveRoofHeight(8,12,"12 m","2");
        Near(3.2f,roofHeight);
        var mesh=BuildingMeshGeometry.BuildStyled(StyledRectangle(12,8),8,"gabled",roofHeight,2,false);
        Near(8,mesh.Facade.Concat(mesh.Roof).Max(p=>p.Y)-mesh.Facade.Concat(mesh.Roof).Min(p=>p.Y));
    }),
    ("facade windows stay on exterior walls and small faces omit them", () =>
    {
        var ring=StyledRectangle(12,8);
        var mesh=BuildingMeshGeometry.BuildStyled(ring,9,"flat",0,3,true);
        if(mesh.WindowCount==0)throw new Exception("Nearby building did not receive facade windows.");
        foreach(var point in mesh.Windows)
        {
            if(point.Y<0 || point.Y>mesh.WallTopHeightMeters)throw new Exception("Window exceeded wall vertical extent.");
            var distance=Enumerable.Range(0,ring.Length-1).Min(i=>PointSegmentDistanceXZ(point,ring[i],ring[i+1]));
            if(distance>.02f)throw new Exception("Window geometry is not on a footprint wall face.");
        }
        var shortFace=BuildingMeshGeometry.BuildStyled(StyledRectangle(3,3),8,"flat",0,2,true);
        Equal(0,shortFace.WindowCount);
    }),
    ("window columns, storeys, and per-building limits are capped", () =>
    {
        var mesh=BuildingMeshGeometry.BuildStyled(StyledRectangle(30,20),30,"flat",0,8,true,2,2,5);
        Equal(5,mesh.WindowCount);Equal(10,mesh.WindowTriangleCount);
    }),
    ("buildings beyond detail radius remain plain", () =>
    {
        Equal(true,BuildingVisualStyle.ShouldAddFacadeDetail(80));
        Equal(false,BuildingVisualStyle.ShouldAddFacadeDetail(80.01));
        Equal(false,BuildingVisualStyle.ShouldAddFacadeDetail(double.NaN));
    }),
    ("Dunedin building footprints, base elevations, and heights match original bundle", () =>
    {
        var route=RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var buildings=RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json",route.Id)
            .OrderBy(b=>b.OsmType,StringComparer.Ordinal).ThenBy(b=>b.OsmId);
        using var buffer=new MemoryStream();using(var writer=new BinaryWriter(buffer,System.Text.Encoding.UTF8,true))
        foreach(var b in buildings)
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes(b.OsmType));writer.Write(b.OsmId);writer.Write(b.HeightMeters);writer.Write(b.BaseElevationMeters!.Value);
            foreach(var point in b.Footprint){writer.Write(point.Longitude);writer.Write(point.Latitude);}
        }
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        Equal("b8a97cd9ca042fec11dd402e77f2f4c30f4f12f501879b8bade193f4c73f7123",hash);
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
    ("sharp-seam footpaths have no cap triangles or cross-route bridges", () =>
    {
        var positions = SyntheticSharpLoop();
        foreach (var side in new[] { -1f, 1f })
        {
            var width = 6f - 3.73f;
            var offset = side * (6f + 3.73f) / 2;
            var quads = RoadRibbonMath.BuildQuads(positions, width, offset);
            if (quads.Count != positions.Length - 1) throw new Exception("Footpath emitted a duplicate closing segment.");
            if (RoadRibbonMath.ShouldBuildSharpProfileCap(side * 3.73f, side * 6f))
                throw new Exception("Horizontal footpath generated a discontinuity cap.");
            AssertFiniteQuads(quads);
            AssertMaximumExtent(quads, positions, 6.01f);
            AssertNondegenerateTriangles(quads);
            AssertNoTriangleCrossesRoute(quads, positions, side);
            if (MaximumTriangleEdge(quads) > 10.5f)
                throw new Exception("Footpath contains an unexpectedly long triangle edge at the discontinuity.");
            // Each quad is emitted from one consecutive segment only. There is no
            // triangle joining the incoming terminal quad to the outgoing first quad.
            if (quads.Count != positions.Length - 1 || quads[^1].EndLeft == quads[0].StartLeft)
                throw new Exception("Footpath discontinuity sections were joined.");
        }
    }),
    ("Dunedin styled building meshes stay bounded and produce batched surface statistics", () =>
    {
        var route=RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry=new VisualRoute(route);
        var buildings=RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "GWift.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json",route.Id);
        var triangles=0;var facade=0;var roof=0;var windows=0;var detailed=0;
        var flat=0;var gabled=0;var hipped=0;var fallback=0;var chunksWithWindows=new HashSet<int>();
        var buildTimer=System.Diagnostics.Stopwatch.StartNew();
        foreach(var building in buildings)
        {
            var ring=building.Footprint.Select(point=>geometry.GetPositionForCoordinate(point.Latitude,point.Longitude,
                building.BaseElevationMeters!.Value)).ToArray();
            var tags=building.SourceTags;
            var shape=BuildingVisualStyle.NormalizeRoofShape(tags.GetValueOrDefault("roof:shape"));
            var width=Math.Min(ring.Max(p=>p.X)-ring.Min(p=>p.X),ring.Max(p=>p.Z)-ring.Min(p=>p.Z));
            var roofHeight=BuildingVisualStyle.ResolveRoofHeight((float)building.HeightMeters,width,
                tags.GetValueOrDefault("roof:height"),tags.GetValueOrDefault("roof:levels"));
            var levels=ParseStoreysForBuilding(tags.GetValueOrDefault("building:levels"),(float)building.HeightMeters);
            var mesh=BuildingMeshGeometry.BuildStyled(ring,(float)building.HeightMeters,shape,roofHeight,levels,
                BuildingVisualStyle.ShouldAddFacadeDetail(building.DistanceFromRouteMeters));
            triangles+=mesh.FacadeTriangleCount+mesh.RoofTriangleCount+mesh.WindowTriangleCount;
            facade+=mesh.FacadeTriangleCount;roof+=mesh.RoofTriangleCount;windows+=mesh.WindowTriangleCount;
            if(mesh.WindowCount>0){detailed++;chunksWithWindows.Add(Math.Clamp((int)(building.NearestRouteDistanceMeters/350),0,
                (int)Math.Ceiling(route.DistanceMeters/350)-1));}
            if(mesh.EffectiveRoofShape=="gabled")gabled++;
            else if(mesh.EffectiveRoofShape=="hipped")hipped++;
            else {flat++;if(!string.IsNullOrWhiteSpace(tags.GetValueOrDefault("roof:shape"))
                && !tags.GetValueOrDefault("roof:shape")!.Equals("flat",StringComparison.OrdinalIgnoreCase))fallback++;}
            AssertFiniteBuildingMesh(mesh);
            foreach(var i in Enumerable.Range(0,mesh.RoofTriangleCount))
            {
                var normal=Vector3.Cross(mesh.Roof[i*3+1]-mesh.Roof[i*3],mesh.Roof[i*3+2]-mesh.Roof[i*3]);
                if(normal.Y<=0)throw new Exception("Roof triangles must have upward-facing normals.");
            }
        }
        buildTimer.Stop();
        var chunkCount=buildings.Select(b=>Math.Clamp((int)(b.NearestRouteDistanceMeters/350),0,
            (int)Math.Ceiling(route.DistanceMeters/350)-1)).Distinct().Count();
        var surfaceCount=chunkCount*2+chunksWithWindows.Count;
        Console.WriteLine($"BUILDING STYLE REPORT category={string.Join(',',buildings.GroupBy(b=>b.VisualCategory).OrderBy(g=>g.Key).Select(g=>$"{g.Key}:{g.Count()}"))}; " +
            $"flat/gabled/hipped={flat}/{gabled}/{hipped}; fallback={fallback}; detailed={detailed}; " +
            $"triangles={triangles} facade={facade} roof={roof} windows={windows}; chunks={chunkCount}; instances={chunkCount}; surfaces={surfaceCount}; shared materials=3; palette variants={BuildingVisualStyle.PaletteCount}; geometryBuildMs={buildTimer.Elapsed.TotalMilliseconds:F1}");
        if(buildings.Count!=888 || detailed==0 || gabled+hipped==0 || triangles>100000)
            throw new Exception("Dunedin visual geometry counts are outside the expected lightweight range.");
    }),
    ("sharp-seam kerb tops terminate without horizontal cap triangles", () =>
    {
        var positions = SyntheticSharpLoop();
        foreach (var side in new[] { -1f, 1f })
        {
            var quads = RoadRibbonMath.BuildQuads(positions, 0.23f, side * 3.615f);
            if (quads.Count != positions.Length - 1) throw new Exception("Kerb emitted a duplicate closing segment.");
            if (RoadRibbonMath.ShouldBuildSharpProfileCap(side * 3.5f, side * 3.73f))
                throw new Exception("Kerb top generated a horizontal discontinuity cap.");
            AssertFiniteQuads(quads);
            AssertMaximumExtent(quads, positions, 3.74f);
            AssertNondegenerateTriangles(quads);
            AssertNoTriangleCrossesRoute(quads, positions, side);
            if (MaximumTriangleEdge(quads) > 10.5f)
                throw new Exception("Kerb top contains an unexpectedly long triangle edge at the discontinuity.");
        }
    }),
    ("sharp-seam road triangles are finite, nondegenerate and bounded", () =>
    {
        var positions = SyntheticSharpLoop();
        var quads = RoadRibbonMath.BuildQuads(positions, 7, 0);
        if (quads.Count != positions.Length - 1) throw new Exception("Road closing segment was not emitted exactly once.");
        AssertFiniteQuads(quads);
        AssertNondegenerateTriangles(quads);
        AssertMaximumExtent(quads, positions, 3.51f);
        const float roadWidth = 7;
        var fill = RoadRibbonMath.BuildSharpJoinFill(Vector3.Zero, roadWidth / 2 + .05f);
        if (fill.Count != 16 || fill.Any(triangle => triangle.A != Vector3.Zero))
            throw new Exception("Expected a local asphalt fan at the sharp join.");
        if (fill.Any(triangle => Math.Abs(Vector3.Distance(triangle.B, Vector3.Zero) - (roadWidth / 2 + .05f)) > .001f
            || Math.Abs(Vector3.Distance(triangle.C, Vector3.Zero) - (roadWidth / 2 + .05f)) > .001f))
            throw new Exception("Asphalt fill does not extend slightly beyond the road edge to cover the join.");
    }),
    ("profile ribbons exclude sharp seam by canonical distance on synthetic and Dunedin routes", () =>
    {
        const double gap = 6;
        static IReadOnlyList<RoadProfileRibbonQuad> Profiles(IReadOnlyList<Vector3> positions,
            IReadOnlyList<double> distances, float lateralStart, float lateralEnd, float startHeight, float endHeight) =>
            RoadRibbonMath.BuildProfileQuads(positions, distances, lateralStart, lateralEnd,
                startHeight, endHeight, gap);

        static IReadOnlyList<RoadProfileRibbonQuad> AllProfiles(IReadOnlyList<Vector3> positions,
            IReadOnlyList<double> distances) =>
        [
            .. new[] { -1f, 1f }.SelectMany(side => new[]
            {
                Profiles(positions, distances, side * 3.5f, side * 3.5f, .005f, .14f), // kerb vertical face
                Profiles(positions, distances, side * 3.5f, side * 3.73f, .14f, .14f), // kerb top
                Profiles(positions, distances, side * 3.73f, side * 6f, .14f, .11f) // footpath top/profile
            }).SelectMany(x => x)
        ];

        var synthetic = SyntheticSharpLoop();
        var syntheticDistances = Enumerable.Range(0, synthetic.Length).Select(i => (double)i * 10).ToArray();
        AssertExcluded(AllProfiles(synthetic, syntheticDistances), syntheticDistances[^1], gap);

        var route = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var samples = route.Samples;
        var first = samples[0];
        var lastUnique = samples[^2];
        var closing = samples[^1];
        NearVector(first.Position, closing.Position);
        if (Vector3.Distance(first.Position, lastUnique.Position) < 1e-5f)
            throw new Exception("Expected a distinct last unique sample before the duplicate closure.");
        var seamTurn = RoadRibbonMath.ClassifyJoin(lastUnique.Position, closing.Position, samples[1].Position);
        if (seamTurn != RoadJoinType.Discontinuity)
            throw new Exception($"Dunedin closing turn classified as {seamTurn}.");

        Console.WriteLine($"DIAG Dunedin: length={route.LengthMeters:F3}; first={first.DistanceMeters:F3} {first.Position} tangent={first.Forward}; "
            + $"lastUnique={lastUnique.DistanceMeters:F3} {lastUnique.Position} tangent={lastUnique.Forward}; "
            + $"duplicate={closing.DistanceMeters:F3} {closing.Position}; endpointGap={Vector3.Distance(first.Position,lastUnique.Position):F3}; "
            + $"turn={MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(closing.Position-lastUnique.Position),
                Vector3.Normalize(samples[1].Position-closing.Position)), -1, 1))*180/MathF.PI:F2} deg");
        var positions = samples.Select(s => s.Position).ToArray();
        var distances = samples.Select(s => s.DistanceMeters).ToArray();
        var dunedinProfiles = AllProfiles(positions, distances);
        AssertExcluded(dunedinProfiles, route.LengthMeters, gap);
        var near5406 = Enumerable.Range(0, samples.Count).MinBy(i => Math.Abs(samples[i].DistanceMeters - 5406));
        Console.WriteLine($"DIAG seam source: nearest5406={near5406}@{samples[near5406].DistanceMeters:F3}m; "
            + $"previous={near5406 - 1}@{samples[near5406 - 1].DistanceMeters:F3}m; "
            + $"next={near5406 + 1}@{samples[near5406 + 1].DistanceMeters:F3}m; "
            + "profile vertices at +/-6m=0");
        foreach (var side in new[] { -1f, 1f })
        {
            var oldKerbFaces = RoadRibbonMath.BuildProfileQuads(positions, distances,
                side * 3.5f, side * 3.5f, .005f, .14f, 4);
            foreach (var face in oldKerbFaces.Where(q => q.SourceSegmentIndex == near5406 - 1
                && q.StartDistanceMeters <= 5406 && q.EndDistanceMeters >= 5406))
                Console.WriteLine($"DIAG old 4m kerb-face candidate side={side:+0;-0}; source={face.SourceSegmentIndex}->{face.SourceSegmentIndex + 1} "
                    + $"distance={face.StartDistanceMeters:F3}..{face.EndDistanceMeters:F3}; "
                    + $"v={face.Quad.StartLeft};{face.Quad.EndLeft};{face.Quad.StartRight};{face.Quad.EndRight}");
        }
        if (dunedinProfiles.Count == 0) throw new Exception("Expected non-asphalt profiles to resume outside the seam gap.");
        foreach (var quad in dunedinProfiles)
            if (!(quad.EndDistanceMeters < route.LengthMeters - gap
                && quad.StartDistanceMeters > gap))
                throw new Exception("A profile quad touches the closed +/-6 m seam exclusion.");
        if (!dunedinProfiles.Any(q => q.EndDistanceMeters < route.LengthMeters - gap
            && q.EndDistanceMeters > route.LengthMeters - gap - 11)
            || !dunedinProfiles.Any(q => q.EndDistanceMeters < gap + 11 && q.EndDistanceMeters > gap))
            throw new Exception("Profile geometry did not resume normally just outside both seam exclusion edges.");

        // The profile path is shared by kerb faces, kerb tops, and footpath tops.
        // Each returned quad emits two triangles/six vertices; zero quads in the
        // seam interval therefore means no side wall, cap, skirt, or top vertices.
        if (dunedinProfiles.Any(q => q.SourceSegmentIndex == samples.Count - 2
            || q.SourceSegmentIndex == samples.Count - 1))
            throw new Exception("Duplicated closing endpoint generated edge-profile geometry.");

        var asphalt = RoadRibbonMath.BuildQuads(positions, 7, 0);
        if (asphalt.Count != samples.Count - 1)
            throw new Exception("Authoritative asphalt ribbon no longer emits every route segment through closure.");
        var asphaltFill = RoadRibbonMath.BuildSharpJoinFill(Vector3.Zero, 3.55f);
        if (asphaltFill.Count == 0) throw new Exception("Local asphalt seam fill is missing.");

        static void AssertExcluded(IReadOnlyList<RoadProfileRibbonQuad> quads, double length, double exclusion)
        {
            foreach (var quad in quads)
            {
                if (quad.StartDistanceMeters < exclusion - 1e-7
                    || quad.EndDistanceMeters > length - exclusion + 1e-7)
                    throw new Exception($"Profile quad survived inside seam exclusion: {quad.StartDistanceMeters:F3}..{quad.EndDistanceMeters:F3} m.");
                if (quad.SourceSegmentIndex < 0 || quad.SourceSegmentIndex >= int.MaxValue)
                    throw new Exception("Invalid source segment index.");
            }
        }
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
        var cap = RoadRibbonMath.BuildSharpEndCap(Vector3.Zero, new Vector3(0, 2, 0), -Vector3.UnitZ, .08f, true);
        Near(.08f, Vector3.Distance(cap.StartLeft, cap.StartRight));
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
    ("terrain grid emits the expected regular two-triangle cells with shared finite normals", () =>
    {
        var data = SyntheticTerrain(2, 2, Enumerable.Repeat((double?)100, 9).ToArray(), Enumerable.Repeat(true, 4).ToArray());
        var route = new[] { new VisualRouteSample(0, new Vector3(100, 0, 100), Vector3.UnitX),
            new VisualRouteSample(20, new Vector3(120, 0, 100), Vector3.UnitX) };
        var built = TerrainMeshGeometry.Build(data, route);
        Equal(8, built.TriangleCount);
        Equal(9, built.UniqueVertexCount);
        Equal(1, built.Chunks.Length);
        Equal(24, built.Chunks[0].Vertices.Length);
        if (built.Chunks[0].Normals.Any(n => !float.IsFinite(n.X+n.Y+n.Z) || Math.Abs(n.Length()-1) > .001f))
            throw new Exception("Terrain smoothing did not produce finite unit normals.");
        if (built.Chunks[0].Vertices.Any(v => !float.IsFinite(v.X+v.Y+v.Z)))
            throw new Exception("Terrain emitted a non-finite vertex.");
        Near(-.08f, built.Chunks[0].Vertices[0].Y);
        Near(0, built.Chunks[0].Vertices[0].X);
        Near(0, built.Chunks[0].Vertices[0].Z);
    }),
    ("terrain coordinate datum and route clearance match visual route conventions", () =>
    {
        var route = new[] { new VisualRouteSample(0, new Vector3(0, 2, 10), Vector3.UnitX),
            new VisualRouteSample(20, new Vector3(20, 2, 10), Vector3.UnitX) };
        var terrain = TerrainMeshGeometry.ApplyRoadClearance(new Vector3(10, 8, 10), route, 5, .15);
        Near(1.85f, terrain.Y);
        var outside = TerrainMeshGeometry.ApplyRoadClearance(new Vector3(10, 8, 20), route, 5, .15);
        Near(8, outside.Y);
        var routeDefinition = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var visual = new VisualRoute(routeDefinition);
        var origin = routeDefinition.Points![0];
        NearVector(Vector3.Zero, visual.GetPositionForCoordinate(origin.Latitude, origin.Longitude, origin.ElevationMeters));
    }),
    ("terrain NoData cells are safely omitted and adjacent chunks share edge elevations", () =>
    {
        var nodata = SyntheticTerrain(1, 1, [100, null, 100, 100], [true]);
        var route = new[] { new VisualRouteSample(0, new Vector3(100, 0, 100), Vector3.UnitX),
            new VisualRouteSample(20, new Vector3(120, 0, 100), Vector3.UnitX) };
        Equal(0, TerrainMeshGeometry.Build(nodata, route).TriangleCount);
        var first = SyntheticTerrain(1, 1, [100, 101, 101, 102], [true], "a", 0, 0).Chunks[0];
        var second = SyntheticTerrain(1, 1, [101, 102, 102, 103], [true], "b", 10, 0).Chunks[0];
        var pair = SyntheticTerrain(1, 1, [100, 101, 101, 102], [true]) with
            { Chunks = [first, second] };
        var meshes = TerrainMeshGeometry.Build(pair, route).Chunks;
        var seamA = meshes[0].Vertices.Where(v => Math.Abs(v.X-10) < .001).Select(v => v.Y).Distinct().Order().ToArray();
        var seamB = meshes[1].Vertices.Where(v => Math.Abs(v.X-10) < .001).Select(v => v.Y).Distinct().Order().ToArray();
        if (!seamA.SequenceEqual(seamB)) throw new Exception("Terrain chunk edge elevations do not match.");
    }),
    ("near and far terrain overlap with finite DEM surfaces and a safe vertical bias", () =>
    {
        var near = SyntheticTerrain(1, 1, [100, 100, 100, 100], [true], "near", 0, 0).Chunks[0];
        var far = SyntheticTerrain(1, 1, [100, 100, 100, 100], [true], "far", 0, 0).Chunks[0];
        var data = SyntheticTerrain(1, 1, [100, 100, 100, 100], [true]) with
        { FarSpacingMeters = 25, FarVerticalBiasMeters = .15, NearRadiusMeters = 250,
          FarRadiusMeters = 750, TransitionOverlapMeters = 10 };
        var route = new[] { new VisualRouteSample(0, new Vector3(100, 0, 100), Vector3.UnitX),
            new VisualRouteSample(20, new Vector3(120, 0, 100), Vector3.UnitX) };
        var built = TerrainMeshGeometry.Build(data, route,
        [new TerrainZoneChunk(near, 10, .08, false), new TerrainZoneChunk(far, 25, .15, false)]);
        Equal(2, built.Chunks.Length); Equal(4, built.TriangleCount);
        if (built.Chunks.SelectMany(c => c.Vertices).Any(v => !float.IsFinite(v.X+v.Y+v.Z)))
            throw new Exception("Near/far transition geometry contains non-finite vertices.");
        Near(-.07f, built.Chunks[1].Vertices[0].Y - built.Chunks[0].Vertices[0].Y);
        Equal(true, data.FarRadiusMeters > data.NearRadiusMeters);
        Equal(true, data.NearRadiusMeters - data.TransitionOverlapMeters < data.NearRadiusMeters);
        foreach (var mesh in built.Chunks)
        for (var i = 0; i < mesh.Indices.Length; i += 3)
            if (Vector3.Cross(mesh.Vertices[mesh.Indices[i + 1]] - mesh.Vertices[mesh.Indices[i]],
                mesh.Vertices[mesh.Indices[i + 2]] - mesh.Vertices[mesh.Indices[i]]).LengthSquared() < 1e-10f)
                throw new Exception("Transition geometry contains a degenerate triangle.");
    }),
    ("bundled Dunedin terrain loads deterministically and includes route elevation range", () =>
    {
        const string resource = "GWift.GameData.Routes.dunedin-one-way-oval-gardens.terrain.json";
        var a = RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly, resource,
            "dunedin-one-way-oval-gardens")!;
        var b = RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly, resource,
            "dunedin-one-way-oval-gardens")!;
        Equal(a.SpacingMeters, b.SpacingMeters);
        Equal(a.Chunks.Length, b.Chunks.Length);
        if (!a.Chunks.SelectMany(c => c.ElevationsMeters).SequenceEqual(b.Chunks.SelectMany(c => c.ElevationsMeters)))
            throw new Exception("Terrain bundle did not load deterministically.");
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var routePoints = route.Points!;
        var minRoute = routePoints.Min(p => p.ElevationMeters);
        var maxRoute = routePoints.Max(p => p.ElevationMeters);
        if (a.MinimumSourceElevationMeters > minRoute || a.MaximumSourceElevationMeters < maxRoute)
            throw new Exception("Terrain DEM elevation range does not contain the known route elevation range.");
        Equal(10d, a.SpacingMeters);
        Equal(44, a.Chunks.Length);
        Equal(4, a.InterpolatedVertices);
        Equal(250d, a.NearRadiusMeters);
        Equal(750d, a.FarRadiusMeters);
        Equal(25d, a.FarSpacingMeters);
        Equal(10d, a.TransitionOverlapMeters);
        if (a.FarChunks.Length == 0) throw new Exception("The real Dunedin bundle has no far terrain coverage.");
        if (a.FarChunks.SelectMany(c => c.ElevationsMeters).Where(v => v.HasValue).Distinct().Count() < 100)
            throw new Exception("Far terrain does not contain varied real DEM elevation.");
        if (a.FarChunks.Sum(c => c.CellMask.Count(active => active) * 2) == 0)
            throw new Exception("Far terrain emitted no triangles.");
        if (a.MinimumSourceElevationMeters > minRoute || a.MaximumSourceElevationMeters < maxRoute)
            throw new Exception("Combined near/far DEM range omitted route elevations.");
        var meshRoute = new[] { new VisualRouteSample(0, new Vector3(0, 0, 0), Vector3.UnitX),
            new VisualRouteSample(20, new Vector3(20, 0, 0), Vector3.UnitX) };
        var generated = TerrainMeshGeometry.Build(a, meshRoute);
        Equal(a.TriangleCount, generated.TriangleCount);
        if (generated.Chunks.SelectMany(c => c.Vertices).Any(v => !float.IsFinite(v.X+v.Y+v.Z)))
            throw new Exception("Real near/far LINZ geometry contains non-finite vertices.");
        foreach (var mesh in generated.Chunks)
        for (var i = 0; i < mesh.Indices.Length; i += 3)
            if (Vector3.Cross(mesh.Vertices[mesh.Indices[i + 1]] - mesh.Vertices[mesh.Indices[i]],
                mesh.Vertices[mesh.Indices[i + 2]] - mesh.Vertices[mesh.Indices[i]]).LengthSquared() < 1e-10f)
                throw new Exception("Real near/far LINZ geometry contains a degenerate triangle.");
        Near((float)routePoints[0].ElevationMeters, (float)a.RouteStartElevationMeters);
        if (RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly, "missing.terrain.json", route.Id) is not null)
            throw new Exception("Missing terrain bundle should activate fallback ground.");
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
    ("northbound rider lane offset is to local left", () =>
    {
        NearVector(new Vector3(-RiderLaneMath.RiderLateralOffsetMeters,0,0),
            RiderLaneMath.OffsetPosition(new Vector3(4,2,7),new Vector3(0,0,-1))-new Vector3(4,2,7));
    }),
    ("reversed route tangent derives its own local left", () =>
    {
        NearVector(new Vector3(RiderLaneMath.RiderLateralOffsetMeters,0,0),
            RiderLaneMath.OffsetPosition(Vector3.Zero,new Vector3(0,0,1)));
    }),
    ("visual lane offset does not change authoritative route distance", () =>
    {
        var route=new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var distance=1234.5;var before=route.GetPoseAtDistance(distance);_ = RiderLaneMath.OffsetPosition(before.Position,before.Forward);
        Near((float)distance,(float)route.GetPoseAtDistance(distance).LoopDistanceMeters);
    }),
    ("chase camera follow and look targets use the offset rider anchor", () =>
    {
        var rider=RiderLaneMath.OffsetPosition(new Vector3(10,3,20),new Vector3(0,0,-1));
        NearVector(rider-new Vector3(0,0,-1)*8+Vector3.UnitY*4,ChaseCameraMath.FollowPosition(rider,new Vector3(0,0,-1)));
        NearVector(rider+new Vector3(0,0,-1)*6+Vector3.UnitY,ChaseCameraMath.LookTarget(rider,new Vector3(0,0,-1)));
    }),
    ("crossing nodes retain source location but suppress physical proxy", () =>
    {
        var source=new Vector3(2,1,3);var placement=StreetFurniturePlacementMath.Place("crossing",source,Vector3.Zero,new Vector3(0,0,-1),0,7);
        Equal(true,placement.ProxySuppressed);Equal(false,placement.Relocated);NearVector(source,placement.SourcePosition);
    }),
    ("in-road signal proxy moves to left roadside", () =>
    {
        var placement=StreetFurniturePlacementMath.Place("traffic-signal",Vector3.Zero,Vector3.Zero,new Vector3(0,0,-1),1,7);
        Equal(true,placement.Relocated);Near(4.25f,Vector3.Distance(placement.VisualPosition,Vector3.Zero));
        Near(-4.25f,placement.VisualPosition.X);
    }),
    ("relocated signal preserves source coordinate separately", () =>
    {
        var source=new Vector3(.5f,2,.25f);var placement=StreetFurniturePlacementMath.Place("traffic-signal",source,Vector3.Zero,new Vector3(1,0,0),2,7);
        NearVector(source,placement.SourcePosition);Equal(false,placement.SourcePosition==placement.VisualPosition);
    }),
    ("in-road stop and give-way signs move to roadside", () =>
    {
        foreach(var category in new[]{"stop-sign","give-way-sign"})
        {var p=StreetFurniturePlacementMath.Place(category,Vector3.Zero,Vector3.Zero,new Vector3(0,0,-1),2,7);Equal(true,p.Relocated);Near(-4.25f,p.VisualPosition.X);}
    }),
    ("bench and bus-stop outside the road keep mapped positions", () =>
    {
        foreach(var category in new[]{"bench","bus-stop"})
        {var source=new Vector3(5,1,2);var p=StreetFurniturePlacementMath.Place(category,source,Vector3.Zero,new Vector3(0,0,-1),4,7);Equal(false,p.Relocated);NearVector(source,p.VisualPosition);}
    }),
    ("bollards inside the road remain intentionally at mapped positions", () =>
    {
        var source=new Vector3(.5f,0,1);var p=StreetFurniturePlacementMath.Place("bollard",source,Vector3.Zero,new Vector3(0,0,-1),1,7);
        Equal(false,p.Relocated);Equal(true,p.RetainedInRoad);NearVector(source,p.VisualPosition);
    }),
    ("furniture placement remains finite through curved route tangents", () =>
    {
        for(var i=0;i<64;i++){var angle=i*MathF.Tau/64;var tangent=new Vector3(MathF.Cos(angle),.1f,MathF.Sin(angle));
            var p=StreetFurniturePlacementMath.Place("traffic-signal",Vector3.Zero,new Vector3(i,0,i),tangent,1,7);
            if(!float.IsFinite(p.VisualPosition.X+p.VisualPosition.Y+p.VisualPosition.Z))throw new Exception("Non-finite roadside placement.");}
    }),
    ("traffic-signal primitive dimensions still pass size sanity checks", () =>
    {
        foreach(var size in new[]{new Vector3(.11f,4,.11f),new Vector3(.30f,.92f,.24f),new Vector3(.105f,.105f,.045f)})
            Equal(true,RoadsidePrimitiveMath.IsPlausibleStreetFurnitureSize(size));
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

static RouteTerrainData SyntheticTerrain(int columns, int rows, double?[] elevations, bool[] cellMask,
    string id = "terrain-test", double originX = 0, double originZ = 0) => new(
    id, "synthetic DEM", "EPSG:2193", 10, 20, 20, 100, .08, .15, 5,
    elevations.Where(value => value is not null).Min()!.Value,
    elevations.Where(value => value is not null).Max()!.Value,
    elevations.Count(value => value is not null), cellMask.Count(value => value) * 2, 0, 0,
    [new RouteTerrainChunkData(id, originX, originZ, columns, rows, 0, elevations, cellMask)]);

static Vector3[] StyledRectangle(float length, float width) =>
    [new(0,0,0),new(length,0,0),new(length,0,width),new(0,0,width),new(0,0,0)];

static int ParseStoreysForBuilding(string? value,float height) =>
    double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var levels)
        && double.IsFinite(levels)&&levels>0?Math.Clamp((int)Math.Ceiling(levels),1,6):Math.Clamp((int)Math.Ceiling(height/3f),1,6);

static void AssertFiniteBuildingMesh(BuildingMeshGeometry.StyledMesh mesh)
{
    foreach(var vertices in new[]{mesh.Facade,mesh.Roof,mesh.Windows})
    {
        if(vertices.Any(p=>!float.IsFinite(p.X+p.Y+p.Z)))throw new Exception("Building styling emitted a non-finite vertex.");
        for(var i=0;i<vertices.Length;i+=3)
            if(Vector3.Cross(vertices[i+1]-vertices[i],vertices[i+2]-vertices[i]).LengthSquared()<1e-10f)
                throw new Exception("Building styling emitted a degenerate triangle.");
    }
}

static float PointSegmentDistanceXZ(Vector3 point,Vector3 a,Vector3 b)
{
    var delta=new Vector2(b.X-a.X,b.Z-a.Z);var length=delta.LengthSquared();
    var t=length>0?Math.Clamp(Vector2.Dot(new Vector2(point.X-a.X,point.Z-a.Z),delta)/length,0,1):0;
    return Vector2.Distance(new Vector2(point.X,point.Z),new Vector2(a.X,a.Z)+delta*t);
}

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

static void AssertNoTriangleCrossesRoute(IReadOnlyList<RoadRibbonQuad> quads, IReadOnlyList<Vector3> centerline, float side)
{
    for (var i = 0; i < quads.Count; i++)
    {
        var direction = Vector3.Normalize(centerline[i + 1] - centerline[i]);
        var lateral = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY)) * side;
        var quad = quads[i];
        foreach (var point in new[] { quad.StartLeft, quad.EndLeft, quad.StartRight, quad.EndRight })
        {
            var along = Math.Clamp(Vector3.Dot(point - centerline[i], direction), 0, Vector3.Distance(centerline[i], centerline[i + 1]));
            var onCenterline = centerline[i] + direction * along;
            if (Vector3.Dot(point - onCenterline, lateral) <= 0)
                throw new Exception("Footpath/kerb triangle crossed the route centreline or reached its wrong side.");
        }
    }
}

static float MaximumTriangleEdge(IReadOnlyList<RoadRibbonQuad> quads)
{
    var maximum = 0f;
    foreach (var quad in quads)
    {
        maximum = Math.Max(maximum, TriangleEdge(quad.StartLeft, quad.EndLeft, quad.StartRight));
        maximum = Math.Max(maximum, TriangleEdge(quad.StartRight, quad.EndLeft, quad.EndRight));
    }
    return maximum;

    static float TriangleEdge(Vector3 a, Vector3 b, Vector3 c) =>
        Math.Max(Vector3.Distance(a, b), Math.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
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

static void Equal<T>(T expected,T actual)
{
    if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}.");
}
