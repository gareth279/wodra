using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public partial class RouteCatalogueRideScreenReviewTest : Node
{
	private readonly string _output = Path.Combine(Path.GetTempPath(), "WodraRouteCatalogueReview");
	private int _assertions;
	private static bool IsHeadless => RenderingServer.GetCurrentRenderingDriverName().Contains("headless", StringComparison.OrdinalIgnoreCase)
		|| RenderingServer.GetCurrentRenderingDriverName().Contains("dummy", StringComparison.OrdinalIgnoreCase)
		|| DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

	public override async void _Ready()
	{
		try
		{
			Directory.CreateDirectory(_output);
			var ride = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
			AddChild(ride);
			await Frames(8);
			foreach (var profileWindow in ride.GetNode<StartPanelController>("StartPanel").GetChildren().OfType<Window>())
				profileWindow.Hide();
			var selector = ride.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
			selector.ShowRouteSelection();
			await Frames(6);
			GD.Print("[RouteCatalogueReview] selector tree=" + Dump(selector, 0));
			var panel = selector.GetNode<PanelContainer>("PanelContainer");
			var rows = selector.GetNode<VBoxContainer>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/RouteList/RouteScroll/RouteRows");
			var search = selector.GetNode<LineEdit>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Filters/RouteSearch");
			if (RouteCatalog.Routes.Count == 0) throw new InvalidOperationException("The production route catalogue is empty.");
			Check(rows.GetChildCount() > 0, "production route catalogue shows installed routes");
			var selectedId = selector.SelectedRouteId;
			var preview = selector.GetNode<RouteElevationPreview>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview/RouteOverview");
			Check(preview.Route?.Id == selectedId, "route preview uses the selected route definition");
			Check(selector.GetNode<Button>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Footer/StartRide").Disabled == false,
				"Start Ride is enabled for the selected installed route");
			var roadSelectionId = selectedId;
			var trackRoute = RouteCatalog.GetById("wodra-velodrome-250")
				?? throw new InvalidOperationException("Built-in velodrome route is missing from the production catalogue.");
			var trackChoice = rows.GetChildren().OfType<HBoxContainer>().Select(row => row.GetNode<Button>("RouteChoice"))
				.FirstOrDefault(button => button.Text.Contains(trackRoute.Name, StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException("Velodrome route is not visible in the route browser.");
			trackChoice.EmitSignal(Button.SignalName.Pressed);
			await Frames(2);
			var activityPicker = selector.GetNode<OptionButton>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Footer/TrackActivityMode");
			var pacerPicker = selector.GetNode<OptionButton>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Footer/PacerPresetPicker");
			Check(selector.SelectedRouteId == trackRoute.Id && preview.Route?.Id == trackRoute.Id,
				"velodrome route and its selected preview use the same definition");
			Check(activityPicker.IsVisibleInTree() && activityPicker.ItemCount == trackRoute.SupportedModes.Count,
				"track mode selector exposes exactly the route's supported activities");
			Check(!pacerPicker.IsVisibleInTree(), "unsupported pacer selector is hidden for the solo track");
			if (!IsHeadless) Capture("velodrome-route-selection.png");
			var roadChoice = rows.GetChildren().OfType<HBoxContainer>().Select(row => row.GetNode<Button>("RouteChoice"))
				.First(button => button.Name == "RouteChoice" && button.Text.Contains(RouteCatalog.GetById(roadSelectionId)!.Name,
					StringComparison.OrdinalIgnoreCase));
			roadChoice.EmitSignal(Button.SignalName.Pressed);
		await Frames(6);
			selectedId = roadSelectionId;
			var routeGhostChoice = selector.GetNode<OptionButton>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview/RouteGhostChoiceRow/RouteGhostChoice");
			Check(routeGhostChoice.IsVisibleInTree() && routeGhostChoice.ItemCount == 3 && routeGhostChoice.Selected == 0,
				"road preview offers no-ghost, best-lap and previous-lap comparison choices");
			if (!IsHeadless)
			{
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
				DisplayServer.WindowSetSize(new Vector2I(960, 540));
				await Frames(8);
				CheckPanelWithinWindow(panel, "road preview minimum window");
				Capture("route-catalogue-road-ghost-choice.png");
			}
			var routeGhostBeforeOptions = routeGhostChoice.Selected;

			var saved = GameBootstrap.Settings.RouteFavoritesByProfile.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
			var activeProfile = GameBootstrap.Settings.ActiveProfileId;
			var favoritesButton = selector.GetNode<Button>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/Preview/PreviewHeader/FavoriteRoute");
			favoritesButton.EmitSignal(Button.SignalName.Pressed);
			await Frames(2);
			if (activeProfile is { } profile)
				Check(GameBootstrap.Settings.RouteFavoritesByProfile.TryGetValue(profile, out var stored) && stored.Contains(selectedId),
					"favourite action stores the stable selected route ID in the active profile");
			GameBootstrap.Settings.RouteFavoritesByProfile = saved;
			GameBootstrap.Settings.Save();

			var searchTarget = RouteCatalog.Routes.FirstOrDefault(route => route.Id != selector.SelectedRouteId)
				?? RouteCatalog.Routes[0];
			search.Text = searchTarget.Name;
			search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
			await Frames(2);
			var filteredRows = rows.GetChildren().OfType<HBoxContainer>().ToArray();
			Check(filteredRows.Length == 1, "search narrows to one matching installed route");
			var filteredChoice = filteredRows.Single().GetNode<Button>("RouteChoice");
			selectedId = selector.SelectedRouteId;
			Check(filteredChoice.Text.Contains(searchTarget.Name, StringComparison.OrdinalIgnoreCase)
				&& preview.Route?.Id == selectedId,
			"search updates the selected route preview to the matching route");

			search.Text = "no such route in Wodra";
			search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
			await Frames(2);
			Check(selector.GetNode<Label>("PanelContainer/RouteBrowserMargin/RouteBrowserLayout/Content/RouteList/RouteScroll/RouteRows/NoResults").IsVisibleInTree(),
				"empty search state is shown clearly");
			search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
			await Frames(2);

			if (!IsHeadless)
			{
				Capture("route-catalogue-default.png");
				foreach (var size in new[] { new Vector2I(960, 540), new Vector2I(1152, 648) })
				{
					DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
					DisplayServer.WindowSetSize(size);
					await Frames(8);
					CheckPanelWithinWindow(panel, $"{size.X}x{size.Y}");
					Capture($"route-catalogue-{size.X}x{size.Y}.png");
				}
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
				await Frames(8);
				CheckPanelWithinWindow(panel, "fullscreen");
				Capture("route-catalogue-fullscreen.png");
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
			}

			selector.Call("OpenOptions");
			await Frames(8);
			var options = ride.GetNode<Window>("OptionsWindow");
			var gamingToggle = options.GetNodeOrNull<CheckButton>("OptionsMargin/OptionsLayout/OptionsScroll/OptionsContent/GameFeaturesToggle");
			var onlineToggle = options.GetNodeOrNull<CheckButton>("OptionsMargin/OptionsLayout/OptionsScroll/OptionsContent/OnlineRidingToggle");
			var mediaToggle = options.GetNodeOrNull<CheckButton>("OptionsMargin/OptionsLayout/OptionsScroll/OptionsContent/MediaControlsToggle");
			Check(gamingToggle is not null,
				"Options window contains the moved gaming toggle");
			Check(onlineToggle is not null,
				"Options window contains the moved Online Riding toggle");
			Check(mediaToggle is not null && new AppSettings().InRideMediaControlsEnabled == false,
				"pre-ride Options exposes optional Windows media controls, disabled by default");
			Check(gamingToggle?.ButtonPressed == GameFeaturesPreference.Enabled
				&& onlineToggle?.ButtonPressed == GameBootstrap.Settings.OnlineRidingEnabled
				&& mediaToggle?.ButtonPressed == GameBootstrap.Settings.InRideMediaControlsEnabled,
				"Options initializes from saved settings without changing their values");
			var aboutBuildInformation = options.GetNode<Label>("OptionsMargin/OptionsLayout/OptionsScroll/OptionsContent/AboutBuildInformation");
			var expectedBuildInformation = BuildIdentity.Current.ToPlainText();
			Check(aboutBuildInformation.Text == expectedBuildInformation &&
				aboutBuildInformation.Text.Contains(BuildIdentity.Current.ApplicationVersion, StringComparison.Ordinal) &&
				aboutBuildInformation.Text.Contains(BuildIdentity.Current.SourceCommit, StringComparison.Ordinal),
				"About shows the shared application, channel, configuration and source identity");
			var copyBuildInformation = options.GetNode<Button>("OptionsMargin/OptionsLayout/OptionsScroll/OptionsContent/AboutActions/CopyBuildInformationButton");
			copyBuildInformation.EmitSignal(Button.SignalName.Pressed);
			await Frames(2);
			Check(options.GetNode<Label>("OptionsMargin/OptionsLayout/OptionsScroll/OptionsContent/AboutActions/BuildInformationCopyStatus").Text == "Copied.",
				"Copy build information confirms the action");
			if (!IsHeadless)
				Check(DisplayServer.ClipboardGet().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n') == expectedBuildInformation,
					"clipboard text exactly matches the displayed build identity");
			var previousMediaPreference = GameBootstrap.Settings.InRideMediaControlsEnabled;
			mediaToggle!.ButtonPressed = !previousMediaPreference;
			mediaToggle.EmitSignal(CheckButton.SignalName.Toggled, mediaToggle.ButtonPressed);
			await Frames(3);
			Check(GameBootstrap.Settings.InRideMediaControlsEnabled == !previousMediaPreference
				&& AppSettings.LoadOrCreate().InRideMediaControlsEnabled == !previousMediaPreference,
				"media preference is saved and survives settings reload");
			mediaToggle.ButtonPressed = previousMediaPreference;
			mediaToggle.EmitSignal(CheckButton.SignalName.Toggled, mediaToggle.ButtonPressed);
			await Frames(3);
			if (!IsHeadless)
			{
				var optionsScroll = options.GetNode<ScrollContainer>("OptionsMargin/OptionsLayout/OptionsScroll");
				foreach (var (size, label) in new[] { (new Vector2I(960, 540), "minimum"), (new Vector2I(1152, 648), "default") })
				{
					DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
					DisplayServer.WindowSetSize(size);
					await Frames(6);
					optionsScroll.ScrollVertical = (int)optionsScroll.GetVScrollBar().MaxValue;
					await Frames(3);
					Check(options.Size.Y <= size.Y - 24,
						$"Options About panel fits the {label} window height");
					Capture(options, $"options-about-{label}.png");
					optionsScroll.ScrollVertical = 0;
					await Frames(3);
					Capture(options, $"options-{label}.png");
				}
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
				await Frames(8);
				optionsScroll.ScrollVertical = (int)optionsScroll.GetVScrollBar().MaxValue;
				await Frames(3);
				Capture(options, "options-about-fullscreen.png");
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
			}
			var pacerBefore = GameBootstrap.Settings.PacerPresetId;
			options.EmitSignal(Window.SignalName.CloseRequested);
			await Frames(6);
			Check(selector.IsVisibleInTree() && selector.SelectedRouteId == selectedId,
				"Options returns to route selection without changing the selected route");
			Check(routeGhostChoice.Selected == routeGhostBeforeOptions,
				"Options navigation preserves route ghost comparison selection");
			Check(GameBootstrap.Settings.PacerPresetId == pacerBefore,
				"Options navigation preserves the selected pacer");
			if (_assertions == 0) throw new InvalidOperationException("Review test did not run assertions.");
			GD.Print($"[RouteCatalogueReview] PASS: {_assertions} assertions; screenshots={_output}");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[RouteCatalogueReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private void Check(bool condition, string message)
	{
		_assertions++;
		if (!condition) throw new InvalidOperationException(message);
		GD.Print($"[RouteCatalogueReview] PASS: {message}");
	}
	private void CheckPanelWithinWindow(Control panel, string context)
	{
		var logical = GetViewport().GetVisibleRect().Size;
		var size = DisplayServer.WindowGetSize();
		var scale = new Vector2(size.X / logical.X, size.Y / logical.Y);
		var rect = panel.GetGlobalRect();
		var end = (rect.Position + rect.Size) * scale;
		var start = rect.Position * scale;
		Check(start.X >= -2 && start.Y >= -2 && end.X <= size.X + 2 && end.Y <= size.Y + 2,
			$"route panel remains within the rendered window at {context}: start={start}, end={end}, window={size}");
	}
	private static string Dump(Node node, int depth) => string.Join(" ", node.GetChildren().Select(child => new string(' ', depth) + child.Name + (child.GetChildCount() > 0 ? "{" + Dump(child, depth + 1) + "}" : "")));
	private async Task Frames(int count)
	{
		for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}
	private void Capture(string name) => Capture(GetViewport(), name);
	private void Capture(Viewport viewport, string name)
	{
		if (IsHeadless) return;
		RenderingServer.ForceDraw(false);
		var image = viewport.GetTexture().GetImage();
		if (image.IsEmpty()) throw new InvalidOperationException($"Could not capture {name}.");
		image.SavePng(Path.Combine(_output, name));
	}
}
