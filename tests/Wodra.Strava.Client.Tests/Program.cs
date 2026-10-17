using System;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

var riderA = Guid.Parse("a713e6c1-9fc4-4fe1-9f1e-e0e647530813");
var riderB = Guid.Parse("98e49dfa-8636-4f35-8b51-5eb8a9f87021");
var defaultUploadMetadata = StravaPolicies.BuildActivityMetadata();
Check(defaultUploadMetadata.Name == "Wodra Ride" && defaultUploadMetadata.Description.Contains(StravaPolicies.ReleasesUrl),
    "manual and automatic uploads should use Wodra's default name and releases link");
var explicitUploadMetadata = StravaPolicies.BuildActivityMetadata("  Hill repeats  ", "Signal Hill · 5 laps");
Check(explicitUploadMetadata.Name == "Hill repeats" && explicitUploadMetadata.Description.StartsWith("Signal Hill · 5 laps") &&
    explicitUploadMetadata.Description.Contains(StravaPolicies.ReleasesUrl),
    "explicit activity names and existing useful description metadata should be preserved");
var linkedDescription = StravaPolicies.BuildActivityMetadata(null, $"{StravaPolicies.ReleasesUrl} · recorded with Wodra");
Check(linkedDescription.Description.Split(StravaPolicies.ReleasesUrl).Length == 2,
    "an existing releases link should not be duplicated");
Check(StravaPolicies.CredentialTarget(riderA) != StravaPolicies.CredentialTarget(riderB), "profile credentials must have isolated targets");
Check(StravaPolicies.CredentialTarget(riderA).EndsWith(riderA.ToString("N")), "credential target must use stable profile GUID");
Check(StravaPolicies.UploadUnavailableReason(false, true).Contains("Manage Profiles"), "unlinked profile must explain how to link");
Check(StravaPolicies.UploadUnavailableReason(true, false).Contains("FIT"), "ride without FIT must explain why upload is disabled");
Check(StravaPolicies.UploadUnavailableReason(true, true).Length == 0, "valid saved FIT with linked profile should enable upload");
Check(StravaPolicies.ClassifyCallback(false, null, "valid-code") == StravaOAuthOutcome.Invalid, "OAuth state mismatch must fail");
Check(StravaPolicies.ClassifyCallback(true, "access_denied", null) == StravaOAuthOutcome.Cancelled, "denied consent must be handled as cancellation");
Check(StravaPolicies.ClassifyCallback(true, null, null) == StravaOAuthOutcome.Incomplete, "missing code must not link");
Check(StravaPolicies.ClassifyCallback(true, null, "code") == StravaOAuthOutcome.Authorized, "valid callback should proceed");
Check(StravaPolicies.MaySubmitAgain(null) && StravaPolicies.MaySubmitAgain(StravaUploadStatus.Failed), "new and definite failure may submit");
Check(!StravaPolicies.MaySubmitAgain(StravaUploadStatus.Submitting) && !StravaPolicies.MaySubmitAgain(StravaUploadStatus.Unknown), "ambiguous submission must block duplicate retry");
Check(!StravaPolicies.MaySubmitAgain(StravaUploadStatus.Processing) && !StravaPolicies.MaySubmitAgain(StravaUploadStatus.Complete), "pending and complete upload must not resubmit");
Check(StravaPolicies.MaySubmitAgain(StravaUploadStatus.Queued), "durable queued upload can be claimed once");
Check(StravaPolicies.ShouldAutomaticallyUpload(true, true, false, false, true), "eligible real linked ride is queued");
Check(!StravaPolicies.ShouldAutomaticallyUpload(true, true, true, false, true) && !StravaPolicies.ShouldAutomaticallyUpload(true, true, false, true, true), "test profiles and synthetic rides are excluded");
Check(!StravaPolicies.ShouldAutomaticallyUpload(true, false, false, false, true) && !StravaPolicies.ShouldAutomaticallyUpload(true, true, false, false, false), "unlinked and missing FIT rides are excluded");
Check(StravaPolicies.AutomaticRetryDelay(1) < StravaPolicies.AutomaticRetryDelay(2), "automatic retry backoff increases");
var profileLinkJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, StravaProfileLink>
{
    [riderA.ToString("D")] = new StravaProfileLink(riderA, 42, "Athlete", "read,activity:write", true),
    [riderB.ToString("D")] = new StravaProfileLink(riderB, 43, "Second Athlete", "read,activity:write")
});
var restoredLinks = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, StravaProfileLink>>(profileLinkJson)!;
Check(restoredLinks[riderA.ToString("D")].AutomaticUploadEnabled && !restoredLinks[riderB.ToString("D")].AutomaticUploadEnabled,
    "automatic upload preference persists per profile and defaults off");
