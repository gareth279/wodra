namespace Wodra.RouteBuilder;

public sealed record RoutedPath(IReadOnlyList<RoadNode> Nodes, IReadOnlyList<RoadEdge> Edges, double DistanceMeters);

public static class RoutedPathValidation
{
    public static void ValidateConnected(RoutedPath path)
    {
        if (path.Nodes.Count != path.Edges.Count + 1)
            throw new InvalidOperationException("Routed path node and edge counts do not form a connected graph path.");
        for (var i = 0; i < path.Edges.Count; i++)
        {
            var edge = path.Edges[i];
            if (edge.FromNodeId != path.Nodes[i].Id || edge.ToNodeId != path.Nodes[i + 1].Id)
                throw new InvalidOperationException($"Routed path has an unrepresented chord between nodes {path.Nodes[i].Id} and {path.Nodes[i + 1].Id}.");
        }
    }
}

public static class RouteSolver
{
    private const int MaximumSnapCandidates = 8;
    private const double MaximumCorridorDiagnosticMeters = 3000;

    private sealed record LegChoice(RoutedPath Path, IReadOnlyList<GeoPoint> Points,
        double MaximumDeviationMeters, double SelectionCost, IReadOnlyList<RoadWaySummary> Ways);

    public static GeoPoint ProjectPointOntoSegment(GeoPoint point, GeoPoint start, GeoPoint end, out double fraction)
    {
        var latitude = (point.Latitude + start.Latitude + end.Latitude) / 3 * Math.PI / 180;
        var xScale = GeoMath.EarthRadiusMeters * Math.Cos(latitude) * Math.PI / 180;
        var yScale = GeoMath.EarthRadiusMeters * Math.PI / 180;
        var ax = start.Longitude * xScale;
        var ay = start.Latitude * yScale;
        var bx = end.Longitude * xScale;
        var by = end.Latitude * yScale;
        var px = point.Longitude * xScale;
        var py = point.Latitude * yScale;
        var dx = bx - ax;
        var dy = by - ay;
        var denominator = dx * dx + dy * dy;
        fraction = denominator > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / denominator, 0, 1) : 0;
        return new GeoPoint(start.Latitude + (end.Latitude - start.Latitude) * fraction,
            start.Longitude + (end.Longitude - start.Longitude) * fraction);
    }

    public static IReadOnlyList<SnapCandidate> GenerateSnapCandidates(RoadGraph graph, RouteAnchor anchor,
        double maximumDistanceMeters, RouteAnchor? previous = null, RouteAnchor? next = null,
        string profile = "road", IReadOnlyDictionary<string, double>? profileOverrides = null,
        int maximumCandidates = MaximumSnapCandidates)
    {
        if (maximumCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        if (graph.EdgeCount == 0) throw new InvalidOperationException("The eligible road graph is empty.");
        var target = new GeoPoint(anchor.Latitude, anchor.Longitude);
        var travelFrom = previous is null ? target : new GeoPoint(previous.Latitude, previous.Longitude);
        var travelTo = next is null ? target : new GeoPoint(next.Latitude, next.Longitude);
        if (previous is null && next is not null) travelFrom = target;
        if (previous is not null && next is null) travelTo = target;
        var candidates = new List<(SnapCandidate Candidate, double Score)>();

        foreach (var edge in graph.Edges)
        {
            var from = graph.Nodes[edge.FromNodeId].Coordinate;
            var to = graph.Nodes[edge.ToNodeId].Coordinate;
            var projected = ProjectPointOntoSegment(target, from, to, out var fraction);
            var snapDistance = GeoMath.HaversineMeters(target, projected);
            if (snapDistance > maximumDistanceMeters) continue;
            var bearing = BearingDegrees(from, to);
            var alignment = previous is null && next is null ? 0
                : GeoMath.DirectionDifferenceDegrees(from, to, travelFrom, travelTo);
            var compatible = alignment <= 90;
            var candidate = new SnapCandidate(edge.FromNodeId, edge.ToNodeId, edge.WayId, fraction,
                projected, snapDistance, bearing, alignment, compatible, edge.Highway, edge.Name, edge.Ref,
                edge.Bridge, edge.Tunnel, edge.Layer);
            var directionPenalty = alignment * 0.55 + (compatible ? 0 : 250);
            var roadPenalty = (RoadProfiles.Multiplier(profile, edge.Highway, profileOverrides) - 1) * 35;
            candidates.Add((candidate, snapDistance * 1.25 + directionPenalty + roadPenalty));
        }

        var selected = candidates.OrderBy(item => item.Score)
            .ThenBy(item => item.Candidate.WayId)
            .ThenBy(item => item.Candidate.FromNodeId)
            .ThenBy(item => item.Candidate.EdgeFraction)
            .Take(maximumCandidates).Select(item => item.Candidate).ToArray();
        if (selected.Length == 0)
        {
            var nearest = graph.Edges.Select(edge => GeoMath.HaversineMeters(target,
                ProjectPointOntoSegment(target, graph.Nodes[edge.FromNodeId].Coordinate,
                    graph.Nodes[edge.ToNodeId].Coordinate, out _))).DefaultIfEmpty(double.PositiveInfinity).Min();
            throw new InvalidOperationException($"Anchor '{anchor.Name}' is {nearest:F1} m from the nearest eligible directed road edge "
                + $"(maximum {maximumDistanceMeters:F1} m).");
        }
        return selected;
    }

    public static AnchorSnap Snap(RoadGraph graph, RouteAnchor anchor, double maximumDistanceMeters,
        RouteAnchor? previous = null, RouteAnchor? next = null, string profile = "road",
        IReadOnlyDictionary<string, double>? profileOverrides = null)
    {
        var candidate = GenerateSnapCandidates(graph, anchor, maximumDistanceMeters, previous, next,
            profile, profileOverrides, 1)[0];
        return ToAnchorSnap(anchor, candidate);
    }

    private static AnchorSnap ToAnchorSnap(RouteAnchor anchor, SnapCandidate candidate) => new(anchor,
        candidate.FromNodeId, candidate.ToNodeId, candidate.WayId, candidate.ProjectedCoordinate,
        candidate.SnapDistanceMeters, candidate.BearingDegrees, candidate.DirectionAlignmentDegrees,
        candidate.Highway, candidate.RoadName, candidate.RoadRef);

    private static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        var latitude = (from.Latitude + to.Latitude) * Math.PI / 360;
        var dx = (to.Longitude - from.Longitude) * Math.Cos(latitude);
        var dy = to.Latitude - from.Latitude;
        return (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
    }

    public static RoutedPath FindPath(RoadGraph graph, long startNodeId, long endNodeId,
        GeoPoint corridorStart, GeoPoint corridorEnd, double corridorMeters,
        string profile = "road", IReadOnlyDictionary<string, double>? profileOverrides = null)
    {
        if (!graph.Nodes.ContainsKey(startNodeId) || !graph.Nodes.ContainsKey(endNodeId))
            throw new InvalidOperationException("Route endpoint is not in the road graph.");
        if (startNodeId == endNodeId) return new RoutedPath([graph.Nodes[startNodeId]], [], 0);
        var allowedEdges = graph.Edges.Where(edge => IsInsideCorridor(graph.Nodes[edge.FromNodeId].Coordinate,
            graph.Nodes[edge.ToNodeId].Coordinate, corridorStart, corridorEnd, corridorMeters)).ToArray();
        var minCostRatio = allowedEdges.Select(edge => RoadProfiles.Multiplier(profile, edge.Highway, profileOverrides))
            .DefaultIfEmpty(1).Min();
        var allowedByNode = allowedEdges.GroupBy(edge => edge.FromNodeId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RoadEdge>)group.ToArray());
        var goal = graph.Nodes[endNodeId];
        var costs = new Dictionary<long, double> { [startNodeId] = 0 };
        var previous = new Dictionary<long, RoadEdge>();
        var open = new PriorityQueue<long, double>();
        open.Enqueue(startNodeId, GeoMath.HaversineMeters(graph.Nodes[startNodeId].Coordinate, goal.Coordinate) * minCostRatio);
        while (open.TryDequeue(out var current, out var priority))
        {
            if (!costs.TryGetValue(current, out var currentCost)) continue;
            var heuristic = GeoMath.HaversineMeters(graph.Nodes[current].Coordinate, goal.Coordinate) * minCostRatio;
            if (priority > currentCost + heuristic + 0.000001) continue;
            if (current == endNodeId) break;
            if (!allowedByNode.TryGetValue(current, out var outgoing)) continue;
            foreach (var edge in outgoing)
            {
                var candidate = currentCost + edge.LengthMeters * RoadProfiles.Multiplier(profile, edge.Highway, profileOverrides);
                if (costs.TryGetValue(edge.ToNodeId, out var old) && candidate >= old) continue;
                costs[edge.ToNodeId] = candidate;
                previous[edge.ToNodeId] = edge;
                var next = graph.Nodes[edge.ToNodeId];
                open.Enqueue(edge.ToNodeId, candidate + GeoMath.HaversineMeters(next.Coordinate, goal.Coordinate) * minCostRatio);
            }
        }
        if (!costs.ContainsKey(endNodeId))
            throw new InvalidOperationException($"No directed path from node {startNodeId} to node {endNodeId} within the {corridorMeters:F0} m authored corridor.");
        var pathEdges = new List<RoadEdge>();
        var cursor = endNodeId;
        while (cursor != startNodeId)
        {
            if (!previous.TryGetValue(cursor, out var edge)) throw new InvalidOperationException("Path reconstruction failed.");
            pathEdges.Add(edge);
            cursor = edge.FromNodeId;
        }
        pathEdges.Reverse();
        var nodes = new List<RoadNode> { graph.Nodes[startNodeId] };
        nodes.AddRange(pathEdges.Select(edge => graph.Nodes[edge.ToNodeId]));
        return new RoutedPath(nodes, pathEdges, pathEdges.Sum(edge => edge.LengthMeters));
    }

    private static LegChoice? RouteCandidatePair(RoadGraph graph, SnapCandidate start, SnapCandidate end,
        GeoPoint corridorStart, GeoPoint corridorEnd, double corridorMeters, string profile,
        IReadOnlyDictionary<string, double>? profileOverrides, double straightLineDistance)
    {
        try
        {
            var fromCoordinate = graph.Nodes[start.FromNodeId].Coordinate;
            var toCoordinate = graph.Nodes[start.ToNodeId].Coordinate;
            var endFromCoordinate = graph.Nodes[end.FromNodeId].Coordinate;
            var endToCoordinate = graph.Nodes[end.ToNodeId].Coordinate;
            RoutedPath network;
            var sameDirectedEdge = start.WayId == end.WayId && start.FromNodeId == end.FromNodeId
                && start.ToNodeId == end.ToNodeId && end.EdgeFraction >= start.EdgeFraction;
            if (sameDirectedEdge)
                network = new RoutedPath([], [], 0);
            else
                network = FindPath(graph, start.ToNodeId, end.FromNodeId,
                    corridorStart, corridorEnd, corridorMeters, profile, profileOverrides);

            var startTailLength = sameDirectedEdge ? 0
                : GeoMath.HaversineMeters(start.ProjectedCoordinate, toCoordinate);
            var endHeadLength = sameDirectedEdge ? 0
                : GeoMath.HaversineMeters(endFromCoordinate, end.ProjectedCoordinate);
            var pathDistance = sameDirectedEdge
                ? GeoMath.HaversineMeters(start.ProjectedCoordinate, end.ProjectedCoordinate)
                : startTailLength + network.DistanceMeters + endHeadLength;
            var points = new List<GeoPoint> { start.ProjectedCoordinate };
            if (sameDirectedEdge)
            {
                points.Add(end.ProjectedCoordinate);
            }
            else
            {
                points.Add(toCoordinate);
                points.AddRange(network.Nodes.Select(node => node.Coordinate));
                points.Add(endFromCoordinate);
                points.Add(end.ProjectedCoordinate);
            }
            points = RemoveAdjacentDuplicates(points);
            if (points.Count < 2) return null;
            var maxDeviation = points.Max(point => GeoMath.PointToSegmentMeters(point, corridorStart, corridorEnd));
            if (maxDeviation > corridorMeters + 0.01) return null;
            var ways = new List<RoadWaySummary> { ToWaySummary(start), ToWaySummary(end) };
            ways.AddRange(network.Edges.Select(edge => new RoadWaySummary(edge.WayId, edge.Highway, edge.Name, edge.Ref, edge.Bridge, edge.Tunnel, edge.Layer)));
            var uniqueWays = ways.DistinctBy(way => way.WayId).ToArray();
            var ratio = straightLineDistance > 0 ? pathDistance / straightLineDistance : double.PositiveInfinity;
            var alignmentPenalty = (start.DirectionAlignmentDegrees + end.DirectionAlignmentDegrees) * 0.4;
            var snapPenalty = (start.SnapDistanceMeters + end.SnapDistanceMeters) * 1.4;
            var profilePenalty = (RoadProfiles.Multiplier(profile, start.Highway, profileOverrides)
                + RoadProfiles.Multiplier(profile, end.Highway, profileOverrides) - 2) * 30;
            var detourPenalty = Math.Max(0, ratio - 1.5) * 100;
            var shortRoutePenalty = Math.Max(0, 0.9 - ratio) * 100;
            var selectionCost = pathDistance + alignmentPenalty + snapPenalty + profilePenalty
                + maxDeviation * 0.12 + detourPenalty + shortRoutePenalty;
            return new LegChoice(new RoutedPath(network.Nodes, network.Edges, pathDistance), points,
                maxDeviation, selectionCost, uniqueWays);
        }
        catch (InvalidOperationException) { return null; }
    }

    private static RoadWaySummary ToWaySummary(SnapCandidate candidate) =>
        new(candidate.WayId, candidate.Highway, candidate.RoadName, candidate.RoadRef, candidate.Bridge, candidate.Tunnel, candidate.Layer);

    private static List<GeoPoint> RemoveAdjacentDuplicates(IReadOnlyList<GeoPoint> points)
    {
        var result = new List<GeoPoint>();
        foreach (var point in points)
            if (result.Count == 0 || GeoMath.HaversineMeters(result[^1], point) > 0.001) result.Add(point);
        return result;
    }

    private static bool IsInsideCorridor(GeoPoint from, GeoPoint to, GeoPoint start, GeoPoint end, double width) =>
        GeoMath.PointToSegmentMeters(from, start, end) <= width
        && GeoMath.PointToSegmentMeters(to, start, end) <= width;

    public static RouteBuildResult Build(RoadGraph graph, RouteAuthoringConfig config)
    {
        config.Validate();
        if (config.RoutingMode == "roadRef") return RoadRefRouteBuilder.Build(graph, config);
        var candidates = new IReadOnlyList<SnapCandidate>[config.Anchors.Count];
        for (var i = 0; i < config.Anchors.Count; i++)
        {
            if (config.Loop && i == config.Anchors.Count - 1)
            {
                candidates[i] = candidates[0];
                continue;
            }
            var previous = i > 0 ? config.Anchors[i - 1] : config.Loop ? config.Anchors[^2] : null;
            var next = i + 1 < config.Anchors.Count ? config.Anchors[i + 1] : config.Loop ? config.Anchors[1] : null;
            candidates[i] = GenerateSnapCandidates(graph, config.Anchors[i], config.MaxSnapDistanceMeters,
                previous, next, config.Profile, config.RoadCostMultipliers);
        }

        var chosenLegs = SelectContinuousRoute(graph, config, candidates);
        var snaps = new AnchorSnap[config.Anchors.Count];
        snaps[0] = ToAnchorSnap(config.Anchors[0], candidates[0][chosenLegs.CandidateIndexes[0]]);
        for (var i = 1; i < snaps.Length; i++)
            snaps[i] = ToAnchorSnap(config.Anchors[i], candidates[i][chosenLegs.CandidateIndexes[i]]);

        var legs = new List<RoutedLeg>();
        var sourceCentreline = new List<GeoPoint>();
        var warnings = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < chosenLegs.Choices.Length; i++)
        {
            var choice = chosenLegs.Choices[i];
            foreach (var point in choice.Points)
                if (sourceCentreline.Count == 0 || GeoMath.HaversineMeters(sourceCentreline[^1], point) > 0.001)
                    sourceCentreline.Add(point);
            var fromAnchor = config.Anchors[i];
            var toAnchor = config.Anchors[i + 1];
            var straightDistance = GeoMath.HaversineMeters(new(fromAnchor.Latitude, fromAnchor.Longitude),
                new(toAnchor.Latitude, toAnchor.Longitude));
            var ratio = straightDistance > 0 ? choice.Path.DistanceMeters / straightDistance : double.PositiveInfinity;
            var status = ratio > config.DetourFailureRatio ? "failed"
                : ratio > config.DetourWarningRatio ? "warning" : "passed";
            if (status == "warning") warnings.Add($"{fromAnchor.Name} -> {toAnchor.Name} road/direct ratio {ratio:F2} exceeds warning threshold {config.DetourWarningRatio:F2}.");
            if (status == "failed") errors.Add($"{fromAnchor.Name} -> {toAnchor.Name} road/direct ratio {ratio:F2} exceeds failure threshold {config.DetourFailureRatio:F2}.");
            var start = snaps[i];
            var end = snaps[i + 1];
            var selectedStart = candidates[i][chosenLegs.CandidateIndexes[i]];
            var selectedEnd = candidates[i + 1][chosenLegs.CandidateIndexes[i + 1]];
            legs.Add(new RoutedLeg(fromAnchor.Name, toAnchor.Name, straightDistance, choice.Path.DistanceMeters,
                ratio, choice.MaximumDeviationMeters, start, end, status,
                choice.Ways.Select(way => way.WayId).ToArray(), choice.Ways,
                status == "failed" ? candidates[i] : [], status == "failed" ? candidates[i + 1] : [],
                choice.Points));
        }

        if (config.Loop && sourceCentreline.Count > 1)
        {
            if (GeoMath.HaversineMeters(sourceCentreline[0], sourceCentreline[^1]) > 0.001)
                throw new InvalidOperationException("Loop route did not end at its starting projected edge position.");
            sourceCentreline[^1] = sourceCentreline[0];
        }
        if (sourceCentreline.Count < 2) throw new InvalidOperationException("Route produced fewer than two OSM centreline points.");

        var rawDistance = 0d;
        var maximumSourceGap = 0d;
        for (var i = 1; i < sourceCentreline.Count; i++)
        {
            var gap = GeoMath.HaversineMeters(sourceCentreline[i - 1], sourceCentreline[i]);
            if (gap <= 0.001) throw new InvalidOperationException("Route contains adjacent duplicate coordinates.");
            rawDistance += gap;
            maximumSourceGap = Math.Max(maximumSourceGap, gap);
        }
        var centreline = Densify(sourceCentreline, config.MaxOutputSpacingMeters);
        var outputCumulative = new double[centreline.Count];
        var maximumOutputGap = 0d;
        for (var i = 1; i < centreline.Count; i++)
        {
            var gap = GeoMath.HaversineMeters(centreline[i - 1], centreline[i]);
            outputCumulative[i] = outputCumulative[i - 1] + gap;
            maximumOutputGap = Math.Max(maximumOutputGap, gap);
        }
        var points = centreline.Select((point, index) => new RoutePointOutput(outputCumulative[index],
            point.Latitude, point.Longitude)).ToArray();
        var maximumDeviationOverall = legs.Count == 0 ? 0 : legs.Max(leg => leg.MaximumCorridorDeviationMeters);
        return new RouteBuildResult(centreline, points, snaps, legs, rawDistance,
            maximumOutputGap, maximumSourceGap, maximumDeviationOverall, sourceCentreline.Count,
            centreline.Count - sourceCentreline.Count, errors.Count == 0, warnings, errors);
    }

    private static (int[] CandidateIndexes, LegChoice[] Choices) SelectContinuousRoute(RoadGraph graph,
        RouteAuthoringConfig config, IReadOnlyList<SnapCandidate>[] candidates)
    {
        var anchorCount = config.Anchors.Count;
        var pairChoices = new LegChoice?[anchorCount - 1][][];
        for (var leg = 0; leg < anchorCount - 1; leg++)
        {
            pairChoices[leg] = new LegChoice?[candidates[leg].Count][];
            var from = config.Anchors[leg];
            var to = config.Anchors[leg + 1];
            var corridorStart = new GeoPoint(from.Latitude, from.Longitude);
            var corridorEnd = new GeoPoint(to.Latitude, to.Longitude);
            var direct = GeoMath.HaversineMeters(corridorStart, corridorEnd);
            for (var startIndex = 0; startIndex < candidates[leg].Count; startIndex++)
            {
                pairChoices[leg][startIndex] = new LegChoice?[candidates[leg + 1].Count];
                for (var endIndex = 0; endIndex < candidates[leg + 1].Count; endIndex++)
                    pairChoices[leg][startIndex][endIndex] = RouteCandidatePair(graph,
                        candidates[leg][startIndex], candidates[leg + 1][endIndex], corridorStart,
                        corridorEnd, config.RouteCorridorMeters, config.Profile,
                        config.RoadCostMultipliers, direct);
            }
            if (pairChoices[leg].All(row => row.All(choice => choice is null)))
                throw new InvalidOperationException($"No candidate edge pair could route '{from.Name}' to '{to.Name}' inside the configured {config.RouteCorridorMeters:F0} m corridor.");
        }

        var bestTotal = double.PositiveInfinity;
        int[]? bestIndexes = null;
        LegChoice[]? bestChoices = null;
        var initialCount = config.Loop ? candidates[0].Count : candidates[0].Count;
        for (var initial = 0; initial < initialCount; initial++)
        {
            var costs = Enumerable.Repeat(double.PositiveInfinity, candidates[0].Count).ToArray();
            costs[initial] = CandidateQuality(candidates[0][initial]);
            var predecessors = new int[anchorCount][];
            for (var i = 0; i < anchorCount; i++) predecessors[i] = Enumerable.Repeat(-1, candidates[i].Count).ToArray();
            for (var leg = 0; leg < anchorCount - 1; leg++)
            {
                var nextCosts = Enumerable.Repeat(double.PositiveInfinity, candidates[leg + 1].Count).ToArray();
                for (var fromIndex = 0; fromIndex < costs.Length; fromIndex++)
                {
                    if (!double.IsFinite(costs[fromIndex])) continue;
                    for (var toIndex = 0; toIndex < nextCosts.Length; toIndex++)
                    {
                        if (config.Loop && leg == anchorCount - 2 && toIndex != initial) continue;
                        var choice = pairChoices[leg][fromIndex][toIndex];
                        if (choice is null) continue;
                        var candidateCost = costs[fromIndex] + choice.SelectionCost
                            + CandidateQuality(candidates[leg + 1][toIndex]);
                        if (candidateCost >= nextCosts[toIndex]) continue;
                        nextCosts[toIndex] = candidateCost;
                        predecessors[leg + 1][toIndex] = fromIndex;
                    }
                }
                costs = nextCosts;
            }
            var finalIndex = config.Loop ? initial : Array.IndexOf(costs, costs.Min());
            if (finalIndex < 0 || !double.IsFinite(costs[finalIndex]) || costs[finalIndex] >= bestTotal) continue;
            var indexes = new int[anchorCount];
            indexes[^1] = finalIndex;
            for (var i = anchorCount - 1; i > 0; i--)
            {
                indexes[i - 1] = predecessors[i][indexes[i]];
                if (indexes[i - 1] < 0) break;
            }
            if (indexes[0] != initial) continue;
            var choices = new LegChoice[anchorCount - 1];
            for (var i = 0; i < choices.Length; i++) choices[i] = pairChoices[i][indexes[i]][indexes[i + 1]]!;
            bestTotal = costs[finalIndex];
            bestIndexes = indexes;
            bestChoices = choices;
        }
        if (bestIndexes is null || bestChoices is null)
            throw new InvalidOperationException("No continuous set of directed edge snaps could route the ordered anchors within their corridors.");
        return (bestIndexes, bestChoices);
    }

    private static double CandidateQuality(SnapCandidate candidate) => candidate.SnapDistanceMeters * 1.25
        + candidate.DirectionAlignmentDegrees * 0.55 + (candidate.DirectionCompatible ? 0 : 250);

    public static List<GeoPoint> Densify(IReadOnlyList<GeoPoint> source, double maximumSpacingMeters)
    {
        if (source.Count < 2) throw new ArgumentException("At least two source points are required.", nameof(source));
        if (!double.IsFinite(maximumSpacingMeters) || maximumSpacingMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSpacingMeters));
        var result = new List<GeoPoint> { source[0] };
        for (var i = 1; i < source.Count; i++)
        {
            var start = source[i - 1];
            var end = source[i];
            var distance = GeoMath.HaversineMeters(start, end);
            var subdivisions = Math.Max(1, (int)Math.Ceiling(distance / maximumSpacingMeters));
            for (var step = 1; step <= subdivisions; step++)
            {
                var t = (double)step / subdivisions;
                result.Add(new GeoPoint(start.Latitude + (end.Latitude - start.Latitude) * t,
                    start.Longitude + (end.Longitude - start.Longitude) * t));
            }
        }
        return result;
    }
}
