namespace Wodra.RouteBuilder;

/// <summary>Builds an ordered out-and-back loop on configured OSM road references.</summary>
internal static class RoadRefRouteBuilder
{
    private const int MaximumAreaNodes = 48;

    private sealed record CandidatePath(RoutedPath Path, double PreferredDistanceMeters);
    private sealed record LoopChoice(long StartNodeId, long TurnInNodeId, long TurnOutNodeId, long FinishNodeId,
        RoutedPath Northbound, RoutedPath Turnaround, RoutedPath Southbound, RoutedPath OvalClosure,
        double PreferredDistanceMeters, double TotalDistanceMeters);

    public static RouteBuildResult Build(RoadGraph graph, RouteAuthoringConfig config)
    {
        var startArea = config.StartArea!;
        var turnaroundArea = config.TurnaroundArea!;
        var preferredEdges = graph.Edges.Where(edge => IsPreferredRef(edge, config.PreferredRefs)).ToArray();
        if (preferredEdges.Length == 0)
            throw new InvalidOperationException($"No eligible graph edges matched preferred refs: {string.Join(", ", config.PreferredRefs)}.");

        var startNodes = NodesNearArea(graph, preferredEdges, startArea, config.StartAreaRadiusMeters);
        var turnaroundNodes = NodesNearArea(graph, preferredEdges, turnaroundArea, config.TurnaroundAreaRadiusMeters);
        if (startNodes.Length == 0) throw new InvalidOperationException("No preferred-ref road node was found near the configured Oval start area.");
        if (turnaroundNodes.Length < 2)
        {
            var nearestTurnaround = preferredEdges.SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
                .Distinct().Select(nodeId => (NodeId: nodeId, Distance: GeoMath.HaversineMeters(
                    graph.Nodes[nodeId].Coordinate, turnaroundArea)))
                .OrderBy(item => item.Distance).Take(5).ToArray();
            throw new InvalidOperationException("Fewer than two preferred-ref road nodes were found near the Gardens turnaround area. "
                + $"Nearest SH1 nodes: {string.Join("; ", nearestTurnaround.Select(item => $"{item.NodeId} at {item.Distance:F1}m"))}.");
        }

        var preferredFilter = (RoadEdge edge) => IsPreferredRef(edge, config.PreferredRefs);
        var northPaths = new CandidatePath?[startNodes.Length, turnaroundNodes.Length];
        var southPaths = new CandidatePath?[turnaroundNodes.Length, startNodes.Length];
        var connectorPaths = new RoutedPath?[turnaroundNodes.Length, turnaroundNodes.Length];
        var closurePaths = new RoutedPath?[startNodes.Length, startNodes.Length];
        for (var start = 0; start < startNodes.Length; start++)
        for (var turn = 0; turn < turnaroundNodes.Length; turn++)
        {
            var northPath = FindShortestPath(graph, startNodes[start], turnaroundNodes[turn], preferredFilter, config.Profile, config.RoadCostMultipliers);
            var southPath = FindShortestPath(graph, turnaroundNodes[turn], startNodes[start], preferredFilter, config.Profile, config.RoadCostMultipliers);
            northPaths[start, turn] = northPath is null ? null
                : new CandidatePath(northPath, PreferredDistance(northPath.Edges, config.PreferredRefs));
            southPaths[turn, start] = southPath is null ? null
                : new CandidatePath(southPath, PreferredDistance(southPath.Edges, config.PreferredRefs));
        }
        for (var from = 0; from < turnaroundNodes.Length; from++)
        for (var to = 0; to < turnaroundNodes.Length; to++)
            connectorPaths[from, to] = FindShortestPath(graph, turnaroundNodes[from], turnaroundNodes[to],
                _ => true, config.Profile, config.RoadCostMultipliers);
        for (var from = 0; from < startNodes.Length; from++)
        for (var to = 0; to < startNodes.Length; to++)
            closurePaths[from, to] = FindShortestPath(graph, startNodes[from], startNodes[to],
                edge => !config.ExcludedClosureEdges.Any(exclusion => exclusion.Matches(edge)),
                config.Profile, config.RoadCostMultipliers);

        LoopChoice? best = null;
        var bestScore = double.PositiveInfinity;
        var completePathCount = 0;
        var bestCoverage = 0d;
        var shortestConnector = double.PositiveInfinity;
        for (var start = 0; start < startNodes.Length; start++)
        for (var finish = 0; finish < startNodes.Length; finish++)
        for (var turnIn = 0; turnIn < turnaroundNodes.Length; turnIn++)
        for (var turnOut = 0; turnOut < turnaroundNodes.Length; turnOut++)
        {
            var north = northPaths[start, turnIn];
            var transition = connectorPaths[turnIn, turnOut];
            var south = southPaths[turnOut, finish];
            var closure = closurePaths[finish, start];
            if (north is null || transition is null || south is null || closure is null) continue;
            completePathCount++;
            shortestConnector = Math.Min(shortestConnector, transition.DistanceMeters);
            if (north.Path.DistanceMeters <= 0 || south.Path.DistanceMeters <= 0
                || transition.DistanceMeters > config.MaximumConnectorDistanceMeters
                || closure.DistanceMeters > config.MaximumConnectorDistanceMeters) continue;
            var total = north.Path.DistanceMeters + transition.DistanceMeters + south.Path.DistanceMeters + closure.DistanceMeters;
            var preferredDistance = north.PreferredDistanceMeters + south.PreferredDistanceMeters
                + PreferredDistance(transition.Edges, config.PreferredRefs)
                + PreferredDistance(closure.Edges, config.PreferredRefs);
            if (total <= 0) continue;
            var percent = preferredDistance / total * 100;
            bestCoverage = Math.Max(bestCoverage, percent);
            if (percent + 1e-8 < config.MinimumPreferredRefPercent) continue;
            var areaBias = GeoMath.HaversineMeters(graph.Nodes[startNodes[start]].Coordinate, startArea) * 0.15
                + GeoMath.HaversineMeters(graph.Nodes[startNodes[finish]].Coordinate, startArea) * 0.15
                + (GeoMath.HaversineMeters(graph.Nodes[turnaroundNodes[turnIn]].Coordinate, turnaroundArea)
                    + GeoMath.HaversineMeters(graph.Nodes[turnaroundNodes[turnOut]].Coordinate, turnaroundArea)) * 0.1;
            // Prefer the configured approximate lap length when present; geometry remains measured on OSM edges.
            var distanceFit = config.TargetDistanceMeters is { } target
                ? Math.Abs(total - target) * 2.0 : total;
            var score = distanceFit + areaBias;
            if (score >= bestScore) continue;
            bestScore = score;
            best = new LoopChoice(startNodes[start], turnaroundNodes[turnIn], turnaroundNodes[turnOut],
                startNodes[finish], north.Path, transition, south.Path, closure,
                preferredDistance, total);
        }
        if (best is null)
            throw new InvalidOperationException("Could not form a directed SH1 out-and-back loop with the configured turnaround connector and preferred-ref percentage. "
                + $"Directed SH1 paths: {northPaths.Cast<CandidatePath?>().Count(path => path is not null)} northbound, "
                + $"{southPaths.Cast<CandidatePath?>().Count(path => path is not null)} southbound; "
                + $"turnaround connectors: {connectorPaths.Cast<RoutedPath?>().Count(path => path is not null)}; "
                + $"Oval closures: {closurePaths.Cast<RoutedPath?>().Count(path => path is not null)}. "
                + $"Complete directed combinations: {completePathCount}; shortest turnaround connector: "
                + $"{(double.IsFinite(shortestConnector) ? shortestConnector.ToString("F1") + "m" : "none")}; best preferred-ref coverage: {bestCoverage:F1}%.");

        var startEdge = best.Northbound.Edges.First(edge => IsPreferredRef(edge, config.PreferredRefs));
        var turnInEdge = best.Northbound.Edges.Last(edge => IsPreferredRef(edge, config.PreferredRefs));
        var turnOutEdge = best.Southbound.Edges.First(edge => IsPreferredRef(edge, config.PreferredRefs));
        var finishEdge = best.Southbound.Edges.Last(edge => IsPreferredRef(edge, config.PreferredRefs));
        var startSnap = CreateSnap(graph, "Oval start / finish", startArea, graph.Nodes[best.StartNodeId], startEdge);
        var turnInSnap = CreateSnap(graph, "Gardens turnaround in", turnaroundArea, graph.Nodes[best.TurnInNodeId], turnInEdge);
        var turnOutSnap = CreateSnap(graph, "Gardens turnaround out", turnaroundArea, graph.Nodes[best.TurnOutNodeId], turnOutEdge);
        var finishSnap = CreateSnap(graph, "Oval closure", startArea, graph.Nodes[best.FinishNodeId], finishEdge);

        var legs = new List<RoutedLeg>();
        var roadSegments = new List<RoutedRoadSegment>();
        var sectionDistance = 0d;
        var allPoints = new List<GeoPoint>();
        AddSection("Northbound SH1", "northbound", best.Northbound, startSnap, turnInSnap);
        AddSection("Gardens turnaround connector", "connector", best.Turnaround, turnInSnap, turnOutSnap);
        AddSection("Southbound SH1", "southbound", best.Southbound, turnOutSnap, finishSnap);
        AddSection("Oval closure connector", "connector", best.OvalClosure, finishSnap, startSnap);
        if (allPoints.Count < 2 || GeoMath.HaversineMeters(allPoints[0], allPoints[^1]) > 0.001)
            throw new InvalidOperationException("The generated road-ref route did not close at its selected Oval node.");
        allPoints[^1] = allPoints[0];

        var sourceOsmNodeCount = allPoints.Count;
        var turnaroundResult = config.TurnaroundWidthMeters is { } turnaroundWidth
            ? TurnaroundGeometry.Repair(allPoints, turnaroundWidth, config.TurnaroundWidthSource)
            : new TurnaroundGeometryResult(allPoints, []);
        allPoints = turnaroundResult.Points.ToList();

        var rawDistance = 0d;
        var maximumSourceGap = 0d;
        for (var i = 1; i < allPoints.Count; i++)
        {
            var gap = GeoMath.HaversineMeters(allPoints[i - 1], allPoints[i]);
            rawDistance += gap;
            maximumSourceGap = Math.Max(maximumSourceGap, gap);
        }
        var centreline = RouteSolver.Densify(allPoints, config.MaxOutputSpacingMeters);
        var routePoints = new RoutePointOutput[centreline.Count];
        var maximumOutputGap = 0d;
        var cumulative = 0d;
        routePoints[0] = new RoutePointOutput(0, centreline[0].Latitude, centreline[0].Longitude);
        for (var i = 1; i < centreline.Count; i++)
        {
            var gap = GeoMath.HaversineMeters(centreline[i - 1], centreline[i]);
            cumulative += gap;
            maximumOutputGap = Math.Max(maximumOutputGap, gap);
            routePoints[i] = new RoutePointOutput(cumulative, centreline[i].Latitude, centreline[i].Longitude);
        }

        var nonPreferredConnectors = new[]
            {
                (Edges: best.Turnaround.Edges, Reason: "Gardens transition between directed SH1 carriageways"),
                (Edges: best.OvalClosure.Edges, Reason: "short Oval closure between directed SH1 carriageways")
            }
            .SelectMany(section => section.Edges.Where(edge => !IsPreferredRef(edge, config.PreferredRefs))
                .GroupBy(edge => edge.WayId)
                .Select(group =>
                {
                    var edge = group.First();
                    return new NonPreferredConnector(edge.WayId, edge.Highway, edge.Name, edge.Ref,
                        group.Sum(item => item.LengthMeters), section.Reason);
                })).ToArray();
        var preferredPercent = rawDistance > 0 ? best.PreferredDistanceMeters / rawDistance * 100 : 0;
        var suspicious = preferredPercent + 1e-8 < config.MinimumPreferredRefPercent;
        var summary = new RouteRefSummary(config.PreferredRefs,
            PreferredDistance(best.Northbound.Edges, config.PreferredRefs),
            PreferredDistance(best.Southbound.Edges, config.PreferredRefs),
            best.Turnaround.DistanceMeters + best.OvalClosure.DistanceMeters,
            best.PreferredDistanceMeters, preferredPercent, config.MinimumPreferredRefPercent,
            legs.SelectMany(leg => leg.WayIds).Distinct().ToArray(),
            legs.SelectMany(leg => leg.Ways).Select(way => way.Name).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray(),
            nonPreferredConnectors, suspicious);
        var warnings = suspicious
            ? new[] { $"Only {preferredPercent:F1}% of route distance is on preferred refs; configured minimum is {config.MinimumPreferredRefPercent:F1}%." }
            : Array.Empty<string>();
        var snaps = new[] { startSnap, turnInSnap, turnOutSnap, finishSnap };
        return new RouteBuildResult(centreline, routePoints, snaps, legs, rawDistance, maximumOutputGap,
            maximumSourceGap, 0, sourceOsmNodeCount, centreline.Count - sourceOsmNodeCount,
            true, warnings, [], summary, roadSegments, turnaroundResult.Turnarounds);

        void AddSection(string name, string sectionType, RoutedPath path, AnchorSnap from, AnchorSnap to)
        {
            RoutedPathValidation.ValidateConnected(path);
            var sectionPoints = path.Nodes.Select(node => node.Coordinate).ToArray();
            foreach (var point in sectionPoints)
                if (allPoints.Count == 0 || GeoMath.HaversineMeters(allPoints[^1], point) > 0.001)
                    allPoints.Add(point);
            var ways = path.Edges.Select(ToWaySummary)
                .DistinctBy(way => way.WayId).ToArray();
            foreach (var edge in path.Edges)
            {
                roadSegments.Add(new RoutedRoadSegment(sectionDistance, sectionDistance + edge.LengthMeters,
                    edge.WayId, edge.Highway, edge.Name, edge.Ref, edge.Bridge, edge.Tunnel, edge.Layer,
                    edge.FromNodeId, edge.ToNodeId, edge.Oneway, edge.Access, edge.Bicycle,
                    edge.Vehicle, edge.MotorVehicle, edge.Motorcar, edge.OnewayBicycle));
                sectionDistance += edge.LengthMeters;
            }
            var distance = path.Edges.Sum(edge => edge.LengthMeters);
            var direct = GeoMath.HaversineMeters(from.SnappedCoordinate, to.SnappedCoordinate);
            var ratio = direct > 0 ? distance / direct : 0;
            legs.Add(new RoutedLeg(from.Anchor.Name, name, direct, distance, ratio, 0,
                from, to, "passed", ways.Select(way => way.WayId).ToArray(), ways, [], [], sectionPoints, sectionType));
        }
    }

