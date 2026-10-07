using System.Text.Json;
using GWift.RouteBuilder;

var tests = new (string Name, Action Run)[]
{
    ("OSM street lamps extract with compact name and direction metadata", () =>
    {
        var lamp = StreetFurnitureExtractor.TryCreateCandidate("node", 10, new GeoPoint(0, .0005),
            new Dictionary<string,string> {{"highway","street_lamp"},{"direction","450"},{"name","Lamp"},{"operator","City"},{"unrelated","discard"}})!;
        Equal("street-lamp",lamp.Category); Equal(90d,lamp.DirectionDegrees!.Value); Equal("Lamp",lamp.Name);
        Equal(false,lamp.Tags.ContainsKey("unrelated"));
        Equal("street-lamp",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"man_made","street_lamp"}}));
    }),
    ("OSM traffic signals extract as a mapped roadside category", () =>
    {
        Equal("traffic-signal",StreetFurnitureExtractor.TryCreateCandidate("node",11,new GeoPoint(0,.0005),
            new Dictionary<string,string>{{"highway","traffic_signals"}})?.Category);
    }),
    ("OSM stop and give-way signs remain distinct", () =>
    {
        Equal("stop-sign",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"highway","stop"}}));
        Equal("give-way-sign",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"highway","give_way"}}));
    }),
    ("OSM bollards extract without accepting generic barriers", () =>
    {
        Equal("bollard",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"barrier","bollard"}}));
        Equal<string?>(null,StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"barrier","gate"}}));
    }),
    ("street furniture search radius is configurable and filters by nearest route", () =>
    {
        var items = new[]
        {
            StreetFurnitureExtractor.TryCreateCandidate("node",1,new GeoPoint(.0001,.0005),new Dictionary<string,string>{{"highway","street_lamp"}})!,
            StreetFurnitureExtractor.TryCreateCandidate("node",2,new GeoPoint(.001,.0005),new Dictionary<string,string>{{"amenity","bench"}})!
        };
        var selected = StreetFurnitureExtractor.SelectByRoute(items,LandmarkTestRoute(),30);
        Equal(2,selected.TotalCandidates); Equal(1,selected.Items.Count); Equal(1L,selected.Items[0].OsmId);
    }),
    ("street furniture records nearest route distance, chainage, and tangent", () =>
    {
        var item=StreetFurnitureExtractor.TryCreateCandidate("node",3,new GeoPoint(.0001,.001),new Dictionary<string,string>{{"highway","traffic_signals"}})!;
        var selected=StreetFurnitureExtractor.SelectByRoute([item],LandmarkTestRoute(),50).Items.Single();
        Near(100,selected.NearestRouteDistanceMeters,.02); Near(11.1,selected.DistanceFromRouteMeters,1); Near(90,selected.RouteTangentDegrees,.1);
    }),
    ("street furniture reports features placed inside the generated road ribbon", () =>
    {
        var item=StreetFurnitureExtractor.TryCreateCandidate("node",4,new GeoPoint(0,.001),new Dictionary<string,string>{{"amenity","bench"}})!;
        Equal(1,StreetFurnitureExtractor.SelectByRoute([item],LandmarkTestRoute(),20).ItemsInsideRoadRibbon);
        Equal(true,StreetFurnitureExtractor.IsInsideRoadRibbon(3.49)); Equal(false,StreetFurnitureExtractor.IsInsideRoadRibbon(3.5));
    }),
    ("street furniture candidate ordering and serialization are deterministic", () =>
    {
        var a=StreetFurnitureExtractor.TryCreateCandidate("node",8,new GeoPoint(0,.001),new Dictionary<string,string>{{"highway","street_lamp"}})!;
        var b=StreetFurnitureExtractor.TryCreateCandidate("node",7,new GeoPoint(0,.0005),new Dictionary<string,string>{{"amenity","bench"}})!;
        var first=StreetFurnitureExtractor.SelectByRoute([a,b],LandmarkTestRoute(),200);
        var second=StreetFurnitureExtractor.SelectByRoute([b,a],LandmarkTestRoute(),200);
        Equal(JsonSerializer.Serialize(first.Items),JsonSerializer.Serialize(second.Items));
    }),
    ("malformed OSM street furniture coordinates are safely skipped", () =>
    {
        Equal<StreetFurnitureCandidate?>(null,StreetFurnitureExtractor.TryCreateCandidate("node",9,new GeoPoint(double.NaN,0),new Dictionary<string,string>{{"amenity","bench"}}));
        Equal<StreetFurnitureCandidate?>(null,StreetFurnitureExtractor.TryCreateCandidate("node",0,new GeoPoint(0,0),new Dictionary<string,string>{{"amenity","bench"}}));
    }),
    ("bus stops and benches classify only useful mapped tags", () =>
    {
        Equal("bus-stop",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"highway","bus_stop"}}));
        Equal("bus-stop",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"public_transport","platform"},{"bus","yes"}}));
        Equal("bench",StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"amenity","bench"}}));
        Equal<string?>(null,StreetFurnitureExtractor.Classify(new Dictionary<string,string>{{"amenity","waste_basket"}}));
    }),
    ("closed vegetation area ways extract without fabricating open rings", () =>
    {
        var coords = new Dictionary<long,GeoPoint> { [1]=new(0,.0004), [2]=new(.0001,.0004), [3]=new(.0001,.0005), [4]=new(0,.0005) };
        var tags = new Dictionary<string,string> { ["leisure"]="park" };
        Equal("park", VegetationExtractor.CreateClosedAreaWay(15,[1,2,3,4,1],tags,coords)?.Category);
        Equal<RawVegetationArea?>(null, VegetationExtractor.CreateClosedAreaWay(16,[1,2,3,4],tags,coords));
    }),
    ("vegetation tags classify selected park, garden, woodland and open-area types", () =>
    {
        Equal("garden", VegetationExtractor.ClassifyArea(new Dictionary<string,string>{{"leisure","garden"}}));
        Equal("nature-reserve", VegetationExtractor.ClassifyArea(new Dictionary<string,string>{{"leisure","nature_reserve"}}));
        Equal("wood", VegetationExtractor.ClassifyArea(new Dictionary<string,string>{{"natural","wood"}}));
        Equal("recreation", VegetationExtractor.ClassifyArea(new Dictionary<string,string>{{"landuse","recreation_ground"}}));
        Equal<string?>(null, VegetationExtractor.ClassifyArea(new Dictionary<string,string>{{"natural","scrub"}}));
    }),
    ("natural tree nodes are extracted only from explicit tree tags", () =>
    {
        var point = new GeoPoint(.001,.002);
        Equal(4L, VegetationExtractor.CreateTreeNode(4,point,new Dictionary<string,string>{{"natural","tree"}})?.OsmId);
        Equal<ExplicitTreeCandidate?>(null, VegetationExtractor.CreateTreeNode(5,point,new Dictionary<string,string>{{"natural","wood"}}));
    }),
    ("tree rows receive consistent approximately ten metre instance spacing", () =>
    {
        var points = VegetationExtractor.PlaceAlongLine([new(0,0),new(0,.001)],10);
        Equal(11, points.Count);
        for (var i=1;i<points.Count;i++) Near(10,GeoMath.HaversineMeters(points[i-1],points[i]),.05);
    }),
    ("vegetation simplification conserves a closed valid boundary", () =>
    {
        var polygon = new[] { new GeoPoint(0,0),new GeoPoint(0,.00001),new GeoPoint(0,.00002),
            new GeoPoint(.0001,.00002),new GeoPoint(.0001,.00001),new GeoPoint(.0001,0),new GeoPoint(0,0) };
        var simple = VegetationExtractor.Simplify(polygon,1);
        Equal(simple[0],simple[^1]); if (simple.Count < 5) throw new Exception("Simplification destroyed the area ring.");
    }),
    ("seeded area tree candidates are deterministic", () =>
    {
        var polygon = GreenSquare(.001,.001);
        var a = VegetationExtractor.GenerateDeterministicPoints(polygon,"route",77,500);
        var b = VegetationExtractor.GenerateDeterministicPoints(polygon,"route",77,500);
        Equal(JsonSerializer.Serialize(a),JsonSerializer.Serialize(b));
    }),
    ("generated tree candidates remain inside their vegetation polygon", () =>
    {
        var polygon = GreenSquare(.001,.001);
        var generated = VegetationExtractor.GenerateDeterministicPoints(polygon,"route",78,500);
        if (generated.Count == 0 || generated.Any(point => !VegetationExtractor.PointInPolygon(point,polygon)))
            throw new Exception("Generated tree point left its source polygon.");
    }),
    ("generated trees are rejected within the road exclusion distance", () =>
    {
        Equal("road",VegetationExtractor.GeneratedTreeRejection(new(.00001,.001),LandmarkTestRoute(),[],[],[]));
    }),
    ("generated trees are rejected inside bundled building footprints", () =>
    {
        var point = new GeoPoint(.0005,.001);
        var building = new RouteBuilding("way",5,null,"yes",8,"Fallback",0,
            [new(.0009,.0004),new(.0011,.0004),new(.0011,.0006),new(.0009,.0006),new(.0009,.0004)],0,0,new Dictionary<string,string>());
        Equal("building",VegetationExtractor.GeneratedTreeRejection(point,LandmarkTestRoute(),[building],[],[]));
    }),
    ("generated trees avoid explicit tree duplicates", () =>
    {
        var point = new GeoPoint(.0005,.001);
        Equal("duplicate",VegetationExtractor.GeneratedTreeRejection(point,LandmarkTestRoute(),[],[point],[]));
    }),
    ("closed OSM building ways are extracted and open rings are rejected", () =>
    {
        var coords = new Dictionary<long, GeoPoint> { [1]=new(0,.0004), [2]=new(.0001,.0004), [3]=new(.0001,.0005), [4]=new(0,.0005) };
        var tags = new Dictionary<string,string> { ["building"]="commercial" };
        Equal(true, BuildingExtractor.CreateClosedWay(7, [1,2,3,4,1], tags, coords) is not null);
        Equal<RawBuilding?>(null, BuildingExtractor.CreateClosedWay(7, [1,2,3,4], tags, coords));
        Equal<RawBuilding?>(null, BuildingExtractor.CreateClosedWay(8, [1,2,3,4,1], new Dictionary<string,string>{{"building","no"}}, coords));
    }),
    ("building polygon distance uses closest edges rather than centroid", () =>
    {
        var polygon = new[] { new GeoPoint(.0001,.0002), new GeoPoint(.0001,.0018), new GeoPoint(.0002,.0018),
            new GeoPoint(.0002,.0002), new GeoPoint(.0001,.0002) };
        var result = BuildingExtractor.PolygonToRouteDistance(polygon, LandmarkTestRoute());
        Near(11.1, result.DistanceMeters, 1.0);
    }),
    ("building radius filter retains only nearby footprint geometry", () =>
    {
        var near = TestBuilding(1, .0001); var far = TestBuilding(2, .002);
        var result = BuildingExtractor.SelectByRoute([near, far], LandmarkTestRoute(), 20);
        Equal(1, result.NearbyCandidates); Equal(1, result.Buildings.Count); Equal(1L, result.Buildings[0].OsmId);
    }),
    ("OSM height parsing handles metres and rejects implausible values", () =>
    {
        Near(12, BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","yes"},{"height","12 m"}}).Height);
        Near(12, BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","yes"},{"height","12"}}).Height);
        Near(3.5, BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","yes"},{"height","3.5"}}).Height);
        var invalid = BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","yes"},{"height","9999"}});
        Near(8, invalid.Height); Equal(true, invalid.Implausible);
    }),
    ("building levels multiply by configured metres per level", () =>
    {
        var parsed = BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","yes"},{"building:levels","4"}});
        Near(12, parsed.Height); Equal("Levels", parsed.Source);
    }),
    ("building fallback distinguishes ordinary and small service footprints", () =>
    {
        Near(8, BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","commercial"}}).Height);
        Near(3.5, BuildingExtractor.ParseHeight(new Dictionary<string,string>{{"building","garage"}}).Height);
    }),
    ("footprint simplification retains a valid closed polygon", () =>
    {
        var polygon = new[] { new GeoPoint(0,0), new GeoPoint(0,.00001), new GeoPoint(0,.00002),
            new GeoPoint(.0001,.00002), new GeoPoint(.0001,.00001), new GeoPoint(.0001,0), new GeoPoint(0,0) };
        var simplified = BuildingExtractor.Simplify(polygon, 2);
        if (simplified.Count < 5 || simplified[0] != simplified[^1]) throw new Exception("Simplification invalidated the polygon.");
    }),
    ("malformed polygon records are safely skipped", () =>
    {
        var result = BuildingExtractor.SelectByRoute([new RawBuilding("way", 4,
            new Dictionary<string,string>{{"building","yes"}}, [1,2,3], [new(0,0),new(0,0),new(0,0)])], LandmarkTestRoute());
        Equal(0, result.Buildings.Count); Equal(1, result.MalformedOrExcludedRecords);
    }),
    ("building output ordering and serialization are deterministic", () =>
    {
        var a = TestBuilding(8, .0001); var b = TestBuilding(3, .00011);
        var first = BuildingExtractor.SelectByRoute([a,b], LandmarkTestRoute()).Buildings;
        var second = BuildingExtractor.SelectByRoute([b,a], LandmarkTestRoute()).Buildings;
        Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }),
    ("point-to-route polyline distance uses metric projection", () =>
    {
        var route = LandmarkTestRoute();
        Near(111.195, LandmarkExtractor.PointToPolylineDistanceMeters(new(0.001, 0.001), route), 0.5);
    }),
    ("nearest route-distance reports chainage at the closest projection", () =>
    {
        var route = LandmarkTestRoute();
        Near(150, LandmarkExtractor.NearestRouteDistanceMeters(new(0, 0.0015), route), 0.01);
    }),
    ("landmark candidates are filtered by route radius", () =>
    {
        var route = LandmarkTestRoute();
        var candidates = new[] { TestLandmark("Near Park", "park", 0.0001, 0.001), TestLandmark("Far Park", "park", 0.01, 0.001) };
        var result = LandmarkExtractor.RankAndFilter(candidates, route, 50);
        Equal(1, result.NearbyCandidatesConsidered); Equal("Near Park", result.Landmarks.Single().Name);
    }),
    ("sparse polygon and line geometry proximity considers edges, not just vertices", () =>
    {
        var route = LandmarkTestRoute();
        var crossing = new RawLandmark("Crossing feature", "attraction", 0, .001,
            [new GeoPoint(.001, -.001), new GeoPoint(-.001, .003)], "way", 77,
            new Dictionary<string,string> { ["name"]="Crossing feature" }, 10);
        var result = LandmarkExtractor.RankAndFilter([crossing], route, 10);
        Equal(1, result.Landmarks.Count); Near(0, result.Landmarks[0].DistanceFromRouteMeters, 1);
        Near(100, result.Landmarks[0].NearestRouteDistanceMeters, 2);
    }),
    ("unnamed and trivial POIs are excluded", () =>
    {
        Equal<string?>(null, LandmarkExtractor.Classify(new Dictionary<string,string> { ["leisure"]="park" }));
        Equal<string?>(null, LandmarkExtractor.Classify(new Dictionary<string,string> { ["name"]="Unnamed", ["amenity"]="bench" }));
        Equal<string?>(null, LandmarkExtractor.Classify(new Dictionary<string,string> { ["name"]="A", ["tourism"]="attraction" }));
    }),
    ("same-site OSM node and way landmarks are deduplicated", () =>
    {
        var route = LandmarkTestRoute();
        var items = new[] { TestLandmark("Dunedin Station", "railway/station", .0001, .001, "node", 5, 10),
            TestLandmark("Dunedin Station", "railway/station", .00011, .001, "way", 6, 11) };
        var result = LandmarkExtractor.RankAndFilter(items, route, 50);
        Equal(2, result.NearbyCandidatesConsidered); Equal(1, result.DeduplicatedCandidateCount); Equal(1, result.Landmarks.Count);
    }),
    ("candidate ranking is deterministic", () =>
    {
        var route = LandmarkTestRoute();
        var items = new[] { TestLandmark("A Park", "park", .0001, .001, "way", 20, 9),
            TestLandmark("B Museum", "attraction", .0001, .001, "node", 10, 12) };
        var first = LandmarkExtractor.RankAndFilter(items, route, 50).Landmarks.Select(item => item.OsmId).ToArray();
        var second = LandmarkExtractor.RankAndFilter(items.Reverse(), route, 50).Landmarks.Select(item => item.OsmId).ToArray();
        if (!first.SequenceEqual(second)) throw new Exception("Ranking order changed with input enumeration order.");
    }),
    ("Haversine distance", () => Near(111_195, GeoMath.HaversineMeters(new(0, 0), new(0, 1)), 5)),
    ("edge projection and snap threshold", () =>
    {
        var graph = LineGraph();
        var snapped = RouteSolver.Snap(graph, new RouteAnchor("near", 0, 0.00001, 0.00001), 10);
        Equal(1L, snapped.FromNodeId);
        if (snapped.SnapDistanceMeters <= 0) throw new Exception("Expected a nonzero snap distance.");
        Throws<InvalidOperationException>(() => RouteSolver.Snap(graph, new RouteAnchor("far", 0, 0.00001, 0.00001), 0.5));
    }),
    ("projection returns point and fraction on road segment", () =>
    {
        var projected = RouteSolver.ProjectPointOntoSegment(new(0.0001, 0.0005), new(0, 0), new(0, 0.001), out var fraction);
        Near(0, projected.Latitude);
        Near(0.0005, projected.Longitude);
        Near(0.5, fraction);
    }),
    ("candidate generation retains directed edge metadata", () =>
    {
        var graph = DirectedTwoNodeGraph(new Dictionary<string, string> { ["highway"] = "primary", ["oneway"] = "yes", ["name"] = "North Road", ["ref"] = "SH 1" });
        var candidates = RouteSolver.GenerateSnapCandidates(graph, new("mid", 0, 0, 0.0005), 10,
            next: new("north", 100, 0.001, 0.0005));
        if (candidates.Count == 0) throw new Exception("No edge snap candidates were generated.");
        Equal(1L, candidates[0].WayId);
        Equal("North Road", candidates[0].RoadName);
        Equal(true, candidates[0].DirectionCompatible);
        Near(0.5, candidates[0].EdgeFraction);
    }),
    ("direction alignment scores candidate road orientation", () =>
    {
        var graph = new RoadGraph();
        var coordinates = new Dictionary<long, GeoPoint> { [1] = new(0, -0.001), [2] = new(0, 0.001), [3] = new(-0.001, 0), [4] = new(0.001, 0) };
        graph.AddWay(10, [1, 2], OneWay("primary"), coordinates);
        graph.AddWay(11, [3, 4], OneWay("primary"), coordinates);
        var candidates = RouteSolver.GenerateSnapCandidates(graph, new("crossing", 0, 0, 0), 5,
            next: new("north", 100, 0.001, 0));
        Equal(11L, candidates[0].WayId);
        if (candidates[0].DirectionAlignmentDegrees > 1) throw new Exception("Expected northbound edge alignment.");
    }),
    ("A-star selects a connected lowest-cost path", () =>
    {
        var graph = new RoadGraph();
        var coords = new Dictionary<long, GeoPoint>
        {
            [1] = new(0, 0), [2] = new(0, 0.0001), [3] = new(0.001, 0), [4] = new(0.001, 0.001)
        };
        graph.AddWay(10, [1, 2], Tags("residential"), coords);
        graph.AddWay(11, [2, 4], Tags("residential"), coords);
        graph.AddWay(12, [1, 3], Tags("residential"), coords);
        graph.AddWay(13, [3, 4], Tags("residential"), coords);
        var path = RouteSolver.FindPath(graph, 1, 4, coords[1], coords[4], 1000);
        Equal(3, path.Nodes.Count);
        Equal(10L, path.Edges[0].WayId);
        Equal(11L, path.Edges[1].WayId);
        if (path.DistanceMeters <= 0) throw new Exception("Path distance should be positive.");
    }),
    ("anchors route in configured order and retain authored expected distance", () =>
    {
        var graph = LineGraph();
        var config = Config([new("A", 0, 0, 0), new("C", 200, 0, 0.002), new("B", 300, 0, 0.001)]);
        var built = RouteSolver.Build(graph, config);
        Equal("A", built.Legs[0].FromAnchor);
        Equal("C", built.Legs[0].ToAnchor);
        Equal("C", built.Legs[1].FromAnchor);
        Equal("B", built.Legs[1].ToAnchor);
        Near(GeoMath.HaversineMeters(new(0, 0), new(0, 0.002)), built.Legs[0].StraightLineDistanceMeters);
        Near(GeoMath.HaversineMeters(new(0, 0.002), new(0, 0.001)), built.Legs[1].StraightLineDistanceMeters);
        Equal(200d, built.Snaps[1].Anchor.SourceDistanceMeters);
    }),
    ("corridor restriction excludes a shorter remote detour", () =>
    {
        var graph = new RoadGraph();
        var coords = new Dictionary<long, GeoPoint> { [1] = new(0, 0), [2] = new(0, 0.002), [10] = new(0.0015, 0.001) };
        var zigzag = new List<long> { 1 };
        for (var i = 1; i <= 9; i++)
        {
            var id = 20 + i;
            coords[id] = new GeoPoint(i % 2 == 1 ? 0.0007 : -0.0007, i * 0.0002);
            zigzag.Add(id);
        }
        zigzag.Add(2);
        graph.AddWay(1, zigzag, Tags("primary"), coords);
        graph.AddWay(2, [1, 10, 2], Tags("primary"), coords);
        var remote = RouteSolver.FindPath(graph, 1, 2, coords[1], coords[2], 500);
        var constrained = RouteSolver.FindPath(graph, 1, 2, coords[1], coords[2], 100);
        if (remote.DistanceMeters >= constrained.DistanceMeters)
            throw new Exception("Synthetic fixture did not make the remote path shorter.");
        if (constrained.Nodes.Any(node => node.Id == 10)) throw new Exception("Corridor admitted the remote node.");
        if (constrained.Nodes.Count < 5) throw new Exception("Expected the constrained in-corridor path.");
    }),
    ("parallel opposite one-way roads select correctly directed edge", () =>
    {
        var graph = new RoadGraph();
        var coords = new Dictionary<long, GeoPoint>
        {
            [1] = new(0, 0), [2] = new(0, 0.00005), [3] = new(0, 0.001), [4] = new(0.001, 0.00005)
        };
        graph.AddWay(1, [1, 3], OneWay("primary"), coords);
        graph.AddWay(2, [2, 4], OneWay("primary"), coords);
        var snap = RouteSolver.Snap(graph, new RouteAnchor("junction", 0, 0, 0), 20,
            next: new RouteAnchor("north", 100, 0.001, 0), profile: "road");
        Equal(2L, snap.WayId);
    }),
    ("road profile prefers nearby road to parallel cycleway", () =>
    {
        var graph = new RoadGraph();
        var coords = new Dictionary<long, GeoPoint>
        {
            [1] = new(0, 0), [2] = new(0.0002, 0.001), [3] = new(0, 0.002)
        };
        graph.AddWay(10, [1, 2, 3], OneWay("primary"), coords);
        graph.AddWay(11, [1, 3], new Dictionary<string, string> { ["highway"] = "cycleway" }, coords);
        var path = RouteSolver.FindPath(graph, 1, 3, coords[1], coords[3], 50, "road");
        if (path.Edges.Any(edge => edge.WayId == 11)) throw new Exception("Road profile selected the parallel cycleway.");
        if (!path.Edges.Any(edge => edge.WayId == 10)) throw new Exception("Road profile did not select primary road geometry.");
    }),
    ("oneway yes true and 1 prevent reverse route", () =>
    {
        foreach (var value in new[] { "yes", "true", "1" })
        {
            var graph = DirectedTwoNodeGraph(new Dictionary<string, string> { ["highway"] = "primary", ["oneway"] = value });
            _ = RouteSolver.FindPath(graph, 1, 2, new(0, 0), new(0, 0.001), 20);
            Throws<InvalidOperationException>(() => RouteSolver.FindPath(graph, 2, 1, new(0, 0.001), new(0, 0), 20));
        }
    }),
    ("oneway -1 reverses permitted direction", () =>
    {
        var graph = DirectedTwoNodeGraph(new Dictionary<string, string> { ["highway"] = "primary", ["oneway"] = "-1" });
        _ = RouteSolver.FindPath(graph, 2, 1, new(0, 0.001), new(0, 0), 20);
        Throws<InvalidOperationException>(() => RouteSolver.FindPath(graph, 1, 2, new(0, 0), new(0, 0.001), 20));
    }),
    ("oneway bicycle no allows both directions", () =>
    {
        var graph = DirectedTwoNodeGraph(new Dictionary<string, string>
        {
            ["highway"] = "primary", ["oneway"] = "yes", ["oneway:bicycle"] = "no"
        });
        _ = RouteSolver.FindPath(graph, 1, 2, new(0, 0), new(0, 0.001), 20);
        _ = RouteSolver.FindPath(graph, 2, 1, new(0, 0.001), new(0, 0), 20);
    }),
    ("bicycle no and private access exclude roads", () =>
    {
        var coords = new Dictionary<long, GeoPoint> { [1] = new(0, 0), [2] = new(0, 0.001) };
        foreach (var tags in new[]
        {
            new Dictionary<string, string> { ["highway"] = "residential", ["bicycle"] = "no" },
            new Dictionary<string, string> { ["highway"] = "residential", ["access"] = "no" },
            new Dictionary<string, string> { ["highway"] = "residential", ["access"] = "private" }
        })
        {
            var graph = new RoadGraph();
            graph.AddWay(1, [1, 2], tags, coords);
            Equal(0, graph.EdgeCount);
        }
    }),
    ("roundabouts are one-way unless explicitly overridden for bicycles", () =>
    {
        var coords = new Dictionary<long, GeoPoint> { [1] = new(0, 0), [2] = new(0, 0.001) };
        var roundabout = new RoadGraph();
        roundabout.AddWay(1, [1, 2], new Dictionary<string, string> { ["highway"] = "tertiary", ["junction"] = "roundabout" }, coords);
        _ = RouteSolver.FindPath(roundabout, 1, 2, coords[1], coords[2], 20);
        Throws<InvalidOperationException>(() => RouteSolver.FindPath(roundabout, 2, 1, coords[2], coords[1], 20));
        var overrideGraph = new RoadGraph();
        overrideGraph.AddWay(1, [1, 2], new Dictionary<string, string>
        {
            ["highway"] = "tertiary", ["junction"] = "roundabout", ["oneway:bicycle"] = "no"
        }, coords);
        _ = RouteSolver.FindPath(overrideGraph, 2, 1, coords[2], coords[1], 20);
    }),
    ("detour warning and failure thresholds are reported", () =>
    {
        var curved = CurvedGraph();
        var warningConfig = Config([new("A", 0, 0, 0), new("B", 100, 0, 0.001)], warning: 1.05, failure: 1.6);
        var warning = RouteSolver.Build(curved, warningConfig);
        Equal("warning", warning.Legs[0].Status);
        if (!warning.IsValid || warning.Warnings.Count == 0) throw new Exception("Expected warning-only route result.");

        var failureConfig = Config([new("A", 0, 0, 0), new("B", 100, 0, 0.001)], warning: 1.05, failure: 1.2);
        var failed = RouteSolver.Build(curved, failureConfig);
        Equal("failed", failed.Legs[0].Status);
        if (failed.IsValid || failed.Errors.Count == 0) throw new Exception("Failure threshold did not invalidate the route.");
        if (failed.Legs[0].AlternativeStartCandidates.Count == 0 || failed.Legs[0].AlternativeEndCandidates.Count == 0)
            throw new Exception("Failed leg omitted alternate edge snap candidates.");
    }),
    ("output densification caps spacing without smoothing source line", () =>
    {
        var source = new[] { new GeoPoint(0, 0), new GeoPoint(0, 0.001) };
        var dense = RouteSolver.Densify(source, 25);
        if (dense.Count < 5) throw new Exception("Expected intermediate resampled points.");
        Equal(source[0], dense[0]);
        Equal(source[^1], dense[^1]);
        for (var i = 1; i < dense.Count; i++)
            if (GeoMath.HaversineMeters(dense[i - 1], dense[i]) > 25.01)
                throw new Exception("Densified spacing exceeded limit.");
        foreach (var point in dense)
            Near(0, GeoMath.PointToSegmentMeters(point, source[0], source[1]), 0.001);
    }),
    ("graph cache round-trip and source or bbox invalidation", () =>
    {
        var graph = LineGraph();
        const string key = "valid-key";
        var bytes = RoadGraphCache.SerializeGraph(key, graph, 11, 2, 73.8);
        if (!RoadGraphCache.TryDeserializeGraph(bytes, key, out var loaded, out var retained, out var ways, out var cacheBuild))
            throw new Exception("Cache did not round-trip.");
        Equal(graph.Nodes.Count, loaded!.Nodes.Count);
        Equal(graph.EdgeCount, loaded.EdgeCount);
        Equal(11, retained);
        Equal(2, ways);
        Near(73.8, cacheBuild);
        if (RoadGraphCache.TryDeserializeGraph(bytes, "other-key", out _, out _, out _, out _))
            throw new Exception("Cache accepted a mismatched source/bbox identity.");
        var bbox = new BoundingBox(-1, -1, 1, 1);
        var same = RoadGraphCache.BuildCacheKey("nz.osm.pbf", 100, 9, bbox, 300);
        var changedSource = RoadGraphCache.BuildCacheKey("nz.osm.pbf", 101, 9, bbox, 300);
        var changedBox = RoadGraphCache.BuildCacheKey("nz.osm.pbf", 100, 9, new(-2, -1, 1, 1), 300);
        if (same == changedSource || same == changedBox) throw new Exception("Cache key failed to invalidate changed inputs.");
    }),
    ("edge projection prevents short anchors collapsing to the same node", () =>
    {
        var graph = new RoadGraph();
        var coordinates = new Dictionary<long, GeoPoint> { [1] = new(0, 0), [2] = new(0, 0.002) };
        graph.AddWay(1, [1, 2], Tags("residential"), coordinates);
        var config = Config([new("A", 0, 0.0002, 0.0003), new("B", 60, 0.0002, 0.0007)], warning: 2, failure: 3);
        var built = RouteSolver.Build(graph, config);
        if (built.Snaps[0].FromNodeId != built.Snaps[1].FromNodeId || built.Snaps[0].ToNodeId != built.Snaps[1].ToNodeId)
            throw new Exception("Fixture expected two anchors on the same source segment.");
        if (GeoMath.HaversineMeters(built.Snaps[0].SnappedCoordinate, built.Snaps[1].SnappedCoordinate) < 40)
            throw new Exception("Projected anchor positions collapsed onto one graph node.");
        Near(built.RawDistanceMeters, built.RoutePoints[^1].DistanceMeters, 0.001);
    }),
    ("candidate-pair routing avoids wrong-carriageway detour", () =>
    {
        var graph = new RoadGraph();
        var coords = new Dictionary<long, GeoPoint>
        {
            [1] = new(0, 0), [2] = new(0, 0.001), [3] = new(0, 0.002),
            [4] = new(0.00008, 0), [5] = new(0.00008, 0.001), [6] = new(0.00008, 0.002),
            [7] = new(-0.0002, 0.001)
        };
        graph.AddWay(10, [1, 2, 3], OneWay("primary"), coords);
        graph.AddWay(11, [6, 5, 4], OneWay("primary"), coords);
        graph.AddWay(12, [2, 7, 5], Tags("residential"), coords);
        var config = Config([new("A", 0, 0, 0.0001), new("B", 100, 0, 0.0019)], warning: 2, failure: 3);
        var built = RouteSolver.Build(graph, config);
        if (built.Legs[0].DistanceMeters > 250) throw new Exception($"Expected continuous eastbound carriageway route, got {built.Legs[0].DistanceMeters:F1}m.");
        Equal(10L, built.Snaps[0].WayId);
        Equal(10L, built.Snaps[1].WayId);
    }),
    ("route distances use actual OSM length instead of legacy reference scaling", () =>
    {
        var graph = new RoadGraph();
        var coords = new Dictionary<long, GeoPoint>
        {
            [1] = new(0, 0), [2] = new(0, 0.001), [3] = new(0.001, 0.001), [4] = new(0.001, 0)
        };
        graph.AddWay(1, [1, 2, 3, 4, 1], Tags("residential"), coords);
        var config = Config([new("start", 0, 0, 0), new("far point", 100, 0.001, 0.001), new("finish", 200, 0, 0)], loop: true, target: 200);
        var built = RouteSolver.Build(graph, config);
        Equal(built.Centreline[0], built.Centreline[^1]);
        Near(0, built.RoutePoints[0].DistanceMeters);
        Near(built.RawDistanceMeters, built.RoutePoints[^1].DistanceMeters, 0.001);
        if (Math.Abs(built.RoutePoints[^1].DistanceMeters - 200) < 1)
            throw new Exception("Generated route was scaled to the legacy reference length.");
        for (var i = 1; i < built.RoutePoints.Count; i++)
            if (built.RoutePoints[i].DistanceMeters <= built.RoutePoints[i - 1].DistanceMeters)
                throw new Exception("Cumulative route distances are not strictly increasing.");
    }),
    ("GeoJSON coordinates are longitude then latitude", () =>
    {
        var graph = LineGraph();
        var config = Config([new("A", 0, 0, 0), new("B", 222, 0, 0.002)]);
        var built = RouteSolver.Build(graph, config);
        using var json = JsonDocument.Parse(BuildOutputs.SerializeGeoJson(config, built, DateTimeOffset.UnixEpoch));
        var coordinates = json.RootElement.GetProperty("geometry").GetProperty("coordinates");
        Near(0, coordinates[0][0].GetDouble());
        Near(0, coordinates[0][1].GetDouble());
        var lastCoordinate = coordinates[coordinates.GetArrayLength() - 1];
        Near(0.002, lastCoordinate[0].GetDouble());
        Near(0, lastCoordinate[1].GetDouble());
    }),
    ("route review HTML embeds data safely and includes OSM attribution", () =>
    {
        var config = new RouteAuthoringConfig
        {
            Id = "test", Name = "Review </script><img src=x>", Bbox = new(-1, -1, 1, 1),
            Anchors = [new("<img src=x onerror=alert(1)>", 0, 0, 0), new("End", 200, 0, 0.002)],
            MaxSnapDistanceMeters = 50, RouteCorridorMeters = 1000
        };
        var html = BuildOutputs.SerializePreviewHtml(config, RouteSolver.Build(LineGraph(), config));
        if (!html.Contains("leaflet@1.9.4", StringComparison.OrdinalIgnoreCase)
            || !html.Contains("© OpenStreetMap contributors", StringComparison.Ordinal)
            || html.Contains("__ROUTE_REVIEW_DATA__", StringComparison.Ordinal)
            || html.Contains("<img src=x", StringComparison.Ordinal)
            || !html.Contains("\\u003C", StringComparison.Ordinal))
            throw new Exception("Preview HTML is incomplete or user-provided text was not escaped.");
    }),
    ("route review exposes optional street-furniture markers and popup metadata", () =>
    {
        var config=Config([new RouteAnchor("Start",0,0,0),new RouteAnchor("Finish",200,0,.002)]);
        var route=RouteSolver.Build(LineGraph(),config);
        var item=new StreetFurnitureItem("node",123,"street-lamp",0,.001,12.5,null,90,8,100,"Lamp",
            new Dictionary<string,string>{{"highway","street_lamp"}});
        var extraction=new StreetFurnitureExtractionResult(1,0,0,[item],1);
        var html=BuildOutputs.SerializePreviewHtml(config,route,streetFurniture:extraction);
        if(!html.Contains("street-furniture-toggle",StringComparison.Ordinal)
            || !html.Contains("streetFurniture",StringComparison.Ordinal)
            || !html.Contains("street-lamp",StringComparison.Ordinal)
            || !html.Contains("123",StringComparison.Ordinal))
            throw new Exception("Street furniture preview layer or item data is missing.");
    }),
    ("roadRef routing keeps the preferred ref as the route spine", () =>
    {
        var result = RouteSolver.Build(RoadRefLoopGraph(), RoadRefConfig());
        if (result.Legs.Where(leg => leg.SectionType is "northbound" or "southbound")
            .SelectMany(leg => leg.Ways).Any(way => way.Ref != "SH 1"))
            throw new Exception("A non-preferred way was used in the roadRef spine.");
        if (result.RouteRefSummary!.PreferredRefPercent < 90) throw new Exception("Preferred-ref coverage was below the configured threshold.");
    }),
    ("roadRef rejects a shorter residential or cycleway shortcut", () =>
    {
        var result = RouteSolver.Build(RoadRefLoopGraph(includeShortcuts: true), RoadRefConfig());
        var spineWays = result.Legs.Where(leg => leg.SectionType is "northbound" or "southbound")
            .SelectMany(leg => leg.WayIds).ToArray();
        if (spineWays.Contains(900) || spineWays.Contains(901))
            throw new Exception("An unrelated shortcut was selected for the preferred-ref spine.");
    }),
    ("roadRef follows the directed one-way pair", () =>
    {
        var result = RouteSolver.Build(RoadRefLoopGraph(), RoadRefConfig());
        if (!result.Legs.Single(leg => leg.SectionType == "northbound").WayIds.Contains(100))
            throw new Exception("Northbound section did not follow the outward SH1 carriageway.");
        if (!result.Legs.Single(leg => leg.SectionType == "southbound").WayIds.Contains(200))
            throw new Exception("Southbound section did not follow the return SH1 carriageway.");
    }),
    ("roadRef allows short connectors between configured areas and carriageways", () =>
    {
        var result = RouteSolver.Build(RoadRefLoopGraph(), RoadRefConfig());
        if (!result.Legs.Where(leg => leg.SectionType == "connector").Any(leg => leg.WayIds.Contains(300)))
            throw new Exception("The Gardens transition connector was not used.");
        if (!result.Legs.Where(leg => leg.SectionType == "connector").Any(leg => leg.WayIds.Contains(301)))
            throw new Exception("The Oval closure connector was not used.");
    }),
    ("roadRef loop travels out and back on separate directed carriageways", () =>
    {
        var result = RouteSolver.Build(RoadRefLoopGraph(), RoadRefConfig());
        Equal(result.Centreline[0], result.Centreline[^1]);
        if (result.RouteRefSummary!.NorthboundPreferredDistanceMeters <= 0
            || result.RouteRefSummary.SouthboundPreferredDistanceMeters <= 0)
            throw new Exception("Both directed SH1 legs must contribute distance.");
    }),
    ("roadRef report calculates preferred-ref percentage from routed distance", () =>
    {
        var result = RouteSolver.Build(RoadRefLoopGraph(), RoadRefConfig());
        var summary = result.RouteRefSummary!;
        Near(summary.PreferredRefDistanceMeters / result.RawDistanceMeters * 100, summary.PreferredRefPercent);
        if (summary.NonPreferredConnectors.Count == 0) throw new Exception("Expected connector way details in the summary.");
    }),
    ("legacy route points do not influence roadRef routing", () =>
    {
        var graph = RoadRefLoopGraph();
        var config = RoadRefConfig();
        var first = RouteSolver.Build(graph, config);
        var second = RouteSolver.Build(graph, new RouteAuthoringConfig
        {
            Id = config.Id, Name = config.Name, Loop = config.Loop, RoutingMode = config.RoutingMode,
            PreferredRefs = config.PreferredRefs, StartArea = config.StartArea, TurnaroundArea = config.TurnaroundArea,
            StartAreaRadiusMeters = config.StartAreaRadiusMeters, TurnaroundAreaRadiusMeters = config.TurnaroundAreaRadiusMeters,
            MaximumConnectorDistanceMeters = config.MaximumConnectorDistanceMeters,
            MinimumPreferredRefPercent = config.MinimumPreferredRefPercent, MaxOutputSpacingMeters = config.MaxOutputSpacingMeters,
            Bbox = config.Bbox, LegacyReferencePoints = [new("wrong legacy point", 42, 0.2, 0.2)]
        });
        Equal(first.Centreline.Count, second.Centreline.Count);
        for (var i = 0; i < first.Centreline.Count; i++) Equal(first.Centreline[i], second.Centreline[i]);
    })
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS: {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL: {test.Name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;

static RoadGraph LineGraph()
{
    var graph = new RoadGraph();
    var coordinates = new Dictionary<long, GeoPoint>
    {
        [1] = new(0, 0), [2] = new(0, 0.001), [3] = new(0, 0.002)
    };
    graph.AddWay(1, [1, 2, 3], Tags("residential"), coordinates);
    return graph;
}

static RoadGraph CurvedGraph()
{
    var graph = new RoadGraph();
    var coordinates = new Dictionary<long, GeoPoint>
    {
        [1] = new(0, 0), [2] = new(0.00035, 0.0005), [3] = new(0, 0.001)
    };
    graph.AddWay(1, [1, 2, 3], Tags("residential"), coordinates);
    return graph;
}

static RoadGraph DirectedTwoNodeGraph(Dictionary<string, string> tags)
{
    var graph = new RoadGraph();
    graph.AddWay(1, [1, 2], tags, new Dictionary<long, GeoPoint> { [1] = new(0, 0), [2] = new(0, 0.001) });
    return graph;
}

static RoadGraph RoadRefLoopGraph(bool includeShortcuts = false)
{
    var graph = new RoadGraph();
    var coordinates = new Dictionary<long, GeoPoint>
    {
        [1] = new(0, 0), [2] = new(0.008, 0.01),
        [3] = new(0, 0.0001), [4] = new(0.008, 0.0101)
    };
    graph.AddWay(100, [1, 2], new Dictionary<string, string>
        { ["highway"] = "trunk", ["oneway"] = "yes", ["ref"] = "SH 1", ["name"] = "Northbound SH1" }, coordinates);
    graph.AddWay(200, [4, 3], new Dictionary<string, string>
        { ["highway"] = "trunk", ["oneway"] = "yes", ["ref"] = "SH 1", ["name"] = "Southbound SH1" }, coordinates);
    graph.AddWay(300, [2, 4], new Dictionary<string, string>
        { ["highway"] = "residential", ["name"] = "Gardens connector" }, coordinates);
    graph.AddWay(301, [3, 1], new Dictionary<string, string>
        { ["highway"] = "service", ["name"] = "Oval connector" }, coordinates);
    if (includeShortcuts)
    {
        graph.AddWay(900, [1, 4], new Dictionary<string, string>
            { ["highway"] = "residential", ["name"] = "Unrelated shortcut" }, coordinates);
        graph.AddWay(901, [1, 2, 4], new Dictionary<string, string>
            { ["highway"] = "cycleway", ["name"] = "Cycleway shortcut" }, coordinates);
    }
    return graph;
}

static RouteAuthoringConfig RoadRefConfig() => new()
{
    Id = "road-ref-test", Name = "Road ref test", Loop = true, RoutingMode = "roadRef",
    PreferredRefs = ["SH 1"], StartArea = new(0, 0), TurnaroundArea = new(0.008, 0.01005),
    StartAreaRadiusMeters = 15, TurnaroundAreaRadiusMeters = 15,
    MaximumConnectorDistanceMeters = 20, MinimumPreferredRefPercent = 90,
    MaxOutputSpacingMeters = 30, Bbox = new(-1, -1, 1, 1)
};

static Dictionary<string, string> Tags(string highway) => new(StringComparer.OrdinalIgnoreCase) { ["highway"] = highway };
static Dictionary<string, string> OneWay(string highway) => new(StringComparer.OrdinalIgnoreCase)
{ ["highway"] = highway, ["oneway"] = "yes" };

static RoutePointOutput[] LandmarkTestRoute() => [new(0, 0, 0), new(100, 0, 0.001), new(200, 0, 0.002)];

static RawLandmark TestLandmark(string name, string category, double lat, double lon,
    string type = "node", long id = 1, double score = 9) =>
    new(name, category, lat, lon, [new GeoPoint(lat, lon)], type, id,
        new Dictionary<string, string> { ["name"] = name, ["amenity"] = "library" }, score);

static RawBuilding TestBuilding(long id, double latitude)
{
    var polygon = new[] { new GeoPoint(latitude,.0004), new GeoPoint(latitude,.0005),
        new GeoPoint(latitude+.00001,.0005), new GeoPoint(latitude+.00001,.0004), new GeoPoint(latitude,.0004) };
    return new RawBuilding("way", id, new Dictionary<string,string>{{"building","commercial"}}, [1,2,3,4,1], polygon);
}

static GeoPoint[] GreenSquare(double latitude, double longitude) =>
    [new(latitude,longitude),new(latitude,longitude+.001),new(latitude+.001,longitude+.001),new(latitude+.001,longitude),new(latitude,longitude)];

static RouteAuthoringConfig Config(List<RouteAnchor> anchors, bool loop = false, double? target = null,
    double warning = 1.6, double failure = 2.2) => new()
{
    Id = "test", Name = "Test", Loop = loop, ReferenceDistanceMeters = target,
    Bbox = new(-1, -1, 1, 1), Anchors = anchors, MaxSnapDistanceMeters = 50,
    RouteCorridorMeters = 1000, MaxOutputSpacingMeters = 30,
    DetourWarningRatio = warning, DetourFailureRatio = failure
};

static void Near(double expected, double actual, double tolerance = 0.001)
{
    if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        throw new Exception($"Expected {expected}, got {actual}.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
