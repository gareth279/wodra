using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;
using System.Globalization;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("Zero-power flat riding decelerates and settles at rest", ZeroPowerFlatDeceleration),
    ("Zero-power downhill riding accelerates when gravity exceeds losses", ZeroPowerDownhillCoasting),
    ("Uphill speed is lower than flat speed at equal power", UphillSlowerThanFlat),
    ("Higher power produces greater acceleration", HigherPowerAcceleratesMore),
    ("A 97 kg system starts at 200 W on a 10% climb", TenPercentHillStart),
    ("A rider starts at reasonable power on an 8% climb", EightPercentHillStart),
    ("Zero power on an uphill does not create motion", ZeroPowerUphillStart),
    ("Low-speed reference is configurable and speed is used above it", LaunchReferenceIsConfigurable),
    ("Speed never becomes negative", SpeedNeverNegative),
    ("Trainer speed telemetry does not determine game speed", TrainerSpeedIsIgnored),
	("HUD telemetry uses live ride state and simulated speed", HudMetricsUseRideState),
	("HUD marks stale rider sensors unavailable", HudMetricsMarkStaleSensors),
    ("Mass and bike resistance profile affect acceleration", ProfilesAffectPhysics),
    ("Ride pause freezes progress and resumes without a distance jump", RidePauseSemantics),
    ("Repeated ride pause action toggles explicit ride state", RidePauseToggle),
    ("Debug mode off emits no periodic snapshots", DebugModeOff),
    ("fallback building heights and roof profiles vary deterministically", BuildingFallbackVariation),
    ("procedural facade windows vary by OSM identity and remain repeatable", BuildingFacadeVariation),
    ("Debug snapshot contains ride state and calculated lap position", DebugSnapshotFields),
    ("Debug marker numbering increments", DebugMarkerNumbers),
    ("Screenshot filename and marker sidecar share collision-safe names", ScreenshotNames),
    ("Screenshot sidecar contains compact ride metadata", ScreenshotMetadataFields),
    ("F12 selects clean or visible HUD capture", ScreenshotModeSelection),
    ("Screenshot directory falls back when Pictures is unavailable", ScreenshotDirectoryFallback),
    ("Screenshot failure does not interrupt debug marker creation", ScreenshotFailureKeepsMarker),
    ("Debug distance jump preserves elapsed ride state and supports later laps", DebugDistanceJump)
    ,("Remote riders interpolate absolute lap progress and telemetry", RemoteRiderInterpolation)
    ,("Remote rider reposition clears the old interpolation path", RemoteRiderRepositionReset)
    ,("Stale remote rider tracks stop producing render positions", RemoteRiderStaleness)
    ,("FIT recording segment rebases route distance after catch-up", RecordingSegmentRebasesAfterTeleport)
    ,("Catch-up position stays behind absolute distance across laps and route seams", CatchUpRoutePosition)
	,("Human rider relative distance is signed and wraps at lap boundaries", SignedRemoteRiderDistance)
    ,("Catch-up matches a fresh remote speed and leaves subsequent motion to local physics", CatchUpMatchesFreshRemoteSpeed)
    ,("Catch-up speed falls back safely for stale invalid stationary and paused targets", CatchUpSpeedFallbacks)
    ,("Trainer difficulty scales signed grades and preserves actual ride physics", TrainerDifficultyScalesSignedGrades)
    ,("Catch-up grade transition is gradual and safely replaceable", CatchUpTrainerGradeTransition)
    ,("Trainer readiness requires successful setup and ignores stale attempt results", TrainerConnectionReadiness)
    ,("Bluetooth discovery automatically selects one and remembers the preferred trainer", TrainerDiscoverySelection)
    ,("Bluetooth discovery asks riders to choose among multiple compatible trainers", TrainerDiscoveryMultipleSelection)
    ,("FTMS parser reads optional fields and distinguishes unsupported metrics", FtmsTelemetryParsing)
    ,("Telemetry presentation distinguishes zero awaiting unsupported and stale readings", TrainerTelemetryPresentationStates)
    ,("Stale trainer readings cannot continue to drive rider power or cadence", StaleTelemetryIsNotRiderInput)
    ,("Trainer recovery is bounded and never follows an intentional disconnect", TrainerRecoveryIsBounded)
    ,("GATT discovery initiates the link before connection status is required", GattDiscoveryStartsConnection)
    ,("Paused catch-up retains matched resume speed without paused movement", PausedCatchUpResumeSpeed)
    ,("Startup online status stays compact for offline configured and unavailable states", StartupOnlineStatusIsCompact)
    ,("EOS lobby UI status distinguishes membership and P2P phases", EosLobbyStatusPhases)
    ,("EOS P2P requests are deferred until lobby membership is confirmed", EosPeerRequestWaitsForMembership)
    ,("EOS receive buffer capacity matches MaxDataSizeBytes and bounds malformed sizes", EosReceiveBufferSizing)
    ,("EOS Product User ID diagnostics are stable truncated hashes", EosProductUserHashesArePrivate)
    ,("EOS client configuration loads file values and environment overrides", EosConfigurationPrecedence)
    ,("EOS client configuration reports missing and malformed sources safely", EosConfigurationValidation)
    ,("Packaged route directory resolves relative to the exported application", PackagedRouteDirectoryResolution)
    ,("Controller preference defaults to auto-detect and round-trips in settings JSON", ControllerDevicePreferencePersists)
    ,("Settings returns to its connection caller only before an active ride", SettingsReturnRespectsEntryContext)
    ,("Lateral steering applies dead zone and holds on neutral input", SteeringDeadZoneAndNeutralHold)
    ,("Keyboard steering is bounded, prioritized, and held on release", KeyboardSteeringPriorityAndBounds)
    ,("Stale Sterzo input recentres unless keyboard owns the position", SteeringStaleInputRecovery)
    ,("Steering pauses safely and respects text focus", SteeringPauseAndTextFocus)
    ,("Steering sensitivity and dead zone persist with bounded settings", SteeringSettingsPersist)
    ,("Window placement is clamped to a visible display and chooses a remaining monitor", WindowPlacementRestoresSafely)
    ,("Shared steering sensitivity persists and scales keyboard and Sterzo response", SteeringSensitivityIsShared)
    ,("Collectible layout is deterministic and includes blue and rare gold cats", CollectibleLayoutIsDeterministic)
    ,("Collectible swept hit catches frame crossing and rejects lateral misses", CollectibleSweptDetection)
    ,("Collectible attempts prevent duplicate awards and flag practice eligibility", CollectibleAttemptEligibility)
	,("Collectible user-facing text uses artwork-agnostic token wording", CollectibleUiUsesTokenWording)
    ,("Diagnostic log sanitizer removes tokens secrets keys and local paths", DiagnosticLogSanitization)
    ,("Diagnostic archive includes selected files without changing sources and enforces its bound", DiagnosticArchiveWriting)
	,("Diagnostic redaction handles profile path variants and keeps JSONL valid", DiagnosticRedactionPreservesJsonl)
	,("Diagnostic ZIP prioritizes bounded tails of current logs before recent history", DiagnosticCurrentLogsArePrioritized)
	,("Diagnostic collector reads active sessions and reports missing or locked logs", DiagnosticCurrentLogReadFailures)
	,("Diagnostic session catalog selects matching Wodra, rotated Godot, and marker files", DiagnosticSessionFilesAreScoped)
	,("Explorer navigation normalizes mixed Windows separators", ExplorerPathNormalization)
    ,("Game features default off and persist independently per profile", GameFeaturesPersistPerProfile)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS: {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL: {test.Name}: {ex.Message}"); }
}
return failed == 0 ? 0 : 1;

static void ControllerDevicePreferencePersists()
{
    var settings = new TestControllerPreference();
    Assert(settings.DeviceType == ControllerDeviceTypePreference.AutoDetect, "Auto-detect must be the default.");
    settings.DeviceType = ControllerDeviceTypePreference.EliteSterzoSmart;
    var restored = JsonSerializer.Deserialize<TestControllerPreference>(JsonSerializer.Serialize(settings));
    Assert(restored?.DeviceType == ControllerDeviceTypePreference.EliteSterzoSmart,
        "The explicit existing Sterzo adapter selection must survive settings serialization.");
}

static void CollectibleLayoutIsDeterministic()
{
    var samples = Enumerable.Range(0, 1001).Select(i => ((double)i * 10, System.Numerics.Vector3.UnitZ)).ToArray();
    var first = CollectibleChallenge.Generate("test-road", 10000, samples);
    var second = CollectibleChallenge.Generate("test-road", 10000, samples);
    Assert(first.SequenceEqual(second), "The same route and challenge version must produce identical pickup placements.");
    Assert(first.Any(p => p.Kind == CollectibleKind.BlueCat && p.Points == 1), "The layout needs one point blue cats.");
    Assert(first.Any(p => p.Kind == CollectibleKind.GoldenCat && p.Points == 5), "The layout needs five point rare gold cats.");
    Assert(!first.SequenceEqual(CollectibleChallenge.Generate("other-road", 10000, samples)), "Route identity must be part of the deterministic seed.");

    var uTurnSamples = Enumerable.Range(0, 91).Select(i => (i * 10d,
        i < 36 ? System.Numerics.Vector3.UnitZ : -System.Numerics.Vector3.UnitZ)).ToArray();
    Assert(CollectibleChallenge.Generate("u-turn", 900, uTurnSamples).All(p => Math.Abs(p.RouteDistanceMeters - 360) > 120),
        "A sharp reversal must not receive a pickup at the U-turn.");
}

static void CollectibleSweptDetection()
{
    var pickup = new CollectiblePlacement("v1:road:100", 100, 0.5, CollectibleKind.BlueCat, 1);
    Assert(CollectibleChallenge.SweptHit(95, 105, 0, 1, pickup, 1000, false), "Crossing between frames should collect when the interpolated lateral path hits.");
    Assert(!CollectibleChallenge.SweptHit(95, 105, 4, 4, pickup, 1000, false), "A rider outside lateral clearance must miss.");
    Assert(!CollectibleChallenge.SweptHit(105, 95, 0.5, 0.5, pickup, 1000, false), "Reverse re-entry must not collect.");
}

static void CollectibleAttemptEligibility()
{
    var pickup = new CollectiblePlacement("v1:road:100", 100, 0, CollectibleKind.GoldenCat, 5);
    var attempt = new CollectibleAttemptState();
    Assert(attempt.Award(pickup, 0), "First pickup identity should award.");
    Assert(!attempt.Award(pickup, 0) && attempt.Score == 5, "Duplicate award in the same lap must be ignored.");
    Assert(attempt.Award(pickup, 1) && attempt.Score == 10, "A new explicit lap identity may award the pickup once.");
    attempt.MarkPractice(toggled: true);
    var result = attempt.CreateResult("road", "fingerprint", 1, 1, 3, true, 90);
    Assert(!result.EligibleForPersonalBest && result.GameFeatureToggledDuringRide && result.SchemaVersion == 1,
        "Toggled attempts must retain a versioned result and be ineligible for PB comparison.");
    var partial = new CollectibleAttemptState().CreateResult("road", "fingerprint", 1, 1, 3, false, 30);
    Assert(!partial.EligibleForPersonalBest, "Partial rides must remain ineligible.");
}