var now = DateTimeOffset.UtcNow;
Check(StravaAutomaticUploadJournal.CanResume(StravaUploadStatus.Queued, true, true, 42, 42, null, now), "queued work resumes for its recorded athlete");
Check(!StravaAutomaticUploadJournal.CanResume(StravaUploadStatus.Queued, true, true, 42, 43, null, now), "relinking to another athlete cannot transfer queued work");
Check(!StravaAutomaticUploadJournal.CanResume(StravaUploadStatus.Queued, true, false, 42, 42, null, now), "disabling automatic upload stops queued work");
Check(!StravaAutomaticUploadJournal.CanResume(StravaUploadStatus.Queued, true, true, 42, 42, now.AddMinutes(1), now), "offline retry waits for its backoff deadline");
Check(StravaAutomaticUploadJournal.CanResumeAfterRestart(StravaUploadStatus.Queued) && StravaAutomaticUploadJournal.CanResumeAfterRestart(StravaUploadStatus.Processing), "queued and known upload IDs survive restart");
Check(!StravaAutomaticUploadJournal.CanResumeAfterRestart(StravaUploadStatus.Unknown) && StravaAutomaticUploadJournal.CanRetryAfterHttpFailure(429) && !StravaAutomaticUploadJournal.CanRetryAfterHttpFailure(403), "ambiguous work is not retried and only rate limits auto retry");
Check(StravaAutomaticUploadJournal.IsTransientFailure(StravaUploadStatus.Queued, 429, false) &&
    StravaAutomaticUploadJournal.IsTransientFailure(StravaUploadStatus.Failed, 429, false) &&
    StravaAutomaticUploadJournal.IsTransientFailure(StravaUploadStatus.Queued, null, true) &&
    StravaAutomaticUploadJournal.IsTransientFailure(StravaUploadStatus.Processing, 503, false), "safe pre-submission network and known-upload failures can retry");
Check(!StravaAutomaticUploadJournal.IsTransientFailure(StravaUploadStatus.Queued, 403, false) &&
    !StravaAutomaticUploadJournal.IsTransientFailure(StravaUploadStatus.Unknown, 503, false), "access failures and ambiguous submissions never auto retry");
Check(StravaAutomaticUploadJournal.CanApplyRetryUpdate(StravaUploadStatus.Failed, now, StravaUploadStatus.Failed, now) &&
    !StravaAutomaticUploadJournal.CanApplyRetryUpdate(StravaUploadStatus.Submitting, now.AddTicks(1), StravaUploadStatus.Failed, now) &&
    !StravaAutomaticUploadJournal.CanApplyRetryUpdate(StravaUploadStatus.Complete, now.AddTicks(1), StravaUploadStatus.Failed, now),
    "background retry updates cannot overwrite a manual claim or completed upload");
var queued = new StravaUploadEntry("key", riderA, Guid.NewGuid(), 0, "hash", "external", StravaUploadStatus.Queued,
    null, null, null, null, now, 42, true, "ride.fit", 2, now.AddMinutes(5));
var restoredQueue = System.Text.Json.JsonSerializer.Deserialize<StravaUploadEntry>(System.Text.Json.JsonSerializer.Serialize(queued))!;
Check(restoredQueue.Status == StravaUploadStatus.Queued && restoredQueue.IsAutomatic && restoredQueue.AthleteId == 42 && restoredQueue.NextAttemptAtUtc == queued.NextAttemptAtUtc,
    "durable queued segment restores its owner, retry time, and safe status");
Check(StravaPolicies.JournalKey(riderA, Guid.NewGuid(), 0, "abc") != StravaPolicies.JournalKey(riderB, Guid.NewGuid(), 0, "abc"), "upload journal keys must include profile identity");

