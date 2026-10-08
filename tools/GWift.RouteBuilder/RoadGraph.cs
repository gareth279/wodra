namespace GWift.RouteBuilder;

public sealed record RoadNode(long Id, GeoPoint Coordinate);
public sealed record RoadEdge(long FromNodeId, long ToNodeId, double LengthMeters, double CostMeters,
    long WayId, string Highway, string? Name, string? Ref, string? Bridge = null, string? Tunnel = null,
    string? Layer = null, string? Oneway = null, string? Access = null, string? Bicycle = null,
    string? Vehicle = null, string? MotorVehicle = null, string? Motorcar = null, string? OnewayBicycle = null);

public sealed class RoadGraph
{
    private readonly Dictionary<long, RoadNode> _nodes = [];
    private readonly Dictionary<long, List<RoadEdge>> _outgoing = [];
    private readonly Dictionary<long, List<RoadEdge>> _incoming = [];
    private int _edgeCount;

    public IReadOnlyDictionary<long, RoadNode> Nodes => _nodes;
    public int EdgeCount => _edgeCount;

    public void AddNode(RoadNode node) => _nodes[node.Id] = node;

    public IEnumerable<RoadEdge> Edges => _outgoing.Values.SelectMany(edges => edges);

    public void AddWay(long wayId, IReadOnlyList<long> nodeIds, IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<long, GeoPoint> coordinates)
    {
        if (!RoadEligibility.TryGetHighway(tags, out var highway)) return;
        var oneway = RoadEligibility.GetOnewayMode(tags);
        var name = tags.GetValueOrDefault("name");
        var reference = tags.GetValueOrDefault("ref");
        var bridge = tags.GetValueOrDefault("bridge");
        var tunnel = tags.GetValueOrDefault("tunnel");
        var layer = tags.GetValueOrDefault("layer");

        for (var i = 0; i < nodeIds.Count - 1; i++)
        {
            var aId = nodeIds[i];
            var bId = nodeIds[i + 1];
            if (!coordinates.TryGetValue(aId, out var a) || !coordinates.TryGetValue(bId, out var b)) continue;
            AddNode(new RoadNode(aId, a));
            AddNode(new RoadNode(bId, b));
            var length = GeoMath.HaversineMeters(a, b);
            if (!double.IsFinite(length) || length <= 0) continue;
            var forward = CreateEdge(aId, bId);
            var reverse = CreateEdge(bId, aId);
            if (oneway >= 0) AddEdge(forward);
            if (oneway <= 0) AddEdge(reverse);

            RoadEdge CreateEdge(long from, long to) => new(from, to, length, length, wayId,
                highway, name, reference, bridge, tunnel, layer, tags.GetValueOrDefault("oneway"),
                tags.GetValueOrDefault("access"), tags.GetValueOrDefault("bicycle"),
                tags.GetValueOrDefault("vehicle"), tags.GetValueOrDefault("motor_vehicle"),
                tags.GetValueOrDefault("motorcar"), tags.GetValueOrDefault("oneway:bicycle"));
        }
    }

    public IReadOnlyList<RoadEdge> Outgoing(long nodeId) => _outgoing.TryGetValue(nodeId, out var edges) ? edges : [];
    public IReadOnlyList<RoadEdge> Incoming(long nodeId) => _incoming.TryGetValue(nodeId, out var edges) ? edges : [];

    public void AddDirectedEdge(RoadEdge edge)
    {
        AddEdge(edge);
    }

    private void AddEdge(RoadEdge edge)
    {
        if (!_nodes.ContainsKey(edge.FromNodeId) || !_nodes.ContainsKey(edge.ToNodeId))
            throw new InvalidOperationException("Graph edge references a node that was not retained.");
        if (!_outgoing.TryGetValue(edge.FromNodeId, out var edges)) _outgoing[edge.FromNodeId] = edges = [];
        edges.Add(edge);
        if (!_incoming.TryGetValue(edge.ToNodeId, out var incoming)) _incoming[edge.ToNodeId] = incoming = [];
        incoming.Add(edge);
        _edgeCount++;
    }
}

