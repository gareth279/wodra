using System.IO;
using Velopack;

var cases = new (string Name, string Installed, string Candidate, bool Expected)[]
{
    ("newer alpha is offered", "0.1.0-alpha.1", "0.1.0-alpha.2", true),
    ("same version is suppressed", "0.1.0-alpha.2", "0.1.0-alpha.2", false),
    ("older version is suppressed", "0.1.0-alpha.3", "0.1.0-alpha.2", false),
    ("stable release outranks alpha", "0.1.0-alpha.9", "0.1.0", true),
    ("later Alpha patch series outranks earlier Stable", "0.1.0", "0.1.1-alpha.1", true),
    ("same series stable outranks prerelease", "0.1.0-alpha.94", "0.1.0", true),
    ("higher prerelease ordinal is newer", "0.1.0-alpha.9", "0.1.0-alpha.10", true),
};

foreach (var test in cases)
{
    var actual = UpdateVersionPolicy.IsNewer(SemanticVersion.Parse(test.Installed), SemanticVersion.Parse(test.Candidate));
    if (actual != test.Expected)
        throw new InvalidOperationException($"{test.Name}: expected {test.Expected}, got {actual}.");
    Console.WriteLine($"PASS {test.Name}");
}

if (UpdateVersionPolicy.IsNewer(null, SemanticVersion.Parse("0.1.0-alpha.1")))
    throw new InvalidOperationException("An unavailable installed version must not trigger an update prompt.");
Console.WriteLine("PASS unavailable installed version is suppressed");

if (UpdateChannelPolicy.InitialForNewInstallation("stable") != "stable" ||
	UpdateChannelPolicy.InitialForNewInstallation("alpha") != "alpha" ||
	UpdateChannelPolicy.InitialForNewInstallation("dev") != "alpha" ||
	UpdateChannelPolicy.ResolvePersistedValue(null, hasExistingSettings: true, installerChannel: "alpha", hasExplicitSetting: false) != "alpha" ||
	UpdateChannelPolicy.ResolvePersistedValue(null, hasExistingSettings: true, installerChannel: "stable", hasExplicitSetting: false) != "stable" ||
	UpdateChannelPolicy.ResolvePersistedValue("alpha", hasExistingSettings: true, installerChannel: "stable") != "alpha" ||
	UpdateChannelPolicy.ResolvePersistedValue(null, hasExistingSettings: false, installerChannel: "stable") != "stable")
	throw new InvalidOperationException("Fresh installer channel initialization must allow exactly Alpha and Stable.");
if (!UpdateChannelPolicy.IsStagedUpdateForChannel(null, "alpha", "alpha") ||
	UpdateChannelPolicy.IsStagedUpdateForChannel("alpha", "alpha", "stable") ||
	!UpdateChannelPolicy.IsStagedUpdateForChannel("stable", "alpha", "stable") ||
	UpdateChannelPolicy.IsStagedUpdateForChannel(string.Empty, "alpha", "alpha"))
	throw new InvalidOperationException("Staged updates must remain isolated to the channel that produced them.");
if (UpdateVersionPolicy.ExplainNoUpdate("0.1.1-alpha.2", "stable", SemanticVersion.Parse("0.1.0")) is null ||
	UpdateVersionPolicy.ExplainNoUpdate("0.1.0-alpha.94", "stable", null) is null)
	throw new InvalidOperationException("An older or unavailable Stable feed must explain that the newer Alpha remains installed.");
if (UpdateVersionPolicy.ExplainNoUpdate("0.1.0", "alpha", SemanticVersion.Parse("0.1.0-alpha.95")) is not null)
	throw new InvalidOperationException("A non-newer Alpha candidate must not be offered as an update.");
Console.WriteLine("PASS two-channel initialization and downgrade explanation");

