using System.Numerics;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Implementations;

var tests = new (string Name, Action Run)[]
{
    ("vegetation ground polygons do not cover mapped road asphalt", () =>
    {
        var area = new[]
        {
            new Vector3(-5, 0, -2), new Vector3(5, 0, -2), new Vector3(5, 0, 2),
            new Vector3(-5, 0, 2), new Vector3(-5, 0, -2)
        };
        var crossing = new TerrainRoadCorridor([new Vector3(0, 0, -10), new Vector3(0, 0, 10)], 6, 1234);
        Equal(1234L, VegetationGroundGeometry.FindOverlappingRoadId(area, [crossing])!.Value);
        var separated = new TerrainRoadCorridor([new Vector3(-10, 0, 4.4f), new Vector3(10, 0, 4.4f)], 4, 5678);
        if (VegetationGroundGeometry.FindOverlappingRoadId(area, [separated]).HasValue)
            throw new Exception("A land-use polygon was suppressed despite remaining outside the road edge.");
    }),
    ("camera inspection cycles all six views and only accepts unconsumed active-ride F6", () =>
    {
        var expected = new[]
        {
            RiderCameraView.GameplayRefined, RiderCameraView.CloseRear,
            RiderCameraView.RearThreeQuarter, RiderCameraView.Side,
            RiderCameraView.FrontThreeQuarter, RiderCameraView.GameplayOriginal
        };
        if (!RiderCameraViewCycle.Views.SequenceEqual(expected))
            throw new Exception("Camera selector and F6 cycle order differ.");
        var current = expected[0];
        foreach (var view in expected.Skip(1))
        {
            current = RiderCameraViewCycle.Next(current);
            Equal(view, current);
        }
        Equal(expected[0], RiderCameraViewCycle.Next(current));
        if (!CameraInspectionInputPolicy.CanCycle(true, false, true, false))
            throw new Exception("Active ride F6 should be handled while no menu is open.");
        if (CameraInspectionInputPolicy.CanCycle(false, false, true, false) ||
            CameraInspectionInputPolicy.CanCycle(true, true, true, false) ||
            CameraInspectionInputPolicy.CanCycle(true, false, false, false) ||
            CameraInspectionInputPolicy.CanCycle(true, false, true, true))
            throw new Exception("Inactive, modal, released or repeated F6 input should be ignored.");
    }),
    ("failed world loads are attempted once until route or ride recovery", () =>
    {
        var firstRoute = new object();
        var secondRoute = new object();
        var attempt = new RouteWorldLoadAttempt<object>();
        if (!attempt.TryBegin(firstRoute)) throw new Exception("Initial route load was not started.");
        Equal(RouteWorldLoadState.Loading, attempt.State);
        if (attempt.TryBegin(firstRoute)) throw new Exception("A loading route was retried.");
        attempt.MarkFailed(firstRoute, "test failure");
        if (attempt.TryBegin(firstRoute)) throw new Exception("A failed route was retried every frame.");
        Equal("test failure", attempt.Error);
        if (!attempt.TryBegin(secondRoute)) throw new Exception("Switching routes did not allow a new load.");
        attempt.MarkLoaded(secondRoute);
        if (attempt.TryBegin(secondRoute)) throw new Exception("A loaded route was rebuilt every frame.");
        attempt.Reset();
        if (!attempt.TryBegin(secondRoute)) throw new Exception("Starting a new ride did not permit an intentional retry.");
    }),
    ("installed Dunedin route packages register Signal Hill street materials and align buildings", () =>
    {
        var routeIds = new[]
        {
            "dunedin-one-way-oval-gardens", "dunedin-south-coastal-loop",
            "dunedin-rolling-loop", "dunedin-signal-hill-climbing-loop"
        };
        foreach (var id in routeIds)
        {
            Console.WriteLine($"CHECK scenery package {id}");
            var route = RouteCatalog.GetById(id) ?? throw new Exception($"Installed route {id} was not loaded.");
            var resource = $"Wodra.GameData.Routes.{id}.";
            var buildings = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                resource + "buildings.json", id);
            if (buildings.Count == 0) throw new Exception($"{id} has no installed building sidecar data.");
            foreach (var building in buildings)
            {
                if (building.FootprintElevationMeters.Count != building.Footprint.Count)
                    throw new Exception($"{id} building OSM {building.OsmId} is missing per-footprint DEM samples.");
                var samples = building.FootprintElevationMeters.Take(building.Footprint.Count - 1)
                    .Where(value => value.HasValue).Select(value => value!.Value).ToArray();
                if (samples.Length == 0 || samples.Any(value => !double.IsFinite(value)))
                    throw new Exception($"{id} building OSM {building.OsmId} has invalid footprint DEM samples.");
                if (Math.Abs(samples.Average() - building.BaseElevationMeters!.Value) > .05)
                    throw new Exception($"{id} building OSM {building.OsmId} has a base elevation inconsistent with its footprint DEM samples.");
            }
            Console.WriteLine($"CHECK {id} footprint samples={buildings.Sum(item => item.FootprintElevationMeters.Count)}");
            var nearRoadOffsets = buildings.Where(item => item.DistanceFromRouteMeters <= 5)
                .Select(item => Math.Abs(item.BaseElevationMeters!.Value
                    - route.GetSampleAt(item.NearestRouteDistanceMeters).ElevationMeters!.Value))
                .OrderBy(value => value).ToArray();
            var percentile95 = nearRoadOffsets.Length == 0 ? 0
                : nearRoadOffsets[(int)Math.Floor((nearRoadOffsets.Length - 1) * .95)];
            if (nearRoadOffsets.Length > 0 && percentile95 > 1.5)
                throw new Exception($"{id} building bases disagree with route terrain (95th percentile {percentile95:F2} m).");

            var vegetation = RouteVegetationDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
                resource + "vegetation.json", id);
            if (vegetation.Trees.Count == 0) throw new Exception($"{id} has no installed vegetation tree data.");
            var routeVisual = new VisualRoute(route);
            var routeIndex = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(routeVisual.Samples);
            foreach (var tree in vegetation.Trees)
            {
                var position = routeVisual.GetPositionForCoordinate(tree.Latitude, tree.Longitude, tree.ElevationMeters!.Value);
                var nearest = routeIndex.Nearest(position);
                var horizontalDistance = Math.Sqrt(nearest.DistanceSquared);
                var verticalDifference = Math.Abs(position.Y - nearest.Position.Y);
                if (horizontalDistance <= 20 && verticalDifference > 25)
                    throw new Exception($"{id} tree OSM {tree.OsmId} at route {tree.NearestRouteDistanceMeters:F1} m " +
                        $"is {verticalDifference:F1} m above/below nearby route terrain ({horizontalDistance:F1} m away).");
            }
            Console.WriteLine($"CHECK {id} trees={vegetation.Trees.Count}");
            foreach (var area in vegetation.Areas.Where(item => item.RenderGround))
            {
                if (area.ElevationVariationMeters > 3.01)
                    throw new Exception($"{id} vegetation area OSM {area.OsmId} at route {area.NearestRouteDistanceMeters:F1} m " +
                        $"renders flat across {area.ElevationVariationMeters:F1} m of sampled elevation variation.");
                var ring = area.Polygon.Select(point => routeVisual.GetPositionForCoordinate(
                    point[1], point[0], area.BaseElevationMeters!.Value)).ToArray();
                try { _ = BuildingMeshGeometry.TriangulateSurface(ring); }
                catch (ArgumentException ex)
                {
                    throw new Exception($"{id} vegetation area OSM {area.OsmId} at route {area.NearestRouteDistanceMeters:F1} m " +
                        $"has invalid render geometry: {ex.Message}");
                }
            }
            Console.WriteLine($"CHECK {id} render areas={vegetation.Areas.Count(item => item.RenderGround)}");

            var streets = RouteBackgroundStreetDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly,
                resource + "streets.json", id);
            if (streets is null || streets.Streets.Count == 0)
                throw new Exception($"{id} has no installed background-street sidecar data.");
            if (id == "dunedin-south-coastal-loop")
            {
                var mappedRoadsAtFiveKm = streets.Streets.Where(item => item.Highway is not ("footway" or "path")
                    && Math.Abs(item.NearestRouteDistanceMeters - 5000) <= 60 && item.DistanceFromRouteMeters <= 12).ToArray();
                if (mappedRoadsAtFiveKm.Length == 0)
                    throw new Exception("South Dunedin has no mapped asphalt road geometry within 60 m of its 5,000 m junction.");
            }
            var terrainData = RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly,
                resource + "terrain.json", id) ?? throw new Exception($"{id} has no installed terrain sidecar data.");
            var terrainRoads = BackgroundStreetGeometry.BuildTerrainCorridors(routeVisual, streets, routeIndex);
            var terrainMesh = TerrainMeshGeometry.Build(terrainData, routeVisual.Samples, terrainRoads);
            Console.WriteLine($"CHECK {id} terrain triangles={terrainMesh.TriangleCount}");
            var roadHalfWidth = RoadRibbonMath.RoadWidthMeters * .5f;
            var terrainIntrusions = 0;
            (double Distance, double Gap) worstTerrainIntrusion = (0, 0);
            foreach (var chunk in terrainMesh.Chunks)
            for (var vertex = 0; vertex + 2 < chunk.Vertices.Length; vertex += 3)
            {
                var center = (chunk.Vertices[vertex] + chunk.Vertices[vertex + 1] + chunk.Vertices[vertex + 2]) / 3;
                var nearest = routeIndex.Nearest(center);
                var lateral = Math.Sqrt(nearest.DistanceSquared);
                if (lateral >= roadHalfWidth - .2) continue;
                terrainIntrusions++;
                worstTerrainIntrusion = (nearest.DistanceMeters, roadHalfWidth - lateral);
            }
            if (terrainIntrusions > 0)
                throw new Exception($"{id} terrain mesh has {terrainIntrusions} triangle centroids inside route asphalt; " +
                    $"worst intrusion is {worstTerrainIntrusion.Gap:F2} m near route {worstTerrainIntrusion.Distance:F1} m.");
            var spatialIndex = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(routeVisual.Samples);
            if (id == "dunedin-signal-hill-climbing-loop")
            {
                var registry = new BackgroundStreetGeometry.MaterialChunkBucketRegistry<string, int[]>();
                var connectionCount = 0;
                foreach (var street in streets.Streets)
                {
                    var points = BackgroundStreetGeometry.ConvertPoints(routeVisual, street.Points);
                    var connected = BackgroundStreetGeometry.ConnectRouteEnds(points, routeVisual.Samples,
                        spatialIndex: spatialIndex);
                    foreach (var quad in BackgroundStreetGeometry.BuildRibbon(connected.Points, (float)street.WidthMeters))
                    {
                        if (BackgroundStreetGeometry.OverlapsRoute(quad, routeVisual.Samples,
                            (float)street.WidthMeters, spatialIndex: spatialIndex)) continue;
                        var center = (quad.StartLeft + quad.EndLeft + quad.StartRight + quad.EndRight) * .25f;
                        var chunk = BackgroundStreetGeometry.RouteChunkIndex(
                            BackgroundStreetGeometry.NearestRouteDistanceMeters(center, routeVisual.Samples, spatialIndex),
                            routeVisual.LengthMeters);
                        registry.GetOrAdd(chunk, BackgroundStreetGeometry.MaterialGroupForHighway(street.Highway), static () => new int[1])[0] += 2;
                    }
                    foreach (var connection in connected.Connections)
                    {
                        connectionCount++;
                        var chunk = BackgroundStreetGeometry.RouteChunkIndex(connection.RouteDistanceMeters, routeVisual.LengthMeters);
                        registry.GetOrAdd(chunk, "junction-asphalt", static () => new int[1])[0] += 16;
                    }
                }
                if (connectionCount == 0 || !registry.Items.Any(item => item.Key.Material == "junction-asphalt" && item.Value[0] > 0))
                    throw new Exception("Signal Hill route did not register junction asphalt triangle accounting.");
                foreach (var street in streets.Streets)
                    if (!registry.Items.Keys.Any(key => key.Material == BackgroundStreetGeometry.MaterialGroupForHighway(street.Highway)))
                        throw new Exception($"Signal Hill highway class {street.Highway} has no registered surface bucket.");
            }

            foreach (var street in streets.Streets.Take(12))
            foreach (var source in new[] { street.Points[0], street.Points[^1] })
            {
                var point = routeVisual.GetPositionForCoordinate(source.Latitude, source.Longitude, source.ElevationMeters);
                var indexed = spatialIndex.Nearest(point);
                var bestSquared = float.PositiveInfinity;
                for (var i = 0; i < routeVisual.Samples.Count - 1; i++)
                {
                    var a = routeVisual.Samples[i].Position;
                    var b = routeVisual.Samples[i + 1].Position;
                    var edge = new Vector2(b.X - a.X, b.Z - a.Z);
                    var lengthSquared = edge.LengthSquared();
                    var t = lengthSquared > 1e-8f
                        ? Math.Clamp(Vector2.Dot(new Vector2(point.X - a.X, point.Z - a.Z), edge) / lengthSquared, 0, 1)
                        : 0;
                    var projected = Vector3.Lerp(a, b, t);
                    bestSquared = Math.Min(bestSquared,
                        Vector2.DistanceSquared(new Vector2(point.X, point.Z), new Vector2(projected.X, projected.Z)));
                }
                if (Math.Abs(indexed.DistanceSquared - bestSquared) > .01f)
                    throw new Exception($"{id} spatial index differs from full route scan by {Math.Sqrt(Math.Abs(indexed.DistanceSquared - bestSquared)):F3} m.");
            }
        }

        var rolling = RouteCatalog.GetById("dunedin-rolling-loop")!;
        var rollingData = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-rolling-loop.buildings.json", rolling.Id);
        var near350 = rollingData.Single(item => item.OsmId == 381857663);
        var road350 = rolling.GetSampleAt(near350.NearestRouteDistanceMeters).ElevationMeters!.Value;
        if (Math.Abs(near350.BaseElevationMeters!.Value - road350) > 1.0)
            throw new Exception($"Rolling near-road building {near350.OsmId} is vertically displaced by {near350.BaseElevationMeters - road350:F2} m.");
    }),
    ("dummy W/kg uses body weight and simulation mass adds bike weight", () =>
    {
        Equal(150, DummyTrainerMath.WattsFromKilograms(2.0, 75));
        Equal(180, DummyTrainerMath.WattsFromKilograms(2.0, 90));
        Equal(528, DummyTrainerMath.WattsFromKilograms(6.0, 88));
        Equal(880, DummyTrainerMath.WattsFromKilograms(10.0, 88));
        Equal(1056, DummyTrainerMath.WattsFromKilograms(12.0, 88));
        Equal(1500, DummyTrainerMath.CalculateWatts("Watts", 1800, 12, 88));
        Equal(150, DummyTrainerMath.CalculateWatts("Watts", 150, 2.0, 75));
        Equal(150, DummyTrainerMath.CalculateWatts("Watts", 150, 2.0, 90));
        Equal(83.0, new RiderProfile("Test", 75, Guid.NewGuid(), 8).TotalMassKg);
        Equal(150, DummyTrainerMath.ValidateWatts(150));
        Equal(90, DummyTrainerMath.ValidateCadence(90));
    }),
    ("dummy trainer live controls update emitted power and cadence", () =>
    {
        var dummy = new FakeTrainerClient(); dummy.ConnectAsync().GetAwaiter().GetResult();
        dummy.SetDummyOutput(150, 85);
        var before = dummy.GetLatestTelemetry(); Equal(150, before.PowerWatts); Equal(85, before.CadenceRpm);
        dummy.SetDummyOutput(165, 90);
        var after = dummy.GetLatestTelemetry(); Equal(165, after.PowerWatts); Equal(90, after.CadenceRpm);
        dummy.SetDummyOutput(1800, 170);
        var capped = dummy.GetLatestTelemetry(); Equal(1500, capped.PowerWatts); Equal(160, capped.CadenceRpm);
    }),
    ("ghost trace interpolates recorded fields and rejects unsafe gaps", () =>
    {
        var trace = new GhostRideTrace([
            new GhostRideSample(0, 0, 2, 100, 70),
            new GhostRideSample(2, 20, 4, 200, 90)]);
        var sample = trace.SampleAt(1);
        NearD(10, sample.DistanceMeters); NearD(3, sample.SpeedMps!.Value);
        Equal(150, sample.PowerWatts!.Value); Equal(80, sample.CadenceRpm!.Value);
        var gap = new GhostRideTrace([new GhostRideSample(0, 0, null, null, null), new GhostRideSample(30, 300, null, null, null)]).SampleAt(15);
        NearD(0, gap.DistanceMeters);
    }),
    ("ghost source wraps distance, counts laps, freezes on pause and finishes", () =>
    {
        var trace = new GhostRideTrace([new GhostRideSample(0, 0, 3, null, null), new GhostRideSample(10, 120, 3, null, null)]);
        var source = new GhostRiderStateSource(trace, "test");
        source.Update(5, 50, true);
        var paused = source.GetRiders().Single(); NearD(60, paused.RouteDistanceMeters); NearD(10, paused.WrappedRouteDistanceMeters);
        if (!paused.Paused || paused.CadenceRpm != 0) throw new Exception("Paused ghost was not frozen.");
        source.Update(10, 50, false);
        var finished = source.GetRiders().Single(); Equal(3, finished.LapNumber); NearD(120, finished.RouteDistanceMeters);
        if (!source.Finished || !finished.Paused) throw new Exception("Finished ghost did not stop.");
    }),
    ("cars use the left lane in each permitted direction", () =>
    {
        var road = new AmbientRoadInput(10, "residential", false, 6.4, [Vector3.Zero, new Vector3(30,0,0)]);
        var paths = AmbientActorMath.BuildCarPaths([road]); Equal(2, paths.Count);
        var forward = paths.Single(path => path.Direction == 1).Points[0];
        var reverse = paths.Single(path => path.Direction == -1).Points[0];
        Near(0, forward.X); Near(-AmbientActorMath.MaximumLaneOffsetMeters, forward.Z);
        Near(30, reverse.X); Near(AmbientActorMath.MaximumLaneOffsetMeters, reverse.Z);
    }),
    ("one-way roads prohibit reverse car travel and ineligible paths are excluded", () =>
    {
        var oneWay = new AmbientRoadInput(1, "primary", true, 8, [Vector3.Zero, new Vector3(25,0,0)]);
        var footway = new AmbientRoadInput(2, "footway", false, 4, [Vector3.Zero, new Vector3(25,0,0)]);
        var paths = AmbientActorMath.BuildCarPaths([oneWay, footway]);
        Equal(1, paths.Count); Equal(1, paths[0].Direction); Equal(1L, paths[0].SourceId);
    }),
    ("car eligibility and category speeds match the initial road classes", () =>
    {
        foreach (var road in new[] { "trunk", "primary", "secondary", "tertiary", "residential", "unclassified" })
            if (!AmbientActorMath.IsEligibleRoad(road)) throw new Exception($"{road} should be eligible.");
        if (AmbientActorMath.IsEligibleRoad("service") || AmbientActorMath.IsEligibleRoad("cycleway")) throw new Exception("Non-car path was eligible.");
        var range = AmbientActorMath.CarSpeedRange("residential");
        Near(25 / 3.6f, range.MinimumMetersPerSecond); Near(40 / 3.6f, range.MaximumMetersPerSecond);
    }),
    ("cumulative polyline interpolation follows its selected segments", () =>
    {
        var path = new AmbientActorPath(AmbientActorKind.Car, 1, 1,
            [Vector3.Zero, new Vector3(3,0,0), new Vector3(3,0,4)], new AmbientSpeedRange(1,2));
        var sample = path.Sample(5.5);
        Near(3, sample.Position.X); Near(0, sample.Position.Y); Near(2.5f, sample.Position.Z);
        Near(1, sample.Heading.Length());
    }),
    ("car actor advances only along its selected street polyline", () =>
    {
        var path = new AmbientActorPath(AmbientActorKind.Car, 7, 1,
            [Vector3.Zero, new Vector3(10,0,0), new Vector3(10,0,10)], new AmbientSpeedRange(2,3));
        var start = path.Sample(5);
        var state = new AmbientActorState(4, AmbientActorKind.Car, start.Position, start.Heading, 2, true);
        var moved = AmbientActorMath.Advance(state, path, 5, 2, false);
        Near(9, moved.WorldPosition.X); Near(0, moved.WorldPosition.Z);
    }),
    ("actors despawn at a path end without jumping to another path", () =>
    {
        var path = new AmbientActorPath(AmbientActorKind.Car, 1, 1, [Vector3.Zero, new Vector3(5,0,0)], new AmbientSpeedRange(2,2));
        var state = new AmbientActorState(1, AmbientActorKind.Car, Vector3.Zero, Vector3.UnitX, 2, true);
        var ended = AmbientActorMath.Advance(state, path, 4, 1, false);
        if (ended.Active) throw new Exception("Actor should be inactive after reaching its path end.");
    }),
    ("spawn selection is deterministic with a fixed seed", () =>
    {
        AmbientActorPath[] paths = [new AmbientActorPath(AmbientActorKind.Car, 1, 1,
            [new Vector3(100,0,0), new Vector3(500,0,0)], new AmbientSpeedRange(1,2))];
        var a = AmbientActorMath.TrySelectSpawn(paths, Vector3.Zero, 50, 600, new Random(84721), 1, out var first);
        var b = AmbientActorMath.TrySelectSpawn(paths, Vector3.Zero, 50, 600, new Random(84721), 1, out var second);
        if (!a || !b || first != second) throw new Exception("Fixed seed did not reproduce a spawn selection.");
    }),
    ("spawning rejects a car inside its minimum rider distance", () =>
    {
        AmbientActorPath[] paths = [new AmbientActorPath(AmbientActorKind.Car, 1, 1,
            [new Vector3(0,0,0), new Vector3(10,0,0)], new AmbientSpeedRange(1,2))];
        if (AmbientActorMath.TrySelectSpawn(paths, Vector3.Zero, 25, 50, new Random(1), 20, out _))
            throw new Exception("A too-close car was selected for spawning.");
    }),
    ("pedestrian paths follow the footpath side beyond asphalt", () =>
    {
        var paths = AmbientActorMath.BuildFootpathPaths(StraightAmbientRoute(), 100);
        if (paths.Count < 4) throw new Exception("Expected both footpaths and walking directions.");
        foreach (var point in paths.SelectMany(path => path.Points))
        {
            var lateral = Math.Abs(point.Z);
            if (lateral < 3.7 || lateral > 6.1) throw new Exception($"Pedestrian is not on the footpath: {point}.");
        }
    }),
    ("pedestrians support forward and reverse walking on each footpath", () =>
    {
        var paths = AmbientActorMath.BuildFootpathPaths(StraightAmbientRoute(), 100);
        if (!paths.Any(path => path.Direction == 1) || !paths.Any(path => path.Direction == -1))
            throw new Exception("Both walking directions should be available.");
        foreach (var group in paths.GroupBy(path => path.SourceId))
            if (group.Count() != 2) throw new Exception("Each footpath side should have two directed paths.");
    }),
    ("walking speed bounds stay within the configured range", () =>
    {
        Near(1.1f, AmbientActorMath.PedestrianMinimumSpeedMetersPerSecond);
        Near(1.7f, AmbientActorMath.PedestrianMaximumSpeedMetersPerSecond);
        foreach (var path in AmbientActorMath.BuildFootpathPaths(StraightAmbientRoute(), 100))
        {
            if (path.SpeedRange.MinimumMetersPerSecond < 1.1f || path.SpeedRange.MaximumMetersPerSecond > 1.7f)
                throw new Exception("Walking speed range fell outside the configured bounds.");
        }
    }),
    ("pause freezes actor movement and resume advances it", () =>
    {
        var path = new AmbientActorPath(AmbientActorKind.Pedestrian, 1, 1,
            [Vector3.Zero, new Vector3(20,0,0)], new AmbientSpeedRange(1.4f,1.4f));
        var state = new AmbientActorState(1, AmbientActorKind.Pedestrian, Vector3.Zero, Vector3.UnitX, 1.4f, true);
        var paused = AmbientActorMath.Advance(state, path, 0, 2, true); if (paused != state) throw new Exception("Paused actor advanced.");
        var resumed = AmbientActorMath.Advance(state, path, 0, 2, false); Near(2.8f, resumed.WorldPosition.X);
    }),
    ("actors outside the world-space visibility radius are culled", () =>
    {
        if (AmbientActorMath.IsVisible(new Vector3(101,0,0), Vector3.Zero, 100)) throw new Exception("Distant actor remained visible.");
        if (!AmbientActorMath.IsVisible(new Vector3(99,0,0), Vector3.Zero, 100)) throw new Exception("Nearby actor was culled.");
    }),
    ("zero and short path segments never produce non-finite transforms", () =>
    {
        var path = new AmbientActorPath(AmbientActorKind.Car, 1, 1,
            [Vector3.Zero, Vector3.Zero, new Vector3(.001f,0,0), new Vector3(.001f,0,0)], new AmbientSpeedRange(1,2));
        var sample = path.Sample(.0005);
        if (!float.IsFinite(sample.Position.X + sample.Position.Y + sample.Position.Z + sample.Heading.X + sample.Heading.Y + sample.Heading.Z)
            || path.IsUsable) throw new Exception("Short path should be safely rejected without producing NaN values.");
        var tooShort = new AmbientActorPath(AmbientActorKind.Pedestrian, 2, 1, [Vector3.Zero, Vector3.Zero], new AmbientSpeedRange(1,2));
        if (tooShort.IsUsable) throw new Exception("Zero-length path was considered usable.");
    }),
    ("the bundled Dunedin streets produce only permitted car paths and safe footpaths", () =>
    {
        var route = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var bundle = RouteBackgroundStreetDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.streets.json", "dunedin-one-way-oval-gardens");
        var roads = bundle.Streets.Select(street => new AmbientRoadInput(street.OsmId, street.Highway, street.Oneway,
            street.WidthMeters, BackgroundStreetGeometry.ConvertPoints(route, street.Points))).ToArray();
        var cars = AmbientActorMath.BuildCarPaths(roads);
        var oneWayIds = bundle.Streets.Where(street => street.Oneway).Select(street => street.OsmId).ToHashSet();
        if (cars.Any(path => oneWayIds.Contains(path.SourceId) && path.Direction < 0))
            throw new Exception("Bundled one-way street produced reverse traffic.");
        var pedPaths = AmbientActorMath.BuildFootpathPaths(route.Samples.Select(sample =>
            new AmbientRouteSample(sample.DistanceMeters, sample.Position, sample.Forward)).ToArray(), route.LengthMeters);
        if (pedPaths.Count == 0) throw new Exception("Dunedin route produced no footpath paths.");
        var seam = route.Samples[0].Position;
        if (pedPaths.SelectMany(path => path.Points).Any(point => Vector3.Distance(point, seam) < 6))
            throw new Exception("Pedestrian path crossed the lap seam exclusion.");
    }),
    ("bundled ambient paths cover 600m and 1200m and deterministic spawning reaches targets", () =>
    {
        var route = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var bundle = RouteBackgroundStreetDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.streets.json", "dunedin-one-way-oval-gardens");
        var roads = bundle.Streets.Select(street => new AmbientRoadInput(street.OsmId, street.Highway,
            street.Oneway, street.WidthMeters, BackgroundStreetGeometry.ConvertPoints(route, street.Points), street.Name)).ToArray();
        var paths = AmbientActorMath.BuildCarPaths(roads);
        var eligible = roads.Where(road => AmbientActorMath.IsEligibleRoad(road.Highway)).ToArray();
        var categories = new[] { "trunk", "primary", "secondary", "tertiary", "residential", "unclassified" }
            .Select(category => $"{category}={roads.Count(road => road.Highway.Equals(category, StringComparison.OrdinalIgnoreCase))}");
        Console.WriteLine($"AMBIENT BUNDLE streets={roads.Length} eligible-polylines={eligible.Length} directed-paths={paths.Count} oneway={roads.Count(road => road.Oneway)} eligible-oneway={eligible.Count(road => road.Oneway)} classes[{string.Join(", ", categories)}]");
        foreach (var distance in new[] { 600d, 1200d, 3000d })
        {
            var pose = route.GetPoseAtDistance(distance);
            var lane = RiderLaneMath.OffsetPosition(pose.Position, pose.Forward);
            var nearest = paths.SelectMany(path => path.Points).Min(point => Vector3.Distance(point, lane));
            var within = eligible.Count(road => road.Points.Any(point => Vector3.Distance(point, lane) <= 300));
            var carSpawn = AmbientActorMath.TrySelectSpawn(paths, lane, 100,
                300, new Random(84721), 100, out var carSelection);
            var pedPaths = AmbientActorMath.BuildFootpathPaths(route.Samples.Select(sample =>
                new AmbientRouteSample(sample.DistanceMeters, sample.Position, sample.Forward)).ToArray(), route.LengthMeters,
                exclusionMeters: 6);
            var pedNearest = pedPaths.SelectMany(path => path.Points).Min(point => Vector3.Distance(point, lane));
            var pedSpawn = AmbientActorMath.TrySelectSpawn(pedPaths, lane,
                35, 160, new Random(84721), 100, out var pedestrianSelection);
            Console.WriteLine($"AMBIENT at={distance:F0}m rider-local={lane} nearest-car-path={nearest:F1}m nearby-streets={within} car-candidate={carSpawn} candidate-distance={(carSpawn ? Vector3.Distance(carSelection.Position, lane) : -1):F1}m nearest-footpath={pedNearest:F1}m pedestrian-candidate={pedSpawn} candidate-distance={(pedSpawn ? Vector3.Distance(pedestrianSelection.Position, lane) : -1):F1}m");
            if (within == 0 || !carSpawn || !pedSpawn) throw new Exception($"Ambient candidate missing at route distance {distance} m.");
        }
        var routePose = route.GetPoseAtDistance(600);
        var rider = RiderLaneMath.OffsetPosition(routePose.Position, routePose.Forward);
        var carRng = new Random(84721);
        var pedPathsAt600 = AmbientActorMath.BuildFootpathPaths(route.Samples.Select(sample =>
            new AmbientRouteSample(sample.DistanceMeters, sample.Position, sample.Forward)).ToArray(), route.LengthMeters,
            exclusionMeters: 6);
        Console.WriteLine($"AMBIENT PEDESTRIAN valid-directed-paths={pedPathsAt600.Count} valid-sections={pedPathsAt600.Count / 4} valid-directed-length={pedPathsAt600.Sum(path => path.LengthMeters):F0}m excluded-seam-samples={AmbientActorMath.CountFootpathSamplesExcludedNearSeams(route.Samples.Select(sample => new AmbientRouteSample(sample.DistanceMeters, sample.Position, sample.Forward)).ToArray(), route.LengthMeters)}");
        var carCount = 0; var pedestrianCount = 0;
        for (var i = 0; i < 15; i++)
            if (AmbientActorMath.TrySelectSpawn(paths, rider, 100, 300, carRng, Math.Min(100, paths.Count * 3), out _)) carCount++;
        for (var i = 0; i < 10; i++)
            if (AmbientActorMath.TrySelectSpawn(pedPathsAt600, rider, 35, 160, carRng, Math.Min(100, pedPathsAt600.Count * 3), out _)) pedestrianCount++;
        if (carCount == 0 || pedestrianCount == 0) throw new Exception("Deterministic Dunedin spawn simulation produced zero actors.");
        Console.WriteLine($"AMBIENT deterministic initial population at 600m cars={carCount}/15 pedestrians={pedestrianCount}/10");
    }),
    ("RiderState has no Godot or persistence assembly dependency", () =>
    {
        var references = typeof(RiderState).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? "").ToArray();
        if (references.Any(name => name.Contains("Godot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Persistence", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)))
            throw new Exception($"RiderState assembly has an engine or persistence dependency: {string.Join(",", references)}");
    }),
    ("AI target power produces finite speed and distance progression", () =>
    {
        var controller = new AiRiderController(new AiRiderProfile("Test", 190, 84, 75, .005, .32, 0, 1.7, 7.5));
        var player = TestRider(0, 0, 1000);
        controller.PlaceRelativeTo(player, 1000);
        controller.Update(1000, _ => 0, 30, false);
        var state = controller.State;
        if (!double.IsFinite(state.VirtualSpeedMps) || !double.IsFinite(state.RouteDistanceMeters)
            || state.VirtualSpeedMps <= 0 || state.RouteDistanceMeters <= 0)
            throw new Exception($"AI progression was invalid: {state}.");
    }),
    ("uphill grade slows an otherwise identical AI rider", () =>
    {
        var profile = new AiRiderProfile("Test", 190, 84, 75, .005, .32, 0, 1.7, 8);
        var flat = new AiRiderController(profile); var uphill = new AiRiderController(profile);
        var player = TestRider(0, 0, 1000); flat.PlaceRelativeTo(player, 1000); uphill.PlaceRelativeTo(player, 1000);
        flat.Update(1000, _ => 0, 15, false); uphill.Update(1000, _ => 8, 15, false);
        if (!(uphill.State.VirtualSpeedMps < flat.State.VirtualSpeedMps)) throw new Exception("Uphill AI was not slower than its flat comparison.");
    }),
    ("downhill grade increases AI speed compared with flat terrain", () =>
    {
        var profile = new AiRiderProfile("Test", 190, 84, 75, .005, .32, 0, 1.7, 8);
        var flat = new AiRiderController(profile); var downhill = new AiRiderController(profile);
        var player = TestRider(0, 0, 1000); flat.PlaceRelativeTo(player, 1000); downhill.PlaceRelativeTo(player, 1000);
        flat.Update(1000, _ => 0, 15, false); downhill.Update(1000, _ => -6, 15, false);
        if (!(downhill.State.VirtualSpeedMps > flat.State.VirtualSpeedMps)) throw new Exception("Downhill AI did not accelerate relative to flat terrain.");
    }),
    ("AI route position wraps and lap count increments at route length", () =>
    {
        var controller = new AiRiderController(new AiRiderProfile("Test", 200, 80, 75, .005, .32, 0, 1.7, 8));
        controller.PlaceRelativeTo(TestRider(98, 98, 100), 100);
        controller.Update(100, _ => 0, 1, false);
        if (controller.State.LapNumber != 2 || controller.State.WrappedRouteDistanceMeters >= 10)
            throw new Exception($"Expected second lap near route start, got {controller.State}.");
    }),
    ("paused AI freezes route distance, speed and simulation time", () =>
    {
        var controller = new AiRiderController(new AiRiderProfile("Test", 200, 80, 75, .005, .32, 0, 1.7, 8));
        controller.PlaceRelativeTo(TestRider(20, 20, 1000), 1000);
        var before = controller.State; controller.Update(1000, _ => 5, 10, true); var after = controller.State;
        Near((float)before.RouteDistanceMeters, (float)after.RouteDistanceMeters);
        Near((float)before.VirtualSpeedMps, (float)after.VirtualSpeedMps);
        Near((float)before.SimulationTimeSeconds, (float)after.SimulationTimeSeconds);
        if (!after.Paused) throw new Exception("Paused state was not exposed to the rider visual.");
    }),
    ("AI profiles are deterministic and stay within requested power cadence and mass ranges", () =>
    {
        var first = AiRiderPopulation.CreateProfiles(8, 84721); var second = AiRiderPopulation.CreateProfiles(8, 84721);
        if (!first.SequenceEqual(second)) throw new Exception("Fixed AI seed did not reproduce its profiles.");
        foreach (var profile in first)
            if (profile.TargetPowerWatts is < 120 or > 280 || profile.CadenceRpm is < 70 or > 95
                || profile.TotalMassKg is < 65 or > 90) throw new Exception($"AI profile outside requested range: {profile}.");
        if (!first.Any(profile => profile.TargetPowerWatts <= 160)
            || !first.Any(profile => profile.TargetPowerWatts is >= 170 and <= 220)
            || !first.Any(profile => profile.TargetPowerWatts >= 230)) throw new Exception("Expected easy/moderate/strong power groups.");
    }),
    ("AI riders spawn around player with separation and remain inside the left lane", () =>
    {
        var population = new AiRiderPopulation(seed: 84721);
        foreach (var profile in population.Profiles)
        {
            if (Math.Abs(profile.InitialRelativeDistanceMeters) < 25) throw new Exception("AI spawned too near the player.");
            if (profile.LateralOffsetMeters is < 1.2 or > 2.2) throw new Exception("AI lateral offset left the left-lane range.");
        }
        if (!population.Profiles.Any(profile => profile.InitialRelativeDistanceMeters > 0)
            || !population.Profiles.Any(profile => profile.InitialRelativeDistanceMeters < 0))
            throw new Exception("AI population must spawn both ahead of and behind the player.");
    }),
    ("overlap separation avoids exact visual stacking", () =>
    {
        var riders = new[] { TestRider(100, 100, 1000),
            TestRider(100, 100, 1000) with { RiderId = "ai-a", DisplayName = "A", LateralOffsetMeters = 1.75 },
            TestRider(100, 100, 1000) with { RiderId = "ai-b", DisplayName = "B", LateralOffsetMeters = 1.75 } };
        var offsets = AiRiderPopulation.ResolveVisualOverlapOffsets(riders, 1000);
        if (Math.Abs(offsets["ai-a"] - offsets["ai-b"]) < .001
            || Math.Abs(offsets["ai-a"] - offsets["player"]) < .001
            || Math.Abs(offsets["ai-b"] - offsets["player"]) < .001)
            throw new Exception("Overlapping rider visual offsets remained stacked.");
    }),
    ("relative rider distance chooses the short signed path across the lap seam", () =>
    {
        Near(10, (float)AiRiderPopulation.RelativeDistance(5, 995, 1000));
        Near(-10, (float)AiRiderPopulation.RelativeDistance(995, 5, 1000));
    }),
    ("rider visual mapping applies each independent state speed cadence and lane offset", () =>
    {
        var pose = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!).GetPoseAtDistance(600);
        var first = RiderVisualInputMath.Create(TestRider(600, 600, 5495) with
            { VirtualSpeedMps = 8, CadenceRpm = 72, LateralOffsetMeters = 1.3 }, pose, 2);
        var second = RiderVisualInputMath.Create(TestRider(600, 600, 5495) with
            { RiderId = "ai-b", VirtualSpeedMps = 11, CadenceRpm = 91, LateralOffsetMeters = 2.1, Paused = true }, pose, 2);
        Near(28.8f, (float)first.SpeedKph); Near(39.6f, (float)second.SpeedKph);
        if (first.CadenceRpm == second.CadenceRpm || first.Position == second.Position || !second.Paused)
            throw new Exception("Independent rider states did not produce independent visual inputs.");
    }),
    ("AI simulation does not mutate player RideState or change player physics results", () =>
    {
        var state = new RideState(10, 900, 20, 2, 25, 210, 84);
        var input = new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UnixEpoch, 210, 84, 25),
            2, .25, new RiderProfile("Player", 75), new BikeProfile("Bike", 9));
        var engine = new SimulationEngine(); var baseline = engine.Tick(state, input).NewState;
        var population = new AiRiderPopulation();
        population.Update(TestRider(state.DistanceMeters, 900, 5495), 5495, _ => 5, 20);
        var after = engine.Tick(state, input).NewState;
        if (state.DistanceMeters != 900 || state.VirtualSpeedKph != 25 || baseline != after)
            throw new Exception("AI update altered player state or player physics output.");
    }),
    ("local rider source returns the player and the configured AI population", () =>
    {
        var source = new LocalRiderStateSource(new AiRiderPopulation());
        source.Update(TestRider(1000, 1000, 5495), 5495, _ => 0, .25);
        var riders = source.GetRiders();
        if (riders.Count != 9 || riders[0].RiderId != "player" || riders.Count(rider => rider.RiderId != "player") != 8)
            throw new Exception("Source boundary did not expose player plus eight AI states.");
    }),
    ("60 rpm advances one crank revolution per second", () =>
    {
        Near((float)Math.Tau,(float)CyclistAnimationMath.CrankAngularSpeedRadiansPerSecond(60));
        Near((float)Math.PI,(float)CyclistAnimationMath.AdvanceCrankAngle(0,60,.5));
    }),
    ("90 and 110 rpm map directly to crank revolutions", () =>
    {
        Near((float)(Math.Tau * 1.5), (float)CyclistAnimationMath.CrankAngularSpeedRadiansPerSecond(90));
        Near((float)(Math.Tau * 110 / 60), (float)CyclistAnimationMath.CrankAngularSpeedRadiansPerSecond(110));
        Near((float)Math.PI, (float)CyclistAnimationMath.AdvanceCrankAngle(0, 90, 1));
    }),
    ("cadence changes smooth consistently across frame rates and stale cadence decays", () =>
    {
        var at30 = 0d; var at60 = 0d; var at144 = 0d;
        for (var i = 0; i < 30; i++) at30 = CyclistAnimationMath.SmoothCadence(at30, 90, 1d / 30);
        for (var i = 0; i < 60; i++) at60 = CyclistAnimationMath.SmoothCadence(at60, 90, 1d / 60);
        for (var i = 0; i < 144; i++) at144 = CyclistAnimationMath.SmoothCadence(at144, 90, 1d / 144);
        NearD(at30, at60, .0001); NearD(at60, at144, .0001);
        for (var i = 0; i < 120; i++) at60 = CyclistAnimationMath.SmoothCadence(at60, 0, 1d / 60);
        if (at60 != 0) throw new Exception("Stale cadence did not settle at stopped.");
    }),
    ("visual cadence freshness expires instead of retaining stale sensor values", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new TrainerTelemetry(now, 200, 90, 30);
        var stale = new TrainerTelemetry(now - TimeSpan.FromSeconds(6), 200, 90, 30);
        if (!fresh.IsFreshAt(now) || stale.IsFreshAt(now)) throw new Exception("Trainer freshness window was not applied.");
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
        var hip=new Vector2(.21f,1.0f);
        for(var i=0;i<360;i++)
        {
            var angle=i*Math.PI/180;
            var offset=CyclistAnimationMath.PedalOffset(angle,.17);
            var pedal=new Vector2(.1f+offset.X,.39f+offset.Y);
            var knee=CyclistAnimationMath.SolveKnee(hip,pedal,.43,.43,-1);
            if(!float.IsFinite(knee.X+knee.Y)) throw new Exception($"Invalid knee at {i} degrees.");
            NearD(.43,Vector2.Distance(hip,knee),.001);
            NearD(.43,Vector2.Distance(knee,pedal),.001);
            if(knee.X>=hip.X) throw new Exception($"Knee did not bend forward at {i} degrees.");
        }
    }),
    ("corner lean remains within ten degree visual clamp", () =>
    {
        var lean=CyclistAnimationMath.CornerLeanDegrees(-Vector3.UnitZ,Vector3.UnitX,.001,70);
        if(Math.Abs(lean)>CyclistAnimationMath.MaximumLeanDegrees) throw new Exception($"Lean exceeded clamp: {lean}.");
        Near(0,(float)CyclistAnimationMath.CornerLeanDegrees(-Vector3.UnitZ,Vector3.UnitX,0,30));
    }),
    ("crossing controls create perpendicular zebra bars across the ride road", () =>
    {
        var route = MarkingTestRoute(); var pose = route.GetPoseAtDistance(50);
        var result = RoadMarkingGeometry.Build(route, [MarkingControl("crossing", 1, 50, 0)]);
        var stripes = result.Triangles.Where(triangle => triangle.Kind == RoadMarkingKind.ZebraStripe).ToArray();
        Equal(1, result.ZebraCrossings); Equal(8, stripes.Length);
        var longEdge = LongestEdge(stripes[0]);
        if (Math.Abs(Vector3.Dot(Vector3.Normalize(longEdge), pose.Forward)) > .1f)
            throw new Exception("Zebra bar long axis is not perpendicular to route travel.");
    }),
    ("all bars in one zebra crossing keep a coherent orientation on a curved road", () =>
    {
        var route=MarkingTestRoute();
        var result=RoadMarkingGeometry.Build(route,[MarkingControl("crossing",101,150,0)]);
        var stripes=result.Triangles.Where(item=>item.Kind==RoadMarkingKind.ZebraStripe).ToArray();
        var first=Vector3.Normalize(LongestEdge(stripes[0]));
        foreach(var triangle in stripes)
            if(Math.Abs(Vector3.Dot(first,Vector3.Normalize(LongestEdge(triangle))))<.999f)
                throw new Exception("Zebra bars changed angle across the crossing on a route bend.");
    }),
    ("zebra stripe vertices stay within the configured road width", () =>
    {
        var route = MarkingTestRoute(); var result = RoadMarkingGeometry.Build(route, [MarkingControl("crossing", 2, 50, 0)]);
        foreach (var triangle in result.Triangles)
        {
            var pose = route.GetPoseAtDistance(triangle.RouteDistanceMeters);
            var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, pose.Forward));
            foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
                if (Math.Abs(Vector3.Dot(vertex - pose.Position, side)) > RoadMarkingGeometry.DefaultRoadWidthMeters / 2 - .19f)
                    throw new Exception("Zebra stripe extends beyond the asphalt width.");
        }
    }),
    ("nearby crossing nodes deduplicate by chainage and orientation", () =>
    {
        var result = RoadMarkingGeometry.Build(MarkingTestRoute(),
            [MarkingControl("crossing", 3, 100, 1), MarkingControl("crossing", 4, 104, 0)]);
        Equal(2, result.NearbyCrossingSources); Equal(1, result.ZebraCrossings);
        Equal(1, result.CrossingDuplicatesSuppressed);
    }),
    ("signal stop line is placed before its control point", () =>
    {
        var result = RoadMarkingGeometry.Build(MarkingTestRoute(), [MarkingControl("traffic-signal", 5, 100, 0)]);
        Equal(1, result.TrafficSignalLines);
        var triangle = result.Triangles.First(item => item.Kind == RoadMarkingKind.TrafficSignalLine);
        if (Math.Abs(100 - RoadMarkingGeometry.ControlLineApproachMeters - triangle.RouteDistanceMeters) > .001)
            throw new Exception("Signal line is not 4.5 m before its control point.");
    }),
    ("stop-line orientation follows the local route tangent on a curved route", () =>
    {
        var route = MarkingTestRoute(); var distance = 150d; var forward = route.GetPoseAtDistance(distance).Forward;
        var result = RoadMarkingGeometry.Build(route, [MarkingControl("stop-sign", 6, distance, 0)]);
        var triangle = result.Triangles.First(item => item.Kind == RoadMarkingKind.StopLine);
        if (Math.Abs(Vector3.Dot(Vector3.Normalize(LongestEdge(triangle)), forward)) > .1f)
            throw new Exception("Transverse stop line did not follow the local curved-route tangent.");
    }),
    ("give-way control emits a segmented transverse line", () =>
    {
        var result = RoadMarkingGeometry.Build(MarkingTestRoute(), [MarkingControl("give-way-sign", 7, 100, 0)]);
        Equal(1, result.GiveWayLines);
        Equal(16, result.Triangles.Count(item => item.Kind == RoadMarkingKind.GiveWayDash));
    }),
    ("distant crossing controls are excluded", () =>
    {
        var result = RoadMarkingGeometry.Build(MarkingTestRoute(), [MarkingControl("crossing", 8, 100, 8.01)]);
        Equal(1, result.CrossingSourcesConsidered); Equal(0, result.NearbyCrossingSources);
        Equal(0, result.ZebraCrossings); Equal(0, result.MarkingTriangleCount);
        var configured = RoadMarkingGeometry.Build(MarkingTestRoute(), [MarkingControl("crossing", 8, 100, 8.01)],
            options: new RoadMarkingOptions { MaximumCrossingDistanceFromRouteMeters = 8.1 });
        Equal(1, configured.ZebraCrossings);
    }),
    ("road marking triangles are finite and nondegenerate", () =>
    {
        var result = RoadMarkingGeometry.Build(MarkingTestRoute(),
            [MarkingControl("crossing", 9, 100, 0), MarkingControl("traffic-signal", 10, 150, 0),
             MarkingControl("stop-sign", 11, 220, 0), MarkingControl("give-way-sign", 12, 310, 0)]);
        foreach (var triangle in result.Triangles)
        {
            if (new[] { triangle.A, triangle.B, triangle.C }.Any(p => !float.IsFinite(p.X + p.Y + p.Z)))
                throw new Exception("Road marking has a non-finite vertex.");
            if (Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A).LengthSquared() < 1e-8f)
                throw new Exception("Road marking has a degenerate triangle.");
        }
    }),
    ("road marking paint has a small constant asphalt offset", () =>
    {
        var route = MarkingTestRoute(); var result = RoadMarkingGeometry.Build(route, [MarkingControl("crossing", 13, 100, 0)]);
        Near(RoadMarkingGeometry.RoadMarkingYOffsetMeters, result.Triangles[0].A.Y, .0001f);
    }),
    ("road marking generation is deterministic", () =>
    {
        var controls = new[] { MarkingControl("crossing", 14, 100, 0), MarkingControl("give-way-sign", 15, 200, 2) };
        var first = RoadMarkingGeometry.Build(MarkingTestRoute(), controls);
        var second = RoadMarkingGeometry.Build(MarkingTestRoute(), controls);
        if (!first.Triangles.SequenceEqual(second.Triangles))
            throw new Exception("Repeated marking generation returned different triangle geometry.");
        Equal(first.ZebraCrossings, second.ZebraCrossings);
    }),
    ("marking generation does not change rider lane placement", () =>
    {
        var route = MarkingTestRoute(); var pose = route.GetPoseAtDistance(100);
        var riderBefore = RiderLaneMath.OffsetPosition(pose.Position, pose.Forward);
        _ = RoadMarkingGeometry.Build(route, [MarkingControl("crossing", 16, 100, 0)]);
        var riderAfter = RiderLaneMath.OffsetPosition(route.GetPoseAtDistance(100).Position, route.GetPoseAtDistance(100).Forward);
        NearVector(riderBefore, riderAfter);
        Near((float)RiderLaneMath.RiderLateralOffsetMeters, Vector3.Distance(pose.Position, riderAfter));
    }),
    ("bundled Dunedin crossing and control markings are filtered and counted", () =>
    {
        var route = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var data = RouteStreetFurnitureDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.street-furniture.json", "dunedin-one-way-oval-gardens");
        var result = RoadMarkingGeometry.Build(route, data.Items.Select(item =>
            new RoadMarkingControl(item.Category, item.OsmId, item.NearestRouteDistanceMeters, item.DistanceFromRouteMeters)));
        var meshChunks = result.Triangles.Select(item => (int)(item.RouteDistanceMeters / RoadMarkingGeometry.SpatialChunkLengthMeters)).Distinct().Count();
        Console.WriteLine($"ROAD MARKING PREVIEW crossings={result.CrossingSourcesConsidered} considered, {result.NearbyCrossingSources} near, {result.ZebraCrossings} rendered, {result.CrossingDuplicatesSuppressed} deduplicated; signal={result.TrafficSignalLines}, stop={result.StopLines}, give-way={result.GiveWayLines}; triangles={result.MarkingTriangleCount}; meshes={meshChunks}, layer+meshes={meshChunks + 1}");
        Equal(194, result.CrossingSourcesConsidered);
        if (result.ZebraCrossings >= result.CrossingSourcesConsidered || result.ZebraCrossings == 0)
            throw new Exception("Dunedin crossings were not meaningfully filtered and deduplicated.");
        if (result.Triangles.Any(triangle => !double.IsFinite(triangle.RouteDistanceMeters)
            || triangle.RouteDistanceMeters < 0 || triangle.RouteDistanceMeters >= route.LengthMeters))
            throw new Exception("Bundled road marking references an invalid wrapped route distance.");
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
        var route = SyntheticTestRoute();
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
            if (route.Id != "dunedin-one-way-oval-gardens" && Vector3.Dot(before.Forward, after.Forward) < 0.99f)
                throw new Exception($"Direction seam: {route.Id}");
            for (double d = 0; d < route.DistanceMeters; d += 3.7)
            {
                var pose = geometry.GetPoseAtDistance(d);
                if (!float.IsFinite(pose.Position.X + pose.Position.Y + pose.Position.Z)) throw new Exception("Invalid position.");
                Near(1, pose.Forward.Length());
            }
        }
    }),
    ("Playable route catalogue excludes development fixtures and exposes stable route metadata", () =>
    {
        if (RouteCatalog.GetById("dunedin-one-way-oval-gardens") is null || RouteCatalog.GetById("test-route") is not null
            || RouteCatalog.GetById("flat-test-route") is not null || RouteCatalog.GetById("climb-test-route") is not null)
            throw new Exception("Development test routes must not be playable catalogue entries.");
        var route = RouteCatalog.Routes[0];
        if (route.Id != "dunedin-one-way-oval-gardens" || route.RouteType != "Loop"
            || route.Difficulty != "Easy" || route.GeometryFingerprint.Length != 64
            || string.IsNullOrWhiteSpace(route.SourceAttribution) || route.RouteDataVersion < 1)
            throw new Exception("The playable route is missing catalogue metadata.");
        NearD(18.165, route.TotalAscentMeters, 0.01);
        NearD(123, route.GetLoopDistance(route.DistanceMeters * 2 + 123));
        NearD(route.GetSampleAt(123).ElevationMeters!.Value,
            route.GetSampleAt(route.DistanceMeters * 2 + 123).ElevationMeters!.Value, 0.001);
        if (route.GeometryFingerprint != (route with { Name = "Renamed" }).GeometryFingerprint)
            throw new Exception("Route geometry fingerprint must not depend on display metadata.");
        var changedElevation = route with
        {
            Points = route.Points!.Select((point, index) => index == 1
                ? point with { ElevationMeters = point.ElevationMeters + 1 } : point).ToArray()
        };
        if (route.GeometryFingerprint == changedElevation.GeometryFingerprint)
            throw new Exception("Route fingerprint must change when the elevation profile changes.");
    }),
    ("external route packages load validated geometry and attribution without catalogue code changes", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "wodra-route-package-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "fixture-loop.route-points.json");
            File.WriteAllText(path, """
                {"id":"fixture-loop","name":"Fixture Loop","routeType":"loop","difficulty":"Easy","routeDataVersion":3,
                 "sourceAttribution":"© OpenStreetMap contributors, ODbL 1.0","isValid":true,"actualOsmDistanceMeters":20,
                 "points":[{"distanceMeters":0,"latitude":-45,"longitude":170,"elevationMeters":5,"rawElevationMeters":5,"gradientPercent":1},
                           {"distanceMeters":10,"latitude":-45.00001,"longitude":170,"elevationMeters":5.1,"rawElevationMeters":5.1,"gradientPercent":1},
                           {"distanceMeters":20,"latitude":-45,"longitude":170,"elevationMeters":5,"rawElevationMeters":5,"gradientPercent":-1}]}
                """);
            var route = RouteDataLoader.LoadPackage(path);
            if (route.Id != "fixture-loop" || route.DistanceMeters != 20 || route.RouteType != "Loop"
                || route.Difficulty != "Easy" || route.RouteDataVersion != 3
                || route.SourceAttribution != "© OpenStreetMap contributors, ODbL 1.0"
                || route.GeometryFingerprint.Length != 64)
                throw new Exception("External route package metadata was not loaded.");
        }
        finally { Directory.Delete(folder, true); }
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
        if (RoadRibbonMath.ClassifyJoin(geometry.Samples[^2].Position, geometry.Samples[^1].Position,
                geometry.Samples[1].Position) != RoadJoinType.Discontinuity)
            throw new Exception("The approved Dunedin closure is expected to retain generic sharp-join handling.");
        Near((float)length, (float)geometry.Vertices[^1].DistanceMeters, 0.001f);
    }),
    ("Dunedin loads the generated OSM centreline and exact route length", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var points = route.Points!;
        if (points.Count <= 16) throw new Exception($"Expected dense generated OSM points, found {points.Count}.");
        Near(5495.2928f, (float)route.DistanceMeters, 0.001f);
        Near((float)points[^1].DistanceMeters, (float)route.DistanceMeters, 0.0001f);
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.landmarks.json", route.Id);
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id);
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id)[0];
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
    ("missing building heights vary deterministically within conservative footprint bands", () =>
    {
        var first = BuildingVisualStyle.ResolveFallbackVisualHeight(901, "generic", 72, 8);
        Near(first, BuildingVisualStyle.ResolveFallbackVisualHeight(901, "generic", 72, 8));
        if (first is < 3.3f or > 4.8f) throw new Exception($"Small inferred building height was implausible: {first} m.");
        var variants = Enumerable.Range(1, 40)
            .Select(id => BuildingVisualStyle.ResolveFallbackVisualHeight(id, "generic", 72, 8)).Distinct().Count();
        if (variants < 10) throw new Exception("Fallback buildings did not receive meaningful deterministic height variation.");
        Near(3, BuildingVisualStyle.ResolveFallbackVisualHeight(901, "industrial/service", 72, 3));
    }),
    ("mapped roof tags win and unknown low-rise roofs receive a deterministic mix", () =>
    {
        Equal("hipped", BuildingVisualStyle.ResolveRoofShape(9, "hipped", "generic", "Fallback", 90, 8));
        var shapes = Enumerable.Range(1, 80)
            .Select(id => BuildingVisualStyle.ResolveRoofShape(id, null, "generic", "Fallback", 90, 8)).ToArray();
        Equal(true, shapes.Contains("flat")); Equal(true, shapes.Contains("gabled")); Equal(true, shapes.Contains("hipped"));
        Equal("flat", BuildingVisualStyle.ResolveRoofShape(9, null, "industrial/service", "Fallback", 800, 20));
        Equal("flat", BuildingVisualStyle.ResolveRoofShape(9, null, "generic", "Levels", 90, 8));
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
    ("sloped building footprints conform to terrain while retaining a level roofline", () =>
    {
        var slope = new[]
        {
            new Vector3(0, 0, 0), new Vector3(10, 1, 0), new Vector3(10, 3, 6),
            new Vector3(0, 2, 6), new Vector3(0, 0, 0)
        };
        var mesh = BuildingMeshGeometry.BuildStyled(slope, 8, "flat", 0, 2, false);
        if (mesh.Roof.Length == 0 || mesh.Roof.Max(point => point.Y) - mesh.Roof.Min(point => point.Y) > .001f)
            throw new Exception("A building on sloped terrain did not receive a level roof surface.");
        Near(9.5f, mesh.Roof[0].Y);
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
    ("facade window rhythms are stable but differ between mapped buildings", () =>
    {
        var ring = StyledRectangle(24, 12);
        var firstPattern = BuildingVisualStyle.FacadePattern(431);
        var first = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true, facadePattern: firstPattern);
        var repeat = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true, facadePattern: firstPattern);
        var other = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true,
            facadePattern: BuildingVisualStyle.FacadePattern(877));
        Equal(true, first.Windows.SequenceEqual(repeat.Windows));
        if (first.Windows.SequenceEqual(other.Windows))
            throw new Exception("Different OSM buildings received identical window geometry.");
    }),
    ("buildings beyond detail radius remain plain", () =>
    {
        Equal(true,BuildingVisualStyle.ShouldAddFacadeDetail(80));
        Equal(false,BuildingVisualStyle.ShouldAddFacadeDetail(80.01));
        Equal(false,BuildingVisualStyle.ShouldAddFacadeDetail(double.NaN));
    }),
    ("Dunedin building footprints, base elevations, and heights match regenerated bundle", () =>
    {
        var route=RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var buildings=RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json",route.Id)
            .OrderBy(b=>b.OsmType,StringComparer.Ordinal).ThenBy(b=>b.OsmId);
        using var buffer=new MemoryStream();using(var writer=new BinaryWriter(buffer,System.Text.Encoding.UTF8,true))
        foreach(var b in buildings)
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes(b.OsmType));writer.Write(b.OsmId);writer.Write(b.HeightMeters);writer.Write(b.BaseElevationMeters!.Value);
            foreach(var point in b.Footprint){writer.Write(point.Longitude);writer.Write(point.Latitude);}
        }
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        Equal("dbc60151dfc2a46d871544c15ef38984eee6fe22a427fe27c7379b2df88e248d",hash);
    }),
    ("bundled vegetation elevations and feature metadata load correctly", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var vegetation = RouteVegetationDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.vegetation.json", route.Id);
        if (vegetation.Areas.Count != 25 || vegetation.Trees.Count != 323)
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.street-furniture.json",route.Id);
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.street-furniture.json",route.Id).Items[0];
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.vegetation.json", route.Id);
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
        if (triangleCount != 228) throw new Exception($"Expected 228 vegetation triangles, got {triangleCount}.");
    }),
    ("all bundled OSM footprints can be batched into valid extrusion meshes", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var geometry = new VisualRoute(route);
        var buildings = RouteBuildingDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json", route.Id);
        foreach (var building in buildings)
        {
            var ring = building.Footprint.Select(point =>
            {
                var local = geometry.GetPositionForCoordinate(point.Latitude, point.Longitude, building.BaseElevationMeters!.Value);
                return local;
            }).ToArray();
            Vector3[] mesh;
            try { mesh = BuildingMeshGeometry.Extrude(ring, (float)building.HeightMeters); }
            catch (ArgumentException ex) { throw new Exception($"OSM {building.OsmType} {building.OsmId}: {ex.Message}", ex); }
            if (mesh.Length == 0 || mesh.Length % 3 != 0) throw new Exception($"OSM {building.OsmType} {building.OsmId} made an invalid mesh.");
        }
    }),
    ("Dunedin uses generated LINZ elevations and one shared route gradient", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        var segments = route.Segments;
        Equal("Crawford Street northbound", segments[0].Label);
        Equal("Gardens turnaround connector", segments[5].Label);
        Equal("Wolseley Street closure", segments[^1].Label);
        Near(2632.026f, (float)segments[4].EndMeters, 0.001f);
        Near(2752.237f, (float)segments[5].EndMeters, 0.001f);
        Near(5377.550f, (float)segments[^1].StartMeters, 0.001f);
        Near((float)route.DistanceMeters, (float)segments[^1].EndMeters, 0.001f);
        for (var i = 1; i < segments.Count; i++)
            Near((float)segments[i - 1].EndMeters, (float)segments[i].StartMeters, 0.001f);
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
    ("Dunedin asphalt ribbon sections meet cleanly at route joins and lap boundaries", () =>
    {
        var geometry = new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var samples = geometry.Samples;
        var quads = RoadRibbonMath.BuildQuads(samples.Select(sample => sample.Position).ToArray(),
            RoadRibbonMath.RoadWidthMeters);
        Equal(samples.Count - 1, quads.Count);
        AssertFiniteQuads(quads);
        AssertNondegenerateTriangles(quads);
        var discontinuities = 0;
        for (var i = 1; i < samples.Count - 1; i++)
        {
            var join = RoadRibbonMath.ClassifyJoin(samples[i - 1].Position, samples[i].Position, samples[i + 1].Position);
            if (join != RoadJoinType.Discontinuity)
            {
                NearVector(quads[i - 1].EndLeft, quads[i].StartLeft);
                NearVector(quads[i - 1].EndRight, quads[i].StartRight);
                continue;
            }
            discontinuities++;
            NearVector(samples[i].Position, (quads[i - 1].EndLeft + quads[i - 1].EndRight) * 0.5f);
            NearVector(samples[i].Position, (quads[i].StartLeft + quads[i].StartRight) * 0.5f);
            var fill = RoadRibbonMath.BuildSharpJoinFill(samples[i].Position,
                RoadRibbonMath.RoadWidthMeters * 0.5f + 0.05f);
            if (fill.Any(triangle => Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A).LengthSquared() < 1e-8f))
                throw new Exception($"Sharp asphalt join at route {samples[i].DistanceMeters:F0} m has a degenerate fill triangle.");
        }
        if (discontinuities == 0) throw new Exception("Expected to validate sharp street/intersection joins along Dunedin.");
        var seamJoin = RoadRibbonMath.ClassifyJoin(samples[^2].Position, samples[^1].Position, samples[1].Position);
        if (seamJoin != RoadJoinType.Discontinuity)
            throw new Exception("Expected the route closure to use the same explicit discontinuity join.");
        NearVector(samples[0].Position, (quads[^1].EndLeft + quads[^1].EndRight) * 0.5f);
        NearVector(samples[0].Position, (quads[0].StartLeft + quads[0].StartRight) * 0.5f);
    }),
    ("Cumberland sharp turn asphalt fill covers the outside corner wedge", () =>
    {
        var geometry=new VisualRoute(RouteCatalog.GetById("dunedin-one-way-oval-gardens")!);
        var index=Enumerable.Range(1,geometry.Samples.Count-2)
            .MinBy(i=>Math.Abs(geometry.Samples[i].DistanceMeters-5377.6));
        var sample=geometry.Samples[index];
        var previous=geometry.Samples[index-1].Position;var next=geometry.Samples[index+1].Position;
        Equal(RoadJoinType.Discontinuity,RoadRibbonMath.ClassifyJoin(previous,sample.Position,next));
        var turn=Vector3.Dot(Vector3.Normalize(sample.Position-previous),Vector3.Normalize(next-sample.Position));
        Near(0,turn,.03f);
        var radius=RoadRibbonMath.SharpJoinFillRadius(previous,sample.Position,next,RoadRibbonMath.RoadWidthMeters);
        if(radius<RoadRibbonMath.RoadWidthMeters*.5f*MathF.Sqrt(2)*1.05f)
            throw new Exception($"Sharp turn fill radius {radius:F2} m leaves the outer asphalt corner exposed.");
        var fill=RoadRibbonMath.BuildSharpJoinFill(sample.Position,radius);
        Equal(16,fill.Count);
        if(fill.Any(triangle=>Vector3.Cross(triangle.B-triangle.A,triangle.C-triangle.A).LengthSquared()<1e-8f))
            throw new Exception("Cumberland turn fill has malformed triangles.");
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
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.buildings.json",route.Id);
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
        if(buildings.Count!=902 || detailed==0 || gabled+hipped==0 || triangles>100000)
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
        for (var i = 1; i < distances.Length; i++)
            if (distances[i] <= distances[i - 1])
                throw new Exception($"Non-increasing VisualRoute sample distance at {i - 1}->{i}: {distances[i - 1]:R} -> {distances[i]:R}; positions {positions[i - 1]} -> {positions[i]}.");
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
    ("bundled Dunedin side-street endpoints resolve to deterministic route junctions", () =>
    {
        const string id="dunedin-one-way-oval-gardens";
        var route=new VisualRoute(RouteCatalog.GetById(id)!);
        var data=RouteBackgroundStreetDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.streets.json",id);
        var openings=BackgroundStreetGeometry.BuildJunctionOpenings(route,data);
        var restored=data.Streets.SelectMany(street=>BackgroundStreetGeometry.ConnectRouteEnds(
            BackgroundStreetGeometry.ConvertPoints(route,street.Points),route.Samples).Connections
            .Select(connection=>(street.OsmId,street.Name,connection.RouteDistanceMeters,connection.DistanceMeters))).ToArray();
        Console.WriteLine($"DUNEDIN JUNCTIONS roads={data.Streets.Count} openings={openings.Count} near2150={openings.Count(o=>Math.Abs(o.RouteDistanceMeters-2150)<50)} near5350={openings.Count(o=>Math.Abs(o.RouteDistanceMeters-5350)<50)} restored2150={restored.Count(c=>Math.Abs(c.RouteDistanceMeters-2150)<50)} restored5350={restored.Count(c=>Math.Abs(c.RouteDistanceMeters-5350)<50)}");
        Console.WriteLine($"DUNEDIN RESTORED near5350: {string.Join(", ",restored.Where(c=>Math.Abs(c.RouteDistanceMeters-5350)<50).Select(c=>$"{c.OsmId}/{c.Name ?? "unnamed"}:{c.DistanceMeters:F1}m"))}");
        Console.WriteLine($"DUNEDIN RESTORED near2150: {string.Join(", ",restored.Where(c=>Math.Abs(c.RouteDistanceMeters-2150)<50).Select(c=>$"{c.OsmId}/{c.Name ?? "unnamed"}:{c.DistanceMeters:F1}m"))}");
        if(openings.Count!=39)throw new Exception($"Expected 39 locally plausible OSM route joins after eliminating long false snaps, got {openings.Count}.");
        if(openings.Any(opening=>Math.Abs(opening.RouteDistanceMeters-2150)<50||Math.Abs(opening.RouteDistanceMeters-5350)<50))
            throw new Exception("A nearby but spatially separate street was falsely pulled into the route at a reported defect location.");
        if(!openings.SequenceEqual(BackgroundStreetGeometry.BuildJunctionOpenings(route,data)))
            throw new Exception("Mapped street junction placement is not deterministic.");
    }),
    ("Malcolm-area Walsh Street is classified by local geometry rather than stale overlap metadata", () =>
    {
        const string id="dunedin-one-way-oval-gardens";
        var route=new VisualRoute(RouteCatalog.GetById(id)!);
        var data=RouteBackgroundStreetDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.streets.json",id);
        var walsh=data.Streets.Single(street=>street.OsmId==1548516865);
        var points=BackgroundStreetGeometry.ConnectRouteEnds(
            BackgroundStreetGeometry.ConvertPoints(route,walsh.Points),route.Samples).Points;
        var quads=BackgroundStreetGeometry.BuildRibbon(points,(float)walsh.WidthMeters);
        if(quads.Any(quad=>BackgroundStreetGeometry.OverlapsRoute(quad,route.Samples,(float)walsh.WidthMeters)))
            throw new Exception("Walsh Street is separated from the route in local geometry and must not be treated as a duplicate route surface.");
        if(walsh.DistanceFromRouteMeters!=0)
            throw new Exception("Expected the bundled zero-distance metadata case used by this regression.");
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
    ("terrain cell is cut out when a diagonal road crosses between unadjusted grid vertices", () =>
    {
        var data = SyntheticTerrain(1, 1, [110, 110, 110, 110], [true]);
        var route = new[]
        {
            new VisualRouteSample(0, new Vector3(0, 0, 10), Vector3.UnitX),
            new VisualRouteSample(15, new Vector3(10, 0, 0), Vector3.UnitX)
        };
        var built = TerrainMeshGeometry.Build(data, route);
        Equal(1, built.RoadCutoutCellCount);
        Equal(0, built.TriangleCount);
    }),
    ("near terrain cells are excluded around mapped background streets", () =>
    {
        var data=SyntheticTerrain(1,1,[110,110,110,110],[true]);
        var route=new[]{new VisualRouteSample(0,new Vector3(100,0,100),Vector3.UnitX),new VisualRouteSample(20,new Vector3(120,0,100),Vector3.UnitX)};
        var road=new TerrainRoadCorridor(new[]{new Vector3(5,0,0),new Vector3(5,0,10)},6);
        var built=TerrainMeshGeometry.Build(data,route,[road]);
        Equal(1,built.RoadCutoutCellCount);Equal(0,built.TriangleCount);
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
    ("bundled Dunedin terrain does not rise above the asphalt anywhere along the lap", () =>
    {
        const float ProceduralRoadHalfWidthMeters = RoadRibbonMath.RoadWidthMeters * 0.5f;
        const string routeId = "dunedin-one-way-oval-gardens";
        var definition = RouteCatalog.GetById(routeId)!;
        var route = new VisualRoute(definition);
        var data = RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly,
            "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.terrain.json", routeId)!;
        var built = TerrainMeshGeometry.Build(data, route.Samples);
        var intrusions = 0;
        var worstExcess = 0f;
        var worstRouteDistance = 0d;
        foreach (var chunk in built.Chunks.Where(chunk => !chunk.Id.StartsWith("far-", StringComparison.Ordinal)))
        for (var i = 0; i < chunk.Indices.Length; i += 3)
        {
            var a = chunk.Vertices[chunk.Indices[i]];
            var b = chunk.Vertices[chunk.Indices[i + 1]];
            var c = chunk.Vertices[chunk.Indices[i + 2]];
            var centroid = (a + b + c) / 3;
            var nearest = NearestRoutePoint(centroid, route.Samples);
            if (nearest.DistanceMeters > ProceduralRoadHalfWidthMeters) continue;
            var excess = centroid.Y - nearest.RoadY;
            if (excess <= -0.03f) continue;
            intrusions++;
            if (excess > worstExcess) { worstExcess = excess; worstRouteDistance = nearest.RouteDistanceMeters; }
        }
        if (intrusions > 0)
            throw new Exception($"Found {intrusions} terrain triangle centroids inside asphalt; worst is {worstExcess:F2} m above road near route {worstRouteDistance:F0} m.");
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
        const string resource = "Wodra.GameData.Routes.dunedin-one-way-oval-gardens.terrain.json";
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
        Equal(45, a.Chunks.Length);
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
        if (generated.RoadCutoutCellCount <= 0 || generated.TriangleCount >= a.TriangleCount)
            throw new Exception("The real terrain bundle did not remove cells from its near-road corridor.");
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
    ("open background street ribbon uses endpoint tangents without wrapping across the way", () =>
    {
        var points=new[]{new Vector3(0,0,0),new Vector3(10,0,0),new Vector3(20,0,5)};
        var quads=BackgroundStreetGeometry.BuildRibbon(points,6);
        Equal(2,quads.Count);Near(-3,quads[0].StartLeft.Z);Near(3,quads[0].StartRight.Z);
        foreach(var point in quads.SelectMany(q=>new[]{q.StartLeft,q.EndLeft,q.StartRight,q.EndRight}))
            if(Math.Abs(point.Z)>9)throw new Exception("Open street ribbon used a wrapped endpoint tangent and created a spike.");
    }),
    ("side street endpoints restore a plausible connection to the route", () =>
    {
        var route=new[]{new VisualRouteSample(0,new Vector3(0,0,0),Vector3.UnitX),new VisualRouteSample(20,new Vector3(20,0,0),Vector3.UnitX)};
        var connected=BackgroundStreetGeometry.ConnectRouteEnds(new[]{new Vector3(10,0,15),new Vector3(10,0,7)},route);
        Equal(1,connected.Connections.Count);Near(0,connected.Points[^1].Z);Near(10,connected.Points[^1].X);
        var parallel=BackgroundStreetGeometry.ConnectRouteEnds(new[]{new Vector3(12,0,7),new Vector3(18,0,7)},route);
        Equal(0,parallel.Connections.Count);
    }),
    ("terrain cutouts follow the same restored endpoint used by street rendering", () =>
    {
        const string id="dunedin-one-way-oval-gardens";
        var definition=RouteCatalog.GetById(id)!;var route=new VisualRoute(definition);var origin=definition.Points![0];
        var street=new RouteBackgroundStreet(9,"test",null,"residential",false,6,"test",
            [new RouteBackgroundStreetPoint(origin.Latitude-.00015,origin.Longitude,origin.ElevationMeters),
             new RouteBackgroundStreetPoint(origin.Latitude-.00006,origin.Longitude,origin.ElevationMeters)],0,0);
        var corridors=BackgroundStreetGeometry.BuildTerrainCorridors(route,new RouteBackgroundStreetData(id,[street]));
        Equal(1,corridors.Length);
        var rendered=BackgroundStreetGeometry.ConnectRouteEnds(
            BackgroundStreetGeometry.ConvertPoints(route,street.Points),route.Samples);
        if(!corridors[0].Points.SequenceEqual(rendered.Points))
            throw new Exception("Terrain road cutout ended at pre-extension points instead of the rendered route connection.");
    }),
    ("mapped street sections duplicating the route are excluded while crossing streets remain", () =>
    {
        var route=new[]{new VisualRouteSample(0,new Vector3(0,0,0),Vector3.UnitX),
            new VisualRouteSample(20,new Vector3(20,0,0),Vector3.UnitX)};
        var parallel=BackgroundStreetGeometry.BuildRibbon([new Vector3(2,0,1),new Vector3(18,0,1)],6).Single();
        var crossing=BackgroundStreetGeometry.BuildRibbon([new Vector3(10,0,6),new Vector3(10,0,0)],6).Single();
        Equal(true,BackgroundStreetGeometry.OverlapsRoute(parallel,route,6));
        Equal(false,BackgroundStreetGeometry.OverlapsRoute(crossing,route,6));
    }),
    ("background street segment chunk assignment follows local geometry deterministically", () =>
    {
        var route=new[]{new VisualRouteSample(0,new Vector3(0,0,0),Vector3.UnitX),new VisualRouteSample(1200,new Vector3(1200,0,0),Vector3.UnitX)};
        var road=new[]{new Vector3(100,0,20),new Vector3(300,0,20),new Vector3(500,0,20),new Vector3(700,0,20)};
        var quads=BackgroundStreetGeometry.BuildRibbon(road,6);
        var chunks=quads.Select(q=>BackgroundStreetGeometry.RouteChunkIndex(
            BackgroundStreetGeometry.NearestRouteDistanceMeters((q.StartLeft+q.EndLeft+q.StartRight+q.EndRight)*.25f,route),1200)).ToArray();
        if(!chunks.SequenceEqual(new[]{0,1,1}))throw new Exception($"Street segments were assigned to unexpected route chunks: {string.Join(',',chunks)}.");
        if(!chunks.SequenceEqual(quads.Select(q=>BackgroundStreetGeometry.RouteChunkIndex(
            BackgroundStreetGeometry.NearestRouteDistanceMeters((q.StartLeft+q.EndLeft+q.StartRight+q.EndRight)*.25f,route),1200))))
            throw new Exception("Street chunk assignment changed on a repeated build.");
    }),
    ("mapped side streets open route kerbs and footpaths including across the lap seam", () =>
    {
        var openings=new[]{new RoadJunctionOpening(100,5),new RoadJunctionOpening(0,6)};
        var quad=new RoadProfileRibbonQuad(0,90,110,new RoadRibbonQuad(new Vector3(0,1,0),new Vector3(20,1,0),new Vector3(0,2,0),new Vector3(20,2,0)));
        var pieces=RoadRibbonMath.ClipProfileQuadAtJunctions(quad,1000,openings);
        Equal(2,pieces.Count);Near(90,(float)pieces[0].StartDistanceMeters);Near(95,(float)pieces[0].EndDistanceMeters);
        Near(105,(float)pieces[1].StartDistanceMeters);Near(110,(float)pieces[1].EndDistanceMeters);
        var seam=new RoadProfileRibbonQuad(0,995,1000,new RoadRibbonQuad(new Vector3(0,1,0),new Vector3(5,1,0),new Vector3(0,2,0),new Vector3(5,2,0)));
        Equal(0,RoadRibbonMath.ClipProfileQuadAtJunctions(seam,1000,openings).Count);
        Equal(1,RoadRibbonMath.ClipProfileQuadAtJunctions(quad with { StartDistanceMeters=200,EndDistanceMeters=210 },1000,openings).Count);
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
    ("Dunedin authoritative route excludes the Vogel Street grass-crossing edge", () =>
    {
        var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens")!;
        const double originLatitude = -45.8832;
        const double originLongitude = 170.501;
        const double scaleX = 111320 * 0.6967;
        const double scaleY = 111132;
        Vector2 Local(double latitude, double longitude) => new(
            (float)((longitude - originLongitude) * scaleX), (float)((latitude - originLatitude) * scaleY));
        var a = Local(-45.8837408, 170.5008915); // node 14158950981
        var b = Local(-45.8828212, 170.5014018); // node 5802260307
        var edge = b - a;
        var minimumDistance = double.PositiveInfinity;
        foreach (var point in route.Points!)
        {
            var p = Local(point.Latitude, point.Longitude);
            var fraction = edge.LengthSquared() > 1e-8f
                ? Math.Clamp(Vector2.Dot(p - a, edge) / edge.LengthSquared(), 0, 1) : 0;
            minimumDistance = Math.Min(minimumDistance, Vector2.Distance(p, a + edge * fraction));
        }
        if (minimumDistance < 5)
            throw new Exception($"Authoritative route still follows the excluded Vogel edge (nearest route sample {minimumDistance:F1} m).");
    }),
    ("chase camera follow and look targets use the offset rider anchor", () =>
    {
        var rider=RiderLaneMath.OffsetPosition(new Vector3(10,3,20),new Vector3(0,0,-1));
        NearVector(rider-new Vector3(0,0,-1)*8+Vector3.UnitY*4,ChaseCameraMath.FollowPosition(rider,new Vector3(0,0,-1)));
        NearVector(rider+new Vector3(0,0,-1)*6+Vector3.UnitY,ChaseCameraMath.LookTarget(rider,new Vector3(0,0,-1)));
    }),
    ("rider camera inspection views retain original framing and frame full bike", () =>
    {
        var rider = new Vector3(0, 0, 0);
        var forward = new Vector3(0, 0, -1);
        var original = ChaseCameraMath.FollowPosition(rider, forward, RiderCameraView.GameplayOriginal);
        var refined = ChaseCameraMath.FollowPosition(rider, forward, RiderCameraView.GameplayRefined);
        var close = ChaseCameraMath.FollowPosition(rider, forward, RiderCameraView.CloseRear);
        var side = ChaseCameraMath.FollowPosition(rider, forward, RiderCameraView.Side);
        var front = ChaseCameraMath.FollowPosition(rider, forward, RiderCameraView.FrontThreeQuarter);
        NearVector(original, ChaseCameraMath.FollowPosition(rider, forward));
        if (refined.Length() >= original.Length() || close.Length() >= refined.Length())
            throw new Exception("Refined and close views must frame the rider nearer than original gameplay.");
        if (Math.Abs(side.X) < 3.9f || Math.Abs(side.Z) > .3f)
            throw new Exception("Side view should sit beside the complete bicycle and rider.");
        if (front.Z >= 0)
            throw new Exception("Front three-quarter view should move ahead of the rider.");
        foreach (var view in Enum.GetValues<RiderCameraView>())
        {
            var look = ChaseCameraMath.LookTarget(rider, forward, view);
            if (Vector3.Distance(ChaseCameraMath.FollowPosition(rider, forward, view), look) < 1)
                throw new Exception($"Camera view {view} has an unusably short look vector.");
        }
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

static RiderState TestRider(double routeDistance, double wrappedDistance, double routeLength) =>
    new("player", "You", "test-route", routeDistance, ((wrappedDistance % routeLength) + routeLength) % routeLength,
        (int)Math.Floor(routeDistance / routeLength) + 1, 1.75, 8, 180, 80, false, 0);

static AmbientRouteSample[] StraightAmbientRoute() =>
[
    new AmbientRouteSample(0, new Vector3(0,0,0), Vector3.UnitX),
    new AmbientRouteSample(50, new Vector3(50,0,0), Vector3.UnitX),
    new AmbientRouteSample(100, new Vector3(100,0,0), Vector3.UnitX)
];

static VisualRoute MarkingTestRoute()
{
    var source = GpsRoute();
    return new VisualRoute(source with { Points = source.Points!.Select(point => point with { ElevationMeters = 20 }).ToArray() });
}

static RoadMarkingControl MarkingControl(string category, long id, double routeDistance, double distanceFromRoute) =>
    new(category, id, routeDistance, distanceFromRoute);

static Vector3 LongestEdge(RoadMarkingTriangle triangle)
{
    var edges = new[] { triangle.B - triangle.A, triangle.C - triangle.B, triangle.A - triangle.C };
    return edges.MaxBy(edge => edge.LengthSquared());
}

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

static (double DistanceMeters, float RoadY, double RouteDistanceMeters) NearestRoutePoint(
    Vector3 point, IReadOnlyList<VisualRouteSample> samples)
{
    var bestDistanceSquared = double.PositiveInfinity;
    var roadY = 0f;
    var routeDistance = 0d;
    for (var i = 0; i < samples.Count - 1; i++)
    {
        var a = samples[i]; var b = samples[i + 1];
        var dx = b.Position.X - a.Position.X; var dz = b.Position.Z - a.Position.Z;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared > 1e-8f
            ? Math.Clamp(((point.X - a.Position.X) * dx + (point.Z - a.Position.Z) * dz) / lengthSquared, 0, 1)
            : 0;
        var x = a.Position.X + dx * t; var z = a.Position.Z + dz * t;
        var distanceSquared = (point.X - x) * (point.X - x) + (point.Z - z) * (point.Z - z);
        if (distanceSquared >= bestDistanceSquared) continue;
        bestDistanceSquared = distanceSquared;
        roadY = a.Position.Y + (b.Position.Y - a.Position.Y) * t;
        routeDistance = a.DistanceMeters + (b.DistanceMeters - a.DistanceMeters) * t;
    }
    return (Math.Sqrt(bestDistanceSquared), roadY, routeDistance);
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

static RouteDefinition SyntheticTestRoute() => new("test-route-fixture", "Test Route Fixture", "test-only", 3500,
    [new RouteSegment(0, 500, 0, "Warm-up flat"), new RouteSegment(500, 1500, 3, "Gentle climb"),
        new RouteSegment(1500, 2500, 6, "Main climb"), new RouteSegment(2500, 3500, -2, "Descent")],
    [new RoutePoint(0, -45.8640, 170.5140, 100), new RoutePoint(500, -45.8638, 170.5180, 100),
        new RoutePoint(1500, -45.8620, 170.5240, 130), new RoutePoint(2500, -45.8600, 170.5300, 190),
        new RoutePoint(3500, -45.8640, 170.5140, 170)]);

static void NearD(double expected, double actual, double tolerance = 0.001)
{
    if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}.");
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