static void DiagnosticLogSanitization()
{
	const string input = "GET https://host/path?access_token=token-sentinel&safe=yes clientSecret=secret-sentinel ANT_NETWORK_KEY=ant-key-sentinel Authorization: Bearer opaque-token C:\\Users\\rider\\logs";
	var safe = DiagnosticLogSanitizer.Redact(input, @"C:\Users\rider");
	Assert(!safe.Contains("token-sentinel", StringComparison.Ordinal) && !safe.Contains("secret-sentinel", StringComparison.Ordinal) &&
		!safe.Contains("ant-key-sentinel", StringComparison.Ordinal) && !safe.Contains("opaque-token", StringComparison.Ordinal),
		"Known secret values and bearer tokens must be removed.");
	Assert(safe.Contains("safe=yes", StringComparison.Ordinal) && safe.Contains("%USERPROFILE%", StringComparison.Ordinal),
		"Non-sensitive URL fields should be kept and local profile paths should be replaced.");
}

static void CollectibleUiUsesTokenWording()
{
	Assert(CollectibleUiText.HelperText == "Collect tokens and earn points while you ride.",
		"The pre-ride and Settings helper must use the agreed generic wording.");
	Assert(CollectibleUiText.Score(7, 3, 12).StartsWith("Tokens ·", StringComparison.Ordinal),
		"The ride score should refer to tokens.");
	Assert(CollectibleUiText.PickupFeedback("Blue", 1, 7).Contains("token", StringComparison.OrdinalIgnoreCase) &&
		CollectibleUiText.PickupFeedback("Golden", 5, 12).Contains("token", StringComparison.OrdinalIgnoreCase),
		"Both pickup feedback types should refer to tokens.");
	Assert(CollectibleUiText.ResultSummary(12, 4, 20, "completed").Contains("tokens", StringComparison.Ordinal),
		"Ride summaries should refer to tokens.");
}

static void DiagnosticRedactionPreservesJsonl()
{
	const string profile = @"C:\Users\Gareth WoodΩ";
	var plain = "forward C:/Users/gareth woodω/AppData/Local/log.json and escaped C:\\\\Users\\\\GARETH WOODΩ\\\\Documents\\\\ride.json";
	var redactedPlain = DiagnosticLogSanitizer.Redact(plain, profile);
	Assert(!redactedPlain.Contains("Gareth Wood", StringComparison.OrdinalIgnoreCase) &&
		redactedPlain.Contains("%USERPROFILE%/AppData/Local/log.json", StringComparison.Ordinal) &&
		redactedPlain.Contains("%USERPROFILE%\\\\Documents\\\\ride.json", StringComparison.Ordinal),
		"Forward-slash and JSON-escaped profile paths must redact the username while keeping path suffixes.");

	var jsonl = JsonSerializer.Serialize(new
	{
		path = @"C:\Users\GARETH WOODΩ\AppData\Roaming\session.jsonl",
		nested = new { file = "C:/Users/gareth woodω/Documents/ride.fit" },
		token = "credential-sentinel",
		message = "Authorization: Bearer bearer-sentinel"
	});
	var safeJsonl = DiagnosticLogSanitizer.RedactJsonLine(jsonl, profile);
	using var parsed = JsonDocument.Parse(safeJsonl);
	var serializedSafe = parsed.RootElement.GetProperty("path").GetString()!;
	Assert(serializedSafe.StartsWith("%USERPROFILE%", StringComparison.Ordinal) &&
		parsed.RootElement.GetProperty("nested").GetProperty("file").GetString()!.StartsWith("%USERPROFILE%", StringComparison.Ordinal),
		"Structured JSON string values must redact Windows paths case-insensitively and retain suffixes.");
	Assert(parsed.RootElement.GetProperty("token").GetString() == "[REDACTED]" &&
		!safeJsonl.Contains("credential-sentinel", StringComparison.Ordinal) && !safeJsonl.Contains("bearer-sentinel", StringComparison.Ordinal),
		"Structured credential fields and bearer values must remain redacted.");
	Assert(!safeJsonl.Contains("Gareth Wood", StringComparison.OrdinalIgnoreCase) &&
		!safeJsonl.Contains("gareth wood", StringComparison.OrdinalIgnoreCase),
		"Sanitized JSONL must not retain the profile username.");
}