foreach (var hook in new[] { "--veloapp-install", "--veloapp-obsolete", "--veloapp-updated", "--veloapp-uninstall" })
    if (!VelopackLifecycleArguments.IsLifecycleArgument(hook))
        throw new InvalidOperationException($"Lifecycle argument '{hook}' was not recognized.");
Console.WriteLine("PASS all Velopack lifecycle arguments are recognized before scene startup");
if (VelopackLifecycleArguments.IsLifecycleArgument("--headless"))
    throw new InvalidOperationException("Ordinary Godot arguments must not be treated as Velopack lifecycle hooks.");
Console.WriteLine("PASS ordinary launch arguments remain ordinary");

const string sourceCommit = "64185339fbe9a14b13535ae629e4f9fb4c79d3c5";
var developmentIdentity = BuildIdentityPolicy.Resolve(
	null, null, $"1.0.0+{sourceCommit}", "Debug", "alpha");
if (developmentIdentity.ApplicationVersion != "Development (version unavailable)" ||
	developmentIdentity.BuildType != "Development build (unpackaged)" ||
	developmentIdentity.UpdateChannel != "alpha (configured feed; package channel unavailable)" ||
	developmentIdentity.Configuration != "Debug" || developmentIdentity.SourceCommit != sourceCommit)
	throw new InvalidOperationException("Development build identity must use source metadata and must not look like a published version.");
Console.WriteLine("PASS development build identity is not presented as a published version");

var packagedIdentity = BuildIdentityPolicy.Resolve(
	"0.1.0-alpha.64", "alpha", $"0.1.0-alpha.64+{sourceCommit}", "Release", "alpha");
if (packagedIdentity.ApplicationVersion != "0.1.0-alpha.64" ||
	packagedIdentity.BuildType != "Packaged release" || packagedIdentity.UpdateChannel != "alpha" ||
	packagedIdentity.Configuration != "Release" || packagedIdentity.SourceCommit != sourceCommit)
	throw new InvalidOperationException("Packaged build identity must match the installed package and source revision.");
Console.WriteLine("PASS packaged identity uses the installed package version and channel");

var portableIdentity = BuildIdentityPolicy.Resolve(
	null, "alpha", $"0.1.0-alpha.64+{sourceCommit}", "Release", "alpha", isPortablePackage: true);
if (portableIdentity.ApplicationVersion != "0.1.0-alpha.64" ||
	portableIdentity.BuildType != "Packaged portable build" || portableIdentity.UpdateChannel != "alpha")
	throw new InvalidOperationException("Portable package identity must use its packaged version and channel metadata.");
Console.WriteLine("PASS portable package identity uses embedded package metadata");

var unavailableIdentity = BuildIdentityPolicy.Resolve(null, null, null, null, "alpha");
if (unavailableIdentity.SourceCommit != "Unavailable" || unavailableIdentity.Configuration != "Unavailable")
	throw new InvalidOperationException("Missing source and configuration metadata must be clearly unavailable.");
var copyText = packagedIdentity.ToPlainText();
if (!copyText.Contains(sourceCommit, StringComparison.Ordinal) ||
	copyText.Contains("C:\\", StringComparison.OrdinalIgnoreCase) ||
	copyText.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
	copyText.Contains("profile", StringComparison.OrdinalIgnoreCase))
	throw new InvalidOperationException("Copied build information must include the source and exclude local paths and profile details.");
Console.WriteLine("PASS copied build information is compact and excludes local paths and profile details");

await UpdaterStateTests.RunAsync();

