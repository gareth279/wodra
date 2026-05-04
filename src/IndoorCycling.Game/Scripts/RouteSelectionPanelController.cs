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

		foreach (var child in _routeButtonsContainer.GetChildren())
		{
			child.QueueFree();
		}

		foreach (var route in RouteCatalog.Routes)
		{
			var button = new Button
			{
				Text = $"{route.Name} - {route.DistanceMeters / 1000.0:0.0} km"
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