static void DiagnosticCurrentLogsArePrioritized()
{
	var root = Path.Combine(Path.GetTempPath(), "WodraDiagnosticPriorityTests", Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(root);
	try
	{
		const string profile = @"C:\Users\Unicode Rider Ω";
		var currentJsonl = Path.Combine(root, "current.jsonl");
		var currentGodot = Path.Combine(root, "godot.log");
		var newest = Path.Combine(root, "older-newest.jsonl");
		var middle = Path.Combine(root, "older-middle.jsonl");
		var oldest = Path.Combine(root, "older-oldest.jsonl");
		File.WriteAllText(currentJsonl, string.Concat(Enumerable.Range(0, 80).Select(index =>
			JsonSerializer.Serialize(new { eventName = "current-session", index, message = new string('x', 700) + "🚴", path = profile + @"\AppData\Local\current.jsonl" }) + "\n")));
		File.WriteAllText(currentGodot, string.Join("\n", Enumerable.Range(0, 80).Select(index =>
			$"Godot line {index} {new string('g', 700)} 🚴 current-godot-marker-{index}")) + "\n", new System.Text.UTF8Encoding(false));
		void WriteOld(string path, int id) => File.WriteAllText(path, string.Concat(Enumerable.Range(0, 16).Select(index =>
			JsonSerializer.Serialize(new { eventName = "older-session", id, index, message = new string((char)('a' + id), 700) }) + "\n")));
		WriteOld(newest, 1); WriteOld(middle, 2); WriteOld(oldest, 3);
		var reserved = new (string Name, byte[] Data)[]
		{
			("build.txt", System.Text.Encoding.UTF8.GetBytes("version=test\n")),
			("settings-sanitized.json", System.Text.Encoding.UTF8.GetBytes("{}"))
		};
		var collection = DiagnosticInputCollector.Collect(reserved,
			new[] { new DiagnosticLogSource(currentJsonl, "logs/current-session.jsonl", true), new DiagnosticLogSource(currentGodot, "logs/godot.log", false) },
			new[] { new DiagnosticLogSource(newest, "logs/recent-newest.jsonl", true), new DiagnosticLogSource(middle, "logs/recent-middle.jsonl", true), new DiagnosticLogSource(oldest, "logs/recent-oldest.jsonl", true) },
			profile, 16 * 1024, 15 * 1024, 4 * 1024, manifestReserveBytes: 512);
		Assert(collection.Files.Any(file => file.Name == "logs/current-session.jsonl") && collection.Files.Any(file => file.Name == "logs/godot.log"),
			"Both oversized current log types must survive within their reserved allocation.");
		Assert(collection.Files.Any(file => file.Name == "logs/recent-newest.jsonl") && collection.Files.Any(file => file.Name == "logs/recent-middle.jsonl") &&
			!collection.Files.Any(file => file.Name == "logs/recent-oldest.jsonl"),
			"Remaining capacity must go to recent older sessions in newest-first order.");
		var sessionText = System.Text.Encoding.UTF8.GetString(collection.Files.Single(file => file.Name == "logs/current-session.jsonl").Data);
		Assert(sessionText.Contains("current-session", StringComparison.Ordinal), "The current JSONL tail should include records from the current session.");
		Assert(!sessionText.Contains("Unicode Rider", StringComparison.Ordinal), "The current JSONL tail must redact profile paths.");
		var parsedMessages = new List<string>();
		foreach (var line in sessionText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			using var parsedLine = JsonDocument.Parse(line);
			parsedMessages.Add(parsedLine.RootElement.GetProperty("message").GetString()!);
		}
		Assert(parsedMessages.Any(message => message.Contains("🚴", StringComparison.Ordinal)), "The current JSONL tail should preserve complete UTF-8 characters.");
		var godotText = System.Text.Encoding.UTF8.GetString(collection.Files.Single(file => file.Name == "logs/godot.log").Data);
		Assert(godotText.Contains("current-godot-marker", StringComparison.Ordinal) && !godotText.Contains('\uFFFD'),
			"The bounded current Godot log tail must remain useful UTF-8 text.");
		Assert(collection.Omissions.Any(item => item.Contains("truncated", StringComparison.OrdinalIgnoreCase)) &&
			collection.Omissions.Any(item => item.Contains("size limit", StringComparison.OrdinalIgnoreCase)),
			"The collection manifest data must report truncation and older-log omissions.");

		var zipPath = Path.Combine(root, "diagnostics.zip");
		var archiveFiles = collection.Files.ToList();
		archiveFiles.Add(("manifest.txt", System.Text.Encoding.UTF8.GetBytes("Omissions and truncation:\n- " + string.Join("\n- ", collection.Omissions))));
		DiagnosticArchiveWriter.Write(zipPath, archiveFiles, 16 * 1024);
		using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
		Assert(archive.GetEntry("logs/current-session.jsonl") is not null && archive.GetEntry("logs/godot.log") is not null,
			"The actual diagnostic ZIP must contain both current log types.");
		Assert(archive.Entries.Sum(entry => entry.Length) <= 16 * 1024, "The diagnostic ZIP's uncompressed content must stay within the cap.");
	}
	finally { Directory.Delete(root, recursive: true); }
}

static void DiagnosticCurrentLogReadFailures()
{
	var root = Path.Combine(Path.GetTempPath(), "WodraDiagnosticReadTests", Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(root);
	var activePath = Path.Combine(root, "active.jsonl");
	var lockedPath = Path.Combine(root, "locked.log");
	try
	{
		using (var activeWriter = new FileStream(activePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
		using (var lockedWriter = new FileStream(lockedPath, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			var record = System.Text.Encoding.UTF8.GetBytes("{\"event\":\"active-writer\"}\n");
			activeWriter.Write(record); activeWriter.Flush();
			lockedWriter.Write(System.Text.Encoding.UTF8.GetBytes("locked diagnostics")); lockedWriter.Flush();
			var reserved = new[] { ("build.txt", System.Text.Encoding.UTF8.GetBytes("version=test")) };
			var collection = DiagnosticInputCollector.Collect(reserved,
				new[] { new DiagnosticLogSource(activePath, "logs/current-session.jsonl", true), new DiagnosticLogSource(lockedPath, "logs/godot.log", false) },
				new[] { new DiagnosticLogSource(Path.Combine(root, "missing.jsonl"), "logs/recent-missing.jsonl", true) },
				null, 2048, 1800, 1024, manifestReserveBytes: 256);
			Assert(collection.Files.Any(file => file.Name == "logs/current-session.jsonl"),
				"The current session log must be readable while its writer is active.");
			Assert(collection.Omissions.Any(item => item == "logs/godot.log: unreadable") && collection.Omissions.Any(item => item == "logs/recent-missing.jsonl: missing"),
				"Locked and missing logs must be reported clearly without failing export.");
			using var parsed = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(collection.Files.Single(file => file.Name == "logs/current-session.jsonl").Data));
		}
	}
	finally { Directory.Delete(root, recursive: true); }
}

static void DiagnosticSessionFilesAreScoped()
{
	var root = Path.Combine(Path.GetTempPath(), "WodraSessionCatalogTests", Guid.NewGuid().ToString("N"));
	var logs = Path.Combine(root, "logs");
	var pictures = Path.Combine(root, "pictures");
	Directory.CreateDirectory(logs); Directory.CreateDirectory(pictures);
	try
	{
		var now = DateTimeOffset.Now;
		var first = DiagnosticSessionCatalog.Start(root, "wodra-first.jsonl", "test", true, now.AddHours(-1));
		File.WriteAllText(Path.Combine(logs, first.WodraLogFile), "{\"event\":\"connection_attempt\"}\n");
		DiagnosticSessionCatalog.End(root, first.Id, now.AddMinutes(-40));
		var second = DiagnosticSessionCatalog.Start(root, "wodra-second.jsonl", "test", false, now);
		File.WriteAllText(Path.Combine(logs, second.WodraLogFile), "{\"event\":\"current\"}\n");
		File.WriteAllText(Path.Combine(logs, "godot" + now.ToLocalTime().AddHours(-1).ToString("yyyy-MM-dd'T'HH.mm.ss", CultureInfo.InvariantCulture) + ".log"), "first session engine");
		File.WriteAllText(Path.Combine(logs, "godot" + now.ToLocalTime().ToString("yyyy-MM-dd'T'HH.mm.ss", CultureInfo.InvariantCulture) + ".log"), "second session engine");
		File.WriteAllText(Path.Combine(logs, "godot.log"), "current engine");
		var markerDirectory = Path.Combine(pictures, "Wodra"); Directory.CreateDirectory(markerDirectory);
		var marker = Path.Combine(markerDirectory, "wodra-first-marker-001.json");
		File.WriteAllText(marker, JsonSerializer.Serialize(new { sessionId = first.Id, timestamp = now.AddHours(-1), path = @"C:\Users\rider\ride.json" }));
		File.WriteAllBytes(Path.ChangeExtension(marker, ".png"), [137, 80, 78, 71]);
		var unrelated = Path.Combine(markerDirectory, "wodra-other-marker-001.json");
		File.WriteAllText(unrelated, JsonSerializer.Serialize(new { sessionId = "unrelated", timestamp = now.AddHours(-1) }));
		var all = DiagnosticSessionCatalog.ListRecent(root);
		var selected = DiagnosticSessionFiles.Discover(first, root, pictures, all, second.Id, includeScreenshots: true);
		Assert(selected.Any(file => file.SourcePath == Path.Combine(logs, first.WodraLogFile)), "The selected application session log must be present.");
		Assert(selected.Any(file => file.Kind == DiagnosticSessionFileKind.PlainText && File.ReadAllText(file.SourcePath) == "first session engine"), "The matching rotated Godot log must be present for a prior session.");
		Assert(!selected.Any(file => file.SourcePath.EndsWith("godot.log", StringComparison.OrdinalIgnoreCase)), "The current active Godot log must not be attached to an older session.");
		var currentFiles = DiagnosticSessionFiles.Discover(second, root, pictures, all, second.Id, includeScreenshots: true);
		Assert(currentFiles.Any(file => File.ReadAllText(file.SourcePath) == "second session engine") &&
			!currentFiles.Any(file => File.ReadAllText(file.SourcePath) == "first session engine"), "A rotated Godot file must belong to only its nearest Wodra session.");
		Assert(selected.Any(file => file.Kind == DiagnosticSessionFileKind.MarkerJson) && selected.Any(file => file.Kind == DiagnosticSessionFileKind.ScreenshotPng), "Debug marker JSON and its PNG pair must be discoverable.");
		Assert(!selected.Any(file => file.SourcePath == unrelated), "Markers from other application sessions must be excluded.");
		Assert(!DiagnosticSessionFiles.Discover(first, root, pictures, all, second.Id, includeScreenshots: false).Any(file => file.Kind == DiagnosticSessionFileKind.ScreenshotPng), "Screenshot deselection must omit PNG files.");
	}
	finally { Directory.Delete(root, recursive: true); }
}

static void ExplorerPathNormalization()
{
	if (!OperatingSystem.IsWindows()) return;
	var normalized = ExplorerNavigation.NormalizePath("C:/Users/Gareth Ω/OneDrive\\Wodra/diagnostics/export.zip");
	Assert(normalized.Contains("Gareth Ω", StringComparison.Ordinal) && normalized.Contains(Path.DirectorySeparatorChar), "Mixed path separators and Unicode names must normalize without losing path components.");
	if (Environment.GetEnvironmentVariable("WODRA_VERIFY_EXPLORER") == "1")
	{
		var previousExports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "Wodra", "diagnostics");
		var sample = Directory.Exists(previousExports)
			? Directory.GetFiles(previousExports, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
			: null;
		sample ??= Path.Combine(Environment.CurrentDirectory, "publish.ps1");
		var navigation = ExplorerNavigation.OpenAndVerify(sample, TimeSpan.FromSeconds(10));
		Console.WriteLine($"EXPLORER_VISIBLE={navigation.ExplorerVerified}; FILE_SELECTED={navigation.FileSelected}; {navigation.Message}");
		Assert(navigation.ExplorerVerified && navigation.FileSelected, "Explorer must visibly open the containing folder and select the requested file.");
	}
}

static void DiagnosticArchiveWriting()
{
	var root = Path.Combine(Path.GetTempPath(), "WodraDiagnosticArchiveTests", Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(root);
	try
	{
		var original = Path.Combine(root, "session.jsonl");
		File.WriteAllText(original, "session-log");
		var bytes = File.ReadAllBytes(original);
		var archivePath = Path.Combine(root, "diagnostics.zip");
		DiagnosticArchiveWriter.Write(archivePath, new[] { ("logs/current.jsonl", bytes), ("manifest.txt", System.Text.Encoding.UTF8.GetBytes("omissions: none")) }, 100);
		using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
		Assert(archive.GetEntry("logs/current.jsonl") is not null && archive.GetEntry("manifest.txt") is not null,
			"The ZIP must contain the selected log and manifest.");
		Assert(File.ReadAllBytes(original).SequenceEqual(bytes), "Archive creation must not modify the original log.");
		var tooLarge = Path.Combine(root, "too-large.zip");
		try { DiagnosticArchiveWriter.Write(tooLarge, new[] { ("large.bin", new byte[200]) }, 100); throw new Exception("Oversized archives must be rejected."); }
		catch (InvalidDataException) { }
		Assert(!File.Exists(tooLarge), "An oversized archive must not be left behind.");
	}
	finally { Directory.Delete(root, recursive: true); }
}

static void GameFeaturesPersistPerProfile()
{
	var first = Guid.NewGuid();
	var second = Guid.NewGuid();
	var settings = new Dictionary<string, bool>();
	Assert(!GameFeaturePreferencePolicy.IsEnabled(settings, first) && !GameFeaturePreferencePolicy.IsEnabled(settings, null),
		"Missing preference entries must default off.");
	GameFeaturePreferencePolicy.Set(settings, first, true);
	var restored = JsonSerializer.Deserialize<Dictionary<string, bool>>(JsonSerializer.Serialize(settings))!;
	Assert(GameFeaturePreferencePolicy.IsEnabled(restored, first) && !GameFeaturePreferencePolicy.IsEnabled(restored, second),
		"The profile choice must round-trip and must not affect another rider.");
}

static void SettingsReturnRespectsEntryContext()
{
    Assert(SettingsReturnPolicy.ShouldReturnToConnection(true, false), "Pre-ride Settings should return to the connection screen.");
    Assert(!SettingsReturnPolicy.ShouldReturnToConnection(true, true), "An active ride must retain the ride-menu path.");
    Assert(!SettingsReturnPolicy.ShouldReturnToConnection(false, false), "Other callers must not be redirected to the connection screen.");
}

static void SteeringDeadZoneAndNeutralHold()
{
    var steering = new SteeringLateralController();
    var start = steering.LateralOffsetMeters;
    for (var i = 0; i < 20; i++) steering.Update(.05, false, false, 2.9f, true, false);
    Near(start, steering.LateralOffsetMeters, 1e-9);
    for (var i = 0; i < 20; i++) steering.Update(.05, false, false, 20f, true, false);
    var moved = steering.LateralOffsetMeters;
    if (moved >= start) throw new Exception("Positive Sterzo angle should move right in the route-relative frame.");
    for (var i = 0; i < 20; i++) steering.Update(.05, false, false, 0f, true, false);
    Near(moved, steering.LateralOffsetMeters, 1e-9);
}

static void KeyboardSteeringPriorityAndBounds()
{
    var steering = new SteeringLateralController();
    for (var i = 0; i < 100; i++) steering.Update(.05, false, true, -25f, true, false,
        roadWidthMeters: 7, controllerInputIsNew: false);
    if (steering.LateralOffsetMeters >= SteeringLateralController.NormalLineMeters)
        throw new Exception("Right arrow should move to route-right and override Sterzo.");
    var held = steering.LateralOffsetMeters;
    for (var i = 0; i < 100; i++) steering.Update(.05, true, true, -25f, true, false,
        roadWidthMeters: 7, controllerInputIsNew: false);
    Near(held, steering.LateralOffsetMeters, 1e-9);
    for (var i = 0; i < 100; i++) steering.Update(.05, false, false, -25f, true, false,
        roadWidthMeters: 7, controllerInputIsNew: false);
    Near(held, steering.LateralOffsetMeters, 1e-9);
    steering.Update(.05, false, false, -25f, true, false, roadWidthMeters: 7, controllerInputIsNew: true);
    if (steering.LateralOffsetMeters <= held) throw new Exception("A fresh intentional Sterzo sample should take over after keyboard use.");
    var edge = new SteeringLateralController();
    for (var i = 0; i < 300; i++) edge.Update(.1, true, false, null, false, false, roadWidthMeters: 7);
    if (edge.LateralOffsetMeters > 3.05 || edge.LateralOffsetMeters < -3.05)
        throw new Exception("Lateral movement crossed the rider-clearance road boundary.");
    var fallback = new SteeringLateralController();
    for (var i = 0; i < 300; i++) fallback.Update(.1, true, false, null, false, false, roadWidthMeters: null);
    if (fallback.LateralOffsetMeters > 2.05) throw new Exception("Unknown-width routes must use conservative bounds.");
}

static void SteeringStaleInputRecovery()
{
    var steering = new SteeringLateralController();
    for (var i = 0; i < 40; i++) steering.Update(.05, false, false, -25f, true, false, roadWidthMeters: 7);
    var moved = steering.LateralOffsetMeters;
    for (var i = 0; i < 100; i++) steering.Update(.05, false, false, null, false, false, roadWidthMeters: 7);
    if (Math.Abs(steering.LateralOffsetMeters - SteeringLateralController.NormalLineMeters) >= Math.Abs(moved - SteeringLateralController.NormalLineMeters))
        throw new Exception("Stale/disconnected Sterzo input must smoothly return to the normal line.");
    var keyboardOwned = new SteeringLateralController();
    for (var i = 0; i < 30; i++) keyboardOwned.Update(.05, false, true, null, false, false);
    var keyboardPosition = keyboardOwned.LateralOffsetMeters;
    for (var i = 0; i < 30; i++) keyboardOwned.Update(.05, false, false, null, false, false);
    Near(keyboardPosition, keyboardOwned.LateralOffsetMeters, 1e-9);
}

static void SteeringPauseAndTextFocus()
{
    var steering = new SteeringLateralController();
    var initial = steering.LateralOffsetMeters;
    steering.Update(.1, true, false, null, false, false, textInputFocused: true);
    Near(initial, steering.LateralOffsetMeters, 1e-9);
    steering.Update(.1, true, false, null, false, true);
    Near(initial, steering.LateralOffsetMeters, 1e-9);
    for (var i = 0; i < 20; i++) steering.Update(.05, true, false, null, false, false);
    var moving = steering.LateralOffsetMeters;
    steering.Update(.1, true, false, null, false, true);
    for (var i = 0; i < 20; i++) steering.Update(.05, false, false, null, false, false);
    Near(moving, steering.LateralOffsetMeters, 1e-9);
}

static void SteeringSettingsPersist()
{
    var defaults = new SteeringControlSettings();
    Assert(defaults.Sensitivity == 1.5, "New profiles should default to 1.5× shared steering sensitivity.");
    var settings = new SteeringControlSettings { Sensitivity = 1.6, DeadZoneDegrees = 4.5 };
    var restored = JsonSerializer.Deserialize<SteeringControlSettings>(JsonSerializer.Serialize(settings));
    Assert(restored is not null && restored.Sensitivity == 1.6 && restored.DeadZoneDegrees == 4.5,
        "Sensitivity and dead-zone settings must survive settings serialization.");
    restored!.Sensitivity = 99;
    restored.DeadZoneDegrees = -1;
    restored.Normalize();
    Assert(restored.Sensitivity == 10 && restored.DeadZoneDegrees == 0, "Loaded steering settings must be bounded to the shared 1.0–10.0 range.");

    restored.Sensitivity = 0.2;
    restored.Normalize();
    Assert(restored.Sensitivity == 1.0, "Saved values below 1.0× should clamp to the new minimum without resetting other preferences.");

    // Existing settings.json files use the same Sensitivity property; retain a rider's saved value.
    var previous = JsonSerializer.Deserialize<SteeringControlSettings>("{\"Sensitivity\":1.2,\"DeadZoneDegrees\":5}");
    Assert(previous?.Sensitivity == 1.2 && previous.DeadZoneDegrees == 5,
        "Preferences saved by earlier alpha builds should migrate without resetting the shared sensitivity or dead zone.");

    var oldDefault = new SteeringControlSettings { Sensitivity = 1.0 };
    Assert(SteeringSensitivityPreferencePolicy.Migrate("{\"Steering\":{\"Sensitivity\":1.0}}", oldDefault, 0) &&
        oldDefault.Sensitivity == 1.5,
        "The serialized alpha.36 default should migrate once to the new default.");
    var custom = new SteeringControlSettings { Sensitivity = 1.3 };
    SteeringSensitivityPreferencePolicy.Migrate("{\"Steering\":{\"Sensitivity\":1.3}}", custom, 0);
    Assert(custom.Sensitivity == 1.3, "A saved custom sensitivity should survive migration.");
    var explicitBaseline = new SteeringControlSettings { Sensitivity = 1.0 };
    Assert(!SteeringSensitivityPreferencePolicy.Migrate("{}", explicitBaseline, SteeringSensitivityPreferencePolicy.CurrentVersion) &&
        explicitBaseline.Sensitivity == 1.0,
        "A user's explicit 1.0× selection should survive later restarts after the migration marker is saved.");
}

static void WindowPlacementRestoresSafely()
{
    var screens = new[]
    {
        new WindowRectangle(0, 0, 1920, 1040),
        new WindowRectangle(-1600, 0, 1600, 900)
    };
    var saved = new WindowRectangle(-1500, 40, 1800, 1000);
    Assert(WindowPlacementPolicy.SelectScreen(8, saved, screens) == 1,
        "When the saved monitor index disappears, use the monitor containing the saved window.");
    var restored = WindowPlacementPolicy.Clamp(saved, screens[1]);
    Assert(restored == new WindowRectangle(-1600, 0, 1600, 900),
        "Oversized or partially off-screen bounds should be fitted inside the usable display.");
    var smallDisplay = WindowPlacementPolicy.Clamp(new WindowRectangle(4000, -3000, 1152, 648),
        new WindowRectangle(0, 0, 800, 500));
    Assert(smallDisplay == new WindowRectangle(0, 0, 800, 500),
        "A smaller available display must take precedence over the usual minimum dimensions.");
}

static void SteeringSensitivityIsShared()
{
    Assert(SteeringLateralController.MaximumLateralSpeedMetersPerSecond == 1.25,
        "The alpha.36 Sterzo baseline and stale-input recentering speed must remain unchanged at 1.0×.");
    Assert(SteeringLateralController.KeyboardLateralSpeedMetersPerSecond == 1.5,
        "The alpha.36 keyboard baseline must remain unchanged at 1.0×.");
    Assert(SteeringLateralController.MinimumSensitivity == 1.0 &&
        SteeringLateralController.MaximumSensitivity == 10.0 &&
        SteeringControlSettings.SensitivityStep == 0.1 &&
        SteeringLateralController.DefaultSensitivity == 1.5,
        "The shared setting should expose 1.0–10.0× in 0.1× steps with a 1.5× default.");

    static double KeyboardMove(double sensitivity)
    {
        var steering = new SteeringLateralController();
        for (var i = 0; i < 20; i++) steering.Update(.05, false, true, null, false, false,
            sensitivity: sensitivity, roadWidthMeters: 50);
        return SteeringLateralController.NormalLineMeters - steering.LateralOffsetMeters;
    }
    static double SterzoMove(double sensitivity)
    {
        var steering = new SteeringLateralController();
        for (var i = 0; i < 20; i++) steering.Update(.05, false, false, 16.5f, true, false,
            sensitivity: sensitivity, roadWidthMeters: 50);
        return steering.LateralOffsetMeters - SteeringLateralController.NormalLineMeters;
    }

    foreach (var move in new Func<double, double>[] { KeyboardMove, SterzoMove })
    {
        var baseline = move(1.0);
        var defaultMove = move(1.5);
        var threeMove = move(3.0);
        var fiveMove = move(5.0);
        var tenMove = move(10.0);
        Assert(Math.Abs(defaultMove / baseline - 1.5) < 1e-9,
            "The default shared sensitivity should scale each input's alpha.36 baseline by 1.5×.");
        Assert(Math.Abs(threeMove / baseline - 3.0) < 1e-9 &&
            Math.Abs(fiveMove / baseline - 5.0) < 1e-9 &&
            Math.Abs(tenMove / baseline - 10.0) < 1e-9,
            "1×, 3×, 5× and 10× settings should proportionally increase both steering inputs without an old speed cap.");
    }

    var proportional = new SteeringLateralController();
    for (var i = 0; i < 20; i++) proportional.Update(.05, false, false, 8f, true, false, sensitivity: 3.0);
    var smallTurn = Math.Abs(proportional.LateralOffsetMeters - SteeringLateralController.NormalLineMeters);
    proportional.Reset();
    for (var i = 0; i < 20; i++) proportional.Update(.05, false, false, 20f, true, false, sensitivity: 3.0);
    var largerTurn = Math.Abs(proportional.LateralOffsetMeters - SteeringLateralController.NormalLineMeters);
    Assert(largerTurn > smallTurn, "Controller steering should remain proportional at every shared sensitivity.");

    var deadZone = new SteeringLateralController();
    for (var i = 0; i < 20; i++) deadZone.Update(.05, false, false, 3f, true, false, sensitivity: 3.0);
    Near(SteeringLateralController.NormalLineMeters, deadZone.LateralOffsetMeters, 1e-9);

    var maximum = new SteeringLateralController();
    for (var i = 0; i < 80; i++) maximum.Update(.05, false, true, null, false, false,
        sensitivity: 10, roadWidthMeters: 5);
    Assert(maximum.LateralOffsetMeters >= -(5 / 2.0 - SteeringLateralController.RiderMarginMeters) - 1e-9 &&
        maximum.LateralOffsetMeters <= 5 / 2.0 - SteeringLateralController.RiderMarginMeters + 1e-9,
        "10× keyboard steering must remain within the road edges.");

    var controllerEdge = new SteeringLateralController();
    for (var i = 0; i < 80; i++) controllerEdge.Update(.05, false, false, 30f, true, false,
        sensitivity: 10, roadWidthMeters: 5);
    Assert(controllerEdge.LateralOffsetMeters >= -(5 / 2.0 - SteeringLateralController.RiderMarginMeters) - 1e-9 &&
        controllerEdge.LateralOffsetMeters <= 5 / 2.0 - SteeringLateralController.RiderMarginMeters + 1e-9,
        "10× Sterzo steering must remain within the road edges.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static RideState Tick(RideState state, int power, double grade, double dt = 0.1, double trainerSpeedKph = 30,
    RiderProfile? rider = null, BikeProfile? bike = null, SimulationPhysicsParameters? physics = null)
{
    var input = new SimulationTickInput(
        new TrainerTelemetry(DateTimeOffset.UnixEpoch, power, 80, trainerSpeedKph),
        grade, dt, rider ?? new RiderProfile("Test", 75), bike ?? new BikeProfile("Test", 9));
    return new SimulationEngine(physics).Tick(state, input).NewState;
}

static RideState Run(RideState state, int power, double grade, int ticks, double dt = 0.1)
{
    for (var i = 0; i < ticks; i++) state = Tick(state, power, grade, dt);
    return state;
}

static void HudMetricsUseRideState()
{
	var now = DateTimeOffset.UtcNow;
	var state = RideState.Initial with
	{
		CurrentPowerWatts = 264,
		CurrentCadenceRpm = 85,
		VirtualSpeedKph = 25.5,
		CurrentGradientPercent = 0,
		DistanceMeters = 60
	};
	var rawTelemetry = new TrainerTelemetry(now, 999, 1, 34.5);
	var metrics = RideHudMetrics.From(state, rawTelemetry, now, 12.7);
	if (metrics.PowerWatts != 264 || metrics.CadenceRpm != 85)
		throw new Exception("HUD power and cadence should come from current ride state.");
	Near(25.5, metrics.SimulatedSpeedKph, 0);
	Near(0, metrics.GradientPercent, 0);
	Near(60, metrics.DistanceMeters, 0);
	Near(12.7, metrics.ElapsedSeconds, 0);
	if (metrics.SimulatedSpeedKph == rawTelemetry.SpeedKph)
		throw new Exception("HUD speed must use simulated speed, not raw trainer speed.");
}

static void RemoteRiderInterpolation()
{
    var track = new RemoteRiderInterpolator();
    track.Add(new RemoteRiderSample("r", "Rider", 1990, 18, 70, 150, false, 1, 10));
    track.Add(new RemoteRiderSample("r", "Rider", 2010, 22, 90, 250, false, 2, 10.2, -1.25));
    if (!track.TrySample(10.3, out var sample)) throw new Exception("Expected interpolated remote sample.");
    Near(2000, sample.DistanceMeters, .01);
    Near(20, sample.SpeedKph, .01);
    if (sample.CadenceRpm != 80 || sample.PowerWatts != 200) throw new Exception("Remote telemetry did not interpolate.");
    Near(0.25, sample.LateralOffsetMeters, .01);
}

static void RemoteRiderRepositionReset()
{
    var track = new RemoteRiderInterpolator();
    track.Add(new RemoteRiderSample("r", "Rider", 400, 20, 80, 200, false, 1, 10));
    track.Add(new RemoteRiderSample("r", "Rider", 410, 20, 80, 200, false, 2, 10.2));
    track.Reset(new RemoteRiderSample("r", "Rider", 3000, 35, 80, 200, false, 3, 10.3));
    if (!track.TrySample(10.31, out var sample)) throw new Exception("Expected repositioned sample.");
    Near(3000, sample.DistanceMeters, .01);
    Near(35, sample.SpeedKph, .01);
    if (track.Add(new RemoteRiderSample("r", "Rider", 420, 20, 80, 200, false, 2, 10.4)))
        throw new Exception("Older network state was accepted after reposition.");
}

static void RemoteRiderStaleness()
{
    var track = new RemoteRiderInterpolator();
    track.Add(new RemoteRiderSample("r", "Rider", 500, 36, 80, 200, false, 1, 10));
    if (track.TrySample(12.01, out _)) throw new Exception("A stale rider continued to render.");
}

static void RecordingSegmentRebasesAfterTeleport()
{
    var segment = new RecordingProgress();
    segment.Rebase(3000, 125);
    segment.Advance(3000, 4, 125);
    Near(0, segment.DistanceMeters, .001);
    segment.Advance(3012, 4, 125.48);
    Near(12, segment.DistanceMeters, .001);
    Near(125.48, segment.AltitudeMeters, .001);
    Near(.48, segment.AscentMeters, .001);
}

static void CatchUpRoutePosition()
{
    Near(0, MultiplayerRoutePosition.PositionBehind(2), .001);
    Near(5996, MultiplayerRoutePosition.PositionBehind(6000), .001);
    Near(11996, MultiplayerRoutePosition.PositionBehind(12000), .001);
    Near(4, MultiplayerRoutePosition.WrappedSeparation(5998, 2, 6000), .001);
    Near(20, MultiplayerRoutePosition.WrappedSeparation(12010, 11990, 6000), .001);
}

static void SignedRemoteRiderDistance()
{
	Near(4, MultiplayerRoutePosition.SignedWrappedSeparation(2, 5998, 6000), .001);
	Near(-4, MultiplayerRoutePosition.SignedWrappedSeparation(5998, 2, 6000), .001);
	Near(0, MultiplayerRoutePosition.SignedWrappedSeparation(6004, 4, 6000), .001);
	if (MultiplayerRoutePosition.LapDifference(6004, 4, 6000) != 1)
		throw new Exception("Wrapped relative position must retain the remote rider's lap difference.");
}

static void CatchUpMatchesFreshRemoteSpeed()
{
    var speed = CatchUpSpeedResolver.Resolve(35, .1, targetPaused: false);
    Near(35, speed, 0);
    var snapped = RideState.Initial with
    {
        DistanceMeters = 6000,
        VirtualSpeedKph = speed,
        CurrentPowerWatts = 123,
        CurrentCadenceRpm = 71
    };
    var next = Tick(snapped, power: 200, grade: 0, dt: .1);
    if (next.DistanceMeters <= snapped.DistanceMeters || next.VirtualSpeedKph < 30)
        throw new Exception("The first local physics step did not continue from the matched speed.");
    if (next.CurrentPowerWatts != 200 || next.CurrentCadenceRpm != 80)
        throw new Exception("The snap overwrote local power or cadence telemetry.");
}

static void CatchUpSpeedFallbacks()
{
    Near(0, CatchUpSpeedResolver.Resolve(35, 2.1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(double.NaN, .1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(81, .1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(0, .1, targetPaused: false), 0);
    Near(0, CatchUpSpeedResolver.Resolve(35, .1, targetPaused: true), 0);
}

static void TrainerDifficultyScalesSignedGrades()
{
    Near(0, TrainerDifficultyPolicy.ScaleGrade(12, 0), 1e-9);
    Near(3, TrainerDifficultyPolicy.ScaleGrade(12, 25), 1e-9);
    Near(6, TrainerDifficultyPolicy.ScaleGrade(12, 50), 1e-9);
    Near(9, TrainerDifficultyPolicy.ScaleGrade(12, 75), 1e-9);
    Near(12, TrainerDifficultyPolicy.ScaleGrade(12, 100), 1e-9);
    Near(-6, TrainerDifficultyPolicy.ScaleGrade(-12, 50), 1e-9);
    if (FtmsSimulationGrade.Encode(TrainerDifficultyPolicy.ScaleGrade(12, 100), (-6, 6)) != 600 ||
        FtmsSimulationGrade.Encode(TrainerDifficultyPolicy.ScaleGrade(-12, 100), (-6, 6)) != -600)
        throw new Exception("FTMS inclination capability limits were not retained after scaling.");

    // Physics continues to receive the real route grade. TrainerDifficultyPolicy only
    // calculates a separate output command and is not part of SimulationTickInput.
    var physicsAtHalfDifficulty = Tick(RideState.Initial with { VirtualSpeedKph = 25 }, 200, 12);
    var physicsAtFullDifficulty = Tick(RideState.Initial with { VirtualSpeedKph = 25 }, 200, 12);
    Near(physicsAtHalfDifficulty.VirtualSpeedKph, physicsAtFullDifficulty.VirtualSpeedKph, 1e-9);
    if (!(physicsAtHalfDifficulty.VirtualSpeedKph < Tick(RideState.Initial with { VirtualSpeedKph = 25 }, 200, 6).VirtualSpeedKph))
        throw new Exception("The actual 12% route gradient was not retained for cycling physics.");
}

static void CatchUpTrainerGradeTransition()
{
    var transition = new TrainerGradeTransition();
    transition.Start(0, 12);
    Near(4, transition.Advance(1, 12), 1e-9);
    Near(8, transition.Advance(1, 12), 1e-9);
    if (!transition.IsActive) throw new Exception("The catch-up resistance transition ended too early.");
    Near(8 + 1d / 3d, transition.Advance(1, 9), 1e-9);
    Near(9, transition.Advance(2, 9), 1e-9);
    if (transition.IsActive) throw new Exception("The replacement resistance target did not complete.");

    transition.Start(4, 12);
    var pausedValue = transition.CurrentPercent;
    // The ride controller does not call Advance while paused or disconnected.
    Near(4, pausedValue, 1e-9);
    transition.Cancel(4);
    if (transition.IsActive) throw new Exception("Disconnected transition was not cancelled.");
}

static void TrainerConnectionReadiness()
{
    var state = new TrainerConnectionState();
    if (state.CanContinue) throw new Exception("Idle state was marked ready.");

    var firstAttempt = state.BeginAttempt();
    if (state.Phase != TrainerConnectionPhase.Scanning || state.CanContinue) throw new Exception("Scanning was marked ready.");
    state.SetPhase(firstAttempt, TrainerConnectionPhase.NoTrainer);
    if (state.CanContinue) throw new Exception("No-trainer state was marked ready.");

    var retryAttempt = state.BeginAttempt();
    if (state.SetPhase(firstAttempt, TrainerConnectionPhase.Ready)) throw new Exception("A stale attempt changed the current state.");
    if (state.Phase != TrainerConnectionPhase.Scanning || state.CanContinue) throw new Exception("Retry did not return to scanning state.");
    state.SetPhase(retryAttempt, TrainerConnectionPhase.Connecting);
    state.SetPhase(retryAttempt, TrainerConnectionPhase.SettingUp);
    if (state.CanContinue) throw new Exception("Connection without setup was marked ready.");
    state.SetPhase(retryAttempt, TrainerConnectionPhase.Failed, "telemetry subscription failed");
    if (state.CanContinue) throw new Exception("Setup failure was marked ready.");

    var successfulAttempt = state.BeginAttempt();
    state.SetPhase(successfulAttempt, TrainerConnectionPhase.Connecting);
    state.SetPhase(successfulAttempt, TrainerConnectionPhase.SettingUp);
    state.SetPhase(successfulAttempt, TrainerConnectionPhase.Ready);
    if (!state.CanContinue) throw new Exception("Successful setup was not ready while stationary.");
    state.SetPhase(successfulAttempt, TrainerConnectionPhase.Disconnected);
    if (state.CanContinue) throw new Exception("Disconnect did not clear readiness.");

    var demoAttempt = state.BeginAttempt();
    state.SetPhase(demoAttempt, TrainerConnectionPhase.Demo);
    if (!state.CanContinue || state.Phase != TrainerConnectionPhase.Demo) throw new Exception("Demo mode was not separately recognized.");
}

static void GattDiscoveryStartsConnection()
{
    var connected = false;
    var discoveryStartedWhileDisconnected = false;
    var serviceResult = GattConnectionSequence.DiscoverAndWaitForConnectionAsync(
        _ =>
        {
            discoveryStartedWhileDisconnected = !connected;
            connected = true; // Model Windows raising ConnectionStatusChanged after GATT is requested.
            return Task.FromResult("FTMS service discovered");
        },
        () => connected,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

    if (!discoveryStartedWhileDisconnected || serviceResult != "FTMS service discovered")
        throw new Exception("The sequence required a connected flag before starting GATT discovery.");

    try
    {
        GattConnectionSequence.DiscoverAndWaitForConnectionAsync(
            _ => Task.FromResult("GATT operation completed"),
            () => false,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
        throw new Exception("A link that stayed disconnected was accepted.");
    }
    catch (TimeoutException) { }
}

static void TrainerDiscoverySelection()
{
    var trainer = new BleDiscoveredDevice(11, "Tacx Flux 2", [], -45);
    var selection = TrainerDiscoveryPolicy.Select([trainer], preferredAddress: null);
    if (selection.Outcome != TrainerDiscoveryOutcome.ConnectAutomatically || selection.Selected != trainer)
        throw new Exception("Exactly one compatible Bluetooth trainer should connect automatically.");
    var preferred = new BleDiscoveredDevice(12, "FTMS Bike", [TrainerDiscoveryPolicy.FtmsServiceUuid], -70);
    selection = TrainerDiscoveryPolicy.Select([trainer, preferred], preferred.BluetoothAddress);
    if (selection.Outcome != TrainerDiscoveryOutcome.ConnectAutomatically || selection.Selected != preferred)
        throw new Exception("A remembered trainer should be preferred when available among multiple devices.");
    selection = TrainerDiscoveryPolicy.Select([new BleDiscoveredDevice(13, "Keyboard", [], -10)], null);
    if (selection.Outcome != TrainerDiscoveryOutcome.None) throw new Exception("An incompatible BLE peripheral was accepted as a trainer.");
}

static void TrainerDiscoveryMultipleSelection()
{
    var devices = new[]
    {
        new BleDiscoveredDevice(1, "Tacx Flux 2", [], -55),
        new BleDiscoveredDevice(2, "Wahoo Trainer", [], -42),
        new BleDiscoveredDevice(1, "Tacx Flux 2", [], -50)
    };
    var selection = TrainerDiscoveryPolicy.Select(devices, null);
    if (selection.Outcome != TrainerDiscoveryOutcome.Choose || selection.Candidates.Count != 2 || selection.Selected is not null)
        throw new Exception("Multiple unique compatible trainers should require explicit rider choice.");
}

static void FtmsTelemetryParsing()
{
    var timestamp = DateTimeOffset.UtcNow;
    byte[] packet = [0x44, 0x00, 0xE8, 0x03, 0xB4, 0x00, 0xFA, 0x00];
    if (!FtmsIndoorBikeDataParser.TryParse(packet, timestamp, out var telemetry))
        throw new Exception("A valid FTMS data packet was rejected.");
    if (telemetry.SpeedKph != 10 || telemetry.CadenceRpm != 90 || telemetry.PowerWatts != 250 ||
        !telemetry.HasPowerMeasurement || !telemetry.HasCadenceMeasurement || telemetry.Timestamp != timestamp)
        throw new Exception("FTMS speed, cadence or power fields were parsed at the wrong offsets.");

    byte[] powerOnly = [0x40, 0x00, 0xE8, 0x03, 0x00, 0x00];
    if (!FtmsIndoorBikeDataParser.TryParse(powerOnly, timestamp, out var partial) || !partial.HasPowerMeasurement || partial.HasCadenceMeasurement)
        throw new Exception("An omitted cadence field should be reported as unsupported, not zero cadence.");
    if (FtmsIndoorBikeDataParser.TryParse([0x40, 0x00, 0xE8], timestamp, out _))
        throw new Exception("Truncated optional data was accepted.");
    byte[] noInstantSpeed = [0x41, 0x00, 0xB4, 0x00, 0xFA, 0x00];
    if (!FtmsIndoorBikeDataParser.TryParse(noInstantSpeed, timestamp, out var noSpeed) || noSpeed.HasSpeedMeasurement)
        throw new Exception("The More Data flag was not honored when determining whether instantaneous speed was included.");
}

static void TrainerTelemetryPresentationStates()
{
    var now = DateTimeOffset.UtcNow;
    var waiting = new TrainerTelemetry(now, 0, 0, 0, HasReceivedMeasurement: false, HasPowerMeasurement: false, HasCadenceMeasurement: false);
    if (TrainerTelemetryPresentation.Power(waiting, now).State != TelemetryReadingState.Awaiting)
        throw new Exception("No first notification should be shown as an awaiting reading.");
    var measuredZero = new TrainerTelemetry(now, 0, 0, 0);
    var power = TrainerTelemetryPresentation.Power(measuredZero, now);
    if (power.State != TelemetryReadingState.Fresh || power.Value != 0)
        throw new Exception("A valid stationary zero should remain a real zero reading.");
    if (!measuredZero.HasSpeedMeasurement) throw new Exception("A synthetic fresh stationary sample should include its speed measurement.");
    var unsupported = measuredZero with { HasCadenceMeasurement = false };
    if (TrainerTelemetryPresentation.Cadence(unsupported, now).State != TelemetryReadingState.Unsupported)
        throw new Exception("An omitted cadence measurement was not distinguished from a zero.");
    var stale = measuredZero with { Timestamp = now - TimeSpan.FromSeconds(6) };
    if (TrainerTelemetryPresentation.Power(stale, now).State != TelemetryReadingState.Stale)
        throw new Exception("An old reading was shown as current.");
}

static void StaleTelemetryIsNotRiderInput()
{
    var now = DateTimeOffset.UtcNow;
    var stale = new TrainerTelemetry(now - TimeSpan.FromSeconds(8), 290, 94, 30);
    var input = stale.ForRiderInput(now);
    if (input.PowerWatts != 0 || input.CadenceRpm != 0 || input.Timestamp != stale.Timestamp || input.IsFreshAt(now))
        throw new Exception("Stale measurements were retained as fresh rider input.");
    if (TrainerTelemetryPresentation.Power(input, now).State != TelemetryReadingState.Stale)
        throw new Exception("The stale marker was not retained for UI feedback.");
}

static void TrainerRecoveryIsBounded()
{
    if (!TrainerRecoveryPolicy.ShouldRetry(0, false) || !TrainerRecoveryPolicy.ShouldRetry(7, false) ||
        TrainerRecoveryPolicy.ShouldRetry(8, false) || TrainerRecoveryPolicy.ShouldRetry(0, true))
        throw new Exception("Trainer recovery did not enforce its bounded, non-intentional policy.");
    if (TrainerRecoveryPolicy.DelayBeforeAttempt(1) != TimeSpan.Zero ||
        TrainerRecoveryPolicy.DelayBeforeAttempt(3) != TimeSpan.FromSeconds(2) ||
        TrainerRecoveryPolicy.DelayBeforeAttempt(8) != TimeSpan.FromSeconds(7) ||
        TrainerRecoveryPolicy.AutomaticRecoveryWindow != TimeSpan.FromSeconds(90))
        throw new Exception("Recovery backoff was not deterministic.");
}

static void PausedCatchUpResumeSpeed()
{
    var gate = new RidePauseGate();
    var paused = gate.Pause(RideState.Initial with { DistanceMeters = 4200, VirtualSpeedKph = 20 });
    var beforeResume = gate.Tick(new SimulationEngine(), paused,
        new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UtcNow, 250, 85, 30), 0, 1,
            new RiderProfile("Test", 75), new BikeProfile("Test", 9)));
    Near(4200, beforeResume.DistanceMeters, 0);
    Near(0, beforeResume.VirtualSpeedKph, 0);
    var resumed = gate.Resume(beforeResume, 35);
    Near(4200, resumed.DistanceMeters, 0);
    Near(35, resumed.VirtualSpeedKph, 0);
    if (gate.IsPaused) throw new Exception("The pause gate remained paused after resuming.");
}

static void HudMetricsMarkStaleSensors()
{
	var now = DateTimeOffset.UtcNow;
	var state = RideState.Initial with { CurrentPowerWatts = 250, CurrentCadenceRpm = 90 };
	var staleTelemetry = new TrainerTelemetry(now - TimeSpan.FromSeconds(6), 250, 90, 30);
	var metrics = RideHudMetrics.From(state, staleTelemetry, now, 8);
	if (metrics.PowerWatts is not null || metrics.CadenceRpm is not null)
		throw new Exception("Stale power and cadence should be displayed as unavailable.");
}

static void ZeroPowerFlatDeceleration()
{
    var moving = RideState.Initial with { VirtualSpeedKph = 30 };
    var next = Tick(moving, 0, 0);
    if (!(next.VirtualSpeedKph < moving.VirtualSpeedKph && next.VirtualSpeedKph > 0))
        throw new Exception("Expected positive flat coasting speed to decrease.");
    var stopped = Run(RideState.Initial with { VirtualSpeedKph = 10 }, 0, 0, 2000);
    Near(0, stopped.VirtualSpeedKph, 0.001);
    if (stopped.DistanceMeters <= 0) throw new Exception("Expected distance while slowing from motion.");
}

static void ZeroPowerDownhillCoasting()
{
    var moving = RideState.Initial with { VirtualSpeedKph = 20 };
    var flat = Run(moving, 0, 0, 100);
    var downhill = Run(moving, 0, -5, 100);
    if (!(downhill.VirtualSpeedKph > moving.VirtualSpeedKph && downhill.VirtualSpeedKph > flat.VirtualSpeedKph))
        throw new Exception($"Expected downhill acceleration, flat={flat.VirtualSpeedKph:F2}, downhill={downhill.VirtualSpeedKph:F2}.");
}

static void UphillSlowerThanFlat()
{
    var start = RideState.Initial with { VirtualSpeedKph = 25 };
    var flat = Run(start, 180, 0, 1000);
    var uphill = Run(start, 180, 6, 1000);
    if (!(uphill.VirtualSpeedKph < flat.VirtualSpeedKph))
        throw new Exception($"Expected uphill speed below flat, flat={flat.VirtualSpeedKph:F2}, uphill={uphill.VirtualSpeedKph:F2}.");
}

static void HigherPowerAcceleratesMore()
{
    var start = RideState.Initial with { VirtualSpeedKph = 20 };
    var low = Tick(start, 100, 2).VirtualSpeedKph;
    var high = Tick(start, 300, 2).VirtualSpeedKph;
    var lowAcceleration = low - start.VirtualSpeedKph;
    var highAcceleration = high - start.VirtualSpeedKph;
    if (!(high > low && highAcceleration > lowAcceleration))
        throw new Exception($"Expected higher power to produce greater acceleration, low={lowAcceleration:F3}, high={highAcceleration:F3}.");
}

static void TenPercentHillStart()
{
    var start = Tick(RideState.Initial, 200, 10, rider: new RiderProfile("Rider", 88),
        bike: new BikeProfile("Bike", 9));
    if (!(start.VirtualSpeedKph > 0 && start.DistanceMeters > 0))
        throw new Exception("200 W did not start the 97 kg rider/bike uphill.");
}

static void EightPercentHillStart()
{
    var start = Tick(RideState.Initial, 150, 8);
    if (!(start.VirtualSpeedKph > 0 && start.DistanceMeters > 0))
        throw new Exception("150 W did not start the rider uphill.");
}

static void ZeroPowerUphillStart()
{
    var stopped = Tick(RideState.Initial, 0, 10);
    Near(0, stopped.VirtualSpeedKph, 0);
    Near(0, stopped.DistanceMeters, 0);
}

static void LaunchReferenceIsConfigurable()
{
    var lowerReference = Tick(RideState.Initial, 100, 0, physics:
        new SimulationPhysicsParameters(PowerForceReferenceSpeedMps: 0.5));
    var higherReference = Tick(RideState.Initial, 100, 0, physics:
        new SimulationPhysicsParameters(PowerForceReferenceSpeedMps: 2.0));
    if (!(lowerReference.VirtualSpeedKph > higherReference.VirtualSpeedKph))
        throw new Exception("Configured launch force reference did not affect low-speed acceleration.");

    var atOne = Tick(RideState.Initial with { VirtualSpeedKph = 3.6 }, 100, 0);
    var atTwo = Tick(RideState.Initial with { VirtualSpeedKph = 7.2 }, 100, 0);
    var accelerationAtOne = atOne.VirtualSpeedKph - 3.6;
    var accelerationAtTwo = atTwo.VirtualSpeedKph - 7.2;
    if (!(accelerationAtOne > accelerationAtTwo))
        throw new Exception("Actual speeds above the reference were not used for drive force.");
}

static void SpeedNeverNegative()
{
    var stopped = Tick(RideState.Initial, 0, 15, dt: 10);
    var descending = Tick(RideState.Initial with { VirtualSpeedKph = 0.01 }, 0, 90, dt: 100);
    if (stopped.VirtualSpeedKph < 0 || descending.VirtualSpeedKph < 0)
        throw new Exception("Speed became negative.");
    Near(0, stopped.VirtualSpeedKph, 0.001);
    Near(0, stopped.DistanceMeters, 0.001);
}

static void TrainerSpeedIsIgnored()
{
    var start = RideState.Initial with { VirtualSpeedKph = 12 };
    var slowTrainer = Tick(start, 100, 0, trainerSpeedKph: 2);
    var fastTrainer = Tick(start, 100, 0, trainerSpeedKph: 80);
    Near(slowTrainer.VirtualSpeedKph, fastTrainer.VirtualSpeedKph, 1e-12);
    Near(slowTrainer.DistanceMeters, fastTrainer.DistanceMeters, 1e-12);
}

static void ProfilesAffectPhysics()
{
    var start = RideState.Initial with { VirtualSpeedKph = 15 };
    var light = Tick(start, 200, 0, rider: new RiderProfile("Light", 50));
    var heavy = Tick(start, 200, 0, rider: new RiderProfile("Heavy", 100));
    var lowDragBike = Tick(start, 200, 0, bike: new BikeProfile("Low drag", 9, 0.003, 0.2));
    if (!(light.VirtualSpeedKph > heavy.VirtualSpeedKph && lowDragBike.VirtualSpeedKph > heavy.VirtualSpeedKph))
        throw new Exception("Rider mass / bike road-load values did not affect acceleration as expected.");
}

static void RidePauseSemantics()
{
    var engine = new SimulationEngine();
    var gate = new RidePauseGate();
    var input = new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UnixEpoch, 250, 85, 30),
        0, 0.1, new RiderProfile("Test", 75), new BikeProfile("Test", 9));
    var moving = RideState.Initial with { DistanceMeters = 100, ElapsedSeconds = 20, VirtualSpeedKph = 24 };
    moving = gate.Tick(engine, moving, input);
    var parked = gate.Pause(moving);
    Near(0, parked.VirtualSpeedKph, 0);
    for (var i = 0; i < 50; i++)
    {
        parked = gate.Tick(engine, parked, input);
        Near(moving.DistanceMeters, parked.DistanceMeters, 0);
        Near(moving.ElapsedSeconds, parked.ElapsedSeconds, 0);
        Near(0, parked.VirtualSpeedKph, 0);
    }
    var resumePosition = parked.DistanceMeters;
    gate.Resume();
    Near(resumePosition, parked.DistanceMeters, 0);
    var resumed = gate.Tick(engine, parked, input);
    if (resumed.DistanceMeters <= resumePosition || resumed.VirtualSpeedKph <= 0)
        throw new Exception("Motion did not resume from rest after power was applied.");
    if (resumed.DistanceMeters - resumePosition > 0.1)
        throw new Exception("Resume introduced a distance jump.");
}

static void RidePauseToggle()
{
    var engine = new SimulationEngine();
    var gate = new RidePauseGate(); // No recorder is present: ride pause is independent.
    var input = new SimulationTickInput(new TrainerTelemetry(DateTimeOffset.UnixEpoch, 200, 80, 20),
        0, 0.1, new RiderProfile("Test", 75), new BikeProfile("Test", 9));
    var state = engine.Tick(RideState.Initial, input).NewState;
    var distance = state.DistanceMeters;
    for (var i = 0; i < 3; i++)
    {
        state = gate.Toggle(state);
        if (!gate.IsPaused) throw new Exception("Pause action did not enter PAUSED state.");
        Near(0, state.VirtualSpeedKph, 0);
        var frozenDistance = state.DistanceMeters;
        var frozenElapsed = state.ElapsedSeconds;
        for (var j = 0; j < 10; j++) state = gate.Tick(engine, state, input);
        Near(frozenDistance, state.DistanceMeters, 0);
        Near(frozenElapsed, state.ElapsedSeconds, 0);

        state = gate.Toggle(state);
        if (gate.IsPaused) throw new Exception("Second Pause action did not resume the ride.");
        Near(0, state.VirtualSpeedKph, 0);
        Near(frozenDistance, state.DistanceMeters, 0);
        state = gate.Tick(engine, state, input);
        if (state.DistanceMeters <= frozenDistance || state.VirtualSpeedKph <= 0)
            throw new Exception("Ride did not restart from zero speed after toggle resume.");
    }
    if (state.DistanceMeters <= distance) throw new Exception("Repeated toggles lost resumed movement.");
}

static void DebugModeOff()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(false);
    if (diagnostics.SnapshotDue(10) || diagnostics.NextMarkerNumber() != 0)
        throw new Exception("Disabled diagnostics emitted periodic or marker data.");
}

static void DebugSnapshotFields()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(true);
    if (diagnostics.SnapshotDue(0.24) || !diagnostics.SnapshotDue(0.26))
        throw new Exception("Expected a diagnostic snapshot at the 0.5 second interval.");
    var snapshot = diagnostics.CreateSnapshot(new RideDebugSnapshot
    {
        totalRideDistanceMeters = 5851,
        routeLengthMeters = 5850,
        virtualSpeedKph = 27.4,
        ridePaused = true,
        rideState = "paused",
        visualSampleBefore = 585,
        visualSampleAfter = 586,
        visualInterpolation = 0.42
    }, DateTimeOffset.UnixEpoch);
    if (snapshot.totalRideDistanceMeters != 5851 || snapshot.virtualSpeedKph != 27.4
        || !snapshot.ridePaused || snapshot.wrappedRouteDistanceMeters != 1 || snapshot.lapNumber != 2
        || snapshot.debugSessionId != diagnostics.SessionId || snapshot.timestamp != DateTimeOffset.UnixEpoch)
        throw new Exception("Snapshot omitted ride state or wrapped route fields.");
}

static void DebugMarkerNumbers()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(true);
    if (diagnostics.NextMarkerNumber() != 1 || diagnostics.NextMarkerNumber() != 2)
        throw new Exception("Debug markers did not increment monotonically.");
}

static void ScreenshotNames()
{
    var timestamp = new DateTimeOffset(2026, 10, 8, 14, 23, 15, TimeSpan.FromHours(13));
    var localTimestamp = timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var calls = 0;
    var stem = ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.DebugMarker, 3,
        path => ++calls <= 2); // First stem's PNG and JSON exist.
    if (!stem.Contains($"{localTimestamp}-marker-003-002", StringComparison.Ordinal))
        throw new Exception($"Unexpected collision-safe marker stem: {stem}");
    if (Path.GetFileNameWithoutExtension(stem + ".png") != Path.GetFileNameWithoutExtension(stem + ".json")
        || !stem.Contains("marker-003", StringComparison.Ordinal))
        throw new Exception("Marker PNG/JSON names did not remain associated.");
    if (ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.Clean, null, _ => false)
        != $"wodra-{localTimestamp}-clean") throw new Exception("Clean filename format changed unexpectedly.");
    if (ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.Hud, null, _ => false)
        != $"wodra-{localTimestamp}-hud"
        || ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, ScreenshotCaptureMode.DebugMarker, 3, _ => false)
        != $"wodra-{localTimestamp}-marker-003")
        throw new Exception("HUD or marker screenshot filename did not use Wodra naming.");
}

