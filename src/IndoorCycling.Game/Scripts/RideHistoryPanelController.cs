using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Core.Progress;

public partial class RideHistoryPanelController : Control
{
    private Control? _startPanel;
    private RouteSelectionPanelController? _routeSelection;
    private HudController? _hud;
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _detail = null!;
    private VBoxContainer _detailColumn = null!;
    private VBoxContainer _stravaUploads = null!;
    private Label _summaryEssentials = null!;
    private ScrollContainer _detailScroll = null!;
    private ItemList _list = null!;
    private LineEdit _search = null!;
    private OptionButton _routeFilter = null!;
    private OptionButton _dateFilter = null!;
    private OptionButton _sortFilter = null!;
    private Label _pageStatus = null!;
    private Label _dashboard = null!;
    private RideHistoryTrendChart _trendChart = null!;
    private OptionButton _trendRange = null!;
    private Button _previousPage = null!;
    private Button _nextPage = null!;
    private IRideHistoryRepository? _repositoryOverride;
    private RiderProfileRecord? _activeProfile;
    private int _pageIndex;
    private int _historyRefreshGeneration;
    private int _dashboardGeneration;
    private const int PageSize = 25;
    private readonly List<string> _routeIds = [];
    private Button _viewButton = null!;
    private Button _fitFolderButton = null!;
    private Button _ghostButton = null!;
    private Button _doneButton = null!;
    private Button _diagnosticExportButton = null!;
    private Button _achievementsButton = null!;
    private Button _achievementsBackButton = null!;
    private Control _filters = null!;
    private Control _historyBody = null!;
    private Control _pageControls = null!;
    private Control _dashboardChart = null!;
    private VBoxContainer _achievementsView = null!;
    private VBoxContainer _achievementsList = null!;
    private Label _achievementsTotals = null!;
    private Label _goalStatus = null!;
    private OptionButton _goalMetric = null!;
    private LineEdit _goalTarget = null!;
    private Button _setGoalButton = null!;
    private Button _cancelGoalButton = null!;
    private bool _achievementsMode;
    private string? _completedDiagnosticSessionId;
    private List<RideActivity> _activities = [];
    private RideActivity? _selected;
    private bool _summaryMode;
    private bool _returnToRouteSelection;
    private int _stravaJournalChanged;
    private readonly List<(Guid ProfileId, Guid ActivityId, int SegmentIndex, string Path, Label Status, Button Action)> _stravaRows = [];

