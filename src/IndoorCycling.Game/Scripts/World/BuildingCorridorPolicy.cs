using System;

/// <summary>Rejects only building volume that physically intersects the ride-road corridor.</summary>
public static class BuildingCorridorPolicy
{
    public static bool IntersectsRideCorridor(double footprintGapMeters, double corridorWidthMeters,
        double routeElevationMeters, double footprintBaseMinimumMeters, double footprintBaseMaximumMeters,
        double buildingHeightMeters)
    {
        if (!double.IsFinite(footprintGapMeters) || footprintGapMeters < 0
            || !double.IsFinite(corridorWidthMeters) || corridorWidthMeters <= 0
            || !double.IsFinite(routeElevationMeters)
            || !double.IsFinite(footprintBaseMinimumMeters)
            || !double.IsFinite(footprintBaseMaximumMeters)
            || footprintBaseMinimumMeters > footprintBaseMaximumMeters
            || !double.IsFinite(buildingHeightMeters) || buildingHeightMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(footprintGapMeters));

        var overlapsHorizontally = footprintGapMeters < corridorWidthMeters / 2;
        var overlapsVertically = routeElevationMeters >= footprintBaseMinimumMeters
            && routeElevationMeters <= footprintBaseMaximumMeters + buildingHeightMeters;
        return overlapsHorizontally && overlapsVertically;
    }
}
