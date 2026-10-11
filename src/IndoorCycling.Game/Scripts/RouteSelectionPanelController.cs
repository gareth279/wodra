using Godot;

public partial class RouteSelectionPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _deviceConnectionPanel;
	private Control? _startPanel;

	private VBoxContainer? _routeButtonsContainer;
	private Button? _backButton;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_deviceConnectionPanel = GetNodeOrNull<Control>("../DeviceConnectionPanel");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");

		_routeButtonsContainer = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer/RouteButtonsContainer");
		_backButton = GetNodeOrNull<Button>("PanelContainer/MarginContainer/VBoxContainer/BackButton");

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
	}

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

		foreach (var route in RouteCatalog.Routes)
		{
			var button = new Button
			{
				Text = $"{route.Name} · {route.DistanceMeters / 1000.0:0.0} km · {route.RouteType} · {route.Difficulty} · {route.TotalAscentMeters:0} m ↑",
				TooltipText = $"Route ID: {route.Id}\n{route.SourceAttribution}\nRoute data version: {route.RouteDataVersion}\nGeometry SHA-256: {route.GeometryFingerprint}",
				CustomMinimumSize = new Vector2(0, 58)
			};

			button.Pressed += () => OnRouteSelected(route);

			_routeButtonsContainer.AddChild(button);
		}
	}

	private async void OnRouteSelected(RouteDefinition route)
	{
		if (_rideSessionNode is null)
			return;

		GameLogger.Log("route_selected", new
		{
			route.Id,
			route.Name,
			route.DistanceMeters
		});

		Hide();
		_hudController?.Show();

		await _rideSessionNode.StartTrainerSessionAsync(route);
	}

	private void OnBackPressed()
	{
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
}
