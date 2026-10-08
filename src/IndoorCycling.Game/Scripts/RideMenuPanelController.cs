using System.Threading.Tasks;
using Godot;

public partial class RideMenuPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;

	private RouteSelectionPanelController? _routeSelectionPanel;
	private RideHistoryPanelController? _rideHistoryPanel;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_rideHistoryPanel = GetNodeOrNull<RideHistoryPanelController>("../RideHistoryPanel");

		GetNode<Button>("PanelContainer/VBoxContainer/ResumeButton").Pressed += OnResumeButtonPressed;
		GetNode<Button>("PanelContainer/VBoxContainer/BackToMenuButton").Pressed += OnBackToMenuButtonPressed;
		GetNode<Button>("PanelContainer/VBoxContainer/QuitButton").Pressed += OnQuitButtonPressed;

		Hide();
	}

	public void ShowMenu()
	{
		Show();
		MoveToFront();
		GameLogger.Log("ride_menu_shown", new { });
	}
	private void OnResumeButtonPressed()
	{
		_rideSessionNode?.ResumeRide();
		Hide();
	}

	private async void OnBackToMenuButtonPressed()
	{
		if (_rideSessionNode is not null)
		{
			await _rideSessionNode.ExitToRouteSelectionAsync();
		}

		Hide();
		_hudController?.Hide();
		if (_rideSessionNode?.LastCompletedActivityDraft is { } draft)
		{
			_rideHistoryPanel?.ShowCompletedSummary(draft, _rideSessionNode.LastCompletedActivityId);
			return;
		}
		_startPanel?.Show();
		if (GameBootstrap.TrainerClient is not null)
		{
			_routeSelectionPanel?.ShowRouteSelection();
		}
		else
		{
			_startPanel?.Show();
		}
	}

	private async void OnQuitButtonPressed()
	{
		if (_rideSessionNode is not null)
			await _rideSessionNode.QuitAsync();
	}
}
