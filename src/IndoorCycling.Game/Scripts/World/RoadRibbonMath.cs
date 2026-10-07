using System;
using System.Collections.Generic;
using System.Numerics;

public enum RoadJoinType { Normal, Bevel, Discontinuity }

public readonly record struct RoadRibbonQuad(Vector3 StartLeft, Vector3 EndLeft, Vector3 StartRight, Vector3 EndRight);

/// <summary>Join classification and bounded horizontal offsets for canonical route ribbons.</summary>
public static class RoadRibbonMath
{
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

    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0, value.Z);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