    public override void _Ready()
    {
        StravaIntegrationClient.Shared.UploadJournalChanged += OnStravaJournalChanged;
        _startPanel = GetNodeOrNull<Control>("../StartPanel");
        _routeSelection = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
        _hud = GetNodeOrNull<HudController>("../HudController");
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel = new PanelContainer { Name = "HistoryPanel", AnchorLeft = .5f, AnchorTop = .5f, AnchorRight = .5f, AnchorBottom = .5f };
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18); margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 18); margin.AddThemeConstantOverride("margin_bottom", 18);
        var column = new VBoxContainer();
        _title = new Label { Text = "Ride History" }; column.AddChild(_title);
        _summaryEssentials = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddChild(_summaryEssentials);
        _filters = new VBoxContainer(); var filterTop = new HBoxContainer(); var filterBottom = new HBoxContainer();
        _search = new LineEdit { Name = "HistorySearch", PlaceholderText = "Search route", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 0) };
        _search.TextChanged += text => { _pageIndex = 0; _ = RefreshHistoryAsync(); }; filterTop.AddChild(_search);
        _routeFilter = new OptionButton { Name = "HistoryRouteFilter", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _routeFilter.AddItem("All routes", 0); _routeFilter.ItemSelected += index => { _pageIndex = 0; _ = RefreshHistoryAsync(); }; filterTop.AddChild(_routeFilter);
        _dateFilter = new OptionButton { Name = "HistoryDateFilter", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _dateFilter.AddItem("All dates", 0); _dateFilter.AddItem("Last 7 days", 1); _dateFilter.AddItem("Last 30 days", 2); _dateFilter.AddItem("This month", 3); _dateFilter.AddItem("This year", 4); _dateFilter.ItemSelected += index => { _pageIndex = 0; _ = RefreshHistoryAsync(); }; filterBottom.AddChild(_dateFilter);
        _sortFilter = new OptionButton { Name = "HistorySortFilter", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; foreach (var text in new[] { "Newest", "Oldest", "Longest distance", "Longest duration", "Most climbing" }) _sortFilter.AddItem(text); _sortFilter.ItemSelected += index => { _pageIndex = 0; _ = RefreshHistoryAsync(); }; filterBottom.AddChild(_sortFilter);
        _filters.AddChild(filterTop); _filters.AddChild(filterBottom);
        column.AddChild(_filters);
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _historyBody = body;
        _list = new ItemList { Name = "HistoryActivityList", CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.ItemSelected += OnItemSelected; body.AddChild(_list);
        _detailScroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _detail = new Label { Name = "HistoryDetail", Text = "Select a ride to see details.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        _detailColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _detailColumn.AddChild(_detail);
        _stravaUploads = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
        _detailColumn.AddChild(_stravaUploads);
        _detailScroll.AddChild(_detailColumn); body.AddChild(_detailScroll); column.AddChild(body);
        var pageControls = new HBoxContainer();
        _previousPage = new Button { Name = "HistoryPreviousPage", Text = "Previous" }; _previousPage.Pressed += () => { if (_pageIndex > 0) { _pageIndex--; _ = RefreshHistoryAsync(); } }; pageControls.AddChild(_previousPage);
        _pageStatus = new Label { Name = "HistoryPageStatus", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; pageControls.AddChild(_pageStatus);
        _nextPage = new Button { Name = "HistoryNextPage", Text = "Next" }; _nextPage.Pressed += () => { _pageIndex++; _ = RefreshHistoryAsync(); }; pageControls.AddChild(_nextPage); column.AddChild(pageControls); _pageControls = pageControls;
        _dashboard = new Label { Name = "HistoryDashboard", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; column.AddChild(_dashboard);
        var chartRow = new HBoxContainer(); _trendRange = new OptionButton { Name = "HistoryTrendRange" }; _trendRange.AddItem("6 weeks"); _trendRange.AddItem("6 months"); _trendRange.ItemSelected += index => { if (_activeProfile is not null) _ = RefreshDashboardAsync(_repositoryOverride ?? GameBootstrap.RideHistory!, _activeProfile); }; chartRow.AddChild(_trendRange);
        _trendChart = new RideHistoryTrendChart { Name = "HistoryTrendChart", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 78) }; chartRow.AddChild(_trendChart); column.AddChild(chartRow);
        _dashboardChart = chartRow;
        _achievementsView = new VBoxContainer { Name = "AchievementsGoalsView", Visible = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _achievementsTotals = new Label { Name = "AchievementTotals", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _goalStatus = new Label { Name = "PersonalGoalStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var goalRow = new HBoxContainer();
        _goalMetric = new OptionButton { Name = "PersonalGoalMetric", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _goalMetric.AddItem("Distance goal · km"); _goalMetric.AddItem("Climbing goal · m"); goalRow.AddChild(_goalMetric);
        _goalTarget = new LineEdit { Name = "PersonalGoalTarget", PlaceholderText = "Target", CustomMinimumSize = new Vector2(110, 0), Text = "100" }; goalRow.AddChild(_goalTarget);
        _setGoalButton = new Button { Name = "SetPersonalGoal", Text = "Set / reset goal" }; _setGoalButton.Pressed += SetPersonalGoal; goalRow.AddChild(_setGoalButton);
        _cancelGoalButton = new Button { Name = "CancelPersonalGoal", Text = "Cancel goal" }; _cancelGoalButton.Pressed += ClearPersonalGoal; goalRow.AddChild(_cancelGoalButton);
        _achievementsView.AddChild(_achievementsTotals); _achievementsView.AddChild(_goalStatus); _achievementsView.AddChild(goalRow);
        var achievementScroll = new ScrollContainer { Name = "AchievementsScroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _achievementsList = new VBoxContainer { Name = "AchievementsList", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        achievementScroll.AddChild(_achievementsList); _achievementsView.AddChild(achievementScroll); column.AddChild(_achievementsView);
        var buttons = new HBoxContainer();
        _achievementsButton = new Button { Name = "AchievementsGoalsButton", Text = "Achievements & Goals" }; _achievementsButton.Pressed += ToggleAchievements; buttons.AddChild(_achievementsButton);
        _achievementsBackButton = new Button { Name = "AchievementsBackButton", Text = "Back to history", Visible = false }; _achievementsBackButton.Pressed += () => ShowHistory(); buttons.AddChild(_achievementsBackButton);
        _viewButton = new Button { Text = "View selected ride" }; _viewButton.Pressed += ShowSelectedSummary; buttons.AddChild(_viewButton);
        _fitFolderButton = new Button { Name = "OpenFitFolder", Text = "Open FIT folder", Visible = false }; _fitFolderButton.Pressed += OpenSelectedFitFolder; buttons.AddChild(_fitFolderButton);
        _ghostButton = new Button { Text = "Ride against ghost", Visible = false }; _ghostButton.Pressed += StartAgainstGhost; buttons.AddChild(_ghostButton);
        _diagnosticExportButton = new Button { Text = "Export session diagnostics", Visible = false };
        _diagnosticExportButton.Pressed += () => GetNodeOrNull<DiagnosticPanelController>("../DiagnosticPanel")?.OpenFor(this, _completedDiagnosticSessionId);
        buttons.AddChild(_diagnosticExportButton);
        _doneButton = new Button { Text = "Done / Return to start" }; _doneButton.Pressed += ClosePanel; buttons.AddChild(_doneButton);
        column.AddChild(buttons); margin.AddChild(column); _panel.AddChild(margin); AddChild(_panel);
        Resized += FitPanelToViewport;
        FitPanelToViewport();
        Hide();
    }

    public override void _ExitTree() => StravaIntegrationClient.Shared.UploadJournalChanged -= OnStravaJournalChanged;

    public override void _Process(double delta)
    {
        if (_stravaUploads is null || !_stravaUploads.Visible || Interlocked.Exchange(ref _stravaJournalChanged, 0) == 0) return;
        foreach (var item in _stravaRows)
        {
            var entry = StravaIntegrationClient.Shared.GetUpload(item.ProfileId, item.ActivityId, item.SegmentIndex, item.Path);
            item.Status.Text = entry?.Status switch
            {
                StravaUploadStatus.Queued => $"Segment {item.SegmentIndex + 1}: queued for automatic upload.",
                StravaUploadStatus.Submitting => $"Segment {item.SegmentIndex + 1}: uploading to Strava.",
                StravaUploadStatus.Processing => $"Segment {item.SegmentIndex + 1}: Strava is processing this saved upload.",
                StravaUploadStatus.Complete => $"Segment {item.SegmentIndex + 1}: uploaded.",
                StravaUploadStatus.Failed => $"Segment {item.SegmentIndex + 1}: {entry.SafeMessage ?? "upload failed"}. The saved FIT file is available to retry.",
                StravaUploadStatus.Cancelled => $"Segment {item.SegmentIndex + 1}: automatic upload was disabled. The saved FIT file remains available for manual upload.",
                _ => $"Segment {item.SegmentIndex + 1}: ready to upload."
            };
            item.Action.Text = entry?.Status switch
            {
                StravaUploadStatus.Queued => "Queued",
                StravaUploadStatus.Submitting or StravaUploadStatus.Unknown => "Submission pending",
                StravaUploadStatus.Processing => "Check Status",
                StravaUploadStatus.Complete => "Uploaded",
                StravaUploadStatus.Cancelled or StravaUploadStatus.Failed => "Retry upload",
                _ => "Upload to Strava"
            };
            item.Action.Disabled = entry?.Status is StravaUploadStatus.Queued or StravaUploadStatus.Submitting or StravaUploadStatus.Unknown or StravaUploadStatus.Complete;
        }
    }

    private void OnStravaJournalChanged() => Interlocked.Exchange(ref _stravaJournalChanged, 1);

    private void FitPanelToViewport()
    {
        if (_panel is null || !IsInsideTree()) return;
        var available = GetViewportRect().Size;
        var size = RideSummaryLayoutPolicy.PanelSize((int)available.X, (int)available.Y);
        var width = size.Width;
        var height = size.Height;
        _panel.CustomMinimumSize = new Vector2(width, height);
        _panel.OffsetLeft = -width / 2; _panel.OffsetRight = width / 2;
        _panel.OffsetTop = -height / 2; _panel.OffsetBottom = height / 2;
        _list.CustomMinimumSize = new Vector2(Math.Min(390, Math.Max(180, (width - 64) * .48f)), 0);
    }

    public async void ShowHistory()
    {
        SetAchievementsLayout(false);
        _completedDiagnosticSessionId = null; _diagnosticExportButton.Hide();
        _summaryMode = false; _returnToRouteSelection = false; _selected = null; _fitFolderButton.Hide(); _list.Visible = true; _summaryEssentials.Visible = false; _detailScroll.Visible = true; _list.Clear(); _title.Text = "Ride History";
        _detail.Text = "Loading ride history…"; _viewButton.Visible = false; _ghostButton.Visible = false; _stravaUploads.Visible = false;
        _startPanel?.Hide(); Show(); MoveToFront();
        try
        {
            var profile = _activeProfile ?? await GameBootstrap.GetPersistentRiderAsync();
            _activeProfile = profile;
            _title.Text = $"Ride History — {profile.Name}";
            await RefreshHistoryAsync();
        }
        catch (Exception ex) { _detail.Text = $"Ride history could not be loaded.\n{ex.Message}"; GD.PushWarning($"Ride history load failed: {ex.Message}"); }
    }

    public void ConfigureForIsolatedReview(IRideHistoryRepository repository, RiderProfileRecord profile)
    {
        _repositoryOverride = repository; _activeProfile = profile;
    }

    private void ToggleAchievements()
    {
        if (_achievementsMode) { SetAchievementsLayout(false); return; }
        _ = LoadAchievementsAsync();
    }

    private async Task LoadAchievementsAsync()
    {
        try
        {
            var profile = _activeProfile ?? await GameBootstrap.GetPersistentRiderAsync();
            _activeProfile = profile;
            var repository = _repositoryOverride ?? GameBootstrap.RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.");
            BuildAchievementsView(profile, await repository.GetAchievementProgressAsync(profile.Id));
            SetAchievementsLayout(true);
        }
        catch (Exception ex)
        {
            GameLogger.Log("achievements_progress_load_failed", new { message = ex.Message });
            _goalStatus.Text = "Achievements could not be loaded. Try again later.";
            SetAchievementsLayout(true);
        }
    }

    private void SetAchievementsLayout(bool visible)
    {
        _achievementsMode = visible;
        _title.Text = visible ? "Achievements & Goals" : "Ride History";
        _filters.Visible = !visible; _historyBody.Visible = !visible; _pageControls.Visible = !visible;
        _dashboard.Visible = !visible; _dashboardChart.Visible = !visible; _achievementsView.Visible = visible;
        _achievementsBackButton.Visible = visible; _achievementsButton.Visible = !visible;
        _viewButton.Visible = !visible && _selected is not null;
        _fitFolderButton.Visible = !visible && _selected is not null && !string.IsNullOrWhiteSpace(_selected.FitFilePath);
        _ghostButton.Visible = !visible && _selected is not null && CanOfferGhost(_selected);
        _stravaUploads.Visible = false;
    }

    private void BuildAchievementsView(RiderProfileRecord profile, AchievementProgressSnapshot snapshot)
    {
        var totals = snapshot.Totals;
        _achievementsTotals.Text = $"{profile.Name} · {totals.DistanceMeters / 1000d:0.#} km · " +
            $"{TimeSpan.FromSeconds(totals.RidingSeconds).TotalHours:0.#} h riding · {totals.ClimbingMeters:0} m climbing";
        var awards = snapshot.Awards.ToDictionary(item => item.AchievementId, StringComparer.Ordinal);
        if (snapshot.Goal is { } goal)
        {
            var (progress, target, unit) = goal.Metric == PersonalGoalMetric.DistanceMeters
                ? (goal.ProgressValue / 1000d, goal.TargetValue / 1000d, "km")
                : (goal.ProgressValue, goal.TargetValue, "m climbing");
            _goalStatus.Text = $"Current goal · {progress:0.#} / {target:0.#} {unit} · started {goal.CreatedAtUtc.ToLocalTime():d}";
        }
        else _goalStatus.Text = "No active goal · setting a new goal starts progress from that date.";
        _cancelGoalButton.Disabled = snapshot.Goal is null;

        foreach (var child in _achievementsList.GetChildren()) { _achievementsList.RemoveChild(child); child.QueueFree(); }
        var routes = RouteCatalog.Routes.Where(route => route.SupportsVerifiedCompletionAchievement)
            .Select(route => (route.Id, route.Name)).ToList();
        foreach (var award in snapshot.Awards.Where(item => item.AchievementId.StartsWith("route.first-complete.", StringComparison.Ordinal)))
        {
            var routeId = award.AchievementId["route.first-complete.".Length..];
            if (routes.All(route => route.Id != routeId)) routes.Add((routeId, routeId));
        }
        foreach (var definition in AchievementCatalog.DisplayDefinitions(routes))
        {
            awards.TryGetValue(definition.Id, out var award);
            var progressText = GetAchievementProgressText(definition, totals, awards);
            var earnedText = award is null ? "Not yet earned" : $"Earned {award.EarnedAtUtc.ToLocalTime():d}";
            var supportingText = definition.Metric == AchievementMetric.RouteCompletion
                ? progressText : $"{definition.Description} · {progressText}";
            var label = new Label
            {
                Name = "Achievement_" + definition.Id.Replace('.', '_').Replace('-', '_'),
                Text = $"{(award is null ? "○" : "✓")}  {definition.Title}\n{supportingText}{(award is null ? "" : $" · {earnedText}")}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            if (award is not null) label.AddThemeColorOverride("font_color", new Color(.96f, .84f, .58f));
            _achievementsList.AddChild(label);
        }
    }

    private static string GetAchievementProgressText(AchievementDefinition definition, AchievementTotals totals,
        IReadOnlyDictionary<string, AchievementAwardRecord> awards)
    {
        if (definition.Metric == AchievementMetric.Collection)
        {
            var count = AchievementCatalog.DunedinRoadRouteIds.Count(id => awards.ContainsKey(AchievementCatalog.RouteCompletionId(id)));
            return $"{count} / {AchievementCatalog.DunedinRoadRouteIds.Count} routes";
        }
        if (definition.Threshold is not { } threshold) return "Complete one full route";
        var (value, target, unit) = definition.Metric switch
        {
            AchievementMetric.DistanceMeters => (totals.DistanceMeters / 1000d, threshold / 1000d, "km"),
            AchievementMetric.RidingSeconds => (totals.RidingSeconds / 3600d, threshold / 3600d, "h"),
            AchievementMetric.ClimbingMeters => (totals.ClimbingMeters, threshold, "m"),
            _ => (0d, threshold, "")
        };
        return $"{Math.Min(value, target):0.#} / {target:0.#} {unit}";
    }

    private async void SetPersonalGoal()
    {
        try
        {
            if (_activeProfile is null) _activeProfile = await GameBootstrap.GetPersistentRiderAsync();
            if (!double.TryParse(_goalTarget.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var target) ||
                !double.IsFinite(target) || target <= 0)
            { _goalStatus.Text = "Enter a positive goal target."; return; }
            var metric = _goalMetric.Selected == 0 ? PersonalGoalMetric.DistanceMeters : PersonalGoalMetric.ClimbingMeters;
            if (metric == PersonalGoalMetric.DistanceMeters) target *= 1000;
            await (_repositoryOverride ?? GameBootstrap.RideHistory!).SetPersonalGoalAsync(_activeProfile.Id, metric, target, DateTimeOffset.UtcNow);
            _goalStatus.Text = "Goal reset · progress starts with rides begun from now onward.";
            await LoadAchievementsAsync();
        }
        catch (Exception ex) { GameLogger.Log("personal_goal_save_failed", new { message = ex.Message }); _goalStatus.Text = "Goal could not be saved. Try again."; }
    }

    private async void ClearPersonalGoal()
    {
        try
        {
            if (_activeProfile is null) return;
            await (_repositoryOverride ?? GameBootstrap.RideHistory!).ClearPersonalGoalAsync(_activeProfile.Id);
            _goalStatus.Text = "Goal cancelled.";
            await LoadAchievementsAsync();
        }
        catch (Exception ex) { GameLogger.Log("personal_goal_cancel_failed", new { message = ex.Message }); _goalStatus.Text = "Goal could not be cancelled. Try again."; }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_list is null || _activeProfile is null || !IsInsideTree()) return;
        var generation = Interlocked.Increment(ref _historyRefreshGeneration);
        var repository = _repositoryOverride ?? GameBootstrap.RideHistory;
        if (repository is null) return;
        var keepId = _selected?.Id;
        try
        {
            var (from, to) = DateFilterBounds(_dateFilter.Selected);
            var routeIndex = _routeFilter.Selected;
            var route = routeIndex <= 0 || routeIndex > _routeIds.Count ? null : _routeIds[routeIndex - 1];
            var sort = (RideHistorySort)Math.Clamp(_sortFilter.Selected, 0, 4);
            var result = await repository.GetHistoryPageAsync(_activeProfile.Id, _search.Text, route, from, to, sort, _pageIndex * PageSize, PageSize);
            if (!IsInsideTree() || generation != Volatile.Read(ref _historyRefreshGeneration)) return;
            if (result.Activities.Count == 0 && _pageIndex > 0) { _pageIndex = Math.Max(0, (result.TotalCount - 1) / PageSize); await RefreshHistoryAsync(); return; }
            var routes = await repository.GetHistoryRoutesAsync(_activeProfile.Id);
            if (!IsInsideTree() || generation != Volatile.Read(ref _historyRefreshGeneration)) return;
            _activities = result.Activities.ToList(); _list.Clear();
            PopulateRouteFilter(routes, route);
            foreach (var activity in _activities)
            {
                var local = activity.StartedAtUtc.ToLocalTime();
                var activityLabel = activity.Kind == RideActivityKind.Workout ? $"Workout · {activity.WorkoutName ?? activity.RouteName}" : activity.RouteName;
                _list.AddItem($"{local:yyyy-MM-dd HH:mm} · {activityLabel} · {activity.DistanceMeters / 1000:F1} km · {FormatDuration(activity.DurationSeconds)} active · {activity.AveragePowerWatts?.ToString("F0") ?? "—"} W");
            }
            _pageStatus.Text = result.TotalCount == 0 ? "No rides match these filters." : $"{_pageIndex * PageSize + 1}–{Math.Min((_pageIndex + 1) * PageSize, result.TotalCount)} of {result.TotalCount}";
            _previousPage.Disabled = _pageIndex == 0; _nextPage.Disabled = (_pageIndex + 1) * PageSize >= result.TotalCount;
            if (_activities.Count == 0) { _selected = null; _viewButton.Hide(); _ghostButton.Hide(); _detail.Text = "No completed rides match these filters."; }
            else { var target = _activities.FindIndex(a => a.Id == keepId); target = target >= 0 ? target : 0; _list.Select(target); OnItemSelected(target); }
            await RefreshDashboardAsync(repository, _activeProfile);
        }
        catch (Exception ex) { _detail.Text = $"Ride history could not be loaded.\n{ex.Message}"; GD.PushWarning($"Ride history load failed: {ex.Message}"); }
    }

    private void PopulateRouteFilter(IReadOnlyList<(string RouteId, string RouteName)> routes, string? selectedRoute)
    {
        _routeFilter.Clear(); _routeFilter.AddItem("All routes", 0);
        _routeIds.Clear();
        foreach (var route in routes) { _routeIds.Add(route.RouteId); _routeFilter.AddItem(route.RouteName); }
        var selected = selectedRoute is null ? -1 : _routeIds.IndexOf(selectedRoute);
        _routeFilter.Select(selected >= 0 ? selected + 1 : 0);
    }

    private (DateTimeOffset? From, DateTimeOffset? To) DateFilterBounds(int selected)
    {
        var now = DateTime.Now;
        var start = selected switch { 1 => now.Date.AddDays(-6), 2 => now.Date.AddDays(-29), 3 => new DateTime(now.Year, now.Month, 1), 4 => new DateTime(now.Year, 1, 1), _ => (DateTime?)null };
        return start is null ? (null, null) : (new DateTimeOffset(start.Value, TimeZoneInfo.Local.GetUtcOffset(start.Value)).ToUniversalTime(), new DateTimeOffset(now.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(now.Date.AddDays(1))).ToUniversalTime());
    }

    private async Task RefreshDashboardAsync(IRideHistoryRepository repository, RiderProfileRecord profile)
    {
        var generation = Interlocked.Increment(ref _dashboardGeneration);
        if (profile.IsTestProfile) { _dashboard.Text = "Test profile · excluded from real profile totals"; return; }
        var now = DateTime.Now; var monthStart = new DateTime(now.Year, now.Month, 1); var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        static DateTimeOffset Utc(DateTime d) => new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).ToUniversalTime();
        var routeIndex = _routeFilter.Selected;
        var route = routeIndex <= 0 || routeIndex > _routeIds.Count ? null : _routeIds[routeIndex - 1];
        var (filterFrom, filterTo) = DateFilterBounds(_dateFilter.Selected);
        var weekRange = LimitRange(Utc(weekStart.AddDays(-42)), Utc(weekStart.AddDays(7)), filterFrom, filterTo);
        var monthRange = LimitRange(Utc(monthStart.AddMonths(-5)), Utc(monthStart.AddMonths(1)), filterFrom, filterTo);
        var weekly = await repository.GetHistoryTrendAsync(profile.Id, weekRange.From, weekRange.To, false, _search.Text, route);
        var monthly = await repository.GetHistoryTrendAsync(profile.Id, monthRange.From, monthRange.To, true, _search.Text, route);
        if (!IsInsideTree() || generation != Volatile.Read(ref _dashboardGeneration)) return;
        var w = weekly.LastOrDefault(b => b.StartUtc.ToLocalTime().Date == weekStart);
        var m = monthly.LastOrDefault(b => b.StartUtc.ToLocalTime().Year == now.Year && b.StartUtc.ToLocalTime().Month == now.Month);
        _dashboard.Text = $"This week (Mon–Sun): {w?.RideCount ?? 0} rides · {((w?.DistanceMeters ?? 0) / 1000):F1} km · {FormatDuration(w?.DurationSeconds ?? 0)} active · {(w?.ElevationGainMeters ?? 0):F0} m climb\nThis month: {m?.RideCount ?? 0} rides · {((m?.DistanceMeters ?? 0) / 1000):F1} km · {FormatDuration(m?.DurationSeconds ?? 0)} active · {(m?.ElevationGainMeters ?? 0):F0} m climb";
        _trendChart.SetBuckets(_trendRange.Selected == 0 ? weekly : monthly, _trendRange.Selected == 0 ? "6 weeks · km / hours / m" : "6 months · km / hours / m");
    }

    private static (DateTimeOffset From, DateTimeOffset To) LimitRange(DateTimeOffset from, DateTimeOffset to, DateTimeOffset? filterFrom, DateTimeOffset? filterTo) =>
        (filterFrom is { } lower && lower > from ? lower : from, filterTo is { } upper && upper < to ? upper : to);


    public async void ShowCompletedSummary(RideActivityDraft draft, Guid? activityId, string? fitRecordingError = null, string? challengeSummary = null)
    {
        SetAchievementsLayout(false);
        _achievementsButton.Hide(); _achievementsBackButton.Hide();
        _completedDiagnosticSessionId = GameLogger.SessionId;
        _diagnosticExportButton.Visible = true;
        _summaryMode = true; _selected = null; _fitFolderButton.Hide(); _list.Visible = false; _summaryEssentials.Visible = true; _detailScroll.Visible = true; _list.Clear(); _viewButton.Visible = false; _ghostButton.Visible = false;
        _returnToRouteSelection = GameBootstrap.TrainerClient is not null;
        _startPanel?.Hide(); Show(); MoveToFront();
        _title.Text = "Ride Summary";
        var fit = fitRecordingError is not null
            ? $"FIT RECORDING ISSUE: {fitRecordingError}\nFIT: {draft.FitFilePath ?? "not saved"}"
            : string.IsNullOrWhiteSpace(draft.FitFilePath) ? "FIT: not saved" : $"FIT: {draft.FitFilePath}";
        _summaryEssentials.Text = FormatSummaryEssentials(draft) + (string.IsNullOrWhiteSpace(challengeSummary) ? "" : $"\n{challengeSummary}");
        _detail.Text = $"{fit}\n\nScreenshots: loading…";
        IReadOnlyList<RideActivityMedia> media = Array.Empty<RideActivityMedia>();
        if (activityId is { } id && (_repositoryOverride ?? GameBootstrap.RideHistory) is { } repository)
        {
            try { media = await repository.GetActivityMediaAsync(id); }
            catch (Exception ex) { GD.PushWarning($"Ride media list could not be loaded: {ex.Message}"); }
        }
        if (media.Count == 0 && activityId is { } currentId && _hud is not null)
            media = _hud.CapturedScreenshots.Where(s => s.ActivityId == currentId).Select(s => new RideActivityMedia(
                Guid.NewGuid(), currentId, s.ImagePath, s.SidecarPath, s.CapturedAt.ToUniversalTime(), s.Mode.ToString(), s.MarkerNumber)).ToArray();
        _detail.Text = FormatSummaryDetails(draft, fit, media);
        BuildStravaUploadControls(draft.Id, draft.RiderProfileId, draft.FitFilePath);
        _detailScroll.ScrollVertical = 0;
        _doneButton.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void OnItemSelected(long index)
    {
        if (index < 0 || index >= _activities.Count) return;
        _selected = _activities[(int)index]; _viewButton.Visible = true;
        _ghostButton.Visible = CanOfferGhost(_selected);
        _summaryEssentials.Visible = false;
        _stravaUploads.Visible = false;
        _detail.Text = FormatHistoryDetail(_selected);
        _fitFolderButton.Visible = true;
        _fitFolderButton.Disabled = string.IsNullOrWhiteSpace(_selected.FitFilePath) || !File.Exists(_selected.FitFilePath);
        _fitFolderButton.Text = string.IsNullOrWhiteSpace(_selected.FitFilePath) ? "FIT not saved" : _fitFolderButton.Disabled ? "FIT file unavailable" : "Open FIT folder";
    }

    private void OpenSelectedFitFolder()
    {
        var path = _selected?.FitFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _fitFolderButton.Disabled = true; _fitFolderButton.Text = "FIT file unavailable"; return; }
        OS.ShellOpen(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static bool CanOfferGhost(RideActivity activity)
    {
        if (activity.Status != RideActivityStatus.Completed || RouteCatalog.GetById(activity.RouteId) is null ||
            string.IsNullOrWhiteSpace(activity.FitFilePath) || !File.Exists(activity.FitFilePath)) return false;
        try
        {
            var trace = new GhostRideTraceLoader().Load(activity.FitFilePath);
            var route = RouteCatalog.GetById(activity.RouteId)!;
            if (activity.RouteId == "dunedin-one-way-oval-gardens")
            {
                var inferredLaps = Math.Round(trace.TotalDistanceMeters / route.DistanceMeters);
                if (inferredLaps < 1 || Math.Abs(trace.TotalDistanceMeters - inferredLaps * route.DistanceMeters) > 20) return false;
            }
            return trace.TotalDistanceMeters > 0;
        }
        catch (Exception ex) { GD.PushWarning($"Ghost trace unavailable for activity {activity.Id}: {ex.Message}"); return false; }
    }

    private void StartAgainstGhost()
    {
        if (_selected is not { } activity || string.IsNullOrWhiteSpace(activity.FitFilePath)) return;
        var route = RouteCatalog.GetById(activity.RouteId);
        if (route is null) return;
        try
        {
            var trace = new GhostRideTraceLoader().Load(activity.FitFilePath);
            var session = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
            if (session is null) return;
            session.SetPendingGhost(new GhostRiderStateSource(trace, route.Id), activity.Id);
            Hide();
            _routeSelection?.ShowRouteSelection();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Could not start saved-ride ghost: {ex.Message}");
            _detail.Text += "\n\nThis ride cannot be loaded as a ghost.";
            _ghostButton.Visible = false;
        }
    }

    private async void ShowSelectedSummary()
    {
        if (_selected is null) return;
        _summaryMode = true; _list.Visible = false; _summaryEssentials.Visible = true; _detailScroll.Visible = true; _title.Text = "Ride Summary"; _viewButton.Visible = false; _ghostButton.Visible = false;
        var a = _selected;
        var draft = new RideActivityDraft(a.Id, a.RiderProfileId, a.RouteId, a.RouteName, a.StartedAtUtc, a.Status,
            a.DurationSeconds, a.DistanceMeters, a.CompletedLaps, a.AverageSpeedKph, a.MaxSpeedKph,
            a.AveragePowerWatts, a.MaxPowerWatts, a.AverageCadenceRpm, a.MaxCadenceRpm, a.ElevationGainMeters, a.FitFilePath,
            a.Kind, a.WorkoutDefinitionId, a.WorkoutName, a.WorkoutFtpWatts, a.WorkoutCompleted, a.WorkoutTargetAdherencePercent);
        IReadOnlyList<RideActivityMedia> media = Array.Empty<RideActivityMedia>();
        try { media = await (_repositoryOverride ?? GameBootstrap.RideHistory)!.GetActivityMediaAsync(a.Id); } catch { }
        var fit = a.FitFilePath is null ? "FIT: not saved" : File.Exists(a.FitFilePath) ? $"FIT: {a.FitFilePath}" : $"FIT: file unavailable · {a.FitFilePath}";
        _fitFolderButton.Visible = !string.IsNullOrWhiteSpace(a.FitFilePath); _fitFolderButton.Disabled = string.IsNullOrWhiteSpace(a.FitFilePath) || !File.Exists(a.FitFilePath); _fitFolderButton.Text = string.IsNullOrWhiteSpace(a.FitFilePath) ? "FIT not saved" : _fitFolderButton.Disabled ? "FIT file unavailable" : "Open FIT folder";
        _summaryEssentials.Text = FormatSummaryEssentials(draft);
        _detail.Text = FormatSummaryDetails(draft, fit, media);
        BuildStravaUploadControls(a.Id, a.RiderProfileId, a.FitFilePath);
        _detailScroll.ScrollVertical = 0;
        _doneButton.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void ClosePanel()
    {
        Hide();
        if (_summaryMode && _returnToRouteSelection) _routeSelection?.ShowRouteSelection();
        else _startPanel?.Show();
    }

    private void BuildStravaUploadControls(Guid activityId, Guid profileId, string? fitPath)
    {
        _stravaRows.Clear();
        foreach (var child in _stravaUploads.GetChildren()) { _stravaUploads.RemoveChild(child); child.QueueFree(); }
        _stravaUploads.Visible = true;
        _stravaUploads.AddChild(new HSeparator());
        _stravaUploads.AddChild(new Label { Text = "Strava uploads" });
        var linked = StravaIntegrationClient.Shared.GetLink(profileId);
        var segments = StravaIntegrationClient.GetFitSegments(activityId, fitPath);
        var unavailableReason = StravaPolicies.UploadUnavailableReason(linked is not null, segments.Any(segment => segment.IsValid));
        if (unavailableReason.Length > 0)
        {
            AddDisabledUploadRow(unavailableReason);
            foreach (var segment in segments)
            {
                var saved = segment.IsValid ? StravaIntegrationClient.Shared.GetUpload(profileId, activityId, segment.Index, segment.Path) : null;
                var state = saved?.Status switch
                {
                    StravaUploadStatus.Queued => "queued automatically",
                    StravaUploadStatus.Submitting => "uploading",
                    StravaUploadStatus.Processing => "processing",
                    StravaUploadStatus.Complete => "uploaded",
                    StravaUploadStatus.Failed => saved.SafeMessage ?? "failed · manual retry available",
                    StravaUploadStatus.Cancelled => "automatic upload disabled · manual retry available",
                    StravaUploadStatus.Unknown => "submission outcome unknown · check Strava before retrying",
                    _ => "not uploaded"
                };
                _stravaUploads.AddChild(new Label { Text = $"Segment {segment.Index + 1}: {segment.DisplayName} · {state}", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            }
            return;
        }
        if (linked is null) return;
        _stravaUploads.AddChild(new Label { Text = $"Uploading to {linked.AthleteName} · {segments.Count} saved FIT segment{(segments.Count == 1 ? "" : "s")}." });
        foreach (var segment in segments)
        {
            if (!segment.IsValid)
            {
                AddDisabledUploadRow($"Segment {segment.Index + 1} · {segment.DisplayName}: saved FIT is missing or invalid.");
                continue;
            }
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var status = new Label { Text = $"Segment {segment.Index + 1} · {segment.DisplayName}: ready", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            var existing = StravaIntegrationClient.Shared.GetUpload(profileId, activityId, segment.Index, segment.Path);
            var text = existing?.Status switch
            {
                StravaUploadStatus.Queued => "Queued",
                StravaUploadStatus.Complete => "Uploaded",
                StravaUploadStatus.Processing => "Check Status",
                StravaUploadStatus.Failed or StravaUploadStatus.Cancelled => "Retry upload",
                StravaUploadStatus.Submitting or StravaUploadStatus.Unknown => "Submission pending",
                _ => "Upload to Strava"
            };
            var button = new Button { Text = text, Disabled = existing?.Status is StravaUploadStatus.Complete or StravaUploadStatus.Queued or StravaUploadStatus.Submitting or StravaUploadStatus.Unknown };
            if (existing?.Status == StravaUploadStatus.Queued) status.Text = $"Segment {segment.Index + 1}: queued for automatic upload{(existing.NextAttemptAtUtc is { } retryAt && retryAt > DateTimeOffset.UtcNow ? $" · retry after {retryAt.ToLocalTime():t}" : "")}.";
            if (existing?.Status == StravaUploadStatus.Submitting) status.Text = $"Segment {segment.Index + 1}: uploading to Strava.";
            if (existing?.Status == StravaUploadStatus.Processing) status.Text = $"Segment {segment.Index + 1}: Strava is processing this saved upload.";
            if (existing?.Status == StravaUploadStatus.Complete) status.Text = $"Segment {segment.Index + 1}: uploaded";
            if (existing?.Status is StravaUploadStatus.Unknown or StravaUploadStatus.Submitting)
            {
                status.Text = $"Segment {segment.Index + 1}: submission outcome is unknown. Do not retry without resolving it; that could duplicate the activity.";
                var checkStrava = new Button { Text = "Check Strava" };
                checkStrava.Pressed += () => OS.ShellOpen("https://www.strava.com/athlete/training");
                row.AddChild(checkStrava);
            }
            if (existing?.Status == StravaUploadStatus.Failed) status.Text = $"Segment {segment.Index + 1}: {existing.SafeMessage ?? "upload failed"}. The saved FIT file is available to retry.";
            if (existing?.Status == StravaUploadStatus.Cancelled) status.Text = $"Segment {segment.Index + 1}: automatic upload was disabled. The saved FIT file remains available for manual upload.";
            if (existing?.Status == StravaUploadStatus.Complete && !string.IsNullOrWhiteSpace(existing.ActivityUrl))
            {
                var open = new Button { Text = "Open activity" };
                open.Pressed += () => OS.ShellOpen(existing.ActivityUrl);
                row.AddChild(open);
            }
            button.Pressed += async () =>
            {
                button.Disabled = true; button.Text = "Working…";
                try
                {
                    var result = await StravaIntegrationClient.Shared.UploadAsync(profileId, activityId, segment.Index, segment.Path,
                        message => status.Text = $"Segment {segment.Index + 1}: {message}");
                    status.Text = $"Segment {segment.Index + 1}: {result.Message}";
                    button.Text = result.Status == StravaUploadStatus.Processing ? "Check Status" : "Uploaded";
                    if (result.ActivityUrl is not null)
                    {
                        var open = new Button { Text = "Open activity" };
                        open.Pressed += () => OS.ShellOpen(result.ActivityUrl);
                        row.AddChild(open);
                    }
                    button.Disabled = result.Status == StravaUploadStatus.Complete;
                }
                catch (Exception ex)
                {
                    status.Text = $"Segment {segment.Index + 1}: {ex.Message}";
                    var latest = StravaIntegrationClient.Shared.GetUpload(profileId, activityId, segment.Index, segment.Path);
                    button.Text = latest?.Status switch
                    {
                        StravaUploadStatus.Processing => "Check Status",
                        StravaUploadStatus.Failed or StravaUploadStatus.Cancelled => "Retry upload",
                        StravaUploadStatus.Queued => "Queued",
                        StravaUploadStatus.Unknown or StravaUploadStatus.Submitting => "Submission pending",
                        _ => "Retry upload"
                    };
                    button.Disabled = latest?.Status is StravaUploadStatus.Unknown or StravaUploadStatus.Submitting or StravaUploadStatus.Queued;
                }
            };
            row.AddChild(status); row.AddChild(button); _stravaUploads.AddChild(row);
            _stravaRows.Add((profileId, activityId, segment.Index, segment.Path, status, button));
        }
    }

    private void AddDisabledUploadRow(string explanation)
    {
        var row = new HBoxContainer();
        row.AddChild(new Button { Text = "Upload to Strava", Disabled = true });
        row.AddChild(new Label { Text = explanation, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _stravaUploads.AddChild(row);
    }

    private static string FormatSummaryEssentials(RideActivityDraft a) =>
        $"{(a.Kind == RideActivityKind.Workout ? $"Workout · {a.WorkoutName ?? a.RouteName}" : a.RouteName)}\n{a.StartedAtUtc.ToLocalTime():f}\n" +
        $"Active duration: {FormatDuration(a.DurationSeconds)} · Distance: {a.DistanceMeters / 1000:F2} km" +
        (a.Kind == RideActivityKind.Workout ? $" · {(a.WorkoutCompleted ? "Completed" : "Partial workout")}" : $" · Laps: {a.CompletedLaps}");

    private static string FormatSummaryDetails(RideActivityDraft a, string fit, IReadOnlyList<RideActivityMedia> media) =>
        (a.Kind == RideActivityKind.Workout ? $"FTP captured: {a.WorkoutFtpWatts?.ToString() ?? "Not used"} W · Target adherence: {FormatTargetAdherence(a.WorkoutTargetAdherencePercent)}\nTargets were guidance; recorded power remains measured.\n" : "") +
        $"Average speed: {a.AverageSpeedKph?.ToString("F1") ?? "—"} km/h · Max: {a.MaxSpeedKph?.ToString("F1") ?? "—"} km/h\n" +
        $"Average power: {a.AveragePowerWatts?.ToString("F0") ?? "—"} W · Max: {a.MaxPowerWatts?.ToString("F0") ?? "—"} W\n" +
        $"Average cadence: {a.AverageCadenceRpm?.ToString("F0") ?? "—"} rpm · Max: {a.MaxCadenceRpm?.ToString("F0") ?? "—"} rpm\n" +
        $"Elevation gain: {a.ElevationGainMeters:F0} m\n{fit}\n\n" +
        RideSummaryLayoutPolicy.ScreenshotList(media.Select(m => m.ImagePath));

    private static string FormatHistoryDetail(RideActivity a) =>
        $"{(a.Kind == RideActivityKind.Workout ? $"Workout · {a.WorkoutName ?? a.RouteName}" : a.RouteName)}\n{a.StartedAtUtc.ToLocalTime():f}\n\nDistance: {a.DistanceMeters / 1000:F2} km\n" +
        $"Active duration: {FormatDuration(a.DurationSeconds)}\n" +
        (a.Kind == RideActivityKind.Workout ? $"FTP captured: {a.WorkoutFtpWatts?.ToString() ?? "Not used"} W · {(a.WorkoutCompleted ? "Completed" : "Partial workout")}\nTarget adherence: {FormatTargetAdherence(a.WorkoutTargetAdherencePercent)}\nTargets were separate from measured power.\n" : $"Laps: {a.CompletedLaps}\n") +
        $"Speed: avg {a.AverageSpeedKph?.ToString("F1") ?? "—"} / max {a.MaxSpeedKph?.ToString("F1") ?? "—"} km/h\n" +
        $"Power: avg {a.AveragePowerWatts?.ToString("F0") ?? "—"} / max {a.MaxPowerWatts?.ToString("F0") ?? "—"} W\n" +
        $"Cadence: avg {a.AverageCadenceRpm?.ToString("F0") ?? "—"} / max {a.MaxCadenceRpm?.ToString("F0") ?? "—"} rpm\nElevation gain: {a.ElevationGainMeters:F0} m\n" +
        $"FIT: {(a.FitFilePath is null ? "not saved" : a.FitFilePath)}";

    private static string FormatTargetAdherence(double? percent) => percent is { } value ? $"{value:F0}%" : "Not recorded";

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
