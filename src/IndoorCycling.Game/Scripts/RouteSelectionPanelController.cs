using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Core.Models;

public partial class RouteSelectionPanelController : Control
{
	private RideSessionNode? _session;
	private Control? _hud, _device, _start;
	private PanelContainer? _panel;
	private VBoxContainer? _rows;
	private LineEdit? _search;
	private OptionButton? _sort, _type;
	private OptionButton? _difficulty;
	private CheckButton? _favoritesOnly;
	private Label? _count, _previewTitle, _metadata, _description, _routeEnds, _empty;
	private Button? _favoriteButton, _startButton;
	private RouteElevationPreview? _map, _elevation;
	private OptionButton? _pacerPicker;
	private OptionButton? _routeGhostPicker;
	private RouteEffortRecord? _bestRouteEffort, _previousRouteEffort;
	private string _effortHistoryRouteId = "";
	private int _effortHistoryGeneration;
	private OptionButton? _activityModePicker;
	private Label? _activityModeLabel;
	private Label? _pacerLabel;
	private string _selectedId = RouteCatalog.Routes.FirstOrDefault()?.Id ?? "";
	private readonly Dictionary<string, Button> _routeButtons = new(StringComparer.Ordinal);
	private bool _returnToRouteAfterOptions;
	public string SelectedRouteId => _selectedId;

	public override void _Ready()
	{
		_session = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hud = GetNodeOrNull<Control>("../HudController");
		_device = GetNodeOrNull<Control>("../DeviceConnectionPanel");
		_start = GetNodeOrNull<Control>("../StartPanel");
		_panel = GetNodeOrNull<PanelContainer>("PanelContainer");
		BuildBrowser();
		FitSelectionPanel();
		Hide();
	}
	public override void _Notification(int what)
	{
		if (what == NotificationResized) FitSelectionPanel();
	}
	private void FitSelectionPanel()
	{
		if (_panel is null || !IsInsideTree()) return;
		var viewport = GetViewportRect().Size;
		var width = Math.Min(1100, Math.Max(600, viewport.X - 32));
		var height = Math.Min(620, Math.Max(420, viewport.Y - 32));
		_panel.CustomMinimumSize = new Vector2(width, height);
		_panel.OffsetLeft = -width / 2; _panel.OffsetRight = width / 2;
		_panel.OffsetTop = -height / 2; _panel.OffsetBottom = height / 2;
	}

