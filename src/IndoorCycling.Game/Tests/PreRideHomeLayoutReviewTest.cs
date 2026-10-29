using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>Exercises navigation and responsive layout against the production RideScreen pre-ride UI.</summary>
public partial class PreRideHomeLayoutReviewTest : Node
{
	private int _assertions;
	private static bool IsHeadless => DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase)
		|| RenderingServer.GetCurrentRenderingDriverName().Contains("headless", StringComparison.OrdinalIgnoreCase)
		|| RenderingServer.GetCurrentRenderingDriverName().Contains("dummy", StringComparison.OrdinalIgnoreCase);

	public override async void _Ready()
	{
		try
		{
			var ride = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
			AddChild(ride);
			await Frames(120);
			var start = ride.GetNode<StartPanelController>("StartPanel");
			// A fresh test profile opens the first-run editor asynchronously; hide
			// it for this returning-rider home capture and keep profile data untouched.
			foreach (var window in start.GetChildren().OfType<Window>()) window.Hide();
			var panel = start.GetNode<PanelContainer>("PanelContainer");
			var homeScroll = start.GetNode<ScrollContainer>("PanelContainer/MarginContainer/StartupScroll");
			var home = homeScroll.GetNode<VBoxContainer>("VBoxContainer");
			Check(start.Visible, "pre-ride home is the visible production launch screen");
			Check(home.GetNodeOrNull<Control>("BrandHeader/WodraCatMark")?.Visible == true, "Wodra cat branding appears in the home header");
			Check(home.GetNodeOrNull<Control>("PreRideDeviceCards/TrainerSummary") is not null
				&& home.GetNodeOrNull<Control>("PreRideDeviceCards/HeartRateSummary") is not null
				&& home.GetNodeOrNull<Control>("PreRideDeviceCards/SteeringSummary") is not null,
				"trainer, heart-rate and steering summaries all use production device state");
			Check(home.GetNodeOrNull<Button>("RideChoices/ChooseRouteButton") is not null
				&& home.GetNodeOrNull<Button>("RideChoices/ChooseWorkoutButton") is not null,
				"route and workout choices are available directly from the home screen");
			Check(home.GetNode<Button>("ConnectTrainerButton").Visible && home.GetNode<Button>("ConnectTrainerButton").Text == "Connect devices",
				"device management remains reachable from the home screen");
			Check(!home.GetNode<Button>("Test with Fake Trainer").Visible, "dummy trainer entry point is absent from the normal home screen");
			Check(home.GetNode<Label>("TitleLabel").Text == "Welcome to Wodra", "home heading does not claim a disconnected trainer is ready");
			Check(home.GetNode<Button>("PreRideSecondaryActions/QuitButton").Visible, "Quit remains a secondary normal home action");
			Check(homeScroll.HorizontalScrollMode == ScrollContainer.ScrollMode.Disabled,
				"home uses vertical scrolling only and cannot hide content in a horizontal overflow");

			var connection = ride.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
			Check(connection.GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ContinueButton").Disabled,
				"existing trainer readiness gate remains closed before a trainer is ready");

			var routes = ride.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
			var workouts = ride.GetNode<WorkoutSelectionPanelController>("WorkoutSelectionPanel");
			var selectedRoute = routes.SelectedRouteId;
			var workoutTitle = workouts.GetNode<Label>("WorkoutSelectionPanel/MarginContainer/VBoxContainer/Content/PreviewScroll/Preview/WorkoutTitle").Text;
			home.GetNode<Button>("RideChoices/ChooseRouteButton").EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(routes.Visible && !start.Visible, "Ride a route opens the existing route catalogue");
			routes.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Back")
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(start.Visible && routes.SelectedRouteId == selectedRoute, "returning home preserves the selected route");
			home = homeScroll.GetNode<VBoxContainer>("VBoxContainer");
			home.GetNode<Button>("RideChoices/ChooseWorkoutButton").EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(workouts.Visible && !start.Visible, "Do a workout opens the existing workout selector");
			workouts.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Back")
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(start.Visible && workouts.GetNode<Label>("WorkoutSelectionPanel/MarginContainer/VBoxContainer/Content/PreviewScroll/Preview/WorkoutTitle").Text == workoutTitle,
				"workout Back returns home without changing the selected workout");

			var sizes = new[] { new Vector2I(960, 540), new Vector2I(1280, 720), new Vector2I(1920, 1080), new Vector2I(2560, 1440) };
			foreach (var size in sizes)
			{
				var expected = StartPanelController.CalculateStartupPanelSize(size);
				Check(expected.X <= size.X - 40 && expected.Y <= size.Y - 40,
					$"responsive panel fits {size.X}x{size.Y} without crossing the viewport edge");
				if (IsHeadless) continue;
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
				DisplayServer.WindowSetSize(size);
				await Frames(8);
				var viewport = start.GetViewportRect();
				var panelRect = panel.GetGlobalRect();
				Check(panelRect.Position.X >= -1 && panelRect.Position.Y >= -1
					&& panelRect.End.X <= viewport.End.X + 1 && panelRect.End.Y <= viewport.End.Y + 1,
					$"production home panel remains within {size.X}x{size.Y}");
				var cards = home.GetNode<Control>("PreRideDeviceCards").GetGlobalRect();
				var choices = home.GetNode<Control>("RideChoices").GetGlobalRect();
				Check(cards.End.Y <= choices.Position.Y + 1, $"device cards and ride actions do not overlap at {size.X}x{size.Y}");
				var homeViewport = homeScroll.GetGlobalRect();
				foreach (var name in new[] { "ChooseRouteButton", "ChooseWorkoutButton", "ConnectTrainerButton", "OptionsButton", "HistoryButton" })
				{
					var rect = home.FindChild(name, true, false) is Control control ? control.GetGlobalRect() : new Rect2();
					Check(rect.Position.X >= homeViewport.Position.X && rect.End.X <= homeViewport.End.X
						&& rect.Position.Y >= homeViewport.Position.Y && rect.End.Y <= homeViewport.End.Y,
						$"primary and secondary action '{name}' is visible within the {size.X}x{size.Y} home viewport");
				}
				Capture(ride.GetViewport(), $"pre-ride-home-{size.X}x{size.Y}.png");
			}

			home.GetNode<Button>("ConnectTrainerButton").EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(connection.Visible && !start.Visible, "Connect devices opens the existing device-selection screen");
			connection.GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/BackButton")
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(start.Visible && !connection.Visible, "device Back returns home without starting discovery");

			DisplayServer.WindowSetSize(new Vector2I(1152, 648));
			start.FindChildren("OptionsButton", "Button", true, false).OfType<Button>().Single()
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(5);
			var options = ride.GetNode<Window>("OptionsWindow");
			var optionsScroll = options.FindChild("OptionsScroll", true, false) as ScrollContainer
				?? throw new InvalidOperationException("Options content does not provide its internal scroll container.");
			var advancedToggle = options.FindChildren("AdvancedTestingToggle", "Button", true, false).OfType<Button>().Single();
			var advancedContent = options.FindChild("AdvancedTestingContent", true, false) as VBoxContainer
				?? throw new InvalidOperationException("Options did not create the Advanced Testing & Diagnostics section.");
			Check(!advancedToggle.ButtonPressed && !advancedContent.Visible, "Advanced / Testing & Diagnostics is collapsed by default");
			Check(start.FindChild("DummyTrainerActiveNotice", true, false) is Label { Visible: false }, "dummy mode is not advertised as a physical device while inactive");
			advancedToggle.ButtonPressed = true;
			await Frames(3);
			var dummySettings = advancedContent.FindChild("DummyTrainerSettings", true, false) as GridContainer;
			Check(advancedContent.Visible
				&& dummySettings is { Columns: 2 }
				&& advancedContent.FindChildren("DebugModeCheckButton", "CheckButton", true, false).Count == 1,
				"dummy settings reflow into responsive label/control pairs and Debug Overlay remains available in Advanced");
			if (dummySettings is not null)
			{
				var optionsRect = advancedContent.GetGlobalRect();
				foreach (var control in dummySettings.GetChildren().OfType<Control>())
				{
					var rect = control.GetGlobalRect();
					Check(rect.Position.X >= optionsRect.Position.X && rect.End.X <= optionsRect.End.X,
						$"advanced dummy setting '{control.Name}' remains within the Options window width");
				}
			}
			foreach (var size in new[] { new Vector2I(960, 540), new Vector2I(1280, 720), new Vector2I(1920, 1080) })
			{
				if (IsHeadless)
				{
					var expected = StartPanelController.CalculateOptionsWindowSize(size);
					Check(expected.X <= size.X - 48 && expected.Y <= size.Y - 48,
						$"Options window fits the {size.X}x{size.Y} viewport with its margins");
				}
				else
				{
					options.Hide();
					DisplayServer.WindowSetSize(size);
					await Frames(4);
					start.ShowOptions(returnToRoutes: false);
					await Frames(4);
					var expected = StartPanelController.CalculateOptionsWindowSize(size);
					var mainSize = DisplayServer.WindowGetSize();
					var logicalSize = start.GetViewportRect().Size;
					var scale = new Vector2(mainSize.X / logicalSize.X, mainSize.Y / logicalSize.Y);
					var visibleWidth = options.Size.X * scale.X;
					var visibleHeight = options.Size.Y * scale.Y;
					Check(visibleWidth <= expected.X + 1 && visibleHeight <= expected.Y + 1,
						$"Options popup visible size {visibleWidth:0}x{visibleHeight:0} fits its responsive {expected.X}x{expected.Y} target at {size.X}x{size.Y}");
					Check(options.Position.X >= -1 && options.Position.Y >= -1
						&& options.Position.X * scale.X + visibleWidth <= mainSize.X + 1
						&& options.Position.Y * scale.Y + visibleHeight <= mainSize.Y + 1,
						$"Options popup remains within the visible main window at {size.X}x{size.Y}");
				}
				Check(advancedToggle.ButtonPressed && advancedContent.Visible,
					$"Advanced remains expanded after reopening Options at {size.X}x{size.Y}");
				Check(optionsScroll.HorizontalScrollMode == ScrollContainer.ScrollMode.Disabled,
					$"Options uses vertical scrolling only at {size.X}x{size.Y}");
				optionsScroll.ScrollVertical = (int)optionsScroll.GetVScrollBar().MaxValue;
				await Frames(4);
				Check(optionsScroll.GetVScrollBar().MaxValue > 0
					&& options.FindChild("AboutBuildInformation", true, false) is Label
					&& options.FindChildren("DoneButton", "Button", true, false).Count == 1,
					$"Advanced settings and About remain scroll-accessible with Done available at {size.X}x{size.Y}");
				Check(advancedContent.FindChildren("Test with Fake Trainer", "Button", true, false).Count == 1
					&& advancedContent.FindChildren("QuitButton", "Button", true, false).Count == 0,
					"Dummy Trainer remains in Advanced and Quit remains a home action");
				if (!IsHeadless)
					Capture(options, $"pre-ride-options-advanced-scroll-bottom-{size.X}x{size.Y}.png");
			}
			options.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Done")
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(3);
			Check(start.Visible && !options.Visible, "Done closes Options and returns to home");
			start.ShowOptions(returnToRoutes: false);
			await Frames(3);
			options.EmitSignal(Window.SignalName.CloseRequested);
			await Frames(3);
			Check(start.Visible && !options.Visible, "Options close button returns to home");

			// Exercise the actual relocated diagnostics action; it must close the
			// exclusive Options window, not open a panel behind it.
			start.ShowOptions(returnToRoutes: false);
			await Frames(3);
			advancedContent.FindChildren("LocalDiagnosticsButton", "Button", true, false).OfType<Button>().Single()
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			var diagnostics = ride.GetNode<DiagnosticPanelController>("DiagnosticPanel");
			Check(diagnostics.Visible && !options.Visible && !start.Visible,
				"Advanced Diagnostics opens in front of the closed Options window");
			diagnostics.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Back")
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(3);
			Check(start.Visible && !diagnostics.Visible, "Diagnostics Back restores the home screen");

			// FIT replay must dismiss the exclusive Options window before showing
			// the original file picker. Dismiss without selecting a real file.
			start.ShowOptions(returnToRoutes: false);
			await Frames(3);
			advancedContent.FindChildren("LoadFitButton", "Button", true, false).OfType<Button>().Single()
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(3);
			var fileDialog = start.GetNode<FileDialog>("FileDialog");
			Check(!options.Visible && (IsHeadless || fileDialog.Visible),
				"Advanced FIT replay closes Options before opening the existing file picker");
			fileDialog.Hide();
			await Frames(3);
			Check(start.Visible, "dismissing FIT file selection leaves the pre-ride home available");

			// Use the actual Advanced button, not a direct fake-client setup call.
			// This protects the window close and return-to-home paths.
			start.ShowOptions(returnToRoutes: false);
			await Frames(3);
			advancedContent.FindChildren("Test with Fake Trainer", "Button", true, false).OfType<Button>().Single()
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(4);
			Check(routes.Visible && !options.Visible && !start.Visible,
				"Advanced Dummy Trainer opens route selection without leaving Options over it");
			routes.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Back")
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(3);
			Check(start.Visible && !routes.Visible, "dummy trainer route Back returns to the new home screen");
			Check(start.FindChild("DummyTrainerActiveNotice", true, false) is Label { Visible: true },
				"synthetic trainer mode is surfaced as test mode");
			var trainerCard = home.GetNode<Control>("PreRideDeviceCards/TrainerSummary");
			var trainerCardName = trainerCard.FindChildren("DeviceName", "Label", true, false).OfType<Label>().Single();
			var trainerCardState = trainerCard.FindChildren("DeviceState", "Label", true, false).OfType<Label>().Single();
			Check(trainerCardName.Text == "Dummy trainer" && trainerCardState.Text == "Test mode active",
				"dummy trainer is not presented as a real connected device");

			// Options opened from the in-ride HUD must not offer launch actions that
			// replace or end the current ride. Verify the existing client remains intact.
			var activeTrainer = GameBootstrap.TrainerClient;
			start.ShowOptions(returnToRoutes: false, returnToRide: true);
			await Frames(3);
			Check(!start.Visible && options.Visible
				&& advancedContent.FindChildren("Test with Fake Trainer", "Button", true, false).OfType<Button>().Single().Disabled
				&& advancedContent.FindChildren("LoadFitButton", "Button", true, false).OfType<Button>().Single().Disabled
				&& advancedContent.FindChildren("LocalDiagnosticsButton", "Button", true, false).OfType<Button>().Single().Disabled,
				"Dummy Trainer, FIT replay and Diagnostics are disabled when Options is opened during a ride");
			Check(ReferenceEquals(GameBootstrap.TrainerClient, activeTrainer),
				"opening in-ride Options does not replace the active trainer client");
			options.FindChildren("DoneButton", "Button", true, false).OfType<Button>().Single()
				.EmitSignal(BaseButton.SignalName.Pressed);
			await Frames(3);
			Check(!options.Visible && !start.Visible,
				"closing in-ride Options leaves the ride screen active without revealing pre-ride home");
			GD.Print($"[PreRideHomeLayoutReview] PASS: {_assertions} production assertions; graphical layout captures={(!IsHeadless)}.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[PreRideHomeLayoutReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
		_assertions++;
		GD.Print($"[PreRideHomeLayoutReview] PASS: {message}");
	}

	private async Task Frames(int count)
	{
		for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private void Capture(Viewport viewport, string fileName)
	{
		var output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT");
		if (string.IsNullOrWhiteSpace(output)) output = Path.Combine(Path.GetTempPath(), "WodraPreRideReview");
		Directory.CreateDirectory(output);
		var path = Path.Combine(output, fileName);
		viewport.GetTexture().GetImage().SavePng(path);
		GD.Print($"[PreRideHomeLayoutReview] CAPTURE: {path}");
	}
}