public static class RoadEligibility
{
    private static readonly HashSet<string> AllowedHighways = new(StringComparer.OrdinalIgnoreCase)
    {
        "trunk", "trunk_link", "primary", "primary_link", "secondary", "secondary_link",
        "tertiary", "tertiary_link", "residential", "unclassified", "living_street", "service", "cycleway"
    };

    public static bool TryGetHighway(IReadOnlyDictionary<string, string> tags, out string highway)
    {
        highway = tags.GetValueOrDefault("highway") ?? "";
        if (string.IsNullOrWhiteSpace(highway)) return false;
        var bicycle = tags.GetValueOrDefault("bicycle");
        if (IsDenied(bicycle)) return false;
        var bicycleExplicitlyAllowed = bicycle is not null
            && (bicycle.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || bicycle.Equals("designated", StringComparison.OrdinalIgnoreCase)
                || bicycle.Equals("permissive", StringComparison.OrdinalIgnoreCase));
        if (!bicycleExplicitlyAllowed && IsDenied(tags.GetValueOrDefault("access"))) return false;
        if (AllowedHighways.Contains(highway)) return true;
        if (highway.Equals("path", StringComparison.OrdinalIgnoreCase))
            return IsYesOrDesignated(tags.GetValueOrDefault("bicycle"));
        if (highway.Equals("footway", StringComparison.OrdinalIgnoreCase))
            return IsYesOrDesignated(tags.GetValueOrDefault("bicycle"));
        return false;
    }

    // -1 means reverse node order, 0 means bidirectional, +1 means forward.
    public static int GetOnewayMode(IReadOnlyDictionary<string, string> tags)
    {
        if (EqualsTag(tags, "oneway:bicycle", "no")) return 0;
        var value = tags.GetValueOrDefault("oneway");
        if (value is not null)
        {
            if (value.Equals("-1", StringComparison.OrdinalIgnoreCase)) return -1;
            if (IsYesOrDesignated(value)) return 1;
            if (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        if (EqualsTag(tags, "junction", "roundabout")) return 1;
        return 0;
    }

    public static double DefaultCostMultiplier(string highway) => highway.ToLowerInvariant() switch
    {
        "trunk" or "trunk_link" or "primary" or "primary_link" => 1.00,
        "secondary" or "secondary_link" => 1.01,
        "tertiary" or "tertiary_link" => 1.02,
        "residential" or "unclassified" or "living_street" => 1.04,
        "cycleway" => 1.02,
        "service" => 1.08,
        _ => 1.05
    };

    private static bool IsYesOrDesignated(string? value) => value is not null
        && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("designated", StringComparison.OrdinalIgnoreCase)
            || value.Equals("permissive", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static bool IsDenied(string? value) => value is not null
        && (value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("private", StringComparison.OrdinalIgnoreCase));

    private static bool EqualsTag(IReadOnlyDictionary<string, string> tags, string key, string value) =>
        tags.TryGetValue(key, out var tagValue) && tagValue.Equals(value, StringComparison.OrdinalIgnoreCase);
}

public static class RoadProfiles
{
    public static double Multiplier(string profile, string highway, IReadOnlyDictionary<string, double>? overrides = null)
    {
        if (overrides is not null)
        {
            var match = overrides.FirstOrDefault(pair => pair.Key.Equals(highway, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key)) return match.Value;
        }
        var road = profile.Equals("road", StringComparison.OrdinalIgnoreCase);
        return highway.ToLowerInvariant() switch
        {
            "trunk" or "trunk_link" or "primary" or "primary_link" or "secondary" or "secondary_link" => 1.00,
            "tertiary" or "tertiary_link" => road ? 1.01 : 1.04,
            "unclassified" or "residential" or "living_street" => road ? 1.02 : 1.08,
            "service" => road ? 1.35 : 1.30,
            "cycleway" => road ? 1.80 : 1.00,
            _ => 1.10
        };
    }
}
