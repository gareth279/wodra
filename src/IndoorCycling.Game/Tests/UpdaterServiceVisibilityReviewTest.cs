using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;

public partial class UpdaterServiceVisibilityReviewTest : Node
{
	private readonly FakeBackend _backend = new();
	private VelopackUpdateService? _service;
	private VelopackUpdatePanel? _panel;
	private Control? _startupParent;
	private bool _safeToApply;

	public override async void _Ready()
	{
		try
		{
			_service = new VelopackUpdateService(_backend, "0.1.0-alpha.1", "alpha", () => _safeToApply,
				(_, _) => { }, SynchronizationContext.Current);
			_startupParent = new Control { Name = "HiddenStartupParent" };
			AddChild(_startupParent);
			_panel = new VelopackUpdatePanel(service: _service) { Name = "UpdaterPresenter" };
			_startupParent.AddChild(_panel);
			_panel.Attach();
			var statusRow = Find<HBoxContainer>(_panel, "UpdateStatusRow");
			Require(_panel.GetCombinedMinimumSize().Y >= 40 && statusRow.GetCombinedMinimumSize().Y >= 36,
				"Settings reserves visible height for the updater status and manual check action");

			await _service.CheckForUpdatesAsync();
			Require(_service.Snapshot.Status == UpdaterStatus.Available, "manual check shows the newer package");
			var install = Find<Button>(_panel, "InstallUpdateButton");
			Require(install.Visible && !install.Disabled, "available update exposes an enabled install action");

			install.EmitSignal(Button.SignalName.Pressed);
			await Until(() => _service.Snapshot.Status == UpdaterStatus.Downloading, "download operation to start");
			_startupParent.Hide();
			await _backend.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
			Require(!_startupParent.IsVisibleInTree() && _service.Snapshot.Status == UpdaterStatus.Downloading,
				"hidden startup parent does not cancel the application-owned download");

			_backend.DownloadGate.SetResult();
			await Until(() => _service.Snapshot.Status == UpdaterStatus.Staged, "download to stage while ride is active");
			Require(_backend.ApplyCalls == 0, "active ride prevents the update from applying");
			_startupParent.Show();
			_safeToApply = true;
			_service.ApplyStagedUpdate();
			Require(_backend.ApplyCalls == 1 && _service.Snapshot.Status == UpdaterStatus.Failed,
				"returning without a Velopack restart remains a visible failure");
			Require(Find<Button>(_panel, "InstallUpdateButton").Visible,
				"the install dialog keeps a retry action after apply failure");

			GD.Print("[UpdaterServiceVisibilityReview] PASS startup visibility, active-ride deferral, and visible apply failure");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[UpdaterServiceVisibilityReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private static async Task Until(Func<bool> predicate, string description)
	{
		var end = DateTime.UtcNow + TimeSpan.FromSeconds(3);
		while (!predicate())
		{
			if (DateTime.UtcNow >= end) throw new TimeoutException($"Timed out waiting for {description}.");
			await Task.Delay(10);
		}
	}

	private static T Find<T>(Node root, string name) where T : Node
	{
		if (root is T typed && root.Name == name) return typed;
		foreach (var child in root.GetChildren())
		{
			var found = FindOrNull<T>(child, name);
			if (found is not null) return found;
		}
		throw new InvalidOperationException($"Could not find {typeof(T).Name} '{name}'.");
	}

	private static T? FindOrNull<T>(Node root, string name) where T : Node
	{
		if (root is T typed && root.Name == name) return typed;
		foreach (var child in root.GetChildren())
		{
			var found = FindOrNull<T>(child, name);
			if (found is not null) return found;
		}
		return null;
	}

	private void Require(bool condition, string description)
	{
		if (!condition) throw new InvalidOperationException(description);
		GD.Print($"[UpdaterServiceVisibilityReview] PASS {description}");
	}

	private sealed class FakeBackend : IUpdaterBackend
	{
		public bool IsInstalled => true;
		public string CurrentVersion => "0.1.0-alpha.1";
		public string? Channel => "alpha";
		public UpdateOffer? PendingUpdate => null;
		public int ApplyCalls { get; private set; }
		public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource DownloadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<UpdateOffer?> CheckForUpdatesAsync() => Task.FromResult<UpdateOffer?>(
			new UpdateOffer("0.1.0-alpha.2", "test notes", new object()));
		public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
		{
			DownloadStarted.TrySetResult();
			progress(40);
			return DownloadGate.Task;
		}
		public void ApplyAndRestart(UpdateOffer offer) => ApplyCalls++;
	}
}