static class UpdaterStateTests
{
	private static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
		Console.WriteLine($"PASS {message}");
	}

	public static async Task RunAsync()
	{
		var visible = true;
		var safeToApply = false;
		var backend = new FakeUpdaterBackend();
		var log = new List<string>();
		var service = new VelopackUpdateService(backend, "0.1.0-alpha.1", "alpha", () => safeToApply,
			(name, _) => log.Add(name), checkTimeout: TimeSpan.FromMilliseconds(250));

		var checkResult = new TaskCompletionSource<UpdateOffer?>(TaskCreationOptions.RunContinuationsAsynchronously);
		backend.Check = () => checkResult.Task;
		var check = service.CheckForUpdatesAsync();
		var joinedCheck = service.CheckForUpdatesAsync();
		Require(ReferenceEquals(check, joinedCheck) && backend.CheckCalls == 1,
			"concurrent startup and Settings checks share one feed request");
		checkResult.SetResult(new UpdateOffer("0.1.0-alpha.2", "test release", new object()));
		var available = await check;
		Require(available.Status == UpdaterStatus.Available && available.Offer?.Version == "0.1.0-alpha.2",
			"newer packaged version transitions to Available");

		var downloadResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		backend.Download = (_, progress, _) => { progress(42); return downloadResult.Task; };
		var download = service.DownloadAndInstallAsync();
		visible = false; // PR #152 hides the startup panel while the root service remains alive.
		downloadResult.SetResult();
		var staged = await download;
		Require(!visible && staged.Status == UpdaterStatus.Staged && staged.ProgressPercent == 100 && backend.ApplyCalls == 0,
			"hiding the startup panel does not abort download and an active ride defers apply");
		safeToApply = true;
		backend.Apply = _ => throw new InvalidOperationException("synthetic apply failure");
		var failedApply = service.ApplyStagedUpdate();
		Require(failedApply.Status == UpdaterStatus.Failed && failedApply.ProgressPercent == 100 && backend.ApplyCalls == 1,
			"failed apply remains visible and retryable");
		backend.Apply = _ => { };
		var retryApply = service.ApplyStagedUpdate();
		Require(retryApply.Status == UpdaterStatus.Failed && retryApply.FailureKind == "restart-not-started" && backend.ApplyCalls == 2,
			"unexpected Velopack return is not reported as a successful update");
		Require(log.Contains("update_apply_failed") && log.Contains("update_apply_returned_unexpectedly"),
			"apply failures and unexpected returns are logged");

		var safeToApplyRetry = false;
		var verifyBackend = new FakeUpdaterBackend
		{
			StageCompletedDownload = false,
			Check = () => Task.FromResult<UpdateOffer?>(new UpdateOffer("0.1.0-alpha.2", "verify test", new object())),
			Download = (_, progress, _) =>
			{
				progress(100);
				return Task.FromException(new InvalidDataException("package verification failed"));
			}
		};
		var verifyService = new VelopackUpdateService(verifyBackend, "0.1.0-alpha.1", "alpha",
			() => safeToApplyRetry, (_, _) => { });
		await verifyService.CheckForUpdatesAsync();
		var verifyFailure = await verifyService.DownloadAndInstallAsync();
		Require(verifyFailure.Status == UpdaterStatus.Failed && verifyFailure.ProgressPercent == 100 &&
			!verifyFailure.HasStagedPackage && verifyBackend.ApplyCalls == 0,
			"100 percent download progress followed by package verification failure is not staged");
		verifyBackend.Download = (_, progress, _) => { progress(100); return Task.CompletedTask; };
		var missingStage = await verifyService.DownloadAndInstallAsync();
		Require(missingStage.Status == UpdaterStatus.Failed && missingStage.ProgressPercent == 100 &&
			!missingStage.HasStagedPackage && verifyBackend.ApplyCalls == 0,
			"completed download without a matching Velopack staged package remains retryable");
		verifyBackend.StageCompletedDownload = true;
		var verifiedRetry = await verifyService.DownloadAndInstallAsync();
		Require(verifiedRetry.Status == UpdaterStatus.Staged && verifiedRetry.HasStagedPackage &&
			verifyBackend.ApplyCalls == 0,
			"failed verification retries the download and only successful staging enables apply");
		safeToApplyRetry = true;
		var verifiedApply = verifyService.ApplyStagedUpdate();
		Require(verifiedApply.Status == UpdaterStatus.Failed && verifiedApply.HasStagedPackage &&
			verifyBackend.ApplyCalls == 1,
			"a staged package remains explicitly available to retry after apply fails");

		var neverCompletes = new TaskCompletionSource<UpdateOffer?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var timeoutBackend = new FakeUpdaterBackend { Check = () => neverCompletes.Task };
		var timeoutService = new VelopackUpdateService(timeoutBackend, "0.1.0-alpha.1", "alpha", () => true,
			(_, _) => { }, checkTimeout: TimeSpan.FromMilliseconds(40));
		var timedOut = await timeoutService.CheckForUpdatesAsync();
		Require(timedOut.Status == UpdaterStatus.Failed && timedOut.FailureKind == "timeout",
			"feed timeout is distinct from an up-to-date result");
		neverCompletes.SetResult(null);
		timeoutBackend.Check = () => Task.FromResult<UpdateOffer?>(null);
		var retryCheck = await timeoutService.CheckForUpdatesAsync();
		Require(retryCheck.Status == UpdaterStatus.UpToDate && timeoutBackend.CheckCalls == 2,
			"timed-out feed check can be retried");

		var offlineBackend = new FakeUpdaterBackend { Check = () => Task.FromException<UpdateOffer?>(new HttpRequestException("offline")) };
		var offline = new VelopackUpdateService(offlineBackend, "0.1.0-alpha.1", "alpha", () => true, (_, _) => { });
		var offlineState = await offline.CheckForUpdatesAsync();
		Require(offlineState.Status == UpdaterStatus.Failed && offlineState.FailureKind == nameof(HttpRequestException),
			"offline check reports failure instead of claiming no update");
		offlineBackend.Check = () => Task.FromResult<UpdateOffer?>(null);
		Require((await offline.CheckForUpdatesAsync()).Status == UpdaterStatus.UpToDate,
			"network failure recovers on retry");

		var interruptedBackend = new FakeUpdaterBackend
		{
			Check = () => Task.FromResult<UpdateOffer?>(new UpdateOffer("0.1.0-alpha.2", "notes", new object())),
			Download = (_, _, _) => Task.FromException(new OperationCanceledException("interrupted"))
		};
		var interrupted = new VelopackUpdateService(interruptedBackend, "0.1.0-alpha.1", "alpha", () => false, (_, _) => { });
		await interrupted.CheckForUpdatesAsync();
		var interruptedState = await interrupted.DownloadAndInstallAsync();
		Require(interruptedState.Status == UpdaterStatus.Failed && interruptedState.Offer is not null,
			"interrupted package download preserves the update offer for retry");
		interruptedBackend.Download = (_, _, _) => Task.CompletedTask;
		Require((await interrupted.DownloadAndInstallAsync()).Status == UpdaterStatus.Staged,
			"interrupted download can be resumed by retry");

		var selectedFeeds = new List<string>();
		var switchedBackend = new FakeUpdaterBackend();
		var switched = new VelopackUpdateService(switchedBackend, "0.1.1-alpha.2", "alpha", () => false,
			(_, _) => { }, backendFactory: channel =>
			{
				selectedFeeds.Add(channel);
				return new FakeUpdaterBackend
				{
					ChannelName = channel,
					CurrentVersion = "0.1.1-alpha.2",
					Check = () => Task.FromResult<UpdateOffer?>(channel == "stable"
						? new UpdateOffer("0.1.0", "older stable", new object())
						: new UpdateOffer("0.1.1-alpha.3", "newer alpha", new object()))
				};
			});
		var stableResult = await switched.ChangeChannelAsync("stable");
		Require(selectedFeeds.SequenceEqual(["stable"]) && stableResult.Channel == "stable" &&
			stableResult.Status == UpdaterStatus.UpToDate && stableResult.Message.Contains("newer Alpha", StringComparison.Ordinal),
			"Alpha to Stable rechecks only Stable and keeps a newer Alpha installed");
		var alphaResult = await switched.ChangeChannelAsync("alpha");
		Require(selectedFeeds.SequenceEqual(["stable", "alpha"]) && alphaResult.Channel == "alpha" &&
			alphaResult.Status == UpdaterStatus.Available && alphaResult.Offer?.Version == "0.1.1-alpha.3",
			"Stable to Alpha rechecks the Alpha feed and offers its newer eligible build");

		var downloadBackend = new FakeUpdaterBackend
		{
			Check = () => Task.FromResult<UpdateOffer?>(new UpdateOffer("0.1.0-alpha.2", "download", new object())),
			Download = async (_, _, cancellationToken) => await Task.Delay(Timeout.Infinite, cancellationToken)
		};
		var stagedChannel = (string?)null;
		var changingDuringDownload = new VelopackUpdateService(downloadBackend, "0.1.0-alpha.1", "alpha", () => false,
			(_, _) => { }, backendFactory: channel => new FakeUpdaterBackend { ChannelName = channel },
			setStagedChannel: channel => stagedChannel = channel);
		await changingDuringDownload.CheckForUpdatesAsync();
		var pendingDownload = changingDuringDownload.DownloadAndInstallAsync();
		var changedDuringDownload = await changingDuringDownload.ChangeChannelAsync("stable");
		await pendingDownload;
		Require(changedDuringDownload.Channel == "stable" && !changedDuringDownload.HasStagedPackage && stagedChannel is null,
			"channel switching cancels an in-progress download before replacing its feed");

		var metadataGate = new TaskCompletionSource<UpdateOffer?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var metadataBackend = new FakeUpdaterBackend { ChannelName = "alpha", Check = () => metadataGate.Task };
		var metadataFeeds = new List<string>();
		var metadataPersisted = "alpha";
		var metadataSwitch = new VelopackUpdateService(metadataBackend, "0.1.1-alpha.1", "alpha", () => false,
			(_, _) => { }, backendFactory: channel =>
			{
				metadataFeeds.Add(channel);
				return new FakeUpdaterBackend { ChannelName = channel, CurrentVersion = "0.1.1-alpha.1" };
			}, persistChannel: channel => metadataPersisted = channel);
		var activeMetadataCheck = metadataSwitch.CheckForUpdatesAsync();
		var staleStableSelection = metadataSwitch.ChangeChannelAsync("stable");
		var latestAlphaSelection = metadataSwitch.ChangeChannelAsync("alpha");
		Require(ReferenceEquals(staleStableSelection, latestAlphaSelection),
			"rapid channel requests share one serialized transition");
		metadataGate.SetResult(null);
		await activeMetadataCheck;
		var metadataSwitchResult = await latestAlphaSelection;
		Require(metadataFeeds.SequenceEqual(["alpha"]) && metadataSwitchResult.Channel == "alpha" && metadataPersisted == "alpha",
			"latest Alpha selection wins while an Alpha metadata check is running");

		var persistenceFailureBackend = new FakeUpdaterBackend { ChannelName = "alpha" };
		var persistenceFailure = new VelopackUpdateService(persistenceFailureBackend, "0.1.1-alpha.1", "alpha", () => false,
			(_, _) => { }, backendFactory: channel => new FakeUpdaterBackend { ChannelName = channel },
			persistChannel: channel =>
			{
				if (channel == "stable") throw new IOException("settings save failed");
			});
		var persistenceFailureResult = await persistenceFailure.ChangeChannelAsync("stable");
		Require(persistenceFailureResult.Channel == "alpha" && persistenceFailure.Snapshot.Channel == "alpha" &&
			persistenceFailureResult.Status == UpdaterStatus.Failed,
			"a failed channel save leaves the previous selected backend active");

		var rapidDownloadBackend = new FakeUpdaterBackend
		{
			ChannelName = "alpha",
			CurrentVersion = "0.1.1-alpha.1",
			Check = () => Task.FromResult<UpdateOffer?>(new UpdateOffer("0.1.1-alpha.2", "download", new object())),
			Download = async (_, _, cancellationToken) => await Task.Delay(Timeout.Infinite, cancellationToken)
		};
		var downloadFeeds = new List<string>();
		var downloadPersisted = "alpha";
		string? rapidlyStagedChannel = null;
		var rapidDownloadSwitch = new VelopackUpdateService(rapidDownloadBackend, "0.1.1-alpha.1", "alpha", () => false,
			(_, _) => { }, backendFactory: channel =>
			{
				downloadFeeds.Add(channel);
				return new FakeUpdaterBackend
				{
					ChannelName = channel,
					CurrentVersion = "0.1.1-alpha.1",
					Check = () => Task.FromResult<UpdateOffer?>(channel == "alpha"
						? new UpdateOffer("0.1.1-alpha.3", "latest Alpha", new object())
						: null)
				};
			}, setStagedChannel: channel => rapidlyStagedChannel = channel,
			persistChannel: channel => downloadPersisted = channel);
		await rapidDownloadSwitch.CheckForUpdatesAsync();
		var activeDownload = rapidDownloadSwitch.DownloadAndInstallAsync();
		var downloadStableSelection = rapidDownloadSwitch.ChangeChannelAsync("stable");
		var downloadLatestAlphaSelection = rapidDownloadSwitch.ChangeChannelAsync("alpha");
		await activeDownload;
		var downloadSwitchResult = await downloadLatestAlphaSelection;
		Require(ReferenceEquals(downloadStableSelection, downloadLatestAlphaSelection) &&
			downloadFeeds.SequenceEqual(["alpha"]) && downloadSwitchResult.Channel == "alpha" &&
			downloadPersisted == "alpha" && !downloadSwitchResult.HasStagedPackage && rapidlyStagedChannel is null,
			"latest Alpha selection wins during download cancellation and checks only its feed");

		var lateDownloadCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var lateDownloadBackend = new FakeUpdaterBackend
		{
			ChannelName = "alpha",
			CurrentVersion = "0.1.1-alpha.1",
			Check = () => Task.FromResult<UpdateOffer?>(new UpdateOffer("0.1.1-alpha.2", "late download", new object())),
			Download = async (_, _, _) => await lateDownloadCompletion.Task
		};
		var lateStageChannel = (string?)null;
		var lateDownloadSwitch = new VelopackUpdateService(lateDownloadBackend, "0.1.1-alpha.1", "alpha", () => true,
			(_, _) => { }, backendFactory: channel => new FakeUpdaterBackend
			{
				ChannelName = channel,
				CurrentVersion = "0.1.1-alpha.1",
				PendingUpdate = UpdateChannelPolicy.IsStagedUpdateForChannel(lateStageChannel, "alpha", channel)
					? lateDownloadBackend.PendingUpdate
					: null
			}, setStagedChannel: channel => lateStageChannel = channel,
			persistChannel: _ => { });
		await lateDownloadSwitch.CheckForUpdatesAsync();
		var completingDownload = lateDownloadSwitch.DownloadAndInstallAsync();
		var lateStableSelection = lateDownloadSwitch.ChangeChannelAsync("stable");
		var lateLatestAlphaSelection = lateDownloadSwitch.ChangeChannelAsync("alpha");
		lateDownloadCompletion.SetResult();
		await completingDownload;
		var lateSwitchResult = await lateLatestAlphaSelection;
		Require(ReferenceEquals(lateStableSelection, lateLatestAlphaSelection) && lateDownloadBackend.ApplyCalls == 0 &&
			lateSwitchResult.Channel == "alpha" && !lateSwitchResult.HasStagedPackage && lateStageChannel == string.Empty,
			"a download completing after cancellation is quarantined instead of auto-applying the old selection");

		var alreadyStaged = new UpdateOffer("0.1.1-alpha.2", "previously staged", new object());
		string? persistedStageChannel = "alpha";
		var stagedFeeds = new List<string>();
		var stagedBackend = new FakeUpdaterBackend { ChannelName = "alpha", PendingUpdate = alreadyStaged };
		var stagedSwitch = new VelopackUpdateService(stagedBackend, "0.1.1-alpha.1", "alpha", () => false,
			(_, _) => { }, backendFactory: channel =>
			{
				stagedFeeds.Add(channel);
				var matchingStage = UpdateChannelPolicy.IsStagedUpdateForChannel(persistedStageChannel, "alpha", channel)
					? alreadyStaged
					: null;
				return new FakeUpdaterBackend { ChannelName = channel, PendingUpdate = matchingStage };
			}, setStagedChannel: channel => persistedStageChannel = channel,
			persistChannel: _ => { });
		var stagedStableRequest = stagedSwitch.ChangeChannelAsync("stable");
		var stagedLatestAlphaRequest = stagedSwitch.ChangeChannelAsync("alpha");
		var stagedSwitchResult = await stagedLatestAlphaRequest;
		Require(ReferenceEquals(stagedStableRequest, stagedLatestAlphaRequest) && stagedFeeds.SequenceEqual(["alpha"]) &&
			stagedSwitchResult.Channel == "alpha" && !stagedSwitchResult.HasStagedPackage && persistedStageChannel == string.Empty,
			"rapid channel changes permanently exclude a package staged before the transition");
		await stagedSwitch.ChangeChannelAsync("stable");
		var returnedToAlphaWithInvalidatedStage = await stagedSwitch.ChangeChannelAsync("alpha");
		Require(stagedFeeds.SequenceEqual(["alpha", "stable", "alpha"]) &&
			!returnedToAlphaWithInvalidatedStage.HasStagedPackage && persistedStageChannel == string.Empty,
			"a discarded staged package stays unavailable when later switching back to its original channel");

		var portableBackend = new FakeUpdaterBackend { IsInstalled = false };
		var portable = new VelopackUpdateService(portableBackend, "0.1.0-alpha.1", "alpha", () => true, (_, _) => { });
		portable.StartAutomaticCheck();
		Require(portable.Snapshot.Status == UpdaterStatus.Unavailable && portableBackend.CheckCalls == 0 &&
			portable.Snapshot.Message.Contains("portable", StringComparison.OrdinalIgnoreCase),
			"portable build clearly skips automatic install");
	}
}

