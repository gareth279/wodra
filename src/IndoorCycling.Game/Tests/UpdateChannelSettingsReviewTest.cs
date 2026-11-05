using Godot;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

public partial class UpdateChannelSettingsReviewTest : Control
{
	public override async void _Ready()
	{
		try
		{
			DisplayServer.WindowSetSize(new Vector2I(1180, 760));
			var settings = new AppSettings { UpdateChannel = UpdateChannelPolicy.Alpha };
			typeof(GameBootstrap).GetProperty(nameof(GameBootstrap.Settings), BindingFlags.Public | BindingFlags.Static)!
				.SetValue(null, settings);
			var root = new VBoxContainer { Name = "ReviewSettings", Position = new Vector2(80, 70), Size = new Vector2(760, 560), SizeFlagsHorizontal = SizeFlags.ExpandFill };
			AddChild(root);
			root.AddChild(new Label { Text = "Settings", Name = "SettingsHeading" });
			root.AddChild(new Label { Text = "Updates", Name = "UpdatesHeading" });
			var backendFactoryCalls = new System.Collections.Generic.List<string>();
			var initialBackend = new ChannelReviewBackend("alpha", "0.1.1-alpha.2", "0.1.1-alpha.3");
			var service = new VelopackUpdateService(initialBackend, "0.1.1-alpha.2", "alpha", () => true,
				(_, _) => { }, SynchronizationContext.Current,
				backendFactory: channel =>
				{
					backendFactoryCalls.Add(channel);
					return new ChannelReviewBackend(channel, "0.1.1-alpha.2", channel == "stable" ? "0.1.0" : "0.1.1-alpha.3");
				});
			var panel = new VelopackUpdatePanel(showAvailableUpdateAutomatically: false, showChannelSelector: true, service: service)
			{ Name = "SettingsUpdatePanel", SizeFlagsHorizontal = SizeFlags.ExpandFill };
			root.AddChild(panel);
			panel.Attach();
			var picker = panel.FindChild("UpdateChannelSelector", true, false) as OptionButton
				?? throw new InvalidOperationException("Settings has no update-channel selector.");
			Require(picker.ItemCount == 2 && picker.GetItemText(0) == "Stable" && picker.GetItemText(1) == "Alpha",
				"Settings exposes exactly Stable and Alpha");
			Require(picker.Selected == 1, "legacy Alpha installation selects Alpha by default");
			var description = panel.FindChild("UpdateChannelDescription", true, false) as Label;
			Require(description?.Text.Contains("Tested releases recommended for normal use", StringComparison.Ordinal) == true &&
				description.Text.Contains("may have bugs", StringComparison.Ordinal), "channel guidance describes both audiences");
			await service.CheckForUpdatesAsync();
			var identity = panel.FindChild("InstalledUpdateIdentity", true, false) as Label;
			Require(identity?.Text.Contains("0.1.1-alpha.2", StringComparison.Ordinal) == true &&
				identity.Text.Contains("alpha", StringComparison.Ordinal), "Settings shows the installed version and selected channel");
			Require(panel.FindChild("InstallUpdateButton", true, false) is Button install && install.Visible && install.Text == "Download and Install",
				"a newer eligible build is shown through the existing explicit install flow");

			var screenshot = OS.GetEnvironment("WODRA_CHANNEL_SCREENSHOT");
			settings.UpdateChannel = UpdateChannelPolicy.Stable;
			picker.Select(0);
			var stableResult = await service.ChangeChannelAsync(UpdateChannelPolicy.Stable);
			Require(stableResult.Status == UpdaterStatus.UpToDate && stableResult.Message.Contains("newer Alpha", StringComparison.Ordinal),
				"older Stable release remains unavailable without downgrading the installed Alpha");
			Require(picker.Selected == 0, "channel selector reflects the selected Stable feed");
			if (!string.IsNullOrWhiteSpace(screenshot) && !OS.HasFeature("headless"))
				await CaptureScreenshot(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(screenshot)!, "update-channel-stable-no-newer-release.png"));
			settings.UpdateChannel = UpdateChannelPolicy.Alpha;
			picker.Select(1);
			var alphaAgain = await service.ChangeChannelAsync(UpdateChannelPolicy.Alpha);
			Require(alphaAgain.Status == UpdaterStatus.Available && alphaAgain.Offer?.Version == "0.1.1-alpha.3",
				"returning to Alpha exposes its newer eligible release");
			if (!string.IsNullOrWhiteSpace(screenshot) && !OS.HasFeature("headless")) await CaptureScreenshot(screenshot);
			GD.Print("[UpdateChannelSettingsReview] PASS selector, migration default, channel guidance, installed identity, and update action");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[UpdateChannelSettingsReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private static void Require(bool value, string assertion)
	{
		if (!value) throw new InvalidOperationException(assertion);
		GD.Print($"[UpdateChannelSettingsReview] PASS {assertion}");
	}

	private async Task CaptureScreenshot(string path)
	{
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		var error = GetViewport().GetTexture().GetImage().SavePng(path);
		if (error != Error.Ok) throw new InvalidOperationException($"Could not save channel Settings screenshot: {error}");
	}

	private sealed class ChannelReviewBackend(string channel, string current, string candidate) : IUpdaterBackend
	{
		public bool IsInstalled => true;
		public string CurrentVersion => current;
		public string? Channel => channel;
		public UpdateOffer? PendingUpdate => null;
		public Task<UpdateOffer?> CheckForUpdatesAsync() => Task.FromResult<UpdateOffer?>(new(candidate, "review fixture", new object()));
		public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;
		public void ApplyAndRestart(UpdateOffer offer) { }
	}
}
