using System;
using System.Collections.Generic;

public enum StravaUploadStatus { Submitting, Unknown, Processing, Complete, Failed }
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

    public static bool MaySubmitAgain(StravaUploadStatus? status) => status is null or StravaUploadStatus.Failed;

    public static string JournalKey(Guid profileId, Guid rideId, int segmentIndex, string hash) =>
        $"{profileId:D}/{rideId:N}/{segmentIndex}/{hash}";
}