	private void BuildBrowser()
	{
		if (_panel is null) return;
		_panel.CustomMinimumSize = new Vector2(980, 600);
		var margin = new MarginContainer { Name = "RouteBrowserMargin" };
		margin.AddThemeConstantOverride("margin_left", 20); margin.AddThemeConstantOverride("margin_right", 20);
		margin.AddThemeConstantOverride("margin_top", 16); margin.AddThemeConstantOverride("margin_bottom", 16);
		_panel.AddChild(margin);
		var root = new VBoxContainer { Name = "RouteBrowserLayout" }; root.AddThemeConstantOverride("separation", 10); margin.AddChild(root);
		var header = new HBoxContainer { Name = "TopBar" }; root.AddChild(header);
		var title = new Label { Text = "Choose a route", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
		title.AddThemeFontSizeOverride("font_size", 24); header.AddChild(title);
		var options = new Button { Text = "Options" }; options.Pressed += OpenOptions; header.AddChild(options);
		var filters = new HBoxContainer { Name = "Filters" }; filters.AddThemeConstantOverride("separation", 8); root.AddChild(filters);
		_search = new LineEdit { Name = "RouteSearch", PlaceholderText = "Search routes", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_search.TextChanged += _ => RefreshRows(); filters.AddChild(_search);
		_type = new OptionButton { Name = "RouteTypeFilter", CustomMinimumSize = new Vector2(140, 0) };
		_type.AddItem("All route types"); _type.AddItem("Loops"); _type.AddItem("Out-and-back"); _type.ItemSelected += _ => RefreshRows(); filters.AddChild(_type);
		_difficulty = new OptionButton { Name = "RouteDifficultyFilter", CustomMinimumSize = new Vector2(130, 0) };
		_difficulty.AddItem("Any effort");
		foreach (var difficulty in RouteCatalog.Routes.Select(route => route.Difficulty).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
			_difficulty.AddItem(difficulty);
		_difficulty.ItemSelected += _ => RefreshRows(); filters.AddChild(_difficulty);
		_sort = new OptionButton { Name = "RouteSort", CustomMinimumSize = new Vector2(150, 0) };
		_sort.AddItem("Name A–Z"); _sort.AddItem("Shortest"); _sort.AddItem("Longest"); _sort.AddItem("Least ascent"); _sort.AddItem("Most ascent");
		_sort.ItemSelected += _ => RefreshRows(); filters.AddChild(_sort);
		_favoritesOnly = new CheckButton { Name = "FavoritesFilter", Text = "Favourites" }; _favoritesOnly.Toggled += _ => RefreshRows(); filters.AddChild(_favoritesOnly);
		var content = new HBoxContainer { Name = "Content", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; content.AddThemeConstantOverride("separation", 18); root.AddChild(content);
		var listColumn = new VBoxContainer { Name = "RouteList", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 0.95f };
		content.AddChild(listColumn);
		_count = new Label { Text = "Routes" }; listColumn.AddChild(_count);
		var scroll = new ScrollContainer { Name = "RouteScroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		listColumn.AddChild(scroll); _rows = new VBoxContainer { Name = "RouteRows", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _rows.AddThemeConstantOverride("separation", 5); scroll.AddChild(_rows);
		_empty = new Label { Name = "NoResults", Text = "No routes match these filters.", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		var previewColumn = new VBoxContainer { Name = "Preview", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.15f };
		content.AddChild(previewColumn);
		var detailHeader = new HBoxContainer { Name = "PreviewHeader" }; previewColumn.AddChild(detailHeader);
		_previewTitle = new Label { Text = "Route", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
		_previewTitle.AddThemeFontSizeOverride("font_size", 20); detailHeader.AddChild(_previewTitle);
		_favoriteButton = new Button { Name = "FavoriteRoute", Text = "☆ Favourite" }; _favoriteButton.Pressed += ToggleFavorite; detailHeader.AddChild(_favoriteButton);
		_metadata = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart }; previewColumn.AddChild(_metadata);
		_description = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsVertical = Control.SizeFlags.ShrinkBegin }; previewColumn.AddChild(_description);
		_map = new RouteElevationPreview { Name = "RouteOverview", ShowRoute = true, CustomMinimumSize = new Vector2(0, 150), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		previewColumn.AddChild(_map);
		_elevation = new RouteElevationPreview { Name = "ElevationProfile", CustomMinimumSize = new Vector2(0, 92), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		previewColumn.AddChild(_elevation);
		_routeEnds = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart }; previewColumn.AddChild(_routeEnds);
		var ghostChoiceRow = new HBoxContainer { Name = "RouteGhostChoiceRow" };
		ghostChoiceRow.AddChild(new Label { Text = "Compare with", VerticalAlignment = VerticalAlignment.Center });
		_routeGhostPicker = new OptionButton { Name = "RouteGhostChoice", CustomMinimumSize = new Vector2(160, 0), TooltipText = "Compare against an eligible route lap" };
		_routeGhostPicker.AddItem("No route ghost"); _routeGhostPicker.AddItem("Best lap"); _routeGhostPicker.AddItem("Previous lap");
		ghostChoiceRow.AddChild(_routeGhostPicker); previewColumn.AddChild(ghostChoiceRow);
		var spacer = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; previewColumn.AddChild(spacer);
		var footer = new HBoxContainer { Name = "Footer" }; footer.AddThemeConstantOverride("separation", 8); root.AddChild(footer);
		var back = new Button { Text = "Back" }; back.Pressed += OnBackPressed; footer.AddChild(back);
		_pacerLabel = new Label { Text = "Offline pacer", VerticalAlignment = VerticalAlignment.Center }; footer.AddChild(_pacerLabel);
		_activityModeLabel = new Label { Name = "TrackEffortLabel", Text = "Track mode", Visible = false, VerticalAlignment = VerticalAlignment.Center }; footer.AddChild(_activityModeLabel);
		_activityModePicker = new OptionButton { Name = "TrackActivityMode", Visible = false, CustomMinimumSize = new Vector2(190, 0) };
		_activityModePicker.AddItem("Free ride");
		_activityModePicker.AddItem("Lap timing");
		_activityModePicker.AddItem("Flying 200 m");
		_activityModePicker.AddItem("Standing 500 m");
		_activityModePicker.AddItem("Standing 1 km");
		_activityModePicker.ItemSelected += _ => UpdatePreview();
		footer.AddChild(_activityModePicker);
		_pacerPicker = new OptionButton { Name = "PacerPresetPicker", CustomMinimumSize = new Vector2(270, 0) };
		_pacerPicker.AddItem("No pacer"); foreach (var preset in PacerEffortPresets.All) _pacerPicker.AddItem($"{preset.Name} · {preset.TargetWattsPerKg:0.0} W/kg");
		_pacerPicker.Select(Math.Max(0, PacerEffortPresets.All.ToList().FindIndex(p => p.Id == PacerEffortPresets.NormalizeSelection(GameBootstrap.Settings.PacerPresetId)) + 1));
		_pacerPicker.ItemSelected += index => SavePacerSelection((int)index); footer.AddChild(_pacerPicker);
		_startButton = new Button { Name = "StartRide", Text = "Start Ride", CustomMinimumSize = new Vector2(150, 42), SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
		_startButton.Pressed += StartSelectedRoute; footer.AddChild(_startButton);
		RefreshRows();
	}

	private void SavePacerSelection(int index)
	{
		GameBootstrap.Settings.PacerPresetId = index == 0 ? PacerEffortPreset.DisabledId : PacerEffortPresets.All[index - 1].Id;
		GameBootstrap.Settings.Save();
	}

	private Guid? ActiveProfileId => GameBootstrap.Settings.ActiveProfileId;
	private HashSet<string> Favorites => RouteFavoritesPolicy.ForProfile(GameBootstrap.Settings.RouteFavoritesByProfile, ActiveProfileId);
	private bool IsFavorite(string id) => Favorites.Contains(id);
	private void ToggleFavorite()
	{
		if (ActiveProfileId is not { } profile || string.IsNullOrEmpty(_selectedId)) return;
		RouteFavoritesPolicy.Toggle(GameBootstrap.Settings.RouteFavoritesByProfile, profile, _selectedId);
		GameBootstrap.Settings.Save(); RefreshRows(); UpdatePreview();
	}

	private void RefreshRows()
	{
		if (_rows is null) return;
		foreach (var child in _rows.GetChildren().Where(child => child != _empty)) { _rows.RemoveChild(child); child.QueueFree(); }
		_routeButtons.Clear();
		var query = _search?.Text.Trim() ?? "";
		var routeType = _type?.Selected switch { 1 => "Loop", 2 => "Out-and-Back", _ => "" };
		var sort = (_sort?.Selected ?? 0) switch { 1 => RouteBrowseSort.Shortest, 2 => RouteBrowseSort.Longest, 3 => RouteBrowseSort.LeastAscent, 4 => RouteBrowseSort.MostAscent, _ => RouteBrowseSort.Name };
		var difficulty = _difficulty is { Selected: > 0 } ? _difficulty.GetItemText(_difficulty.Selected) : "";
		var list = RouteBrowsePolicy.Apply(RouteCatalog.Routes,
			new RouteBrowseFilter(query, routeType, _favoritesOnly?.ButtonPressed == true, sort, difficulty), Favorites).ToList();
		var installedIds = RouteCatalog.Routes.Select(route => route.Id).ToHashSet(StringComparer.Ordinal);
		var unavailable = _favoritesOnly?.ButtonPressed == true
			? Favorites.Where(id => !installedIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray()
			: [];
		if (_count is not null) _count.Text = $"{list.Count} route{(list.Count == 1 ? "" : "s")}" + (unavailable.Length > 0 ? $" · {unavailable.Length} unavailable favourite{(unavailable.Length == 1 ? "" : "s")}" : "");
		if (_empty is not null && list.Count == 0 && unavailable.Length == 0) _rows.AddChild(_empty);
		else if (_empty is not null && _empty.GetParent() == _rows) _rows.RemoveChild(_empty);
		foreach (var route in list)
		{
			var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 5); _rows.AddChild(row);
			var button = new Button { Name = "RouteChoice", Text = $"{route.Name}\n{route.DistanceMeters / 1000:0.0} km  ·  {route.TotalAscentMeters:0} m ascent  ·  {route.RouteType}", Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 58), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ToggleMode = true, ButtonPressed = route.Id == _selectedId };
			button.Pressed += () => { _selectedId = route.Id; UpdateSelection(); };
			row.AddChild(button); _routeButtons[route.Id] = button;
			var star = new Button { Text = IsFavorite(route.Id) ? "★" : "☆", TooltipText = IsFavorite(route.Id) ? "Remove favourite" : "Add favourite", CustomMinimumSize = new Vector2(42, 58) };
			star.Pressed += () => { _selectedId = route.Id; ToggleFavorite(); };
			row.AddChild(star);
		}
		foreach (var missingId in unavailable)
		{
			var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 5); _rows.AddChild(row);
			row.AddChild(new Label { Text = $"Unavailable route · {missingId}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center });
			var remove = new Button { Text = "☆", TooltipText = "Remove unavailable favourite", CustomMinimumSize = new Vector2(42, 58) };
			remove.Pressed += () => { if (ActiveProfileId is { } profile) { var values = Favorites; values.Remove(missingId); GameBootstrap.Settings.RouteFavoritesByProfile[profile] = values.ToList(); GameBootstrap.Settings.Save(); RefreshRows(); } };
			row.AddChild(remove);
		}
		if (list.Count > 0 && !list.Any(r => r.Id == _selectedId)) _selectedId = list[0].Id;
		UpdatePreview(); UpdateSelection();
	}

	private void UpdateSelection()
	{
		foreach (var pair in _routeButtons)
			pair.Value.ButtonPressed = pair.Key == _selectedId;
		UpdatePreview();
	}

	private void UpdatePreview()
	{
		var route = RouteCatalog.GetById(_selectedId);
		if (route is null) { if (_previewTitle is not null) _previewTitle.Text = "Route unavailable"; if (_startButton is not null) _startButton.Disabled = true; return; }
		if (_previewTitle is not null) _previewTitle.Text = route.Name;
		if (_metadata is not null) _metadata.Text = $"{route.DistanceMeters / 1000:0.00} km  ·  {route.TotalAscentMeters:0} m ascent  ·  {route.RouteType}";
		if (_metadata is not null && route.Environment == RouteEnvironment.Velodrome &&
			GameBootstrap.Settings.ActiveProfileId is { } profile)
		{
			var selectedMode = (RideActivityMode)Math.Clamp((int)(_activityModePicker?.Selected ?? 0), 0, 4);
			var key = VelodromePersonalBestPolicy.Key(profile, route, selectedMode);
			if (GameBootstrap.Settings.TrackPersonalBests.TryGetValue(key, out var pb))
				_metadata.Text += $"  ·  PB {pb:0.00} s";
		}
		if (_description is not null) _description.Text = route.Description;
		var isTrack = route.Environment == RouteEnvironment.Velodrome;
		var hasEffortModes = route.SupportedModes.Count > 0;
		if (_activityModeLabel is not null) _activityModeLabel.Visible = hasEffortModes;
		if (_activityModePicker is not null) _activityModePicker.Visible = hasEffortModes;
		if (_pacerPicker is not null) _pacerPicker.Visible = route.Capabilities.SupportsPacer;
		if (_pacerLabel is not null) _pacerLabel.Visible = route.Capabilities.SupportsPacer;
		if (_routeGhostPicker is not null) _routeGhostPicker.Visible = route.Environment == RouteEnvironment.Road && route.Capabilities.SupportsGhost;
		if (_startButton is not null) _startButton.Text = isTrack ? "Start Track Ride" : "Start Ride";
		if (_favoriteButton is not null) _favoriteButton.Text = IsFavorite(route.Id) ? "★ Favourited" : "☆ Favourite";
		if (_map is not null) { _map.Route = route; _map.QueueRedraw(); }
		if (_elevation is not null) { _elevation.Route = route; _elevation.QueueRedraw(); }
		var first = route.GetSampleAt(0);
		if (_routeEnds is not null) _routeEnds.Text = isTrack
			? "Reference line: 250 m per lap · start/finish line on the main straight\nChoose an effort below. Flying 200 m has a 50 m run-up; 500 m and 1 km begin from a standing start."
			: $"Start: {route.Segments.FirstOrDefault()?.Label ?? first.SegmentLabel}\nFinish: {route.Segments.LastOrDefault()?.Label ?? first.SegmentLabel} · {route.Segments.Count} route segments per lap";
		if (_startButton is not null) _startButton.Disabled = false;
		if (route.Environment == RouteEnvironment.Road && _effortHistoryRouteId != route.Id)
			_ = RefreshRouteEffortChoicesAsync(route);
	}

	private async Task RefreshRouteEffortChoicesAsync(RouteDefinition route)
	{
		var generation = ++_effortHistoryGeneration;
		_effortHistoryRouteId = route.Id;
		_bestRouteEffort = _previousRouteEffort = null;
		if (_routeGhostPicker is not null) { _routeGhostPicker.Select(0); _routeGhostPicker.SetItemDisabled(1, true); _routeGhostPicker.SetItemDisabled(2, true); }
		var profileId = ActiveProfileId;
		var repository = GameBootstrap.RideHistory;
		if (profileId is null || repository is null || !route.Capabilities.SupportsGhost) return;
		RouteEffortHistory history;
		try
		{
			history = await repository.GetRouteEffortHistoryAsync(profileId.Value, route.Id, route.GeometryFingerprint,
				RouteEffortCatalog.ForwardDirection, RouteEffortCatalog.FullRouteEffortId);
		}
		catch (Exception ex)
		{
			GameLogger.Log("route_effort_history_load_failed", new { routeId = route.Id, message = ex.Message });
			return;
		}
		if (generation != _effortHistoryGeneration || route.Id != _selectedId) return;
		_bestRouteEffort = history.PersonalBest;
		_previousRouteEffort = history.Previous;
		if (_metadata is not null)
			_metadata.Text += $"  ·  Route PB {FormatEffort(_bestRouteEffort)}  ·  Previous {FormatEffort(_previousRouteEffort)}";
		if (_routeGhostPicker is not null)
		{
			_routeGhostPicker.SetItemDisabled(1, !HasGhostFile(_bestRouteEffort));
			_routeGhostPicker.SetItemDisabled(2, !HasGhostFile(_previousRouteEffort));
		}
	}

	private static bool HasGhostFile(RouteEffortRecord? effort) => effort?.FitFilePath is { Length: > 0 } path && System.IO.File.Exists(path);

	private static string FormatEffort(RouteEffortRecord? effort) => effort is null
		? "—" : TimeSpan.FromSeconds(effort.ElapsedSeconds).ToString(effort.ElapsedSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss\.ff");

	private async void StartSelectedRoute()
	{
		if (_session is null || RouteCatalog.GetById(_selectedId) is not { } route) return;
		if (route.Environment == RouteEnvironment.Road && _routeGhostPicker?.Selected is 1 or 2)
			await PrepareSelectedRouteGhostAsync(route, _routeGhostPicker.Selected == 1 ? _bestRouteEffort : _previousRouteEffort);
		else _session.ClearPendingGhost();
		Hide(); _hud?.Show();
		RideActivityMode? mode = null;
		if (route.SupportedModes.Count > 0)
		{
			var index = Math.Clamp((int)(_activityModePicker?.Selected ?? 0), 0, route.SupportedModes.Count - 1);
			mode = route.SupportedModes[index];
		}
		if (!await _session.StartTrainerSessionAsync(route, mode)) { _hud?.Hide(); (_device as DeviceConnectionPanelController)?.ShowTrainerConnectionScreen(); }
	}

	private async Task PrepareSelectedRouteGhostAsync(RouteDefinition route, RouteEffortRecord? effort)
	{
		if (effort is null || GameBootstrap.RideHistory is not { } history)
		{ _session?.ClearPendingGhost(); return; }
		try
		{
			var activity = await history.GetActivityAsync(effort.ActivityId);
			if (activity?.FitFilePath is not { Length: > 0 } fitPath || !System.IO.File.Exists(fitPath))
			{ _session?.ClearPendingGhost(); _hud?.Call("SetStatus", "The selected comparison FIT is unavailable; the ride will start without a ghost."); return; }
			var trace = await Task.Run(() => new GhostRideTraceLoader().Load(fitPath));
			var cropped = trace.SliceByDistance(effort.StartRouteDistanceMeters, effort.EndRouteDistanceMeters);
			var splits = (await history.GetRouteEffortsForActivityAsync(effort.ActivityId, eligibleOnly: true))
				.Where(item => item.Kind == RouteEffortKind.Segment && item.AttemptIndex == effort.AttemptIndex &&
					item.GeometryFingerprint == route.GeometryFingerprint && item.Direction == effort.Direction)
				.ToDictionary(item => item.EffortId, item => item.ElapsedSeconds, StringComparer.Ordinal);
			_session?.SetPendingGhost(new GhostRiderStateSource(cropped, route.Id,
				effort == _bestRouteEffort ? "Best lap" : "Previous lap", true, repeatRouteLap: true, splits), effort.ActivityId);
		}
		catch (Exception ex)
		{
			GameLogger.Log("route_ghost_load_failed", new { activityId = effort.ActivityId, routeId = route.Id, message = ex.Message });
			_session?.ClearPendingGhost();
			_hud?.Call("SetStatus", "The selected comparison could not be loaded; the ride will start without a ghost.");
		}
	}

	private void OpenOptions()
	{
		_returnToRouteAfterOptions = true;
		_start?.Call("ShowOptions", true);
		Hide();
	}
	public void ReturnFromOptions()
	{
		if (!_returnToRouteAfterOptions) return;
		_returnToRouteAfterOptions = false;
		ShowRouteSelection();
	}
	public void ShowRouteSelection()
	{
		_hud?.Hide(); _device?.Hide(); _start?.Hide(); Show(); MoveToFront();
		if (_pacerPicker is not null)
			_pacerPicker.Select(Math.Max(0, PacerEffortPresets.All.ToList().FindIndex(p => p.Id == PacerEffortPresets.NormalizeSelection(GameBootstrap.Settings.PacerPresetId)) + 1));
		RefreshRows();
	}
	private void OnBackPressed()
	{
		Hide();
		if (GameBootstrap.TrainerClient is not null) _device?.Show(); else _start?.Show();
	}
}
