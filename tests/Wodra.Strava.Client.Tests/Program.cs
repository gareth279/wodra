using System;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

var riderA = Guid.Parse("a713e6c1-9fc4-4fe1-9f1e-e0e647530813");
var riderB = Guid.Parse("98e49dfa-8636-4f35-8b51-5eb8a9f87021");
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
Check(StravaPolicies.JournalKey(riderA, Guid.NewGuid(), 0, "abc") != StravaPolicies.JournalKey(riderB, Guid.NewGuid(), 0, "abc"), "upload journal keys must include profile identity");

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
