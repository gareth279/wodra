using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public partial class RouteSelectionPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _deviceConnectionPanel;
	private Control? _startPanel;

	private VBoxContainer? _routeButtonsContainer;
	private Button? _backButton;
	private CheckButton? _onlineRidingToggle;
	private Label? _onlineStatusLabel;
	private VBoxContainer? _startupOnlineControls;
	private readonly Dictionary<string, Button> _routeButtons = new(StringComparer.Ordinal);
	private CancellationTokenSource? _routePresenceCancellation;
	private int _routePresenceGeneration;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_deviceConnectionPanel = GetNodeOrNull<Control>("../DeviceConnectionPanel");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");

		_routeButtonsContainer = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer/RouteButtonsContainer");
		_backButton = GetNodeOrNull<Button>("PanelContainer/MarginContainer/VBoxContainer/BackButton");
		BuildOnlineControls();

		if (_backButton is not null)
			_backButton.Pressed += OnBackPressed;

		BuildRouteButtons();

		Hide();
	}

	public void ShowRouteSelection()
	{
		_hudController?.Hide();
		_deviceConnectionPanel?.Hide();
		_startPanel?.Hide();

		Show();
		MoveToFront();
		_ = RefreshStartupOnlineStatusAsync();
	}

	private void BuildOnlineControls()
	{
		var column = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer");
		if (column is null) return;
		_startupOnlineControls = new VBoxContainer { Name = "OnlineRidingControls" };
		_onlineRidingToggle = new CheckButton
		{
			Text = "Online Riding",
			ButtonPressed = GameBootstrap.Settings.OnlineRidingEnabled
		};
		_onlineRidingToggle.Toggled += OnOnlineRidingToggled;
		_startupOnlineControls.AddChild(_onlineRidingToggle);
		_onlineStatusLabel = new Label
		{
			Name = "OnlineServiceStatus",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			ClipText = true,
			Modulate = new Color(0.78f, 0.78f, 0.72f, 0.9f)
		};
		_onlineStatusLabel.AddThemeFontSizeOverride("font_size", 14);
		_startupOnlineControls.AddChild(_onlineStatusLabel);
		column.AddChild(_startupOnlineControls);
		column.MoveChild(_startupOnlineControls, 1);
	}

	public override void _Process(double delta) => UpdateOnlineStatusLabel();

	private void UpdateOnlineStatusLabel()
	{
		if (_onlineStatusLabel is null || _rideSessionNode is null) return;
		_onlineStatusLabel.Text = !_rideSessionNode.OnlineRidingEnabled
			? "Online Riding: Off"
			: _rideSessionNode.IsMultiplayerConnected
				? "Online"
				: _rideSessionNode.OnlineServiceAvailable == true
					? "Online ready"
					: _rideSessionNode.OnlineServiceAvailable == false
						? OnlineStatusText.ForStartup(true, false, false,
							EpicOnlineServicesMultiplayerProvider.HasRequiredConfiguration,
							OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider),
							EosNativeLibraryLoader.LoadFailed,
							EpicOnlineServicesMultiplayerProvider.ConfigurationInvalid)
						: OnlineStatusText.ForStartup(true, false, null,
							EpicOnlineServicesMultiplayerProvider.HasRequiredConfiguration,
							OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider),
							EosNativeLibraryLoader.LoadFailed,
							EpicOnlineServicesMultiplayerProvider.ConfigurationInvalid);
	}

	private async void OnOnlineRidingToggled(bool enabled)
	{
		if (_rideSessionNode is null || _onlineRidingToggle is null) return;
		_onlineRidingToggle.Disabled = true;
		_routePresenceCancellation?.Cancel();
		try { await _rideSessionNode.SetOnlineRidingEnabledAsync(enabled); }
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_startup_toggle_failed", new { message = ex.Message });
			_onlineStatusLabel!.Text = "Online unavailable";
		}
		finally
		{
			_onlineRidingToggle.Disabled = false;
			UpdateOnlineStatusLabel();
			_ = RefreshRoutePresenceAsync();
		}
	}

	private async Task RefreshStartupOnlineStatusAsync()
	{
		if (_rideSessionNode is null || !_rideSessionNode.OnlineRidingEnabled) return;
		try
		{
			await _rideSessionNode.RefreshOnlineAvailabilityAsync();
			UpdateOnlineStatusLabel();
			await RefreshRoutePresenceAsync();
		}
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_startup_availability_failed", new { message = ex.Message });
			_onlineStatusLabel!.Text = "Online unavailable";
		}
	}

	private async Task RefreshRoutePresenceAsync()
	{
		_routePresenceCancellation?.Cancel();
		_routePresenceCancellation?.Dispose();
		var generation = ++_routePresenceGeneration;
		if (_rideSessionNode is null) return;
		if (!_rideSessionNode.OnlineRidingEnabled)
		{
			SetRoutePresenceSuffix("online off");
			return;
		}
		_routePresenceCancellation = new CancellationTokenSource();
		var token = _routePresenceCancellation.Token;
		SetRoutePresenceSuffix("checking…");
		var available = _rideSessionNode.OnlineServiceAvailable ??
			await _rideSessionNode.RefreshOnlineAvailabilityAsync(token);
		if (token.IsCancellationRequested || generation != _routePresenceGeneration) return;
		if (!available)
		{
			SetRoutePresenceSuffix("presence unavailable");
			return;
		}
		var routes = RouteCatalog.Routes.ToArray();
		var results = await Task.WhenAll(routes.Select(async route =>
			(route.Id, Result: await _rideSessionNode.GetRoutePresenceAsync(route, token))));
		if (token.IsCancellationRequested || generation != _routePresenceGeneration) return;
		foreach (var route in routes)
		{
			var result = results.First(item => item.Id == route.Id).Result;
			if (_routeButtons.TryGetValue(route.Id, out var button))
				button.Text = RouteButtonText(route, result.IsAvailable
					? result.ActiveRiders is { } count ? $"{count} riding" : "rider count unavailable"
					: "presence unavailable");
		}
	}

	private void SetRoutePresenceSuffix(string suffix)
	{
		foreach (var route in RouteCatalog.Routes)
			if (_routeButtons.TryGetValue(route.Id, out var button)) button.Text = RouteButtonText(route, suffix);
	}

	private static string RouteButtonText(RouteDefinition route, string? presence = null) =>
		$"{route.Name} · {route.DistanceMeters / 1000.0:0.0} km · {route.RouteType} · {route.Difficulty} · {route.TotalAscentMeters:0} m ↑" +
		(presence is null ? "" : $" · {presence}");

	private void BuildRouteButtons()
	{
		if (_routeButtonsContainer is null)
			return;
		foreach (var error in RouteCatalog.PackageErrors)
			GD.PushWarning($"Route package ignored: {error}");

		foreach (var child in _routeButtonsContainer.GetChildren())
		{
			child.QueueFree();
		}
		_routeButtons.Clear();

		foreach (var route in RouteCatalog.Routes)
		{
			var button = new Button
			{
				Text = RouteButtonText(route),
				TooltipText = $"Route ID: {route.Id}\n{route.SourceAttribution}\nRoute data version: {route.RouteDataVersion}\nGeometry SHA-256: {route.GeometryFingerprint}",
				CustomMinimumSize = new Vector2(0, 58)
			};

			button.Pressed += () => OnRouteSelected(route);

			_routeButtonsContainer.AddChild(button);
			_routeButtons[route.Id] = button;
		}
		if (GameBootstrap.Settings.OnlineRidingEnabled) _ = RefreshRoutePresenceAsync();
	}

	private async void OnRouteSelected(RouteDefinition route)
	{
		if (_rideSessionNode is null)
			return;
		_routePresenceCancellation?.Cancel();

		GameLogger.Log("route_selected", new
		{
			route.Id,
			route.Name,
			route.DistanceMeters
		});

		Hide();
		_hudController?.Show();

		var started = await _rideSessionNode.StartTrainerSessionAsync(route);
		if (!started)
		{
			_hudController?.Hide();
			(_deviceConnectionPanel as DeviceConnectionPanelController)?.ShowTrainerConnectionScreen();
		}
	}

	private void OnBackPressed()
	{
		_routePresenceCancellation?.Cancel();
		_rideSessionNode?.ClearPendingGhost();
		Hide();

		if (GameBootstrap.TrainerClient is not null)
		{
			_deviceConnectionPanel?.Show();
		}
		else
		{
			_startPanel?.Show();
		}
	}

	public override void _ExitTree()
	{
		_routePresenceCancellation?.Cancel();
		_routePresenceCancellation?.Dispose();
		_routePresenceCancellation = null;
	}
}
