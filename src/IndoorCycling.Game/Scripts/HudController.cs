using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

public partial class HudController : Control
{
	private Label? _modeLabel;

	private Label? _routeLabel;
	private Label? _segmentLabel;
	private Label? _gameScoreLabel;
	private Label? _gameSteeringHint;
	private Label? _catFeedbackLabel;
	private double _catFeedbackSeconds;
	private Label? _statusLabel;
	private Label? _powerLabel;
	private Label? _cadenceLabel;
	private Label? _heartRateLabel;
	private Label? _speedLabel;
	private Label? _gradientLabel;
	private Label? _distanceLabel;
	private Label? _elapsedLabel;
	private Label? _ghostLabel;
	private Label? _multiplayerStatusLabel;
	private Label? _nearbyHumanRidersLabel;
	private string? _pacerStatusText;
	private string _nearbyHumanRidersText = "";
	private Control? _topHudPanel;
	private Control? _bottomHudPanel;
	private Rect2? _cameraSafeViewportRect;
	private int _cameraSafeViewportLayoutHash;
	private RouteGradientInset? _routeGradientInset;
	private Control? _dummyControlPanel;

	private Button? _menuButton;
	private Button? _pauseRideButton;
	private Label? _recordingIndicator;
	private bool _recordingActive;
	private bool _debugInspectionWithoutRecording;
	private bool _recordingFailed;
	private double _shownSpeed = double.NaN;
	private double _shownGradient = double.NaN;
	private double _shownDistance = double.NaN;
	private string? _shownPower;
	private string? _shownCadence;
	private int _shownElapsedSecond = -1;
	private RideMenuPanelController? _rideMenuPanel;
	private RideSessionNode? _rideSessionNode;
	private RideWorldView? _rideWorldView;
	private PanelContainer? _debugOverlay;
	private Label? _debugSummaryLabel;
	private bool _debugEnabled;
	private bool _hudDisplayEnabled = true;
	private bool? _debugEnabledBeforeCapture;
	private ScreenshotCaptureService? _screenshotCapture;
	private Label? _screenshotToast;
	private PanelContainer? _catchUpOfferPanel;
	private Label? _catchUpOfferText;
	private string _catchUpOfferRiderId = "";
	private Label? _cameraInspectionLabel;
	private PanelContainer? _trainerRecoveryPanel;
	private Label? _trainerRecoveryMessage;
	private Button? _trainerRecoveryRetryButton;
	private Control? _debugJumpConfirmation;
	private double _cameraInspectionLabelSeconds;
	private int _screenshotToastSequence;
	private Label? _dummySummary;
	private Control? _dummyWkgPowerRow;
	private Control? _dummyWattsPowerRow;
	private Button? _dummyWkgMinusPointTwo;
	private Button? _dummyWkgMinusPointOne;
	private Button? _dummyWkgPlusPointOne;
	private Button? _dummyWkgPlusPointTwo;
	private Button? _dummyWattsMinusTwentyFive;
	private Button? _dummyWattsMinusTen;
	private Button? _dummyWattsPlusTen;
	private Button? _dummyWattsPlusTwentyFive;
	private Button? _dummyCadenceMinusFive;
	private Button? _dummyCadencePlusFive;
	private bool _dummyActive;