sealed class FakeUpdaterBackend : IUpdaterBackend
{
	private UpdateOffer? _pendingUpdate;
	public bool IsInstalled { get; set; } = true;
	public bool StageCompletedDownload { get; set; } = true;
	public string CurrentVersion { get; set; } = "0.1.0-alpha.1";
	public string? Channel => ChannelName;
	public string ChannelName { get; set; } = "alpha";
	public UpdateOffer? PendingUpdate { get => _pendingUpdate; set => _pendingUpdate = value; }
	public int CheckCalls { get; private set; }
	public int ApplyCalls { get; private set; }
	public Func<Task<UpdateOffer?>> Check { get; set; } = () => Task.FromResult<UpdateOffer?>(null);
	public Func<UpdateOffer, Action<int>, CancellationToken, Task> Download { get; set; } = (_, _, _) => Task.CompletedTask;
	public Action<UpdateOffer> Apply { get; set; } = _ => { };
	public Task<UpdateOffer?> CheckForUpdatesAsync() { CheckCalls++; return Check(); }
	public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
	{
		await Download(offer, progress, cancellationToken);
		if (StageCompletedDownload) _pendingUpdate = offer;
	}
	public void ApplyAndRestart(UpdateOffer offer) { ApplyCalls++; Apply(offer); }
}