static void ScreenshotMetadataFields()
{
    var snapshot = new RideDebugSnapshot
    {
        timestamp = DateTimeOffset.UnixEpoch, marker = 3, routeId = "test-route", routeName = "Test Route",
        totalRideDistanceMeters = 1200, wrappedRouteDistanceMeters = 200, lapNumber = 2,
        virtualSpeedKph = 31.2, rawTrainerSpeedKph = 30.8, rawTrainerPowerWatts = 240,
        rawTrainerCadenceRpm = 88, routeGradientPercent = 2.5, routeElevationMeters = 43,
        visualSampleBefore = 12, visualSampleAfter = 13, visualInterpolation = .4,
        riderWorldPosition = new DebugVector3(1, 2, 3), cameraWorldPosition = new DebugVector3(4, 5, 6),
        ridePaused = false, fitRecordingState = "recording", nearestLandmarkName = "Bridge",
        nearestLandmarkDistanceMeters = 24, nearbyLandmarkCount = 2, buildingsLoaded = 7,
        visibleBuildingChunks = 3, vegetationAreas = 4, treesLoaded = 9, visibleVegetationChunks = 2,
        terrainNearChunks = 8, terrainFarChunks = 6, terrainVisibleChunks = 10
    };
    using var json = JsonDocument.Parse(ScreenshotCaptureFileStore.SerializeSidecar(snapshot));
    var root = json.RootElement;
    if (root.GetProperty("marker").GetInt32() != 3 || root.GetProperty("routeId").GetString() != "test-route"
        || root.GetProperty("wrappedRouteDistanceMeters").GetDouble() != 200
        || root.GetProperty("powerWatts").GetInt32() != 240 || root.GetProperty("nearTerrainChunks").GetInt32() != 8)
        throw new Exception("Screenshot sidecar omitted requested ride metadata.");
    if (root.EnumerateObject().Count() > 40) throw new Exception("Screenshot sidecar is no longer compact.");
}

