using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Media;
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
	private string? _ghostIndicatorText;
	private Label? _trackEffortLabel;
	private string? _routeEffortStatusText;
	private double _routeEffortStatusSeconds;
	private Button? _trackRetryButton;
	private Label? _multiplayerStatusLabel;
	private Label? _nearbyHumanRidersLabel;
	private string? _pacerStatusText;
	private PanelContainer? _pacerPanel;
	private bool? _showPacerPanelForRide;
	private OptionButton? _pacerPicker;
	private Label? _pacerGapLabel;
	private Action<string>? _selectPacer;
	private Action? _syncPacer;
	private Action? _stopPacer;
	public string PacerGapText => _pacerGapLabel?.Text ?? string.Empty;
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
	private PanelContainer? _achievementToastPanel;
	private Label? _achievementToastLabel;
	private Godot.Timer? _achievementToastTimer;
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
	private PanelContainer? _mediaPanel;
	private Label? _mediaTitleLabel;
	private Label? _mediaArtistLabel;
	private Button? _mediaPreviousButton;
	private Button? _mediaPlaybackButton;
	private Button? _mediaNextButton;
	private ISystemMediaSessionProvider? _mediaSessionProvider;
	private SystemMediaSessionSnapshot _mediaSnapshot = SystemMediaSessionSnapshot.Unavailable;
	private SystemMediaSessionSnapshot? _pendingMediaSnapshot;
	private CancellationTokenSource? _mediaSessionCancellation;
	private int _mediaPollInFlight;
	private int _mediaCommandInFlight;
	private int _mediaUiRefreshRequested;
	private int _mediaLifecycleGeneration;
	private double _mediaPollCountdown;
	private ISystemMediaSessionProvider? _mediaSessionProviderOverride;

	/// <summary>Injectable before entering the scene tree for deterministic production-scene review tests.</summary>
	public ISystemMediaSessionProvider? MediaSessionProviderOverride
	{
		get => _mediaSessionProviderOverride;
		set
		{
			_mediaSessionProviderOverride = value;
			if (!IsInsideTree()) return;
			var old = _mediaSessionProvider;
			Interlocked.Increment(ref _mediaLifecycleGeneration);
			_mediaSessionProvider = value ?? (OperatingSystem.IsWindows() ? new WindowsMediaSessionProvider() : null);
			_mediaSnapshot = SystemMediaSessionSnapshot.Unavailable;
			Interlocked.Exchange(ref _pendingMediaSnapshot, null);
			_mediaPollCountdown = 0;
			_mediaSessionCancellation?.Cancel();
			_mediaSessionCancellation?.Dispose();
			_mediaSessionCancellation = null;
			RefreshMediaPanel();
			CallDeferred(nameof(UpdateDebugOverlayLayout));
			if (old is not null) _ = old.DisposeAsync();
		}
	}

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
		_ghostLabel = new Label { Name = "GhostIndicator", Visible = false,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center };
		_ghostLabel.AddThemeColorOverride("font_color", new Color(.96f, .9f, .72f));
		_ghostLabel.AddThemeFontSizeOverride("font_size", 11);
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
		_trackEffortLabel = new Label { Name = "TrackEffortStatus", Visible = false,
			HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_trackEffortLabel.AddThemeColorOverride("font_color", new Color(.96f, .9f, .72f));
		_trackEffortLabel.AddThemeFontSizeOverride("font_size", 14);
		topHudLayout?.AddChild(_trackEffortLabel);
		_trackRetryButton = new Button { Name = "RetryTrackEffort", Text = "Retry effort", Visible = false,
			SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
		_trackRetryButton.Pressed += async () =>
		{
			if (_rideSessionNode is not null) await _rideSessionNode.RetryTrackEffortAsync();
		};
		topHudLayout?.AddChild(_trackRetryButton);
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
		_mediaSessionProvider = _mediaSessionProviderOverride ?? (OperatingSystem.IsWindows() ? new WindowsMediaSessionProvider() : null);
		BuildPacerPanel();
		BuildMediaPanel();
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
		BuildAchievementToast();
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
		CallDeferred(nameof(UpdateDebugOverlayLayout));
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
		UpdatePacerPanelVisibility();
		UpdateNearbyRiderDisplay();
	}

	/// <summary>Captures pacer-panel availability once when a ride starts.</summary>
	public void BeginRideHudSession(bool startedWithPacer)
	{
		_showPacerPanelForRide = startedWithPacer;
		UpdatePacerPanelVisibility();
		CallDeferred(nameof(UpdateDebugOverlayLayout));
	}

	private void BuildPacerPanel()
	{
		_pacerPanel = new PanelContainer
		{
			Name = "OfflinePacerPanel", Visible = false,
			AnchorLeft = 0, AnchorRight = 0, AnchorTop = 0, AnchorBottom = 0,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		var panelStyle = new StyleBoxFlat
		{
			BgColor = new Color(.045f, .055f, .06f, .94f),
			BorderColor = new Color(.48f, .38f, .22f, .9f),
			BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
			CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6
		};
		panelStyle.ContentMarginLeft = panelStyle.ContentMarginRight = 6;
		panelStyle.ContentMarginTop = panelStyle.ContentMarginBottom = 6;
		_pacerPanel.AddThemeStyleboxOverride("panel", panelStyle);
		var row = new HBoxContainer { Name = "PacerLayout" };
		row.AddThemeConstantOverride("separation", 6);
		var selection = new VBoxContainer { Name = "PacerSelection" };
		selection.AddThemeConstantOverride("separation", 2);
		_pacerPicker = new OptionButton
		{
			Name = "PacerSelector",
			CustomMinimumSize = new Vector2(176, 28),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			FocusMode = Control.FocusModeEnum.All
		};
		_pacerPicker.AddItem("No pacer");
		foreach (var preset in PacerEffortPresets.All)
		{
			var swatch = preset.Id switch { "easy" => "🔵", "strong" => "🔴", _ => "🟡" };
			_pacerPicker.AddItem($"{swatch} {preset.Name} · {preset.TargetWattsPerKg:0.0} W/kg");
		}
		_pacerPicker.ItemSelected += index =>
		{
			var id = index == 0 ? PacerEffortPreset.DisabledId : PacerEffortPresets.All[(int)index - 1].Id;
			_selectPacer?.Invoke(id);
		};
		selection.AddChild(_pacerPicker);
		_pacerGapLabel = new Label { Name = "PacerGap", CustomMinimumSize = new Vector2(0, 18) };
		_pacerGapLabel.AddThemeFontSizeOverride("font_size", 12);
		ConfigureClippedLabel(_pacerGapLabel);
		selection.AddChild(_pacerGapLabel);
		row.AddChild(selection);
		var actions = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		actions.AddThemeConstantOverride("separation", 2);
		var sync = new Button { Text = "Bring pacer to me", CustomMinimumSize = new Vector2(148, 28), FocusMode = Control.FocusModeEnum.All };
		sync.Pressed += () => _syncPacer?.Invoke();
		actions.AddChild(sync);
		var stop = new Button { Text = "Stop following", CustomMinimumSize = new Vector2(148, 28), FocusMode = Control.FocusModeEnum.All };
		stop.Pressed += () => _stopPacer?.Invoke();
		actions.AddChild(stop);
		row.AddChild(actions);
		_pacerPanel.AddChild(row);
		AddChild(_pacerPanel);
	}

	public void SetPacerPanel(PacerEffortPreset? preset, string gap, Action<string> select,
		Action sync, Action stop)
	{
		_selectPacer = select;
		_syncPacer = sync;
		_stopPacer = stop;
		if (_pacerPicker is not null)
		{
			var index = preset is null ? 0 : PacerEffortPresets.All.ToList().FindIndex(item => item.Id == preset.Id) + 1;
			if (_pacerPicker.Selected != index) _pacerPicker.Select(index);
		}
		if (_pacerGapLabel is not null) _pacerGapLabel.Text = preset is null ? gap : $"{preset.Name} · {gap}";
		if (_pacerPanel?.GetChild(0).GetChild(1) is VBoxContainer actions)
			foreach (var child in actions.GetChildren())
				if (child is Button button) button.Disabled = preset is null;
		UpdatePacerPanelVisibility();
		CallDeferred(nameof(UpdateDebugOverlayLayout));
	}

	private void UpdatePacerPanelVisibility()
	{
		if (_pacerPanel is not null)
			_pacerPanel.Visible = _showPacerPanelForRide == true && _hudDisplayEnabled && _rideSessionNode?.IsRideActive == true;
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
		if (IsInsideTree()) CallDeferred(nameof(UpdateDebugOverlayLayout));
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
		var topHudBottom = 0f;
		if (_topHudPanel is not null)
		{
			var minimum = _topHudPanel.GetCombinedMinimumSize();
			if (Math.Abs(_topHudPanel.Size.Y - minimum.Y) > 1)
				_topHudPanel.Size = new Vector2(_topHudPanel.Size.X, minimum.Y);
			if (_topHudPanel.Visible) topHudBottom = _topHudPanel.Position.Y + _topHudPanel.Size.Y;
		}
		ResizeBottomBar();
		if (_pacerPanel is not null) _pacerPanel.Size = _pacerPanel.GetCombinedMinimumSize();
		if (_bottomHudPanel is not null) _bottomHudPanel.Size = _bottomHudPanel.GetCombinedMinimumSize();
		if (_mediaPanel is not null) _mediaPanel.Size = _mediaPanel.GetCombinedMinimumSize();
		var layout = RideHudOverlayLayoutPolicy.ForViewport(viewport.X, viewport.Y,
			_routeGradientInset?.Visible == true,
			_pacerPanel?.Visible == true,
			_pacerPanel?.GetCombinedMinimumSize().X ?? 0,
			_pacerPanel?.GetCombinedMinimumSize().Y ?? 0,
			_dummyControlPanel?.Visible == true,
			_bottomHudPanel?.GetCombinedMinimumSize().X ?? 0,
			_bottomHudPanel?.GetCombinedMinimumSize().Y ?? 0,
			_mediaPanel?.Visible == true,
			_mediaPanel?.GetCombinedMinimumSize().X ?? 0,
			_mediaPanel?.GetCombinedMinimumSize().Y ?? 0,
			_debugOverlay?.Visible == true, topHudBottom);
		SetAnchoredBounds(_debugOverlay, layout.DebugPanel);
		SetAnchoredBounds(_routeGradientInset, layout.RouteInset);
		SetAnchoredBounds(_dummyControlPanel, layout.DummyPanel);
		if (_pacerPanel?.Visible == true) SetAnchoredBounds(_pacerPanel, layout.PacerPanel);
		SetAnchoredBounds(_bottomHudPanel, layout.BottomBar);
		if (_mediaPanel?.Visible == true) SetAnchoredBounds(_mediaPanel, layout.MediaPanel);
		PositionAchievementToast(viewport, layout);
	}

	private void PositionAchievementToast(Vector2 viewport, RideHudOverlayLayout layout)
	{
		if (_achievementToastPanel?.Visible != true) return;
		var toastWidth = 410f;
		var toastHeight = 58f;
		var left = _debugOverlay?.Visible == true
			? layout.DebugPanel.Right + RideHudOverlayLayoutPolicy.PanelGap
			: MathF.Max(RideHudOverlayLayoutPolicy.ViewportMargin, (viewport.X - toastWidth) / 2);
		if (_routeGradientInset?.Visible == true && left < layout.RouteInset.Right && left + toastWidth > layout.RouteInset.X)
			toastWidth = MathF.Min(toastWidth, MathF.Max(230, layout.RouteInset.X - left - RideHudOverlayLayoutPolicy.PanelGap));
		if (left + toastWidth > viewport.X - RideHudOverlayLayoutPolicy.ViewportMargin)
			left = MathF.Max(RideHudOverlayLayoutPolicy.ViewportMargin, (viewport.X - toastWidth) / 2);
		var top = viewport.Y - 166;
		var toast = new RideHudOverlayBounds(left, top, MathF.Min(toastWidth, viewport.X - left - RideHudOverlayLayoutPolicy.ViewportMargin), toastHeight);
		foreach (var reserved in new[]
		{
			_mediaPanel?.Visible == true ? layout.MediaPanel : default,
			_dummyControlPanel?.Visible == true ? layout.DummyPanel : default
		})
		{
			if (reserved.Width <= 0 || reserved.Height <= 0) continue;
			var intersectsHorizontally = toast.X < reserved.Right && toast.Right > reserved.X;
			if (intersectsHorizontally && toast.Bottom > reserved.Y - RideHudOverlayLayoutPolicy.PanelGap)
			{
				toast = toast with { Y = reserved.Y - RideHudOverlayLayoutPolicy.PanelGap - toast.Height };
			}
		}
		toast = toast with { Y = MathF.Max(RideHudOverlayLayoutPolicy.ViewportMargin, toast.Y) };
		SetAnchoredBounds(_achievementToastPanel, toast);
	}

	private void BuildMediaPanel()
	{
		_mediaPanel = new PanelContainer
		{
			Name = "MediaControlsPanel", Visible = false,
			CustomMinimumSize = new Vector2(RideHudOverlayLayoutPolicy.MediaPanelMinimumWidth, RideHudOverlayLayoutPolicy.MediaPanelHeight),
			MouseFilter = Control.MouseFilterEnum.Stop, ZIndex = 20
		};
		var panelStyle = new StyleBoxFlat
		{
			BgColor = new Color(.045f, .055f, .06f, .94f),
			BorderColor = new Color(.34f, .39f, .38f, .85f),
			BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
			CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6
		};
		panelStyle.ContentMarginLeft = panelStyle.ContentMarginRight = 6;
		panelStyle.ContentMarginTop = panelStyle.ContentMarginBottom = 5;
		_mediaPanel.AddThemeStyleboxOverride("panel", panelStyle);
		var row = new HBoxContainer { Name = "MediaControlsLayout", Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 4);
		var metadata = new VBoxContainer { Name = "MediaMetadata", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Alignment = BoxContainer.AlignmentMode.Center };
		metadata.AddThemeConstantOverride("separation", 1);
		_mediaTitleLabel = new Label { Name = "MediaTitle", CustomMinimumSize = new Vector2(112, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, VerticalAlignment = VerticalAlignment.Center };
		_mediaTitleLabel.AddThemeFontSizeOverride("font_size", 13);
		_mediaArtistLabel = new Label { Name = "MediaArtist", CustomMinimumSize = new Vector2(112, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, VerticalAlignment = VerticalAlignment.Center };
		_mediaArtistLabel.AddThemeFontSizeOverride("font_size", 11);
		_mediaArtistLabel.AddThemeColorOverride("font_color", new Color(.72f, .75f, .72f, 1));
		metadata.AddChild(_mediaTitleLabel);
		metadata.AddChild(_mediaArtistLabel);
		row.AddChild(metadata);
		_mediaPreviousButton = BuildMediaButton("MediaPreviousButton", "‹", "Previous track", SystemMediaCommand.Previous);
		_mediaPlaybackButton = BuildMediaButton("MediaPlaybackButton", "▶", "Play", SystemMediaCommand.TogglePlayback);
		_mediaNextButton = BuildMediaButton("MediaNextButton", "›", "Next track", SystemMediaCommand.Next);
		row.AddChild(_mediaPreviousButton);
		row.AddChild(_mediaPlaybackButton);
		row.AddChild(_mediaNextButton);
		_mediaPanel.AddChild(row);
		AddChild(_mediaPanel);
	}

	private Button BuildMediaButton(string name, string text, string tooltip, SystemMediaCommand command)
	{
		var button = new Button { Name = name, Text = text, TooltipText = tooltip,
			CustomMinimumSize = new Vector2(30, 34), FocusMode = Control.FocusModeEnum.All };
		button.AddThemeFontSizeOverride("font_size", 16);
		button.Pressed += () => QueueMediaCommand(command);
		return button;
	}

	private void UpdateMediaControls(double delta)
	{
		var enabled = GameBootstrap.Settings?.InRideMediaControlsEnabled == true &&
			_rideSessionNode?.IsRideActive == true && _hudDisplayEnabled && _mediaSessionProvider is not null;
		if (!enabled)
		{
			if (_mediaSessionCancellation is not null)
				Interlocked.Increment(ref _mediaLifecycleGeneration);
			_mediaSessionCancellation?.Cancel();
			_mediaSessionCancellation?.Dispose();
			_mediaSessionCancellation = null;
			_mediaPollCountdown = 0;
			_mediaSnapshot = SystemMediaSessionSnapshot.Unavailable;
			Interlocked.Exchange(ref _pendingMediaSnapshot, null);
			RefreshMediaPanel();
			return;
		}

		_mediaSessionCancellation ??= new CancellationTokenSource();
		var incoming = Interlocked.Exchange(ref _pendingMediaSnapshot, null);
		if (incoming is not null && incoming != _mediaSnapshot)
		{
			_mediaSnapshot = incoming;
			RefreshMediaPanel();
			CallDeferred(nameof(UpdateDebugOverlayLayout));
		}
		_mediaPollCountdown -= delta;
		if (_mediaPollCountdown <= 0)
		{
			_mediaPollCountdown = 1.0;
			if (Interlocked.CompareExchange(ref _mediaPollInFlight, 1, 0) == 0)
				_ = ReadMediaSnapshotAsync(_mediaSessionCancellation.Token, Volatile.Read(ref _mediaLifecycleGeneration));
		}
	}

	private async Task ReadMediaSnapshotAsync(CancellationToken cancellationToken, int generation)
	{
		try
		{
			var snapshot = await _mediaSessionProvider!.ReadCurrentAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(1200));
			if (!cancellationToken.IsCancellationRequested && generation == Volatile.Read(ref _mediaLifecycleGeneration))
				Interlocked.Exchange(ref _pendingMediaSnapshot, snapshot);
		}
		catch
		{
			if (!cancellationToken.IsCancellationRequested && generation == Volatile.Read(ref _mediaLifecycleGeneration))
				Interlocked.Exchange(ref _pendingMediaSnapshot, SystemMediaSessionSnapshot.Unavailable);
		}
		finally { Interlocked.Exchange(ref _mediaPollInFlight, 0); }
	}

	private void RefreshMediaPanel()
	{
		if (_mediaPanel is null) return;
		var visible = _mediaSnapshot.IsAvailable && GameBootstrap.Settings?.InRideMediaControlsEnabled == true &&
			_rideSessionNode?.IsRideActive == true && _hudDisplayEnabled;
		_mediaPanel.Visible = visible;
		if (!visible)
		{
			_mediaTitleLabel!.Text = string.Empty;
			_mediaTitleLabel.TooltipText = string.Empty;
			_mediaArtistLabel!.Text = string.Empty;
			_mediaArtistLabel.TooltipText = string.Empty;
			_mediaPreviousButton!.Visible = _mediaPlaybackButton!.Visible = _mediaNextButton!.Visible = false;
			return;
		}

		var hasTitle = !string.IsNullOrWhiteSpace(_mediaSnapshot.Title);
		_mediaTitleLabel!.Text = hasTitle ? _mediaSnapshot.Title! : _mediaSnapshot.Artist ?? "Media session";
		_mediaTitleLabel.TooltipText = hasTitle ? _mediaSnapshot.Title! : _mediaSnapshot.Artist ?? string.Empty;
		var state = _mediaSnapshot.PlaybackState == SystemMediaPlaybackState.Paused ? "Paused" :
			_mediaSnapshot.PlaybackState == SystemMediaPlaybackState.Playing ? "Playing" : "Ready";
		_mediaArtistLabel!.Text = !hasTitle || string.IsNullOrWhiteSpace(_mediaSnapshot.Artist)
			? state : $"{_mediaSnapshot.Artist} · {state}";
		_mediaArtistLabel.TooltipText = _mediaSnapshot.Artist ?? string.Empty;
		var commandBusy = Volatile.Read(ref _mediaCommandInFlight) != 0;
		_mediaPreviousButton!.Visible = _mediaSnapshot.CanSkipPrevious;
		_mediaPreviousButton.Disabled = commandBusy || !_mediaSnapshot.CanSkipPrevious;
		_mediaNextButton!.Visible = _mediaSnapshot.CanSkipNext;
		_mediaNextButton.Disabled = commandBusy || !_mediaSnapshot.CanSkipNext;
		_mediaPlaybackButton!.Visible = _mediaSnapshot.CanTogglePlayback;
		_mediaPlaybackButton.Disabled = commandBusy || !_mediaSnapshot.CanTogglePlayback;
		var pause = _mediaSnapshot.PlaybackState == SystemMediaPlaybackState.Playing;
		_mediaPlaybackButton.Text = pause ? "Ⅱ" : "▶";
		_mediaPlaybackButton.TooltipText = pause ? "Pause" : "Play";
	}

	private void QueueMediaCommand(SystemMediaCommand command)
	{
		if (_mediaSessionProvider is null || !GameBootstrap.Settings.InRideMediaControlsEnabled ||
			Interlocked.CompareExchange(ref _mediaCommandInFlight, 1, 0) != 0) return;
		RefreshMediaPanel();
		_ = ExecuteMediaCommandAsync(command, _mediaSessionCancellation?.Token ?? CancellationToken.None);
	}

	private async Task ExecuteMediaCommandAsync(SystemMediaCommand command, CancellationToken cancellationToken)
	{
		try { _ = await _mediaSessionProvider!.TryExecuteAsync(command, cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(1200)); }
		catch { }
		finally
		{
			Interlocked.Exchange(ref _mediaCommandInFlight, 0);
			Interlocked.Exchange(ref _mediaUiRefreshRequested, 1);
		}
	}

	private void ResizeBottomBar()
	{
		if (_bottomHudPanel is null) return;
		var size = _bottomHudPanel.GetCombinedMinimumSize();
		_bottomHudPanel.AnchorLeft = _bottomHudPanel.AnchorRight = 0;
		_bottomHudPanel.AnchorTop = _bottomHudPanel.AnchorBottom = 1;
		_bottomHudPanel.OffsetLeft = RideHudOverlayLayoutPolicy.ViewportMargin;
		_bottomHudPanel.OffsetTop = -RideHudOverlayLayoutPolicy.BottomPanelMargin - size.Y;
		_bottomHudPanel.OffsetRight = RideHudOverlayLayoutPolicy.ViewportMargin + size.X;
		_bottomHudPanel.OffsetBottom = -RideHudOverlayLayoutPolicy.BottomPanelMargin;
		_bottomHudPanel.Size = size;
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
		_achievementToastTimer?.Stop();
		_achievementToastPanel?.Hide();
		_showPacerPanelForRide = null;
		_pacerPanel?.Hide();
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

	private void BuildAchievementToast()
	{
		_achievementToastPanel = new PanelContainer
		{
			Name = "AchievementNotification",
			Visible = false,
			CustomMinimumSize = new Vector2(230, 58),
			AnchorLeft = .5f, AnchorRight = .5f, AnchorTop = 1, AnchorBottom = 1,
			OffsetLeft = -205, OffsetRight = 205, OffsetTop = -166, OffsetBottom = -108,
			MouseFilter = Control.MouseFilterEnum.Stop,
			ZIndex = 80
		};
		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		_achievementToastLabel = new Label { Name = "AchievementNotificationText", AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
		row.AddChild(_achievementToastLabel);
		var dismiss = new Button { Name = "DismissAchievementNotification", Text = "×", CustomMinimumSize = new Vector2(34, 34) };
		dismiss.Pressed += DismissAchievementNotification;
		row.AddChild(dismiss); _achievementToastPanel.AddChild(row); AddChild(_achievementToastPanel);
		_achievementToastTimer = new Godot.Timer { OneShot = true, WaitTime = 6 };
		_achievementToastTimer.Timeout += DismissAchievementNotification;
		AddChild(_achievementToastTimer);
	}

	public void ShowAchievementNotification(string message)
	{
		if (!GameFeaturesPreference.Enabled || string.IsNullOrWhiteSpace(message) || _achievementToastPanel is null) return;
		_achievementToastLabel!.Text = $"Badge earned\n{message}";
		_achievementToastPanel.Show(); _achievementToastPanel.MoveToFront();
		UpdateDebugOverlayLayout();
		_achievementToastTimer?.Start(6);
	}

	private void DismissAchievementNotification()
	{
		_achievementToastTimer?.Stop(); _achievementToastPanel?.Hide();
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

	public void SetGameScore(bool enabled, int score, int collected, int available,
		int featureLinesCompleted = 0, bool trailCourse = false)
	{
		if (_gameScoreLabel is null) return;
		_gameScoreLabel.Visible = enabled;
		_gameScoreLabel.Text = enabled ? trailCourse
			? $"Trail score · {score} pts · {featureLinesCompleted}/3 lines · {collected}/{available} tokens"
			: CollectibleUiText.Score(score, collected, available) : "";
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

	public void ShowTrailFeatureFeedback(string name, int points, int score)
	{
		if (_catFeedbackLabel is null) return;
		_catFeedbackLabel.Text = $"{name} +{points} · {score} trail points";
		_catFeedbackLabel.Visible = true;
		_catFeedbackSeconds = 2.5;
	}

	public void ShowTrailLapSummary(int lap, int lines, int score)
	{
		if (_catFeedbackLabel is null) return;
		_catFeedbackLabel.Text = $"Lap {lap} · {lines}/3 jump lines · {score} trail points";
		_catFeedbackLabel.Visible = true;
		_catFeedbackSeconds = 4;
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
		var next = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
		if (string.Equals(_ghostIndicatorText, next, StringComparison.Ordinal)) return;
		_ghostIndicatorText = next;
		RefreshRouteEffortIndicator();
	}

	public void SetRouteEffortStatus(string? text, bool active)
	{
		var next = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
		var changed = !string.Equals(_routeEffortStatusText, next, StringComparison.Ordinal);
		_routeEffortStatusText = next;
		_routeEffortStatusSeconds = _routeEffortStatusText is null ? 0 : active ? double.PositiveInfinity : 5.0;
		if (changed) RefreshRouteEffortIndicator();
	}

	private void RefreshRouteEffortIndicator()
	{
		if (_ghostLabel is null) return;
		var lines = new[] { _ghostIndicatorText, _routeEffortStatusText }
			.Where(value => !string.IsNullOrWhiteSpace(value));
		_ghostLabel.Text = string.Join("\n", lines);
		_ghostLabel.Visible = !string.IsNullOrWhiteSpace(_ghostLabel.Text);
		UpdateDebugOverlayLayout();
	}

	public void SetTrackEffortStatus(string? text, bool completed = false)
	{
		if (_trackEffortLabel is null) return;
		_trackEffortLabel.Visible = !string.IsNullOrWhiteSpace(text);
		_trackEffortLabel.Text = text ?? string.Empty;
		if (_segmentLabel is not null) _segmentLabel.Visible = !completed;
		if (_trackRetryButton is not null) _trackRetryButton.Visible = completed;
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
		UpdatePacerPanelVisibility();
		UpdateDebugOverlayLayout();
		RefreshMediaPanel();
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
		if (_routeEffortStatusText is not null && !double.IsPositiveInfinity(_routeEffortStatusSeconds) &&
			(_routeEffortStatusSeconds -= delta) <= 0) SetRouteEffortStatus(null, false);
		UpdateMediaControls(delta);
		if (Interlocked.Exchange(ref _mediaUiRefreshRequested, 0) != 0)
		{
			RefreshMediaPanel();
			CallDeferred(nameof(UpdateDebugOverlayLayout));
		}
		if (_catFeedbackLabel?.Visible == true && (_catFeedbackSeconds -= delta) <= 0) _catFeedbackLabel.Visible = false;
		if (_cameraInspectionLabelSeconds > 0) _cameraInspectionLabelSeconds = Math.Max(0, _cameraInspectionLabelSeconds - delta);
		if (_cameraInspectionLabelSeconds == 0 && _cameraInspectionLabel is not null)
			_cameraInspectionLabel.Visible = false;
	}

	public override void _ExitTree()
	{
		Interlocked.Increment(ref _mediaLifecycleGeneration);
		_mediaSessionCancellation?.Cancel();
		_mediaSessionCancellation?.Dispose();
		_mediaSessionCancellation = null;
		if (_mediaSessionProvider is not null) _ = _mediaSessionProvider.DisposeAsync();
	}

	private static void ConfigureClippedLabel(Label? label)
	{
		if (label is null) return;
		label.AutowrapMode = TextServer.AutowrapMode.Off;
		label.ClipText = true;
		label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
	}
}

