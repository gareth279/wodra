using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Media;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Implementations;

/// <summary>Exercises optional media controls and layout in the production RideScreen using an isolated mock session.</summary>
public partial class MediaControlsRideScreenReviewTest : Node
{
	private sealed class FakeMediaProvider : ISystemMediaSessionProvider
	{
		private SystemMediaSessionSnapshot _current = new(true,
			"A Long Ride Home", "Wodra Test Artist", SystemMediaPlaybackState.Playing, false, true, true, true);
		private string? _previousTitle;
		public bool FailReads { get; set; }
		public ConcurrentQueue<SystemMediaCommand> Commands { get; } = new();
		public void Set(SystemMediaSessionSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
		public Task<SystemMediaSessionSnapshot> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
			FailReads ? Task.FromException<SystemMediaSessionSnapshot>(new InvalidOperationException("mock media API failure")) :
			Task.FromResult(Volatile.Read(ref _current));
		public Task<bool> TryExecuteAsync(SystemMediaCommand command, CancellationToken cancellationToken = default)
		{
			Commands.Enqueue(command);
			var snapshot = Volatile.Read(ref _current);
			if (!snapshot.IsAvailable) return Task.FromResult(false);
			switch (command)
			{
				case SystemMediaCommand.TogglePlayback when snapshot.PlaybackState == SystemMediaPlaybackState.Playing && snapshot.CanPause:
					Set(snapshot with { PlaybackState = SystemMediaPlaybackState.Paused, CanPlay = true, CanPause = false });
					return Task.FromResult(true);
				case SystemMediaCommand.TogglePlayback when snapshot.PlaybackState == SystemMediaPlaybackState.Paused && snapshot.CanPlay:
					Set(snapshot with { PlaybackState = SystemMediaPlaybackState.Playing, CanPlay = false, CanPause = true });
					return Task.FromResult(true);
				case SystemMediaCommand.Next when snapshot.CanSkipNext:
					_previousTitle = snapshot.Title;
					Set(snapshot with { Title = "Changed Track", Artist = "Another Artist" });
					return Task.FromResult(true);
				case SystemMediaCommand.Previous when snapshot.CanSkipPrevious:
					Set(snapshot with { Title = _previousTitle ?? snapshot.Title, Artist = "Wodra Test Artist" });
					return Task.FromResult(true);
				default: return Task.FromResult(false);
			}
		}
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private readonly string _output = Path.Combine(Path.GetTempPath(), "WodraMediaControlsRideScreenReview");
	private int _assertions;
	private bool _originalMediaEnabled;
	private string _originalPacerPreset = PacerEffortPreset.DisabledId;
	private bool _originalDebugEnabled;
	private bool _originalOnlineEnabled;
	private string _originalDummyMode = "W/kg";
	private int _originalDummyWatts;
	private double _originalDummyWkg;
	private int _originalDummyCadence;
	private string _originalRecordingFolder = "";
	private bool _settingsCaptured;
	private FakeMediaProvider? _provider;
	private static bool IsHeadless => RenderingServer.GetCurrentRenderingDriverName().Contains("headless", StringComparison.OrdinalIgnoreCase)
		|| RenderingServer.GetCurrentRenderingDriverName().Contains("dummy", StringComparison.OrdinalIgnoreCase)
		|| DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

	public override async void _Ready()
	{
		try
		{
			Directory.CreateDirectory(_output);
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
			_provider = new FakeMediaProvider();
			screen.GetNode<HudController>("HudController").MediaSessionProviderOverride = _provider;
			AddChild(screen);
			await Frames(8);
			foreach (var popup in screen.FindChildren("*", "Window", recursive: true, owned: false).OfType<Window>())
				popup.Hide();
			_originalMediaEnabled = GameBootstrap.Settings.InRideMediaControlsEnabled;
			_originalPacerPreset = GameBootstrap.Settings.PacerPresetId;
			_originalDebugEnabled = GameBootstrap.Settings.DebugModeEnabled;
			_originalOnlineEnabled = GameBootstrap.Settings.OnlineRidingEnabled;
			_originalDummyMode = GameBootstrap.Settings.DummyTrainer.Mode;
			_originalDummyWatts = GameBootstrap.Settings.DummyTrainer.TargetWatts;
			_originalDummyWkg = GameBootstrap.Settings.DummyTrainer.WattsPerKg;
			_originalDummyCadence = GameBootstrap.Settings.DummyTrainer.CadenceRpm;
			_originalRecordingFolder = GameBootstrap.Settings.Recording.OutputFolder;
			_settingsCaptured = true;
			GameBootstrap.Settings.InRideMediaControlsEnabled = true;
			GameBootstrap.Settings.PacerPresetId = "moderate";
			GameBootstrap.Settings.OnlineRidingEnabled = false;
			GameBootstrap.Settings.DebugModeEnabled = false;
			GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
			GameBootstrap.Settings.DummyTrainer.TargetWatts = 150;
			GameBootstrap.Settings.DummyTrainer.CadenceRpm = 85;
			GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(ProjectSettings.GlobalizePath("user://"), "media-controls-review-fit");
			GameBootstrap.Settings.Save();
			GameBootstrap.ConfigureFakeTrainerMode();
			var profile = new RiderProfileRecord(Guid.NewGuid(), "Media Controls Test", DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow, 72, 9, true, 50);
			await GameBootstrap.SaveProfileAsync(profile);
			await GameBootstrap.SetActiveProfileAsync(profile.Id);
			GameFeaturesPreference.Set(true);

			var session = screen.GetNode<RideSessionNode>("RideSessionNode");
			var world = screen.GetNode<RideWorldView>("RideWorldView");
			var route = RouteCatalog.GetById("dunedin-one-way-oval-gardens") ?? throw new InvalidOperationException("Dunedin Oval to Gardens route is missing.");
			if (!await session.StartTrainerSessionAsync(route)) throw new InvalidOperationException("Could not start dummy production RideScreen for media controls review.");
			var hud = screen.GetNode<HudController>("HudController");
			hud.Show();
			screen.GetNode<StartPanelController>("StartPanel").Hide();
			session._Process(.05);
			session.SetProcess(false);
			await Until(() => world.IsVisibleInTree() && world.RouteGeometry is not null, "production road scene to load");
			world.SetProcess(false);
			hud.SetDummyTrainerControls(GameBootstrap.TrainerClient as FakeTrainerClient);
			await Until(() => hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree(), "mock media session to appear");
			AssertMediaLayout(screen, "1280x720 debug hidden");
			Check(Find<Label>(hud, "MediaTitle").Text == "A Long Ride Home", "current media session title is displayed");
			Check(Find<Label>(hud, "MediaArtist").Text.Contains("Wodra Test Artist") && Find<Label>(hud, "MediaArtist").Text.Contains("Playing"),
				"artist and playing state are displayed");
			hud.ShowAchievementNotification("First ride");
			Check(hud.GetNode<Control>("AchievementNotification").IsVisibleInTree(), "achievement notification remains available alongside the media panel");
			if (!IsHeadless) Capture("media-road-pacer-notification.png");

			var playback = Find<Button>(hud, "MediaPlaybackButton");
			if (IsHeadless)
			{
				playback.EmitSignal(Button.SignalName.Pressed);
				await Until(() => _provider.Commands.Contains(SystemMediaCommand.TogglePlayback) && Find<Label>(hud, "MediaArtist").Text.Contains("Paused"),
					"mock play/pause state change");
				Check(_provider.Commands.Count(command => command == SystemMediaCommand.TogglePlayback) == 1,
					"play/pause issues one asynchronous command to the selected session");
				Find<Button>(hud, "MediaNextButton").EmitSignal(Button.SignalName.Pressed);
				await Until(() => Find<Label>(hud, "MediaTitle").Text == "Changed Track", "mock next track update");
				Find<Button>(hud, "MediaPreviousButton").EmitSignal(Button.SignalName.Pressed);
				await Until(() => Find<Label>(hud, "MediaTitle").Text == "A Long Ride Home", "mock previous track update");
				Check(_provider.Commands.Contains(SystemMediaCommand.Next) && _provider.Commands.Contains(SystemMediaCommand.Previous),
					"supported next and previous controls follow the current session");
			}
			else
				Check(playback.IsVisibleInTree() && Find<Button>(hud, "MediaNextButton").IsVisibleInTree() && Find<Button>(hud, "MediaPreviousButton").IsVisibleInTree(),
					"rendered production media controls expose the supported commands");

			if (IsHeadless)
			{
				var longTitle = new string('L', 140);
				_provider.Set(new SystemMediaSessionSnapshot(true, longTitle, "Long Artist", SystemMediaPlaybackState.Paused, true, false, false, false));
				await Until(() => Find<Label>(hud, "MediaTitle").TooltipText == longTitle, "long title tooltip update");
				Check(Find<Label>(hud, "MediaTitle").ClipText && Find<Label>(hud, "MediaTitle").Text == longTitle &&
					!Find<Button>(hud, "MediaNextButton").IsVisibleInTree() && !Find<Button>(hud, "MediaPreviousButton").IsVisibleInTree(),
					"long titles use clipped label rendering with full tooltip text and unsupported skip controls are hidden");
				_provider.Set(new SystemMediaSessionSnapshot(true, null, "Only Artist", SystemMediaPlaybackState.Paused, true, false, false, false));
				await Until(() => Find<Label>(hud, "MediaTitle").Text == "Only Artist", "missing title fallback");
				Check(Find<Label>(hud, "MediaArtist").Text == "Paused" && Find<Label>(hud, "MediaTitle").TooltipText == "Only Artist",
					"artist-only metadata is not duplicated in the state line and stays available in the tooltip");
				_provider.Set(new SystemMediaSessionSnapshot(true, null, null, SystemMediaPlaybackState.Other, false, false, false, false));
				await Until(() => Find<Label>(hud, "MediaTitle").Text == "Media session", "missing metadata fallback");
				Check(Find<Label>(hud, "MediaArtist").Text == "Ready", "missing title and artist use a compact fallback without stale text");
				_provider.Set(SystemMediaSessionSnapshot.Unavailable);
				await Until(() => !hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree() && Find<Label>(hud, "MediaTitle").Text.Length == 0,
					"closed player clears the panel and metadata");
				_provider.Set(new SystemMediaSessionSnapshot(true, "Recovered", "Player", SystemMediaPlaybackState.Paused, true, false, false, false));
				await Until(() => hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree(), "session switching restores panel");
				_provider.FailReads = true;
				await Until(() => !hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree(), "failed media API hides stale metadata");
				_provider.FailReads = false;
				await Until(() => hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree(), "media API recovery restores panel");
				GameBootstrap.Settings.InRideMediaControlsEnabled = false;
				GameBootstrap.Settings.Save();
				await Frames(3);
				Check(!hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree() && Find<Label>(hud, "MediaTitle").Text.Length == 0,
					"turning the optional preference off immediately clears the in-ride panel");
				GameBootstrap.Settings.InRideMediaControlsEnabled = true;
				GameBootstrap.Settings.Save();
				await Until(() => hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree(), "re-enabled preference restores current media");
			}

			hud.SetDebugDisplayEnabled(true);
			hud.SetDebugMode(true);
			foreach (var (name, size) in new[] { ("minimum", new Vector2I(960, 540)), ("default", new Vector2I(1280, 720)) })
			{
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
				DisplayServer.WindowSetSize(size);
				await Frames(6);
				hud.ShowAchievementNotification("First ride");
				AssertMediaLayout(screen, $"{name} debug visible");
				if (!IsHeadless) Capture($"media-{name}-debug-visible.png");
			}
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
			await Frames(6);
			hud.ShowAchievementNotification("First ride");
			AssertMediaLayout(screen, "fullscreen debug visible");
			if (!IsHeadless) Capture("media-fullscreen-debug-visible.png");
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			hud.SetDebugDisplayEnabled(false);
			hud.SetDebugMode(false);
			await Frames(5);

			var actualSession = System.Environment.GetEnvironmentVariable("WODRA_MEDIA_HOST_ACCEPTANCE") == "1";
			if (actualSession)
				await VerifyActualWindowsSessionAsync(screen, session, hud, world);
			await session.ExitToRouteSelectionAsync();
			await Frames(2);
			Check(!hud.GetNode<Control>("MediaControlsPanel").IsVisibleInTree() && Find<Label>(hud, "MediaTitle").Text.Length == 0,
				"leaving the ride clears the media panel and cached text");
			RestoreSettings();
			GD.Print($"[MediaControlsRideScreenReview] PASS: {_assertions} assertions; mock production RideScreen; actual Windows session exercised={actualSession}; screenshots={_output}");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			RestoreSettings();
			GD.PushError($"[MediaControlsRideScreenReview] FAIL: {error}");
			GetTree().Quit(1);
		}
	}

	private async Task VerifyActualWindowsSessionAsync(Node screen, RideSessionNode session, HudController hud, RideWorldView world)
	{
		var panel = hud.GetNode<Control>("MediaControlsPanel");
		hud.MediaSessionProviderOverride = null;
		await Until(() => panel.IsVisibleInTree(), "real Windows media session");
		var title = Find<Label>(hud, "MediaTitle").TooltipText;
		var artist = Find<Label>(hud, "MediaArtist").TooltipText;
		Check(!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist), "real Windows session provides title or artist metadata");
		if (!IsHeadless) Capture("spotify-production-ride.png");
		var actualArtistText = Find<Label>(hud, "MediaArtist").Text;
		var actualPlaybackExpected = actualArtistText.Contains("Playing", StringComparison.Ordinal) || actualArtistText.Contains("Paused", StringComparison.Ordinal);
		Check(Find<Button>(hud, "MediaPlaybackButton").IsVisibleInTree() == actualPlaybackExpected,
			"production HUD reflects actual Windows playback capability without changing player state during visual capture");
	}