static void ScreenshotModeSelection()
{
    if (ScreenshotCapturePolicy.ForF12(false) != ScreenshotCaptureMode.Clean
        || ScreenshotCapturePolicy.ForF12(true) != ScreenshotCaptureMode.Hud
        || ScreenshotCapturePolicy.ForDebugMarker() != ScreenshotCaptureMode.DebugMarker)
        throw new Exception("Screenshot capture modes do not match their requested controls.");
}

static void ScreenshotDirectoryFallback()
{
    var root = Path.Combine(Path.GetTempPath(), "wodra-screenshot-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var pictures = Path.Combine(root, "Pictures");
        var preferred = ScreenshotCaptureFileStore.CreateScreenshotDirectory(pictures, Path.Combine(root, "unused"));
        if (preferred != Path.Combine(pictures, "Wodra") || !Directory.Exists(preferred))
            throw new Exception("Screenshot store did not create Pictures/Wodra.");
        var blocker = Path.Combine(root, "pictures-file");
        File.WriteAllText(blocker, "not a directory");
        var fallback = Path.Combine(root, "godot-user-data");
        var resolved = ScreenshotCaptureFileStore.CreateScreenshotDirectory(blocker, fallback);
        if (resolved != Path.Combine(fallback, "screenshots") || !Directory.Exists(resolved))
            throw new Exception("Screenshot store did not create its user-data fallback.");
        try { ScreenshotCaptureFileStore.CreateScreenshotDirectory(blocker, blocker); }
        catch (IOException) { return; }
        throw new Exception("Screenshot directory failure was not reported when both locations failed.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void ScreenshotFailureKeepsMarker()
{
    var diagnostics = new DebugRideDiagnostics(); diagnostics.Begin(true);
    var marker = diagnostics.NextMarkerNumber();
    var loggedMarker = marker;
    var failureReported = false;
    var saved = ScreenshotCaptureFailurePolicy.TryCapture(() => throw new IOException("disk full"), _ => failureReported = true);
    if (saved || !failureReported || loggedMarker != 1 || diagnostics.NextMarkerNumber() != 2)
        throw new Exception("Screenshot failure suppressed or altered debug marker numbering.");
}

static void DebugDistanceJump()
{
    var diagnostics = new DebugRideDiagnostics();
    diagnostics.Begin(true);
    var current = RideState.Initial with { DistanceMeters = 100, ElapsedSeconds = 30, VirtualSpeedKph = 25, ElevationGainMeters = 42 };
    if (!diagnostics.TryApplyDistanceJump(current, 2130, out var jumped))
        throw new Exception("Debug jump was rejected while recording; recording must be finalized by the session first.");
    Near(2130, jumped.DistanceMeters, 0);
    Near(0, jumped.VirtualSpeedKph, 0);
    Near(current.ElapsedSeconds, jumped.ElapsedSeconds, 0);
    Near(current.ElevationGainMeters, jumped.ElevationGainMeters, 0);
    if (!diagnostics.TryApplyDistanceJump(jumped, 12_000, out var laterLap))
        throw new Exception("Jump to an absolute distance on a later lap was rejected.");
    Near(12_000, laterLap.DistanceMeters, 0);
    if (diagnostics.TryApplyDistanceJump(current, double.NaN, out _) ||
        diagnostics.TryApplyDistanceJump(current, -1, out _))
        throw new Exception("Invalid route jump distance was accepted.");
    diagnostics.Begin(false);
    if (diagnostics.TryApplyDistanceJump(current, 5830, out _))
        throw new Exception("Jump was allowed outside Debug mode.");
}

static void BuildingFallbackVariation()
{
    var first = BuildingVisualStyle.ResolveFallbackVisualHeight(901, "generic", 72, 8);
    Near(first, BuildingVisualStyle.ResolveFallbackVisualHeight(901, "generic", 72, 8), 0);
    if (first is < 3.3f or > 4.8f) throw new Exception($"Fallback low-rise height was outside its visual band: {first} m.");
    var variants = Enumerable.Range(1, 40)
        .Select(id => BuildingVisualStyle.ResolveFallbackVisualHeight(id, "generic", 72, 8)).Distinct().Count();
    if (variants < 10) throw new Exception("Fallback buildings did not get varied deterministic heights.");
    Near(3, BuildingVisualStyle.ResolveFallbackVisualHeight(901, "industrial/service", 72, 3), 0);

    var roofs = Enumerable.Range(1, 80)
        .Select(id => BuildingVisualStyle.ResolveRoofShape(id, null, "generic", "Fallback", 90, 8)).ToArray();
    if (!roofs.Contains("flat") || !roofs.Contains("gabled") || !roofs.Contains("hipped"))
        throw new Exception("Unknown low-rise roofs did not receive the expected restrained style mix.");
    if (BuildingVisualStyle.ResolveRoofShape(9, "gabled", "generic", "Fallback", 90, 8) != "gabled")
        throw new Exception("Mapped roof metadata was not preserved.");
    var brick = BuildingVisualStyle.FacadeColor(0, null, "brick");
    if (brick != new BuildingRgb(.57f, .34f, .27f)) throw new Exception("Mapped brick facade material was not used.");
    var explicitColour = BuildingVisualStyle.FacadeColor(0, "#abc", "brick");
    if (explicitColour == brick) throw new Exception("Explicit mapped building colour did not take priority over material.");
}

static void BuildingFacadeVariation()
{
    var ring = new[]
    {
        new System.Numerics.Vector3(-12, 0, -6), new System.Numerics.Vector3(12, 0, -6),
        new System.Numerics.Vector3(12, 0, 6), new System.Numerics.Vector3(-12, 0, 6),
        new System.Numerics.Vector3(-12, 0, -6)
    };
    var firstPattern = BuildingVisualStyle.FacadePattern(431);
    var first = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true, facadePattern: firstPattern);
    var repeat = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true, facadePattern: firstPattern);
    var other = BuildingMeshGeometry.BuildStyled(ring, 9, "flat", 0, 3, true,
        facadePattern: BuildingVisualStyle.FacadePattern(877));
    if (!first.Windows.SequenceEqual(repeat.Windows)) throw new Exception("Facade pattern was not repeatable.");
    if (first.Windows.SequenceEqual(other.Windows)) throw new Exception("Distinct buildings got identical window rhythms.");
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}.");
}

