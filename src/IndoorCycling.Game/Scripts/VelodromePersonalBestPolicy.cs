using System;
using System.Collections.Generic;

public static class VelodromePersonalBestPolicy
{
    public static string Key(Guid profileId, RouteDefinition route, RideActivityMode mode) =>
        $"{profileId:N}/{route.Id}/v{route.RouteDataVersion}/{mode}";

    public static bool TrySave(IDictionary<string, double> values, Guid? profileId, RouteDefinition route,
        RideActivityMode mode, TrackEffortResult result, bool testProfile, bool debugRide, bool dummyRide)
    {
        if (profileId is null || route.Environment != RouteEnvironment.Velodrome || !result.Completed ||
            !result.Eligible || testProfile || debugRide || dummyRide || !double.IsFinite(result.ElapsedSeconds) || result.ElapsedSeconds <= 0)
            return false;
        var key = Key(profileId.Value, route, mode);
        if (values.TryGetValue(key, out var existing) && existing <= result.ElapsedSeconds) return false;
        values[key] = result.ElapsedSeconds;
        return true;
    }
}
