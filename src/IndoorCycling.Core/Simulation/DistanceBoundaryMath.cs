namespace IndoorCycling.Core.Simulation;

/// <summary>Shared interpolation rules for interval efforts and timed track efforts.</summary>
public static class DistanceBoundaryMath
{
    public static bool Crossed(double beforeMeters, double afterMeters, double boundaryMeters) =>
        double.IsFinite(beforeMeters) && double.IsFinite(afterMeters) && double.IsFinite(boundaryMeters) &&
        beforeMeters < boundaryMeters && afterMeters >= boundaryMeters;

    public static double InterpolateElapsed(double beforeMeters, double afterMeters,
        double beforeElapsedSeconds, double afterElapsedSeconds, double boundaryMeters) =>
        afterMeters <= beforeMeters ? afterElapsedSeconds : beforeElapsedSeconds +
        (afterElapsedSeconds - beforeElapsedSeconds) * Math.Clamp(
            (boundaryMeters - beforeMeters) / (afterMeters - beforeMeters), 0, 1);
}