static void StartupOnlineStatusIsCompact()
{
    if (!OnlineStatusText.UsesEos("EOS") || !OnlineStatusText.UsesEos("EpicOnlineServices") || OnlineStatusText.UsesEos("WebSocket"))
        throw new Exception("The configured EOS provider names should be recognized consistently.");
    if (OnlineStatusText.ForStartup(false, false, null, false, true) != "Online Riding: Off")
        throw new Exception("New offline users should see the compact off state.");
    if (OnlineStatusText.ForStartup(true, false, false, false, true) != "EOS not configured")
        throw new Exception("Missing EOS settings should have a concise unavailable state.");
    if (OnlineStatusText.ForStartup(true, false, false, true, true, eosConfigurationInvalid: true) != "EOS configuration invalid")
        throw new Exception("Invalid EOS settings should have a concise configuration error state.");
    if (OnlineStatusText.ForStartup(true, false, false, true, true) != "Online unavailable")
        throw new Exception("A temporary EOS outage should not expose provider diagnostics.");
    if (OnlineStatusText.ForStartup(true, false, false, true, true, eosRuntimeUnavailable: true) != "EOS runtime unavailable")
        throw new Exception("A failed native EOS load should be named clearly in the UI.");
    if (OnlineStatusText.ForStartup(true, false, false, true, false) != "Online unavailable")
        throw new Exception("A WebSocket outage should stay concise.");
    if (OnlineStatusText.ForStartup(true, true, true, true, true) != "Online")
        throw new Exception("Connected state should be clear and concise.");
}