	private void AssertMediaLayout(Node screen, string context)
	{
		var hud = screen.GetNode<HudController>("HudController");
		var media = hud.GetNode<Control>("MediaControlsPanel");
		if (!media.IsVisibleInTree()) throw new InvalidOperationException($"Media panel is not visible for layout review at {context}.");
		var viewport = hud.GetViewportRect();
		var rect = media.GetGlobalRect();
		if (rect.Position.X < 0 || rect.Position.Y < 0 || rect.End.X > viewport.Size.X || rect.End.Y > viewport.Size.Y)
			throw new InvalidOperationException($"Media panel is outside the viewport at {context}: {rect}, viewport={viewport.Size}.");
		foreach (var name in new[] { "OfflinePacerPanel", "BottomBar", "RouteGradientInset", "DummyControlPanel", "TopHudPanel", "AchievementNotification", "DebugRideOverlay" })
		{
			var other = hud.GetNodeOrNull<Control>(name);
			if (other?.IsVisibleInTree() == true && rect.Intersects(other.GetGlobalRect()))
				throw new InvalidOperationException($"Media panel overlaps {name} at {context}: {rect} vs {other.GetGlobalRect()}.");
		}
		var toast = hud.GetNodeOrNull<Control>("AchievementNotification");
		if (toast?.IsVisibleInTree() == true)
		{
			foreach (var name in new[] { "OfflinePacerPanel", "BottomBar", "RouteGradientInset", "DummyControlPanel", "DebugRideOverlay" })
			{
				var other = hud.GetNodeOrNull<Control>(name);
				if (other?.IsVisibleInTree() == true && toast.GetGlobalRect().Intersects(other.GetGlobalRect()))
					throw new InvalidOperationException($"Achievement notification overlaps {name} at {context}.");
			}
		}
		foreach (var name in new[] { "MediaMetadata/MediaTitle", "MediaMetadata/MediaArtist", "MediaPreviousButton", "MediaPlaybackButton", "MediaNextButton" })
		{
			var child = media.GetNodeOrNull<Control>($"MediaControlsLayout/{name}");
			if (child is { Visible: true } && (child.GetGlobalRect().Position.X < rect.Position.X || child.GetGlobalRect().End.X > rect.End.X))
				throw new InvalidOperationException($"Media control {name} is clipped at {context}: panel={rect}, child={child.GetGlobalRect()}.");
		}
	}

