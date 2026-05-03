using System.Threading.Tasks;
using Godot;

public partial class RideMenuPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;

	private RouteSelectionPanelController? _routeSelectionPanel;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");

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

	private void OnQuitButtonPressed()
	{
		GetTree().Quit();
	}
}
