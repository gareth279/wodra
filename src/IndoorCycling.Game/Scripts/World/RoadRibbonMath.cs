using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

public enum RoadJoinType { Normal, Bevel, Discontinuity }

public readonly record struct RoadRibbonQuad(Vector3 StartLeft, Vector3 EndLeft, Vector3 StartRight, Vector3 EndRight);
public readonly record struct RoadRibbonTriangle(Vector3 A, Vector3 B, Vector3 C);
public readonly record struct RoadProfileRibbonQuad(int SourceSegmentIndex, double StartDistanceMeters,
    double EndDistanceMeters, RoadRibbonQuad Quad);
public readonly record struct RoadJunctionOpening(double RouteDistanceMeters, float RadiusMeters);

/// <summary>Join classification and bounded horizontal offsets for canonical route ribbons.</summary>
public static class RoadRibbonMath
{
    public const float RoadWidthMeters = 7;
    public const float NormalJoinMaximumDegrees = 25;
    public const float DiscontinuityMinimumDegrees = 60;
    public const float MaximumMiterScale = 1.5f;
    public const float MaximumBevelMiterScale = 1.2f;

    public static RoadJoinType ClassifyJoin(Vector3 previous, Vector3 current, Vector3 next)
    {
        var incoming = Horizontal(current - previous);
        var outgoing = Horizontal(next - current);
        if (incoming.LengthSquared() < 1e-8f) incoming = outgoing;
        if (outgoing.LengthSquared() < 1e-8f) outgoing = incoming;
        if (incoming.LengthSquared() < 1e-8f) return RoadJoinType.Normal;
        incoming = Vector3.Normalize(incoming);
        outgoing = Vector3.Normalize(outgoing);
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(incoming, outgoing), -1f, 1f)) * (180 / MathF.PI);
        return angle >= DiscontinuityMinimumDegrees ? RoadJoinType.Discontinuity
            : angle > NormalJoinMaximumDegrees ? RoadJoinType.Bevel : RoadJoinType.Normal;
    }

    public static Vector3 OffsetAtJoin(Vector3 previous, Vector3 current, Vector3 next,
        float lateralDistance, bool segmentStart = true)
    {
        if (!Finite(previous) || !Finite(current) || !Finite(next) || !float.IsFinite(lateralDistance))
            throw new ArgumentOutOfRangeException(nameof(lateralDistance));

        var incoming = Horizontal(current - previous);
        var outgoing = Horizontal(next - current);
        if (incoming.LengthSquared() < 1e-8f) incoming = outgoing;
        if (outgoing.LengthSquared() < 1e-8f) outgoing = incoming;
        if (incoming.LengthSquared() < 1e-8f) return new Vector3(lateralDistance, 0, 0);
        incoming = Vector3.Normalize(incoming);
        outgoing = Vector3.Normalize(outgoing);

        if (ClassifyJoin(previous, current, next) == RoadJoinType.Discontinuity)
        {
            var direction = segmentStart ? outgoing : incoming;
            return Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY)) * lateralDistance;
        }

        var incomingSide = Vector3.Normalize(Vector3.Cross(incoming, Vector3.UnitY));
        var outgoingSide = Vector3.Normalize(Vector3.Cross(outgoing, Vector3.UnitY));
        var miter = incomingSide + outgoingSide;
        if (miter.LengthSquared() < 1e-8f)
            return outgoingSide * lateralDistance;
        miter = Vector3.Normalize(miter);

        var denominator = Vector3.Dot(miter, outgoingSide);
        if (denominator <= 1e-4f) return outgoingSide * lateralDistance;
        var maximumScale = ClassifyJoin(previous, current, next) == RoadJoinType.Bevel
            ? MaximumBevelMiterScale : MaximumMiterScale;
        var scale = Math.Min(1 / denominator, maximumScale);
        return miter * (lateralDistance * scale);
    }

    /// <summary>Builds one quad per consecutive centreline pair; the explicit endpoint prevents duplicate closure emission.</summary>
    public static IReadOnlyList<RoadRibbonQuad> BuildQuads(IReadOnlyList<Vector3> positions, float width, float offset = 0)
    {
        if (positions.Count < 2 || !float.IsFinite(width) || width < 0 || !float.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(width));
        var quads = new RoadRibbonQuad[positions.Count - 1];
        for (var i = 0; i < quads.Length; i++)
        {
            var start = Section(i, true);
            var end = Section(i + 1, false);
            quads[i] = new RoadRibbonQuad(start.Left, end.Left, start.Right, end.Right);
        }
        return quads;

        (Vector3 Left, Vector3 Right) Section(int index, bool segmentStart)
        {
            var previous = positions[index == 0 ? positions.Count - 2 : index - 1];
            var next = positions[index == positions.Count - 1 ? 1 : index + 1];
            var left = positions[index] + OffsetAtJoin(previous, positions[index], next, offset - width / 2, segmentStart);
            var right = positions[index] + OffsetAtJoin(previous, positions[index], next, offset + width / 2, segmentStart);
            return (left, right);
        }
    }

    public static RoadRibbonQuad BuildSharpEndCap(Vector3 left, Vector3 right, Vector3 inward,
        float depth, bool verticalFace)
    {
        if (!Finite(left) || !Finite(right) || !Finite(inward) || !float.IsFinite(depth) || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (verticalFace)
        {
            var shift = Vector3.Normalize(inward) * depth;
            return new RoadRibbonQuad(left, right, left + shift, right + shift);
        }
        return new RoadRibbonQuad(left, right, left - Vector3.UnitY * depth, right - Vector3.UnitY * depth);
    }

    /// <summary>Only narrow vertical profile faces may be capped at a sharp route discontinuity.</summary>
    public static bool ShouldBuildSharpProfileCap(float lateralStart, float lateralEnd) =>
        float.IsFinite(lateralStart) && float.IsFinite(lateralEnd) && MathF.Abs(lateralStart - lateralEnd) < 1e-5f;

    public static bool ShouldBuildProfileAtJunction(double startDistance,double endDistance,double routeLength,
        IReadOnlyList<RoadJunctionOpening>? openings)
    {
        if(openings is null||openings.Count==0||routeLength<=0)return true;
        var middle=(startDistance+endDistance)*.5;
        foreach(var opening in openings)
        {
            var delta=Math.Abs(middle-opening.RouteDistanceMeters);
            delta=Math.Min(delta,routeLength-delta);
            if(delta<=Math.Max(0,opening.RadiusMeters))return false;
        }
        return true;
    }

    public static IReadOnlyList<RoadProfileRibbonQuad> ClipProfileQuadAtJunctions(RoadProfileRibbonQuad source,
        double routeLength,IReadOnlyList<RoadJunctionOpening>? openings)
    {
        if(openings is null||openings.Count==0||routeLength<=0)return [source];
        var retained=new List<(double Start,double End)>{(source.StartDistanceMeters,source.EndDistanceMeters)};
        foreach(var opening in openings)
        for(var lap=-1;lap<=1;lap++)
        {
            var cutStart=opening.RouteDistanceMeters+lap*routeLength-Math.Max(0,opening.RadiusMeters);
            var cutEnd=opening.RouteDistanceMeters+lap*routeLength+Math.Max(0,opening.RadiusMeters);
            var next=new List<(double Start,double End)>();
            foreach(var range in retained)
            {
                if(cutEnd<=range.Start||cutStart>=range.End){next.Add(range);continue;}
                if(cutStart>range.Start)next.Add((range.Start,Math.Min(cutStart,range.End)));
                if(cutEnd<range.End)next.Add((Math.Max(cutEnd,range.Start),range.End));
            }
            retained=next;
        }
        var duration=source.EndDistanceMeters-source.StartDistanceMeters;
        if(duration<=0)return [];
        return retained.Where(r=>r.End-r.Start>1e-5).Select(r=>
        {
            var t0=(float)((r.Start-source.StartDistanceMeters)/duration);var t1=(float)((r.End-source.StartDistanceMeters)/duration);
            var q=source.Quad;
            var clipped=new RoadRibbonQuad(Vector3.Lerp(q.StartLeft,q.EndLeft,t0),Vector3.Lerp(q.StartLeft,q.EndLeft,t1),
                Vector3.Lerp(q.StartRight,q.EndRight,t0),Vector3.Lerp(q.StartRight,q.EndRight,t1));
            return new RoadProfileRibbonQuad(source.SourceSegmentIndex,r.Start,r.End,clipped);
        }).ToArray();
    }

    /// <summary>Builds the small horizontal asphalt fan used to cover a sharp route join.</summary>
    public static IReadOnlyList<RoadRibbonTriangle> BuildSharpJoinFill(Vector3 center, float radius, int sides = 16)
    {
        if (!Finite(center) || !float.IsFinite(radius) || radius <= 0 || sides < 3)
            throw new ArgumentOutOfRangeException(nameof(radius));
        var triangles = new RoadRibbonTriangle[sides];
        for (var side = 0; side < sides; side++)
        {
            var a = side * MathF.Tau / sides;
            var b = (side + 1) * MathF.Tau / sides;
            triangles[side] = new RoadRibbonTriangle(center,
                center + new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius),
                center + new Vector3(MathF.Cos(b) * radius, 0, MathF.Sin(b) * radius));
        }
        return triangles;
    }

    /// <summary>Corner pad radius sufficient to cover the outside wedge where sharp ribbons meet.</summary>
    public static float SharpJoinFillRadius(Vector3 previous,Vector3 current,Vector3 next,float roadWidth)
    {
        if(!float.IsFinite(roadWidth)||roadWidth<=0)throw new ArgumentOutOfRangeException(nameof(roadWidth));
        var incoming=Horizontal(current-previous);var outgoing=Horizontal(next-current);
        if(incoming.LengthSquared()<1e-8f||outgoing.LengthSquared()<1e-8f)return roadWidth*.5f+.05f;
        var turn=MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(incoming),Vector3.Normalize(outgoing)),-1f,1f));
        // The fan polygon is inscribed, so its radius includes a small allowance between facets.
        // Clamp extreme near-reversals to avoid producing an oversized asphalt disk.
        var radius=roadWidth*.5f/Math.Max(.35f,MathF.Cos(turn*.5f))*1.09f+.05f;
        return Math.Min(radius,roadWidth*.8f);
    }

    /// <summary>
    /// Builds a profile ribbon while excluding a route-distance zone around every sharp join.
    /// Closed routes use their duplicate final sample to measure the wrap-side exclusion.
    /// </summary>
    public static IReadOnlyList<RoadProfileRibbonQuad> BuildProfileQuads(
        IReadOnlyList<Vector3> positions, IReadOnlyList<double> distancesMeters,
        float lateralStart, float lateralEnd, float startHeight, float endHeight,
        double exclusionMeters = 0)
    {
        if (positions.Count < 3 || positions.Count != distancesMeters.Count
            || !float.IsFinite(lateralStart) || !float.IsFinite(lateralEnd)
            || !float.IsFinite(startHeight) || !float.IsFinite(endHeight)
            || !double.IsFinite(exclusionMeters) || exclusionMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(exclusionMeters));
        for (var i = 1; i < distancesMeters.Count; i++)
            if (distancesMeters[i] <= distancesMeters[i - 1])
                throw new ArgumentException("Profile route distances must be strictly increasing.", nameof(distancesMeters));

        var routeLength = distancesMeters[^1];
        var sharpDistances = new List<double>();
        var last = positions.Count - 1;
        for (var i = 0; i < last; i++)
        {
            var previous = positions[i == 0 ? last - 1 : i - 1];
            var next = positions[i + 1];
            if (ClassifyJoin(previous, positions[i], next) == RoadJoinType.Discontinuity)
                sharpDistances.Add(distancesMeters[i]);
        }

        var exclusionZones = new List<(double Start, double End)>();
        foreach (var sharpDistance in sharpDistances)
        {
            if (exclusionMeters == 0) continue;
            if (sharpDistance <= 1e-6 || routeLength - sharpDistance <= 1e-6)
            {
                const double boundaryEpsilonMeters = 0.001;
                exclusionZones.Add((0, Math.Min(routeLength, exclusionMeters + boundaryEpsilonMeters)));
                exclusionZones.Add((Math.Max(0, routeLength - exclusionMeters - boundaryEpsilonMeters), routeLength));
            }
            else
            {
                const double boundaryEpsilonMeters = 0.001;
                exclusionZones.Add((Math.Max(0, sharpDistance - exclusionMeters - boundaryEpsilonMeters),
                    Math.Min(routeLength, sharpDistance + exclusionMeters + boundaryEpsilonMeters)));
            }
        }

        var result = new List<RoadProfileRibbonQuad>(positions.Count - 1);
        for (var segment = 0; segment < last; segment++)
        {
            var segmentStart = distancesMeters[segment];
            var segmentEnd = distancesMeters[segment + 1];
            var allowed = new List<(double Start, double End)> { (segmentStart, segmentEnd) };
            foreach (var zone in exclusionZones)
            {
                var nextAllowed = new List<(double Start, double End)>();
                foreach (var interval in allowed)
                {
                    if (zone.End <= interval.Start || zone.Start >= interval.End)
                    {
                        nextAllowed.Add(interval);
                        continue;
                    }
                    if (zone.Start > interval.Start) nextAllowed.Add((interval.Start, zone.Start));
                    if (zone.End < interval.End) nextAllowed.Add((zone.End, interval.End));
                }
                allowed = nextAllowed;
                if (allowed.Count == 0) break;
            }

            foreach (var interval in allowed)
            {
                var startT = (interval.Start - segmentStart) / (segmentEnd - segmentStart);
                var endT = (interval.End - segmentStart) / (segmentEnd - segmentStart);
                var start = ProfileSection(segment, startT);
                var end = ProfileSection(segment, endT);
                result.Add(new RoadProfileRibbonQuad(segment, interval.Start, interval.End,
                    new RoadRibbonQuad(start.Left, end.Left, start.Right, end.Right)));
            }
        }
        return result;

        (Vector3 Left, Vector3 Right) ProfileSection(int segment, double t)
        {
            var a = positions[segment];
            var b = positions[segment + 1];
            var center = Vector3.Lerp(a, b, (float)t);
            Vector3 Offset(float lateral)
            {
                if (t <= 1e-9)
                {
                    var previous = positions[segment == 0 ? last - 1 : segment - 1];
                    var next = positions[segment + 1];
                    return OffsetAtJoin(previous, a, next, lateral, true);
                }
                if (t >= 1 - 1e-9)
                {
                    var previous = positions[segment];
                    var next = positions[segment + 1 == last ? 1 : segment + 2];
                    return OffsetAtJoin(previous, b, next, lateral, false);
                }
                var forward = Horizontal(b - a);
                if (forward.LengthSquared() < 1e-8f) return new Vector3(lateral, 0, 0);
                return Vector3.Normalize(Vector3.Cross(Vector3.Normalize(forward), Vector3.UnitY)) * lateral;
            }
            return (center + Offset(lateralStart) + Vector3.UnitY * startHeight,
                center + Offset(lateralEnd) + Vector3.UnitY * endHeight);
        }
    }

    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0, value.Z);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