static void EosLobbyStatusPhases()
{
    if (EosLobbyStatusText.ForMembership(1, 0, 0, false) != "EOS lobby joined · 1 rider")
        throw new Exception("One member should be reported as a joined singleton lobby.");
    if (EosLobbyStatusText.ForMembership(2, 0, 0, false) != "EOS lobby joined · 2 riders · peers discovered")
        throw new Exception("Lobby membership should be distinguished from P2P negotiation.");
    if (EosLobbyStatusText.ForMembership(2, 1, 0, false) != "EOS lobby joined · 2 riders · waiting for rider state")
        throw new Exception("An established transport without state must not be reported as gameplay-connected.");
    if (EosLobbyStatusText.ForMembership(2, 1, 1, false) != "EOS lobby joined · 2 riders · rider-state exchange active")
        throw new Exception("Accepted live state should be reported as an active exchange.");
    if (EosLobbyStatusText.ForMembership(2, 0, 0, true) != "EOS lobby joined · 2 riders · P2P failed")
        throw new Exception("A P2P failure should remain distinct from authentication or lobby join.");
}

static void EosPeerRequestWaitsForMembership()
{
	var gate = new EosPeerMembershipGate();
	if (gate.OnIncomingRequest("peer-1"))
		throw new Exception("An unknown peer must not be accepted before the lobby confirms membership.");
	if (!gate.AddMember("peer-1"))
		throw new Exception("The deferred request should become eligible when membership arrives.");
	if (!gate.OnIncomingRequest("peer-1"))
		throw new Exception("A confirmed lobby member's next P2P request should be accepted immediately.");
	gate.RemoveMember("peer-1");
	if (gate.OnIncomingRequest("peer-1"))
		throw new Exception("A peer who left the lobby must no longer be accepted.");
}

