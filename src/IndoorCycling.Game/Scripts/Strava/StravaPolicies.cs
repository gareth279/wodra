using System;
using System.Collections.Generic;

// Preserve the original persisted journal numbers for alpha.14 entries.
public enum StravaUploadStatus { Submitting = 0, Unknown = 1, Processing = 2, Complete = 3, Failed = 4, Queued = 5, Cancelled = 6 }
public enum StravaOAuthOutcome { Authorized, Cancelled, Invalid, Incomplete }

/// <summary>Small deterministic rules shared by the Strava client and offline regression checks.</summary>
public static class StravaPolicies
{
    public static string CredentialTarget(Guid profileId) => $"Wodra.Strava.Profile.{profileId:N}";

    public static StravaOAuthOutcome ClassifyCallback(bool stateMatches, string? error, string? code)
    {
        if (!stateMatches) return StravaOAuthOutcome.Invalid;
        if (string.Equals(error, "access_denied", StringComparison.Ordinal)) return StravaOAuthOutcome.Cancelled;
        return string.IsNullOrWhiteSpace(code) ? StravaOAuthOutcome.Incomplete : StravaOAuthOutcome.Authorized;
    }

    public static string UploadUnavailableReason(bool linked, bool hasValidFit) => !hasValidFit
        ? "No valid saved FIT file exists for this ride."
        : !linked ? "Link Strava in Manage Profiles for the rider who recorded this ride." : "";

    public static bool MaySubmitAgain(StravaUploadStatus? status) => status is null or StravaUploadStatus.Failed or StravaUploadStatus.Queued or StravaUploadStatus.Cancelled;

    public static bool ShouldAutomaticallyUpload(bool enabled, bool linked, bool testProfile, bool syntheticRide, bool hasValidFit) =>
        enabled && linked && !testProfile && !syntheticRide && hasValidFit;

    public static TimeSpan AutomaticRetryDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(30)
    };

    public static string JournalKey(Guid profileId, Guid rideId, int segmentIndex, string hash) =>
        $"{profileId:D}/{rideId:N}/{segmentIndex}/{hash}";
}