	public override void _Ready()
	{
		_modeLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/ModeLabel");
		_topHudPanel = GetNodeOrNull<Control>("TopHudPanel");
		_bottomHudPanel = GetNodeOrNull<Control>("BottomBar");
		_statusLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/StatusLabel");
		_powerLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/PrimaryMetrics/PowerMetric/Value");
		_cadenceLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/PrimaryMetrics/CadenceMetric/Value");
		_heartRateLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/PrimaryMetrics/HeartRateMetric/Value");
		_speedLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/PrimaryMetrics/SpeedMetric/Value");
		_gradientLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/SecondaryMetrics/GradientMetric/Value");
		_distanceLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/SecondaryMetrics/DistanceMetric/Value");
		_elapsedLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/SecondaryMetrics/ElapsedMetric/Value");
		_ghostLabel = new Label { Name = "GhostIndicator", Visible = false };
		GetNodeOrNull<VBoxContainer>("TopHudPanel/MarginContainer/VBoxContainer")?.AddChild(_ghostLabel);
		_routeLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/RouteLabel");
		_multiplayerStatusLabel = new Label { Name = "MultiplayerStatus", Visible = false };
		var topHudLayout = GetNodeOrNull<VBoxContainer>("TopHudPanel/MarginContainer/VBoxContainer");
		topHudLayout?.AddChild(_multiplayerStatusLabel);
		_nearbyHumanRidersLabel = new Label
		{
			Name = "NearbyHumanRiders",
			Visible = false,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_nearbyHumanRidersLabel.AddThemeColorOverride("font_color", new Color(.87f, .84f, .73f, .96f));
		_nearbyHumanRidersLabel.AddThemeFontSizeOverride("font_size", 12);
		topHudLayout?.AddChild(_nearbyHumanRidersLabel);
		_segmentLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/SegmentLabel");
		_gameScoreLabel = new Label { Name = "GameScore", Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
		_gameScoreLabel.AddThemeColorOverride("font_color", new Color(.9f, .78f, .44f));
		_gameScoreLabel.AddThemeFontSizeOverride("font_size", 13);
		topHudLayout?.AddChild(_gameScoreLabel);
		_gameSteeringHint = new Label { Name = "GameSteeringHint", Visible = false, HorizontalAlignment = HorizontalAlignment.Center,
			Text = "← / → arrow keys steer" };
		_gameSteeringHint.AddThemeFontSizeOverride("font_size", 12);
		topHudLayout?.AddChild(_gameSteeringHint);
		_catFeedbackLabel = new Label { Name = "CatCollectionFeedback", Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
		_catFeedbackLabel.AddThemeColorOverride("font_color", new Color(1f, .85f, .52f));
		_catFeedbackLabel.AddThemeFontSizeOverride("font_size", 14);
		topHudLayout?.AddChild(_catFeedbackLabel);
		_dummyControlPanel = GetNodeOrNull<Control>("DummyControlPanel");
		_dummySummary = GetNodeOrNull<Label>("DummyControlPanel/DummyPanel/DummyPanelMargin/DummyPanelLayout/DummySummaryLabel");
		var dummyRows = "DummyPanel/DummyPanelMargin/DummyPanelLayout/DummyControlsScroll/DummyControlsLayout/";
		_dummyWkgPowerRow = GetNodeOrNull<Control>($"DummyControlPanel/{dummyRows}DummyWkgPowerRow");
		_dummyWattsPowerRow = GetNodeOrNull<Control>($"DummyControlPanel/{dummyRows}DummyWattsPowerRow");
		_dummyWkgMinusPointTwo = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWkgPowerRow/MinusPointTwoButton");
		_dummyWkgMinusPointOne = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWkgPowerRow/MinusPointOneButton");
		_dummyWkgPlusPointOne = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWkgPowerRow/PlusPointOneButton");
		_dummyWkgPlusPointTwo = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWkgPowerRow/PlusPointTwoButton");
		_dummyWattsMinusTwentyFive = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWattsPowerRow/MinusTwentyFiveButton");
		_dummyWattsMinusTen = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWattsPowerRow/MinusTenButton");
		_dummyWattsPlusTen = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWattsPowerRow/PlusTenButton");
		_dummyWattsPlusTwentyFive = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyWattsPowerRow/PlusTwentyFiveButton");
		_dummyCadenceMinusFive = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyCadenceRow/MinusFiveButton");
		_dummyCadencePlusFive = GetNodeOrNull<Button>($"DummyControlPanel/{dummyRows}DummyCadenceRow/PlusFiveButton");
		ConfigureClippedLabel(_routeLabel);
		ConfigureClippedLabel(_segmentLabel);
		ConfigureClippedLabel(_statusLabel);
		ConfigureClippedLabel(_dummySummary);
		ConnectDummyControls();

		_menuButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/MenuButton");
		_pauseRideButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/PauseRideButton");
		_recordingIndicator = GetNodeOrNull<Label>("BottomBar/MarginContainer/HBoxContainer/RecordingIndicator");
		_rideMenuPanel = GetNodeOrNull<RideMenuPanelController>("../RideMenuPanel");
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_rideWorldView = GetNodeOrNull<RideWorldView>("../RideWorldView");
		_routeGradientInset = new RouteGradientInset
		{
			Name = "RouteGradientInset",
			AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, AnchorBottom = 0,
			OffsetLeft = -292, OffsetRight = -12, OffsetTop = 222, OffsetBottom = 452,
			CustomMinimumSize = new Vector2(280, 210),
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		AddChild(_routeGradientInset);
		_screenshotCapture = new ScreenshotCaptureService(this, () => _rideSessionNode?.CurrentActivityId,
			() => GameLogger.SessionId);
		_screenshotToast = new Label
		{
			Name = "ScreenshotToast", Text = "Screenshot saved", Visible = false,
			AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1,
			OffsetLeft = -100, OffsetRight = 100, OffsetTop = -74, OffsetBottom = -42,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		AddChild(_screenshotToast);
		BuildCatchUpOffer();
		BuildTrainerRecoveryAction();
		_cameraInspectionLabel = new Label
		{
			Name = "CameraInspectionLabel",
			Text = "CAMERA · GAMEPLAY (REFINED) · F6: VIEW · SHIFT+F12: SAVE",
			Visible = false,
			AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0, AnchorBottom = 0,
			OffsetLeft = -300, OffsetRight = 300, OffsetTop = 10, OffsetBottom = 34,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_cameraInspectionLabel.AddThemeColorOverride("font_color", new Color(0.91f, 0.83f, 0.67f));
		_cameraInspectionLabel.AddThemeColorOverride("font_outline_color", new Color(0.025f, 0.032f, 0.037f, 0.92f));
		_cameraInspectionLabel.AddThemeConstantOverride("outline_size", 3);
		_cameraInspectionLabel.AddThemeFontSizeOverride("font_size", 13);
		AddChild(_cameraInspectionLabel);

		if (_menuButton is not null)
		{
			_menuButton.Pressed += OnMenuPressed;
		}

		if (_pauseRideButton is not null) _pauseRideButton.Pressed += OnPauseRidePressed;
		SetRecordingState(false, false);
		EnsureDebugOverlay();
		UpdateHudVisibility();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized && IsInsideTree())
			CallDeferred(nameof(UpdateDebugOverlayLayout));
	}

	private void BuildTrainerRecoveryAction()
	{
		_trainerRecoveryPanel = new PanelContainer
		{
			Name = "TrainerRecoveryAction",
			Visible = false,
			AnchorLeft = .5f, AnchorRight = .5f, AnchorTop = 0, AnchorBottom = 0,
			OffsetLeft = -260, OffsetRight = 260, OffsetTop = 116, OffsetBottom = 196,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_trainerRecoveryMessage = new Label
		{
			Name = "TrainerRecoveryMessage",
			Text = "Trainer connection was lost.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			VerticalAlignment = VerticalAlignment.Center
		};
		row.AddChild(_trainerRecoveryMessage);
		_trainerRecoveryRetryButton = new Button { Name = "TrainerRecoveryRetryButton", Text = "Retry", Visible = false };
		// Own one stable connection for this HUD's lifetime. Recovery state changes
		// replace only the action; disconnecting an absent signal logs an engine error.
		_trainerRecoveryRetryButton.Pressed += OnTrainerRecoveryRetry;
		row.AddChild(_trainerRecoveryRetryButton);
		_trainerRecoveryPanel.AddChild(row);
		AddChild(_trainerRecoveryPanel);
	}

	/// <summary>Shows a retry action over the active ride after automatic trainer recovery expires.</summary>
	public void SetTrainerRecoveryAction(string? message, Action? retry)
	{
		if (_trainerRecoveryPanel is null) return;
		_trainerRecoveryPanel.Visible = !string.IsNullOrWhiteSpace(message) && _rideSessionNode?.IsRideActive == true;
		if (_trainerRecoveryMessage is not null) _trainerRecoveryMessage.Text = message ?? string.Empty;
		if (_trainerRecoveryRetryButton is null) return;
		_trainerRecoveryRetry = retry;
		_trainerRecoveryRetryButton.Visible = retry is not null;
		_trainerRecoveryRetryButton.Disabled = retry is null;
	}

	private Action? _trainerRecoveryRetry;
	private void OnTrainerRecoveryRetry() => _trainerRecoveryRetry?.Invoke();

	private void BuildCatchUpOffer()
	{
		_catchUpOfferPanel = new PanelContainer
		{
			Name = "CatchUpOffer",
			Visible = false,
			AnchorLeft = .5f, AnchorRight = .5f, AnchorTop = 0, AnchorBottom = 0,
			OffsetLeft = -230, OffsetRight = 230, OffsetTop = 40, OffsetBottom = 106,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_catchUpOfferText = new Label { Name = "OfferText", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
		row.AddChild(_catchUpOfferText);
		var catchUp = new Button { Text = "Catch Up" };
		catchUp.Pressed += () =>
		{
			if (!string.IsNullOrEmpty(_catchUpOfferRiderId)) _rideMenuPanel?.ShowCatchUpConfirmation(_catchUpOfferRiderId);
			if (_catchUpOfferPanel is not null) _catchUpOfferPanel.Visible = false;
		};
		row.AddChild(catchUp);
		var keep = new Button { Text = "Keep Riding" };
		keep.Pressed += () => { if (_catchUpOfferPanel is not null) _catchUpOfferPanel.Visible = false; };
		row.AddChild(keep);
		_catchUpOfferPanel.AddChild(row);
		AddChild(_catchUpOfferPanel);
	}

	public void ShowCatchUpOffer(string riderId, string displayName, double distanceAheadMeters)
	{
		if (_catchUpOfferPanel is null || distanceAheadMeters <= 100) return;
		_catchUpOfferRiderId = riderId;
		if (_catchUpOfferText is not null) _catchUpOfferText.Text =
			$"Ride with {displayName} · {(distanceAheadMeters / 1000):0.0} km ahead";
		_catchUpOfferPanel.Visible = true;
	}

	public void SetMultiplayerStatus(string? status)
	{
		if (_multiplayerStatusLabel is null) return;
		_multiplayerStatusLabel.Visible = !string.IsNullOrWhiteSpace(status);
		_multiplayerStatusLabel.Text = status ?? "";
	}

	public void SetNearbyHumanRiders(IEnumerable<MultiplayerRemoteRider> riders, double localDistanceMeters,
		double routeLengthMeters, double nowSeconds)
	{
		if (_nearbyHumanRidersLabel is null) return;
		if (routeLengthMeters <= 0) { _nearbyHumanRidersText = ""; UpdateNearbyRiderDisplay(); return; }
		var nearby = riders.Where(rider => rider.HasState &&
			-rider.ReceivedAtSeconds + nowSeconds <= RemoteRiderInterpolator.StaleAfterSeconds &&
			rider.DistanceMeters is not null)
			.Select(rider => (Rider: rider, Distance: MultiplayerRoutePosition.SignedWrappedSeparation(
				rider.DistanceMeters!.Value, localDistanceMeters, routeLengthMeters),
				Laps: MultiplayerRoutePosition.LapDifference(rider.DistanceMeters!.Value, localDistanceMeters, routeLengthMeters)))
			.Where(item => Math.Abs(item.Distance) <= 300)
			.OrderBy(item => Math.Abs(item.Distance)).Take(3).ToArray();
		_nearbyHumanRidersText = string.Join("   ·   ", nearby.Select(item =>
		{
			var distance = $"{Math.Abs(item.Distance):0} m {(item.Distance >= 0 ? "ahead" : "behind")}";
			var lap = item.Laps == 0 ? "" : $" · {Math.Abs(item.Laps)} lap{(Math.Abs(item.Laps) == 1 ? "" : "s")} {(item.Laps > 0 ? "ahead" : "behind")}";
			return $"{item.Rider.DisplayName}{lap} · {distance}";
		}));
		UpdateNearbyRiderDisplay();
	}

	public void SetPacerStatus(string? status)
	{
		_pacerStatusText = string.IsNullOrWhiteSpace(status) ? null : status;
		UpdateNearbyRiderDisplay();
	}

	private void UpdateNearbyRiderDisplay()
	{
		if (_nearbyHumanRidersLabel is null) return;
		var lines = new[] { _pacerStatusText, _nearbyHumanRidersText }
			.Where(text => !string.IsNullOrWhiteSpace(text));
		_nearbyHumanRidersLabel.Text = string.Join("\n", lines);
		_nearbyHumanRidersLabel.Visible = !string.IsNullOrWhiteSpace(_nearbyHumanRidersLabel.Text)
			&& _hudDisplayEnabled && _rideSessionNode?.IsRideActive == true;
	}

	public void HideCatchUpOffer()
	{
		if (_catchUpOfferPanel is not null) _catchUpOfferPanel.Visible = false;
		_catchUpOfferRiderId = "";
	}

	private void OnPauseRidePressed()
	{
		_rideSessionNode?.ToggleRidePause();
	}

	public void SetRidePaused(bool paused)
	{
		if (_pauseRideButton is not null) _pauseRideButton.Text = paused ? "Resume" : "Pause";
		UpdateRecordingIndicator(paused);
	}

	public void SetRecordingState(bool active, bool paused)
	{
		_recordingActive = active;
		_debugInspectionWithoutRecording = false;
		_recordingFailed = false;
		UpdateRecordingIndicator(paused);
	}

	public void SetDebugInspectionRecordingState()
	{
		_recordingActive = false;
		_debugInspectionWithoutRecording = true;
		_recordingFailed = false;
		UpdateRecordingIndicator(false);
	}

	public void SetRecordingFailureState()
	{
		_recordingActive = false;
		_debugInspectionWithoutRecording = false;
		_recordingFailed = true;
		UpdateRecordingIndicator(false);
	}

	private void UpdateRecordingIndicator(bool paused)
	{
		if (_recordingIndicator is null) return;
		_recordingIndicator.Visible = _recordingActive || _debugInspectionWithoutRecording || _recordingFailed;
		_recordingIndicator.Text = _recordingFailed ? "FIT SAVE ERROR"
			: _debugInspectionWithoutRecording ? "DEBUG · NOT RECORDING"
			: paused ? "REC PAUSED" : "● REC";
	}

	public void SetDebugMode(bool enabled)
	{
		_debugEnabled = enabled;
		if (enabled) EnsureDebugOverlay();
		UpdateHudVisibility();
		if (_modeLabel is not null && _modeLabel.Text.Length > 0)
			_modeLabel.Text = _modeLabel.Text.Replace(" [DEBUG]", "") + (enabled ? " [DEBUG]" : "");
	}

	public bool HudDisplayEnabled => _hudDisplayEnabled;
	public bool DebugDisplayEnabled => _debugEnabled;

	/// <summary>Largest viewport rectangle not covered by persistent ride HUD panels.
	/// Transient notifications are deliberately excluded so their appearance or expiry
	/// cannot change camera framing.</summary>
	public Rect2 GetCameraSafeViewportRect()
	{
		var screen = GetViewportRect();
		if (screen.Size.X <= 0 || screen.Size.Y <= 0) return new Rect2(0, 0, 1, 1);
		var controls = new[] { _topHudPanel, _bottomHudPanel, _routeGradientInset, _dummyControlPanel,
			(Control?)_debugOverlay };
		var blocked = controls.Where(control => control is { Visible: true } && control.IsVisibleInTree())
			.Select(control => ClipToScreen(Inflate(control!.GetGlobalRect(), 10), screen))
			.Where(rect => rect.Size.X > 0 && rect.Size.Y > 0).ToArray();
		var layoutHash = new HashCode();
		layoutHash.Add(screen.Position); layoutHash.Add(screen.Size);
		foreach (var rect in blocked) { layoutHash.Add(rect.Position); layoutHash.Add(rect.Size); }
		var layoutKey = layoutHash.ToHashCode();
		if (_cameraSafeViewportRect is { } cached && _cameraSafeViewportLayoutHash == layoutKey) return cached;
		var xEdges = new SortedSet<float> { screen.Position.X, screen.End.X };
		var yEdges = new SortedSet<float> { screen.Position.Y, screen.End.Y };
		foreach (var rect in blocked)
		{
			xEdges.Add(rect.Position.X); xEdges.Add(rect.End.X);
			yEdges.Add(rect.Position.Y); yEdges.Add(rect.End.Y);
		}
		var xs = xEdges.ToArray();
		var ys = yEdges.ToArray();
		var best = screen;
		var bestArea = 0f;
		for (var left = 0; left < xs.Length - 1; left++)
		for (var right = left + 1; right < xs.Length; right++)
		for (var top = 0; top < ys.Length - 1; top++)
		for (var bottom = top + 1; bottom < ys.Length; bottom++)
		{
			var candidate = new Rect2(xs[left], ys[top], xs[right] - xs[left], ys[bottom] - ys[top]);
			if (candidate.Size.X < screen.Size.X * .18f || candidate.Size.Y < screen.Size.Y * .28f ||
				blocked.Any(rect => candidate.Intersects(rect))) continue;
			var area = candidate.Size.X * candidate.Size.Y;
			if (area <= bestArea) continue;
			bestArea = area;
			best = candidate;
		}
		_cameraSafeViewportRect = new Rect2((best.Position - screen.Position) / screen.Size, best.Size / screen.Size);
		_cameraSafeViewportLayoutHash = layoutKey;
		return _cameraSafeViewportRect.Value;
	}

	private static Rect2 Inflate(Rect2 rect, float margin) =>
		new(rect.Position - Vector2.One * margin, rect.Size + Vector2.One * margin * 2);

	private static Rect2 ClipToScreen(Rect2 rect, Rect2 screen)
	{
		var left = Mathf.Max(rect.Position.X, screen.Position.X);
		var top = Mathf.Max(rect.Position.Y, screen.Position.Y);
		var right = Mathf.Min(rect.End.X, screen.End.X);
		var bottom = Mathf.Min(rect.End.Y, screen.End.Y);
		return new Rect2(left, top, Mathf.Max(0, right - left), Mathf.Max(0, bottom - top));
	}

	public void SetHudDisplayEnabled(bool enabled)
	{
		_hudDisplayEnabled = enabled;
		UpdateHudVisibility();
	}

	public void SetRouteGradientInsetVisible(bool enabled)
	{
		GameBootstrap.Settings.Display.RouteGradientInsetVisible = enabled;
		GameBootstrap.Settings.Save();
		if (_routeGradientInset is not null)
			_routeGradientInset.Visible = enabled && _rideSessionNode?.IsRideActive == true;
	}

	public void SetDebugDisplayEnabled(bool enabled)
	{
		_debugEnabled = enabled;
		UpdateHudVisibility();
	}

	private void EnsureDebugOverlay()
	{
		if (_debugOverlay is not null) return;
		_debugOverlay = new PanelContainer
		{
			Name = "DebugRideOverlay",
			AnchorLeft = 0,
			AnchorRight = 0,
			AnchorTop = 0,
			AnchorBottom = 0,
			OffsetLeft = 12,
			OffsetRight = 312,
			OffsetTop = 12,
			OffsetBottom = 432,
			ClipContents = true
		};
		var layout = new VBoxContainer { Name = "DebugLayout" };
		layout.AddThemeConstantOverride("separation", 5);
		var details = new ScrollContainer
		{
			Name = "DebugDetailsScroll",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
			VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
			ClipContents = true
		};
		_debugSummaryLabel = new Label
		{
			Name = "DebugRideSummary", Text = "DEBUG\nWaiting for ride state…",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
		};
		details.AddChild(_debugSummaryLabel);
		layout.AddChild(details);
		var actions = new HBoxContainer();
		actions.Name = "DebugControls";
		actions.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		var markButton = new Button { Text = "MARK" };
		markButton.Pressed += () => _rideSessionNode?.WriteDebugMarker();
		actions.AddChild(markButton);
		actions.AddChild(new Label { Text = "Jump to m" });
		var jumpDistance = new LineEdit
		{
			Name = "JumpDistance", PlaceholderText = "5830", CustomMinimumSize = new Vector2(82, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		actions.AddChild(jumpDistance);
		var jumpButton = new Button { Text = "Go" };
		jumpButton.Pressed += () => _rideSessionNode?.JumpToRouteDistance(jumpDistance.Text);
		actions.AddChild(jumpButton);
		layout.AddChild(actions);
		_debugOverlay.AddChild(layout);
		AddChild(_debugOverlay);
		CreateDebugJumpConfirmation();
		UpdateHudVisibility();
	}

	private void CreateDebugJumpConfirmation()
	{
		var shade = new ColorRect
		{
			Name = "DebugJumpConfirmation",
			Color = new Color(0.015f, 0.02f, 0.025f, 0.72f),
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Stop,
			ZIndex = 100
		};
		shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		var panel = new PanelContainer
		{
			Name = "ConfirmationPanel",
			AnchorLeft = 0.5f, AnchorRight = 0.5f,
			AnchorTop = 0.5f, AnchorBottom = 0.5f,
			OffsetLeft = -230, OffsetRight = 230,
			OffsetTop = -82, OffsetBottom = 82,
			MouseFilter = Control.MouseFilterEnum.Stop,
			ZIndex = 101
		};
		var content = new VBoxContainer();
		content.AddThemeConstantOverride("separation", 12);
		content.AddChild(new Label
		{
			Text = "Jumping to another route position will save and stop the current FIT recording. Continue?",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center,
			CustomMinimumSize = new Vector2(420, 58)
		});
		var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var continueButton = new Button { Text = "Continue", CustomMinimumSize = new Vector2(112, 36) };
		continueButton.Pressed += () =>
		{
			HideDebugJumpConfirmation();
			_rideSessionNode?.ConfirmDebugDistanceJump();
		};
		var cancelButton = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(112, 36) };
		cancelButton.Pressed += () =>
		{
			HideDebugJumpConfirmation();
			_rideSessionNode?.CancelDebugDistanceJump();
		};
		buttons.AddChild(continueButton);
		buttons.AddChild(cancelButton);
		content.AddChild(buttons);
		panel.AddChild(content);
		shade.AddChild(panel);
		_debugJumpConfirmation = shade;
		AddChild(shade);
	}

	public void ShowDebugJumpConfirmation()
	{
		if (_debugJumpConfirmation is not null) _debugJumpConfirmation.Visible = true;
	}

	public void HideDebugJumpConfirmation()
	{
		if (_debugJumpConfirmation is not null) _debugJumpConfirmation.Visible = false;
	}

	private void UpdateDebugOverlayLayout()
	{
		var viewport = GetViewportRect().Size;
		if (viewport.X <= 0 || viewport.Y <= 0) return;
		var layout = RideHudOverlayLayoutPolicy.ForViewport(viewport.X, viewport.Y,
			_routeGradientInset?.Visible == true);
		SetAnchoredBounds(_debugOverlay, layout.DebugPanel);
		SetAnchoredBounds(_routeGradientInset, layout.RouteInset);
		SetAnchoredBounds(_dummyControlPanel, layout.DummyPanel);
	}

	private static void SetAnchoredBounds(Control? control, RideHudOverlayBounds bounds)
	{
		if (control is null) return;
		control.AnchorLeft = control.AnchorRight = control.AnchorTop = control.AnchorBottom = 0;
		control.OffsetLeft = bounds.X;
		control.OffsetTop = bounds.Y;
		control.OffsetRight = bounds.Right;
		control.OffsetBottom = bounds.Bottom;
	}

	public void UpdateDebugOverlay(RideDebugSnapshot snapshot)
	{
		if (!_debugEnabled || _debugSummaryLabel is null) return;
		var lines = new List<string>
		{
			"DEBUG",
			$"Profile: {snapshot.profileName ?? "-"}",
			$"Mode: {TrainerLabel(snapshot.trainerMode)} · Ride: {snapshot.rideState} · FIT: {snapshot.fitRecordingState}",
			$"Trainer link: {(snapshot.trainerConnected is null ? "—" : snapshot.trainerConnected.Value ? "Connected" : "Disconnected")}",
			$"Route: {snapshot.routeName ?? "—"} ({snapshot.routeId ?? "—"}) · Segment: {snapshot.routeSegment ?? "—"}"
		};
		if (snapshot.trainerKind == "dummy" && snapshot.dummyTargetWatts is { } watts)
			lines.Add(snapshot.dummyMode == "W/kg"
				? $"Dummy: {snapshot.dummyWattsPerKg:F1} W/kg · {watts} W · {snapshot.rawTrainerCadenceRpm ?? 0} rpm"
				: $"Dummy: {watts} W · {snapshot.rawTrainerCadenceRpm ?? 0} rpm");
		else
			lines.Add($"Trainer: {TrainerLabel(snapshot.trainerKind)} · {snapshot.rawTrainerPowerWatts?.ToString() ?? "—"} W · {snapshot.rawTrainerCadenceRpm?.ToString() ?? "—"} rpm");
		lines.Add($"Ride: {snapshot.totalRideDistanceMeters / 1000:0.00} km · Lap {snapshot.lapNumber} · {snapshot.virtualSpeedKph:0.0} km/h · {snapshot.elapsedRidingSeconds:0}s");
		lines.Add($"Route gradient: {snapshot.routeGradientPercent:+0.0;-0.0;0.0}% · Elevation: {snapshot.routeElevationMeters?.ToString("F0") ?? "—"} m");
		lines.Add($"Raw trainer: {snapshot.rawTrainerPowerWatts?.ToString() ?? "—"} W · {snapshot.rawTrainerCadenceRpm?.ToString() ?? "—"} rpm · {snapshot.rawTrainerSpeedKph?.ToString("F1") ?? "—"} km/h · age {snapshot.telemetryAgeSeconds?.ToString("F1") ?? "—"} s");
		if (snapshot.trainerKind == "real")
		{
			lines.Add($"Trainer difficulty: {snapshot.trainerDifficultyPercent}%");
			lines.Add($"FTMS target grade: {snapshot.trainerGradeTargetPercent:+0.0;-0.0;0.0}%");
			lines.Add($"FTMS commanded grade: {snapshot.trainerGradeCommandedPercent?.ToString("+0.0;-0.0;0.0") ?? "—"}%{(snapshot.trainerGradeTransitionActive ? " · transitioning" : "")}");
		}
		lines.Add($"Riders: AI {snapshot.aiRiderCount} · Nearest {NearestRider(snapshot)}");
		var humanRiders = snapshot.remoteHumanRiders;
		lines.Add($"Human riders: {humanRiders.Count}");
		foreach (var rider in humanRiders)
		{
			if (!rider.hasState)
			{
				lines.Add($"Human {rider.name}: waiting for state · visual {(rider.visualExists ? "exists" : "none")}");
				continue;
			}
			var distance = rider.relativeDistanceMeters is { } relative ? $"{relative:+0;-0;0} m" : "—";
			var age = rider.stateAgeSeconds?.ToString("F1") ?? "—";
			var cameraView = rider.behindCamera == true ? "behind camera" :
				rider.insideCameraFrustum == true ? "in view" : "outside view";
			lines.Add($"Human {rider.name}: {distance} · age {age}s · visual {(rider.visualExists ? rider.visualVisible ? "visible" : "hidden" : "none")} · range {(rider.withinVisualRange ? "in" : "out")} · {cameraView}");
		}
		if (snapshot.ghostActivityId is not null) lines.Add(snapshot.ghostFinished == true
			? $"Ghost: Finished {snapshot.ghostRelativeDistanceMeters:+0;-0;0} m"
			: $"Ghost: {snapshot.ghostRelativeDistanceMeters:+0;-0;0} m");
		lines.Add($"Ambient: {snapshot.ambientCars} cars · {snapshot.ambientPedestrians} pedestrians");
		_debugSummaryLabel.Text = string.Join("\n", lines);
	}

	private static string TrainerLabel(string? trainer) => trainer switch { "real" => "Real FTMS", "fit_replay" => "FIT replay", "dummy" => "Dummy", _ => "Unknown" };
	private static string NearestRider(RideDebugSnapshot snapshot) => snapshot.nearestAiRiderName is null
		? "—" : $"{snapshot.nearestAiRiderName} {snapshot.nearestAiRiderRelativeDistanceMeters:+0;-0;0} m";

	public bool DebugOverlayCaptureVisible => _debugOverlay?.Visible ?? false;
	public IReadOnlyList<CapturedScreenshot> CapturedScreenshots => _screenshotCapture?.CapturedMedia ?? Array.Empty<CapturedScreenshot>();

	public Task<CapturedScreenshot?> CaptureDebugScreenshotAsync(RideDebugSnapshot snapshot) =>
		_screenshotCapture?.CaptureDebugMarkerAsync(snapshot) ?? Task.FromResult<CapturedScreenshot?>(null);

	public Task FlushScreenshotCapturesAsync() => _screenshotCapture?.FlushPendingCapturesAsync() ?? Task.CompletedTask;

	public void SetDebugOverlayForCapture(bool visible)
	{
		if (visible) EnsureDebugOverlay();
		if (visible)
		{
			_debugEnabledBeforeCapture ??= _debugEnabled;
			_debugEnabled = true;
		}
		else if (_debugEnabledBeforeCapture is { } previousDebugState)
		{
			_debugEnabled = previousDebugState;
			_debugEnabledBeforeCapture = null;
		}
		UpdateHudVisibility();
	}

	public void ShowScreenshotToast(string message)
	{
		if (_screenshotToast is null) return;
		var sequence = ++_screenshotToastSequence;
		_screenshotToast.Text = message;
		_screenshotToast.Visible = true;
		_ = HideScreenshotToastLater(sequence);
	}

	public void ShowCameraInspectionView(string view) => UpdateCameraInspectionLabel(view);

	private async Task HideScreenshotToastLater(int sequence)
	{
		await ToSignal(GetTree().CreateTimer(1.6), SceneTreeTimer.SignalName.Timeout);
		if (sequence == _screenshotToastSequence && GodotObject.IsInstanceValid(this) && _screenshotToast is not null)
			_screenshotToast.Visible = false;
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
		if (_rideSessionNode?.IsRideActive == true && key.Keycode == Key.Escape)
		{
			_rideMenuPanel?.ToggleInRideMenu();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_rideMenuPanel?.IsMenuVisible == true)
		{
			GetViewport().SetInputAsHandled();
			return;
		}
		if (key.Keycode == Key.F12 && _rideSessionNode?.IsRideActive == true)
		{
			var mode = ScreenshotCapturePolicy.ForF12(key.ShiftPressed);
			if (mode == ScreenshotCaptureMode.Hud) _ = _screenshotCapture?.CaptureHudAsync();
			else _ = _screenshotCapture?.CaptureCleanAsync();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (key.Keycode == Key.F3 && _rideSessionNode?.IsRideActive == true)
		{
			_debugEnabled = !_debugEnabled;
			UpdateHudVisibility();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (!_debugEnabled) return;
		if (key.Keycode == Key.F8 && _rideSessionNode?.IsRideActive == true)
		{
			_rideSessionNode.WriteDebugMarker();
			GetViewport().SetInputAsHandled();
		}
	}

	private void OnMenuPressed()
	{
		GameLogger.Log("hud_menu_pressed", new
		{
			rideMenuPanelFound = _rideMenuPanel is not null
		});

		if (_rideMenuPanel is null)
		{
			SetStatus("Ride menu panel not found.");
			return;
		}

		_rideMenuPanel.ShowInRideMenu();
	}

	public void FocusRideMenuAction()
	{
		var target = _menuButton is { Disabled: false } ? _menuButton : _pauseRideButton;
		if (target is { Disabled: false } && target.IsVisibleInTree())
			target.CallDeferred(Control.MethodName.GrabFocus);
	}

	public void SetModeText(string mode)
	{
		if (_modeLabel is not null)
			_modeLabel.Text = $"Mode: {mode}" + (_debugEnabled ? " [DEBUG]" : "");
		UpdateHudVisibility();
	}

	public void SetStatus(string status)
	{
		if (_statusLabel is null) return;
		if (status is "Ride paused.")
		{
			_statusLabel.Text = "PAUSED";
			return;
		}
		if (status is "Ride resumed." || status.StartsWith("Trainer session started", StringComparison.Ordinal))
		{
			_statusLabel.Text = string.Empty;
			return;
		}
		_statusLabel.Text = status;
	}

	public bool ClearStatusIfMatches(string expectedStatus)
	{
		if (_statusLabel is null || !string.Equals(_statusLabel.Text, expectedStatus, StringComparison.Ordinal))
			return false;
		_statusLabel.Text = string.Empty;
		return true;
	}

	public void ResetHud()
	{
		UpdateHudVisibility();
		SetRidePaused(false);
		SetModeText("-");
		SetStatus("Choose a mode to begin.");

		if (_routeLabel is not null) _routeLabel.Text = "Route";
		if (_segmentLabel is not null) _segmentLabel.Text = string.Empty;

		if (_powerLabel is not null) _powerLabel.Text = "—";
		if (_cadenceLabel is not null) _cadenceLabel.Text = "—";
		if (_speedLabel is not null) _speedLabel.Text = "0.0";
		if (_gradientLabel is not null) _gradientLabel.Text = "0.0%";
		if (_distanceLabel is not null) _distanceLabel.Text = "0.00";
		if (_elapsedLabel is not null) _elapsedLabel.Text = "00:00";
		_shownSpeed = _shownGradient = _shownDistance = double.NaN;
		_shownPower = _shownCadence = null;
		_shownElapsedSecond = -1;
	}

	public void SetRouteInfo(string routeName, string segmentName, double loopDistanceMeters, double routeDistanceMeters)
	{
		if (_routeLabel is not null)
		{
			_routeLabel.Text = routeName;
			_routeLabel.TooltipText = $"{routeName} · {loopDistanceMeters / 1000.0:0.00}/{routeDistanceMeters / 1000.0:0.00} km";
		}

		if (_segmentLabel is not null)
		{
			_segmentLabel.Text = segmentName;
		}
	}

	public void SetGameScore(bool enabled, int score, int collected, int available)
	{
		if (_gameScoreLabel is null) return;
		_gameScoreLabel.Visible = enabled;
		_gameScoreLabel.Text = enabled ? CollectibleUiText.Score(score, collected, available) : "";
		if (!enabled && _catFeedbackLabel is not null) _catFeedbackLabel.Visible = false;
	}

	public void SetGameSteeringHint(bool enabled) { if (_gameSteeringHint is not null) _gameSteeringHint.Visible = enabled; }

	public void ShowCatCollection(string kind, int points, int score)
	{
		if (_catFeedbackLabel is null) return;
		_catFeedbackLabel.Text = CollectibleUiText.PickupFeedback(kind, points, score);
		_catFeedbackLabel.Visible = true;
		_catFeedbackSeconds = 2;
	}

	public void UpdateFromRideState(RideState rideState, TrainerTelemetry? telemetry, double elapsedSeconds)
	{
		UpdateHudVisibility();
		var metrics = RideHudMetrics.From(rideState, telemetry, DateTimeOffset.UtcNow, elapsedSeconds);
		var powerText = metrics.PowerWatts?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
		if (_powerLabel is not null && _shownPower != powerText)
		{
			_shownPower = powerText;
			_powerLabel.Text = powerText;
		}
		var cadenceText = metrics.CadenceRpm?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
		if (_cadenceLabel is not null && _shownCadence != cadenceText)
		{
			_shownCadence = cadenceText;
			_cadenceLabel.Text = cadenceText;
		}
		if (_heartRateLabel is not null)
			_heartRateLabel.Text = metrics.HeartRateBpm?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
		var speed = Math.Round(metrics.SimulatedSpeedKph, 1);
		if (_speedLabel is not null && _shownSpeed != speed)
		{
			_shownSpeed = speed;
			_speedLabel.Text = speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
		}
		var gradient = Math.Round(metrics.GradientPercent, 1);
		if (_gradientLabel is not null && _shownGradient != gradient)
		{
			_shownGradient = gradient;
			_gradientLabel.Text = $"{gradient:+0.0;-0.0;0.0}%";
		}
		var distance = Math.Round(metrics.DistanceMeters / 1000.0, 2);
		if (_distanceLabel is not null && _shownDistance != distance)
		{
			_shownDistance = distance;
			_distanceLabel.Text = distance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
		}
		var elapsedSecond = Math.Max(0, (int)metrics.ElapsedSeconds);
		if (_elapsedLabel is not null && _shownElapsedSecond != elapsedSecond)
		{
			_shownElapsedSecond = elapsedSecond;
			_elapsedLabel.Text = TimeSpan.FromSeconds(elapsedSecond).ToString(elapsedSecond >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
		}
	}

	public void SetGhostIndicator(string? text)
	{
		if (_ghostLabel is null) return;
		_ghostLabel.Visible = !string.IsNullOrWhiteSpace(text);
		_ghostLabel.Text = text ?? string.Empty;
	}

	private void ConnectDummyControls()
	{
		if (_dummyWkgMinusPointTwo is not null) _dummyWkgMinusPointTwo.Pressed += () => _rideSessionNode?.AdjustDummyPower(-.2);
		if (_dummyWkgMinusPointOne is not null) _dummyWkgMinusPointOne.Pressed += () => _rideSessionNode?.AdjustDummyPower(-.1);
		if (_dummyWkgPlusPointOne is not null) _dummyWkgPlusPointOne.Pressed += () => _rideSessionNode?.AdjustDummyPower(.1);
		if (_dummyWkgPlusPointTwo is not null) _dummyWkgPlusPointTwo.Pressed += () => _rideSessionNode?.AdjustDummyPower(.2);
		if (_dummyWattsMinusTwentyFive is not null) _dummyWattsMinusTwentyFive.Pressed += () => _rideSessionNode?.AdjustDummyPower(-25);
		if (_dummyWattsMinusTen is not null) _dummyWattsMinusTen.Pressed += () => _rideSessionNode?.AdjustDummyPower(-10);
		if (_dummyWattsPlusTen is not null) _dummyWattsPlusTen.Pressed += () => _rideSessionNode?.AdjustDummyPower(10);
		if (_dummyWattsPlusTwentyFive is not null) _dummyWattsPlusTwentyFive.Pressed += () => _rideSessionNode?.AdjustDummyPower(25);
		if (_dummyCadenceMinusFive is not null) _dummyCadenceMinusFive.Pressed += () => _rideSessionNode?.AdjustDummyCadence(-5);
		if (_dummyCadencePlusFive is not null) _dummyCadencePlusFive.Pressed += () => _rideSessionNode?.AdjustDummyCadence(5);
	}

	public void SetDummyTrainerControls(IndoorCycling.Trainer.Implementations.FakeTrainerClient? dummy)
	{
		if (_dummyControlPanel is null) return;
		_dummyActive = dummy?.HasLiveDummyControls == true;
		if (!_dummyActive || dummy is null)
		{
			UpdateHudVisibility();
			return;
		}
		var settings = GameBootstrap.Settings.DummyTrainer;
		var watts = settings.CalculateWatts(_rideSessionNode?.ActiveRiderBodyWeightKg ?? GameBootstrap.RiderProfile.BodyWeightKg);
		_dummySummary!.Text = settings.Mode == "W/kg"
			? $"Dummy: {settings.WattsPerKg:0.0} W/kg = {watts} W · {settings.CadenceRpm} rpm"
			: $"Dummy: {watts} W · {settings.CadenceRpm} rpm";
		UpdateHudVisibility();
	}

	private void UpdateHudVisibility()
	{
		var rideIsActive = _rideSessionNode?.IsRideActive == true;
		if (_topHudPanel is not null) _topHudPanel.Visible = rideIsActive && _hudDisplayEnabled;
		if (_routeGradientInset is not null)
			_routeGradientInset.Visible = rideIsActive && (GameBootstrap.Settings?.Display.RouteGradientInsetVisible ?? true);
		var dummyVisible = rideIsActive && _dummyActive && _debugEnabled;
		if (_dummyControlPanel is not null) _dummyControlPanel.Visible = dummyVisible;
		var wkgMode = GameBootstrap.Settings?.DummyTrainer?.Mode == "W/kg";
		if (_dummyWkgPowerRow is not null) _dummyWkgPowerRow.Visible = wkgMode;
		if (_dummyWattsPowerRow is not null) _dummyWattsPowerRow.Visible = !wkgMode;
		if (_debugOverlay is not null) _debugOverlay.Visible = _debugEnabled;
		UpdateDebugOverlayLayout();
		if (_cameraInspectionLabel is not null)
		{
			if (!rideIsActive) _cameraInspectionLabelSeconds = 0;
			_cameraInspectionLabel.Visible = rideIsActive && _cameraInspectionLabelSeconds > 0;
		}
	}

	private void UpdateCameraInspectionLabel(string view)
	{
		if (_cameraInspectionLabel is null) return;
		_cameraInspectionLabel.Text = $"CAMERA · {view.ToUpperInvariant()} · F6: VIEW · SHIFT+F12: SAVE";
		_cameraInspectionLabelSeconds = 2.8;
		_cameraInspectionLabel.Visible = _rideSessionNode?.IsRideActive == true;
	}

	public override void _Process(double delta)
	{
		if (_catFeedbackLabel?.Visible == true && (_catFeedbackSeconds -= delta) <= 0) _catFeedbackLabel.Visible = false;
		if (_cameraInspectionLabelSeconds <= 0) return;
		_cameraInspectionLabelSeconds = Math.Max(0, _cameraInspectionLabelSeconds - delta);
		if (_cameraInspectionLabelSeconds == 0 && _cameraInspectionLabel is not null)
			_cameraInspectionLabel.Visible = false;
	}

	private static void ConfigureClippedLabel(Label? label)
	{
		if (label is null) return;
		label.AutowrapMode = TextServer.AutowrapMode.Off;
		label.ClipText = true;
		label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
	}
}

