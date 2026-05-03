using Godot;

public partial class RouteSelectionPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _deviceConnectionPanel;
	private Control? _startPanel;

	private Button? _testRouteButton;
	private Button? _backButton;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_deviceConnectionPanel = GetNodeOrNull<Control>("../DeviceConnectionPanel");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");

		_testRouteButton = GetNodeOrNull<Button>("PanelContainer/MarginContainer/VBoxContainer/TestRouteButton");
		_backButton = GetNodeOrNull<Button>("PanelContainer/MarginContainer/VBoxContainer/BackButton");

		if (_testRouteButton is not null)
			_testRouteButton.Pressed += OnTestRoutePressed;

		if (_backButton is not null)
			_backButton.Pressed += OnBackPressed;

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

	private async void OnTestRoutePressed()
	{
		if (_rideSessionNode is null)
			return;

		Hide();
		_hudController?.Show();

		await _rideSessionNode.StartTrainerSessionAsync();
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