static void EosReceiveBufferSizing()
{
	const int maximum = 8192;
	var small = EosReceiveBufferPlan.ForPacket(236, maximum);
	if (small.BufferLength != 236 || small.MaxDataSizeBytes != 236 || small.Oversized)
		throw new Exception("Small packets need a matching allocated buffer and EOS receive capacity.");
	var empty = EosReceiveBufferPlan.ForPacket(0, maximum);
	if (empty.BufferLength != maximum || empty.MaxDataSizeBytes != maximum || empty.Oversized)
		throw new Exception("A zero reported size must still use a bounded, explicit receive capacity and be rejected after receive.");
	var large = EosReceiveBufferPlan.ForPacket(maximum + 1u, maximum);
	if (large.BufferLength != maximum || large.MaxDataSizeBytes != maximum || !large.Oversized)
		throw new Exception("Oversized packets must be drained only into a bounded buffer, then discarded.");
	var next = EosReceiveBufferPlan.ForPacket(64, maximum);
	if (next.BufferLength != 64 || next.MaxDataSizeBytes != 64 || next.Oversized)
		throw new Exception("Buffer capacity must be recalculated independently for successive packets.");
}

static void EosProductUserHashesArePrivate()
{
    const string productUserId = "test-product-user-id-value";
    var first = EosIdentityDiagnostics.HashProductUserId(productUserId);
    var second = EosIdentityDiagnostics.HashProductUserId(productUserId);
    if (first != second || first.Length != 16 || first == productUserId)
        throw new Exception("Product User ID diagnostics must be stable, truncated, and must not expose the source ID.");
    if (first == EosIdentityDiagnostics.HashProductUserId("another-product-user-id"))
        throw new Exception("Distinct Product User IDs should produce distinct diagnostic hashes.");
    if (EosIdentityDiagnostics.HashProductUserId(null) != "missing")
        throw new Exception("Missing Product User IDs should be represented safely.");
}

static void EosConfigurationPrecedence()
{
    var root = Path.Combine(Path.GetTempPath(), "wodra-eos-config-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var userFile = Path.Combine(root, "user.json");
        var packagedFile = Path.Combine(root, "packaged.json");
        File.WriteAllText(userFile, """{"ProductId":"file-product","SandboxId":"file-sandbox","DeploymentId":"file-deployment","ClientId":"file-client","ClientSecret":"file-secret"}""");
        File.WriteAllText(packagedFile, """{"ProductId":"package-product","SandboxId":"package-sandbox","DeploymentId":"package-deployment","ClientId":"package-client","ClientSecret":"package-secret"}""");
        var fileLoaded = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>(), new[] { userFile, packagedFile });
        if (!fileLoaded.IsConfigured || fileLoaded.SourcePath != Path.GetFullPath(userFile) || fileLoaded.ProductId != "file-product")
            throw new Exception("The first (per-user) JSON candidate should load before the packaged file.");

        var overridden = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>
        {
            ["WODRA_EOS_PRODUCT_ID"] = "environment-product",
            ["WODRA_EOS_CLIENT_SECRET"] = "environment-secret"
        }, new[] { userFile, packagedFile });
        if (!overridden.IsConfigured || overridden.ProductId != "environment-product" || overridden.ClientSecret != "environment-secret" || overridden.SandboxId != "file-sandbox")
            throw new Exception("Non-empty environment values should override matching file fields only.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void EosConfigurationValidation()
{
    var root = Path.Combine(Path.GetTempPath(), "wodra-eos-config-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var malformed = Path.Combine(root, "eos-client.json");
        const string secretSentinel = "NEVER-LOG-THIS-SECRET";
        File.WriteAllText(malformed, secretSentinel);
        var invalid = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>(), new[] { malformed });
        if (!invalid.IsInvalid || invalid.IsConfigured || invalid.LoadError!.Contains(secretSentinel, StringComparison.Ordinal))
            throw new Exception("Malformed configuration should yield a generic diagnostic without echoing file contents.");

        var missing = EosClientConfiguration.LoadFromSources(new Dictionary<string, string?>(), Array.Empty<string>());
        if (missing.IsInvalid || missing.IsConfigured || missing.MissingVariables.Count != 5)
            throw new Exception("Missing configuration should be distinguishable from malformed configuration and identify required fields.");
        try { missing.Validate(); throw new Exception("Missing configuration should fail validation."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("WODRA_EOS_CLIENT_SECRET", StringComparison.Ordinal)) { }
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void PackagedRouteDirectoryResolution()
{
    var exportRoot = Path.Combine(Path.GetTempPath(), "WodraExport");
    var resolved = RoutePackageFiles.ResolvePackagedPackagesDirectory(Path.Combine(exportRoot, "wodra.exe"),
        Path.Combine(exportRoot, "managed"));
    if (!string.Equals(resolved, Path.Combine(exportRoot, "Routes"), StringComparison.OrdinalIgnoreCase))
        throw new Exception("Exported route data should resolve from the executable directory, regardless of current working directory.");

    var fallback = RoutePackageFiles.ResolvePackagedPackagesDirectory(null, Path.Combine(exportRoot, "managed"));
    if (!string.Equals(fallback, Path.Combine(exportRoot, "managed", "Routes"), StringComparison.OrdinalIgnoreCase))
        throw new Exception("The application base directory should be used when no executable path is available.");
}

sealed class TestControllerPreference
{
    public ControllerDeviceTypePreference DeviceType { get; set; } = ControllerDeviceTypePreference.AutoDetect;
}