    private static RoadWaySummary ToWaySummary(RoadEdge edge) => new(edge.WayId, edge.Highway,
        edge.Name, edge.Ref, edge.Bridge, edge.Tunnel, edge.Layer, edge.Oneway, edge.Access,
        edge.Bicycle, edge.Vehicle, edge.MotorVehicle, edge.Motorcar, edge.OnewayBicycle);

    private static long[] NodesNearArea(RoadGraph graph, IReadOnlyList<RoadEdge> preferredEdges,
        GeoPoint area, double radiusMeters) => preferredEdges
        .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
        .Distinct()
        .Where(nodeId => GeoMath.HaversineMeters(graph.Nodes[nodeId].Coordinate, area) <= radiusMeters)
        .OrderBy(nodeId => GeoMath.HaversineMeters(graph.Nodes[nodeId].Coordinate, area))
        .Take(MaximumAreaNodes).ToArray();

    private static bool IsPreferredRef(RoadEdge edge, IReadOnlyList<string> preferredRefs) =>
        (edge.Highway.Equals("trunk", StringComparison.OrdinalIgnoreCase)
            || edge.Highway.Equals("trunk_link", StringComparison.OrdinalIgnoreCase))
        && edge.Ref is not null
        && edge.Ref.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => preferredRefs.Contains(value, StringComparer.OrdinalIgnoreCase));

    private static double PreferredDistance(IReadOnlyList<RoadEdge> edges, IReadOnlyList<string> refs) =>
        edges.Where(edge => IsPreferredRef(edge, refs)).Sum(edge => edge.LengthMeters);

    private static AnchorSnap CreateSnap(RoadGraph graph, string name, GeoPoint area, RoadNode node, RoadEdge edge)
    {
        var sourceDistance = 0d;
        var anchor = new RouteAnchor(name, sourceDistance, area.Latitude, area.Longitude);
        var bearing = node.Id == edge.FromNodeId
            ? BearingDegrees(node.Coordinate, graph.Nodes[edge.ToNodeId].Coordinate)
            : BearingDegrees(graph.Nodes[edge.FromNodeId].Coordinate, node.Coordinate);
        return new AnchorSnap(anchor, edge.FromNodeId, edge.ToNodeId, edge.WayId,
            node.Coordinate, GeoMath.HaversineMeters(area, node.Coordinate), bearing, 0,
            edge.Highway, edge.Name, edge.Ref);
    }

    private static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        var latitude = (from.Latitude + to.Latitude) * Math.PI / 360;
        var dx = (to.Longitude - from.Longitude) * Math.Cos(latitude);
        var dy = to.Latitude - from.Latitude;
        return (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
    }

    private static RoutedPath? FindShortestPath(RoadGraph graph, long start, long end,
        Func<RoadEdge, bool> edgeFilter, string profile, IReadOnlyDictionary<string, double>? multipliers)
    {
        if (start == end) return new RoutedPath([graph.Nodes[start]], [], 0);
        var costs = new Dictionary<long, double> { [start] = 0 };
        var previous = new Dictionary<long, RoadEdge>();
        var open = new PriorityQueue<long, double>();
        open.Enqueue(start, 0);
        while (open.TryDequeue(out var current, out var priority))
        {
            if (!costs.TryGetValue(current, out var currentCost) || priority > currentCost + 1e-7) continue;
            if (current == end) break;
            foreach (var edge in graph.Outgoing(current))
            {
                if (!edgeFilter(edge)) continue;
                var nextCost = currentCost + edge.LengthMeters * RoadProfiles.Multiplier(profile, edge.Highway, multipliers);
                if (costs.TryGetValue(edge.ToNodeId, out var old) && nextCost >= old) continue;
                costs[edge.ToNodeId] = nextCost;
                previous[edge.ToNodeId] = edge;
                open.Enqueue(edge.ToNodeId, nextCost);
            }
        }
        if (!costs.ContainsKey(end)) return null;
        var edges = new List<RoadEdge>();
        var cursor = end;
        while (cursor != start)
        {
            if (!previous.TryGetValue(cursor, out var edge)) return null;
            edges.Add(edge);
            cursor = edge.FromNodeId;
        }
        edges.Reverse();
        var nodes = new List<RoadNode> { graph.Nodes[start] };
        nodes.AddRange(edges.Select(edge => graph.Nodes[edge.ToNodeId]));
        return new RoutedPath(nodes, edges, edges.Sum(edge => edge.LengthMeters));
    }
}
