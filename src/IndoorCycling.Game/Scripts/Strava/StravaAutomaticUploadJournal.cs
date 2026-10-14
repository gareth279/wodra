using System;
using System.Text.Json.Serialization;

public sealed record StravaProfileLink(Guid ProfileId, long AthleteId, string AthleteName, string Scopes, bool AutomaticUploadEnabled = false);

public sealed record StravaUploadEntry(string Key, Guid ProfileId, Guid ActivityId, int SegmentIndex, string FileHash,
    string ExternalId, StravaUploadStatus Status, long? UploadId,
    [property: JsonPropertyName("strava_activity_id")] long? StravaActivityId,
    string? ActivityUrl, string? SafeMessage, DateTimeOffset UpdatedAtUtc,
    long AthleteId = 0, bool IsAutomatic = false, string? FitPath = null,
    int RetryCount = 0, DateTimeOffset? NextAttemptAtUtc = null, int? LastHttpStatus = null);

/// <summary>Deterministic ownership and retry rules for durable automatic FIT work.</summary>
public static class StravaAutomaticUploadJournal
{
    public static bool CanResume(StravaUploadStatus status, bool automatic, bool enabled,
        long savedAthleteId, long? linkedAthleteId, DateTimeOffset? retryAtUtc, DateTimeOffset nowUtc)
    {
        if (!automatic || !enabled || linkedAthleteId is null || linkedAthleteId.Value != savedAthleteId) return false;
        if (retryAtUtc is { } due && due > nowUtc) return false;
        return status is StravaUploadStatus.Queued or StravaUploadStatus.Processing;
    }

    public static bool CanRetryAfterHttpFailure(int statusCode) => statusCode == 429;

    public static bool CanApplyRetryUpdate(StravaUploadStatus currentStatus, DateTimeOffset currentUpdatedAtUtc,
        StravaUploadStatus expectedStatus, DateTimeOffset expectedUpdatedAtUtc) =>
        currentStatus == expectedStatus && currentUpdatedAtUtc == expectedUpdatedAtUtc;

    public static bool IsTransientFailure(StravaUploadStatus journalStatus, int? httpStatus, bool networkFailure) =>
        (journalStatus is StravaUploadStatus.Queued or StravaUploadStatus.Processing ||
            journalStatus == StravaUploadStatus.Failed && httpStatus == 429) &&
        (networkFailure || httpStatus == 429 || httpStatus is >= 500);

    public static bool CanResumeAfterRestart(StravaUploadStatus status) =>
        status is StravaUploadStatus.Queued or StravaUploadStatus.Processing;
}
