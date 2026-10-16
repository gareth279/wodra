using System;
using System.Collections.Generic;
using System.Numerics;

/// <summary>Finds repeated physical route sections whose road surface should be emitted only once.</summary>
public static class RepeatedRouteSurfacePolicy
{
    public static bool[] FindDuplicateSegments(IReadOnlyList<Vector3> positions,
        IReadOnlyList<double> distances, double maximumLateralMatchMeters = 0.75,
        double maximumAlongTrackMatchMeters = 6,
        double maximumVerticalDifferenceMeters = 0.35, double minimumTraversalSeparationMeters = 60,
        double minimumRepeatedSpanMeters = 25)
    {
        if (positions.Count != distances.Count)
            throw new ArgumentException("Route positions and distances must have matching lengths.");
        if (!double.IsFinite(maximumLateralMatchMeters) || maximumLateralMatchMeters <= 0
            || !double.IsFinite(maximumAlongTrackMatchMeters) || maximumAlongTrackMatchMeters <= 0
            || !double.IsFinite(maximumVerticalDifferenceMeters) || maximumVerticalDifferenceMeters < 0
            || !double.IsFinite(minimumTraversalSeparationMeters) || minimumTraversalSeparationMeters < 0
            || !double.IsFinite(minimumRepeatedSpanMeters) || minimumRepeatedSpanMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLateralMatchMeters));
        if (positions.Count < 8) return new bool[Math.Max(0, positions.Count - 1)];

        var tangent = new Vector2[positions.Count];
        var cellSize = maximumAlongTrackMatchMeters + maximumLateralMatchMeters;
        var buckets = new Dictionary<(int X, int Z), List<int>>();
        for (var index = 0; index < positions.Count; index++)
        {
            var previous = positions[index == 0 ? positions.Count - 2 : index - 1];
            var next = positions[index == positions.Count - 1 ? 1 : index + 1];
            var direction = new Vector2(next.X - previous.X, next.Z - previous.Z);
            if (direction.LengthSquared() > 1e-8f) tangent[index] = Vector2.Normalize(direction);
            var key = Cell(positions[index], cellSize);
            if (!buckets.TryGetValue(key, out var indexes)) buckets.Add(key, indexes = []);
            indexes.Add(index);
        }

        var nearest = new int?[positions.Count];
        for (var index = 0; index < positions.Count; index++)
        {
            if (tangent[index].LengthSquared() < 1e-8f) continue;
            var bestLateralDistance = maximumLateralMatchMeters;
            var bestAlongDistance = maximumAlongTrackMatchMeters;
            var bestIndex = -1;
            var cell = Cell(positions[index], cellSize);
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                if (!buckets.TryGetValue((cell.X + offsetX, cell.Z + offsetZ), out var candidates)) continue;
                foreach (var other in candidates)
                {
                    if (other == index || Math.Abs(distances[other] - distances[index]) < minimumTraversalSeparationMeters
                        || tangent[other].LengthSquared() < 1e-8f) continue;
                    var dx = positions[index].X - positions[other].X;
                    var dz = positions[index].Z - positions[other].Z;
                    var along = Math.Abs(dx * tangent[index].X + dz * tangent[index].Y);
                    var lateral = Math.Abs(dx * tangent[index].Y - dz * tangent[index].X);
                    if (lateral > bestLateralDistance || along > maximumAlongTrackMatchMeters
                        || Math.Abs(positions[index].Y - positions[other].Y) > maximumVerticalDifferenceMeters
                        || Math.Abs(Vector2.Dot(tangent[index], tangent[other])) < 0.8f) continue;
                    if (lateral > bestLateralDistance || (Math.Abs(lateral - bestLateralDistance) < 1e-6 && along >= bestAlongDistance))
                        continue;
                    bestLateralDistance = lateral;
                    bestAlongDistance = along;
                    bestIndex = other;
                }
            }
            if (bestIndex >= 0) nearest[index] = bestIndex;
        }

        var pairs = new List<(int First, int Second)>();
        for (var index = 0; index < nearest.Length; index++)
        {
            if (nearest[index] is not { } other || index >= other || nearest[other] != index) continue;
            pairs.Add((index, other));
        }
        if (pairs.Count == 0) return new bool[positions.Count - 1];

        var typicalStep = MedianPositiveStep(distances);
        var maximumGapMeters = Math.Max(typicalStep * 2.5, minimumRepeatedSpanMeters / 8);
        var duplicateSegments = new bool[positions.Count - 1];
        var run = new List<(int First, int Second)>();
        foreach (var pair in pairs)
        {
            if (run.Count > 0 && !Continues(run[^1], pair, distances, maximumGapMeters))
            {
                MarkIfSustained(run, distances, minimumRepeatedSpanMeters, duplicateSegments);
                run.Clear();
            }
            run.Add(pair);
        }
        MarkIfSustained(run, distances, minimumRepeatedSpanMeters, duplicateSegments);
        return duplicateSegments;
    }

    private static bool Continues((int First, int Second) previous, (int First, int Second) current,
        IReadOnlyList<double> distances, double maximumGapMeters)
    {
        if (current.First <= previous.First || current.Second == previous.Second) return false;
        var direction = Math.Sign(current.Second - previous.Second);
        return Math.Abs(distances[current.First] - distances[previous.First]) <= maximumGapMeters
            && Math.Abs(distances[current.Second] - distances[previous.Second]) <= maximumGapMeters
            && Math.Sign(distances[current.Second] - distances[previous.Second]) == direction;
    }

    private static void MarkIfSustained(IReadOnlyList<(int First, int Second)> run,
        IReadOnlyList<double> distances, double minimumRepeatedSpanMeters, bool[] duplicateSegments)
    {
        if (run.Count < 5 || Math.Abs(distances[run[^1].First] - distances[run[0].First]) < minimumRepeatedSpanMeters
            || Math.Abs(distances[run[^1].Second] - distances[run[0].Second]) < minimumRepeatedSpanMeters) return;
        var direction = Math.Sign(run[^1].Second - run[0].Second);
        for (var index = 1; index < run.Count; index++)
        {
            var duplicate = direction > 0 ? run[index - 1].Second : run[index].Second;
            if (duplicate >= 0 && duplicate < duplicateSegments.Length) duplicateSegments[duplicate] = true;
        }
    }

    private static double MedianPositiveStep(IReadOnlyList<double> distances)
    {
        var steps = new List<double>(distances.Count - 1);
        for (var index = 1; index < distances.Count; index++)
        {
            var step = distances[index] - distances[index - 1];
            if (step > 0) steps.Add(step);
        }
        if (steps.Count == 0) return 1;
        steps.Sort();
        return steps[steps.Count / 2];
    }

    private static (int X, int Z) Cell(Vector3 position, double size) =>
        ((int)Math.Floor(position.X / size), (int)Math.Floor(position.Z / size));
}
