using System.Collections;
using System.Reflection;
using OsmSharp;

namespace GWift.RouteBuilder;

/// <summary>Shared, non-fabricating OSM tag and outer-ring helpers for PBF polygon extraction.</summary>
public static class OsmPbfGeometry
{
    public static Dictionary<string, string> ToTags(object tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags is not IEnumerable entries) return result;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            var key = entry.GetType().GetProperty("Key")?.GetValue(entry)?.ToString();
            var value = entry.GetType().GetProperty("Value")?.GetValue(entry)?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && value is not null) result[key] = value;
        }
        return result;
    }

    public static IEnumerable<(long Id, string Role)> RelationWayMembers(Relation relation)
    {
        var members = relation.GetType().GetProperty("Members")?.GetValue(relation) as IEnumerable;
        if (members is null) yield break;
        foreach (var member in members)
        {
            if (member is null || !string.Equals(member.GetType().GetProperty("Type")?.GetValue(member)?.ToString(), "Way", StringComparison.OrdinalIgnoreCase)) continue;
            var idValue = member.GetType().GetProperty("Id")?.GetValue(member) ?? member.GetType().GetProperty("MemberId")?.GetValue(member);
            if (idValue is not null && long.TryParse(idValue.ToString(), out var id))
                yield return (id, member.GetType().GetProperty("Role")?.GetValue(member)?.ToString() ?? "");
        }
    }

    public static List<long>? StitchOuterRing(IEnumerable<long> memberIds, IReadOnlyDictionary<long, long[]> ways)
    {
        var ids = memberIds.ToArray();
        if (ids.Length == 0 || ids.Any(id => !ways.ContainsKey(id))) return null;
        var remaining = ids.Select(id => (long[])ways[id].Clone()).ToList();
        if (remaining.Any(nodes => nodes.Length < 2)) return null;
        var ring = remaining[0].ToList(); remaining.RemoveAt(0);
        while (remaining.Count > 0 && ring[0] != ring[^1])
        {
            var at = remaining.FindIndex(nodes => nodes[0] == ring[^1] || nodes[^1] == ring[^1]
                || nodes[0] == ring[0] || nodes[^1] == ring[0]);
            if (at < 0) return null;
            var part = remaining[at]; remaining.RemoveAt(at);
            if (part[0] == ring[^1]) ring.AddRange(part.Skip(1));
            else if (part[^1] == ring[^1]) { Array.Reverse(part); ring.AddRange(part.Skip(1)); }
            else if (part[^1] == ring[0]) ring.InsertRange(0, part.Take(part.Length - 1));
            else if (part[0] == ring[0]) { Array.Reverse(part); ring.InsertRange(0, part.Take(part.Length - 1)); }
        }
        return remaining.Count == 0 && ring[0] == ring[^1] ? ring : null;
    }
}