var forbidden = StravaUploadDiagnostic.Parse(403, "{\"message\":\"This application has been deactivated\",\"errors\":[{\"resource\":\"Application\",\"field\":\"access\",\"code\":\"not_authorized\"}]}");
Check(forbidden.Kind == StravaUploadFailureKind.ApplicationAccessRestricted, "recognized app access restriction should be classified");
Check(forbidden.Resource == "Application" && forbidden.Field == "access" && forbidden.Code == "not_authorized", "recognized Strava diagnostic fields should be retained");
var scope = StravaUploadDiagnostic.Parse(403, "{\"errors\":[{\"resource\":\"Athlete\",\"field\":\"activity:write\",\"code\":\"missing\"}]}");
Check(scope.Kind == StravaUploadFailureKind.MissingActivityWriteScope, "missing activity:write should be classified");
Check(StravaUploadDiagnostic.Parse(401, "{}").Kind == StravaUploadFailureKind.ExpiredAuthorization, "401 should identify expired authorization");
Check(StravaUploadDiagnostic.Parse(429, "{}").Kind == StravaUploadFailureKind.RateLimited, "429 should identify rate limiting");
Check(StravaUploadDiagnostic.Parse(400, "{\"errors\":[{\"resource\":\"Upload\",\"field\":\"file\",\"code\":\"duplicate\"}]}").Kind == StravaUploadFailureKind.DuplicateOrFileProcessing, "duplicate/file errors should be classified");
Check(StravaUploadDiagnostic.ParseProcessingError("This activity is a duplicate of an existing activity").Kind == StravaUploadFailureKind.DuplicateOrFileProcessing, "asynchronous duplicate processing errors should be classified");
var unknown = StravaUploadDiagnostic.Parse(403, "{\"message\":\"secret Bearer should-not-appear\",\"errors\":[{\"resource\":\"HiddenValue\",\"field\":\"password\",\"code\":\"unknown_secret\"}]}");
Check(unknown.Kind == StravaUploadFailureKind.Unknown && unknown.Resource is null && unknown.Field is null && unknown.Code is null, "unknown error values must not be retained");
Check(!unknown.UserMessage.Contains("should-not-appear", StringComparison.Ordinal), "raw Strava response text must not be shown to the rider");
Check(StravaUploadDiagnostic.Parse(403, "{\"errors\":[", truncated: true).Kind == StravaUploadFailureKind.Unknown, "truncated response should produce a safe unknown diagnosis");

var temp = Path.Combine(Path.GetTempPath(), "wodra-fit-segments-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var ride = Guid.NewGuid();
    var firstFit = Path.Combine(temp, "ride-segment-1.fit");
    File.WriteAllBytes(firstFit, [1, 2, 3]);
    var fallbackFit = Path.Combine(temp, "ride.fit");
    File.WriteAllBytes(fallbackFit, [4, 5, 6]);
    File.WriteAllText(Path.Combine(temp, $"{ride:N}.segments.json"),
        "{\"segments\":[{\"FileName\":\"ride-segment-1.fit\"},{\"FileName\":\"missing.fit\"},{\"FileName\":\"wrong.txt\"},{\"FileName\":\"../outside.fit\"}]}");
    var segments = StravaFitSegmentReader.Read(ride, fallbackFit);
    Check(segments.Count == 4, "manifest must keep every recorded segment visible even when a file is missing or invalid");
    Check(segments[0].IsValid && segments[0].Path == firstFit, "valid FIT segment should be uploadable");
    Check(!segments[1].IsValid && segments[1].DisplayName == "missing.fit", "missing segment should remain visible and disabled");
    Check(!segments[2].IsValid && !segments[3].IsValid, "wrong file type and path traversal must never be uploaded");
    File.WriteAllText(Path.Combine(temp, $"{ride:N}.segments.json"), "not-json");
    var corrupt = StravaFitSegmentReader.Read(ride, fallbackFit);
    Check(corrupt.Count == 1 && !corrupt[0].IsValid, "corrupt manifest must not silently fall back and omit recorded segments");
    File.Delete(Path.Combine(temp, $"{ride:N}.segments.json"));
    Check(StravaFitSegmentReader.Read(ride, fallbackFit).Count == 1, "legacy single FIT activity without a manifest should remain uploadable");
}
finally { Directory.Delete(temp, recursive: true); }

Console.WriteLine($"PASS: {checks} Strava client policy checks");
