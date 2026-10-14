using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

public partial class RideHistoryPanelController : Control
{
    private Control? _startPanel;
    private RouteSelectionPanelController? _routeSelection;
    private HudController? _hud;
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _detail = null!;
    private Label _summaryEssentials = null!;
    private ScrollContainer _detailScroll = null!;
    private ItemList _list = null!;
    private Button _viewButton = null!;
    private Button _ghostButton = null!;
    private Button _doneButton = null!;
    private List<RideActivity> _activities = [];
    private RideActivity? _selected;
    private bool _summaryMode;
    private bool _returnToRouteSelection;

    public override void _Ready()
    {
        _startPanel = GetNodeOrNull<Control>("../StartPanel");
        _routeSelection = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
        _hud = GetNodeOrNull<HudController>("../HudController");
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel = new PanelContainer { AnchorLeft = .5f, AnchorTop = .5f, AnchorRight = .5f, AnchorBottom = .5f };
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18); margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 18); margin.AddThemeConstantOverride("margin_bottom", 18);
        var column = new VBoxContainer();
        _title = new Label { Text = "Ride History" }; column.AddChild(_title);
        _summaryEssentials = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddChild(_summaryEssentials);
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list = new ItemList { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.ItemSelected += OnItemSelected; body.AddChild(_list);
        _detailScroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _detail = new Label { Text = "Select a ride to see details.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        _detailScroll.AddChild(_detail); body.AddChild(_detailScroll); column.AddChild(body);
        var buttons = new HBoxContainer();
        _viewButton = new Button { Text = "View selected ride" }; _viewButton.Pressed += ShowSelectedSummary; buttons.AddChild(_viewButton);
        _ghostButton = new Button { Text = "Ride against ghost", Visible = false }; _ghostButton.Pressed += StartAgainstGhost; buttons.AddChild(_ghostButton);
        _doneButton = new Button { Text = "Done / Return to start" }; _doneButton.Pressed += ClosePanel; buttons.AddChild(_doneButton);
        column.AddChild(buttons); margin.AddChild(column); _panel.AddChild(margin); AddChild(_panel);
        Resized += FitPanelToViewport;
        FitPanelToViewport();
        Hide();
    }

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
        _summaryMode = false; _returnToRouteSelection = false; _selected = null; _list.Visible = true; _summaryEssentials.Visible = false; _detailScroll.Visible = true; _list.Clear(); _title.Text = "Ride History";
        _detail.Text = "Loading ride history…"; _viewButton.Visible = false; _ghostButton.Visible = false;
        _startPanel?.Hide(); Show(); MoveToFront();
        try
        {
            await GameBootstrap.GetPersistentRiderAsync();
            var profile = await GameBootstrap.GetPersistentRiderAsync();
            _title.Text = $"Ride History — {profile.Name}";
            _activities = (await GameBootstrap.RideHistory!.GetRecentActivitiesForProfileAsync(profile.Id, 100)).ToList();
            if (_activities.Count == 0) _detail.Text = "No completed rides yet.";
            foreach (var activity in _activities)
            {
                var local = activity.StartedAtUtc.ToLocalTime();
                _list.AddItem($"{local:yyyy-MM-dd HH:mm}  {activity.RouteName}\n{activity.DistanceMeters / 1000:F2} km · {FormatDuration(activity.DurationSeconds)} · {activity.AveragePowerWatts?.ToString("F0") ?? "—"} W avg");
            }
            if (_activities.Count > 0) _list.Select(0);
        }
        catch (Exception ex) { _detail.Text = $"Ride history could not be loaded.\n{ex.Message}"; GD.PushWarning($"Ride history load failed: {ex.Message}"); }
    }

    public async void ShowCompletedSummary(RideActivityDraft draft, Guid? activityId, string? fitRecordingError = null)
    {
        _summaryMode = true; _selected = null; _list.Visible = false; _summaryEssentials.Visible = true; _detailScroll.Visible = true; _list.Clear(); _viewButton.Visible = false; _ghostButton.Visible = false;
        _returnToRouteSelection = GameBootstrap.TrainerClient is not null;
        _startPanel?.Hide(); Show(); MoveToFront();
        _title.Text = "Ride Summary";
        var fit = fitRecordingError is not null
            ? $"FIT RECORDING ISSUE: {fitRecordingError}\nFIT: {draft.FitFilePath ?? "not saved"}"
            : string.IsNullOrWhiteSpace(draft.FitFilePath) ? "FIT: not saved" : $"FIT: {draft.FitFilePath}";
        _summaryEssentials.Text = FormatSummaryEssentials(draft);
        _detail.Text = $"{fit}\n\nScreenshots: loading…";
        IReadOnlyList<RideActivityMedia> media = Array.Empty<RideActivityMedia>();
        if (activityId is { } id && GameBootstrap.RideHistory is { } repository)
        {
            try { media = await repository.GetActivityMediaAsync(id); }
            catch (Exception ex) { GD.PushWarning($"Ride media list could not be loaded: {ex.Message}"); }
        }
        if (media.Count == 0 && activityId is { } currentId && _hud is not null)
            media = _hud.CapturedScreenshots.Where(s => s.ActivityId == currentId).Select(s => new RideActivityMedia(
                Guid.NewGuid(), currentId, s.ImagePath, s.SidecarPath, s.CapturedAt.ToUniversalTime(), s.Mode.ToString(), s.MarkerNumber)).ToArray();
        _detail.Text = FormatSummaryDetails(draft, fit, media);
        _detailScroll.ScrollVertical = 0;
        _doneButton.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void OnItemSelected(long index)
    {
        if (index < 0 || index >= _activities.Count) return;
        _selected = _activities[(int)index]; _viewButton.Visible = true;
        _ghostButton.Visible = CanOfferGhost(_selected);
        _summaryEssentials.Visible = false;
        _detail.Text = FormatHistoryDetail(_selected);
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
            a.AveragePowerWatts, a.MaxPowerWatts, a.AverageCadenceRpm, a.MaxCadenceRpm, a.ElevationGainMeters, a.FitFilePath);
        IReadOnlyList<RideActivityMedia> media = Array.Empty<RideActivityMedia>();
        try { media = await GameBootstrap.RideHistory!.GetActivityMediaAsync(a.Id); } catch { }
        var fit = a.FitFilePath is null ? "FIT: not saved" : $"FIT: {a.FitFilePath}";
        _summaryEssentials.Text = FormatSummaryEssentials(draft);
        _detail.Text = FormatSummaryDetails(draft, fit, media);
        _detailScroll.ScrollVertical = 0;
        _doneButton.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void ClosePanel()
    {
        Hide();
        if (_summaryMode && _returnToRouteSelection) _routeSelection?.ShowRouteSelection();
        else _startPanel?.Show();
    }

    private static string FormatSummaryEssentials(RideActivityDraft a) =>
        $"{a.RouteName}\n{a.StartedAtUtc.ToLocalTime():f}\n" +
        $"Duration: {FormatDuration(a.DurationSeconds)} · Distance: {a.DistanceMeters / 1000:F2} km · Laps: {a.CompletedLaps}";

    private static string FormatSummaryDetails(RideActivityDraft a, string fit, IReadOnlyList<RideActivityMedia> media) =>
        $"Average speed: {a.AverageSpeedKph?.ToString("F1") ?? "—"} km/h · Max: {a.MaxSpeedKph?.ToString("F1") ?? "—"} km/h\n" +
        $"Average power: {a.AveragePowerWatts?.ToString("F0") ?? "—"} W · Max: {a.MaxPowerWatts?.ToString("F0") ?? "—"} W\n" +
        $"Average cadence: {a.AverageCadenceRpm?.ToString("F0") ?? "—"} rpm · Max: {a.MaxCadenceRpm?.ToString("F0") ?? "—"} rpm\n" +
        $"Elevation gain: {a.ElevationGainMeters:F0} m\n{fit}\n\n" +
        RideSummaryLayoutPolicy.ScreenshotList(media.Select(m => m.ImagePath));

    private static string FormatHistoryDetail(RideActivity a) =>
        $"{a.RouteName}\n{a.StartedAtUtc.ToLocalTime():f}\n\nDistance: {a.DistanceMeters / 1000:F2} km\n" +
        $"Duration: {FormatDuration(a.DurationSeconds)}\nLaps: {a.CompletedLaps}\nAverage power: {a.AveragePowerWatts?.ToString("F0") ?? "—"} W\n" +
        $"FIT: {(a.FitFilePath is null ? "not saved" : a.FitFilePath)}";

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