	private async Task Until(Func<bool> condition, string label)
	{
		for (var frame = 0; frame < 150; frame++)
		{
			if (condition()) return;
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		throw new TimeoutException($"Timed out waiting for {label}.");
	}

	private async Task Frames(int count)
	{
		for (var frame = 0; frame < count; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private void RestoreSettings()
	{
		if (!_settingsCaptured) return;
		GameBootstrap.Settings.InRideMediaControlsEnabled = _originalMediaEnabled;
		GameBootstrap.Settings.PacerPresetId = _originalPacerPreset;
		GameBootstrap.Settings.DebugModeEnabled = _originalDebugEnabled;
		GameBootstrap.Settings.OnlineRidingEnabled = _originalOnlineEnabled;
		GameBootstrap.Settings.DummyTrainer.Mode = _originalDummyMode;
		GameBootstrap.Settings.DummyTrainer.TargetWatts = _originalDummyWatts;
		GameBootstrap.Settings.DummyTrainer.WattsPerKg = _originalDummyWkg;
		GameBootstrap.Settings.DummyTrainer.CadenceRpm = _originalDummyCadence;
		GameBootstrap.Settings.Recording.OutputFolder = _originalRecordingFolder;
		GameBootstrap.Settings.Save();
		_settingsCaptured = false;
	}

	private void Check(bool condition, string message)
	{
		_assertions++;
		if (!condition) throw new InvalidOperationException(message);
		GD.Print($"[MediaControlsRideScreenReview] PASS: {message}");
	}

	private static T Find<T>(Node node, string name) where T : Node =>
		node.FindChildren(name, recursive: true, owned: false).OfType<T>().FirstOrDefault()
		?? throw new InvalidOperationException($"Missing review control {name}.");

	private void Capture(string name)
	{
		if (IsHeadless) return;
		RenderingServer.ForceDraw(false);
		var image = GetViewport().GetTexture().GetImage();
		if (image.IsEmpty()) throw new InvalidOperationException($"Could not capture {name}.");
		image.SavePng(Path.Combine(_output, name));
	}
}
