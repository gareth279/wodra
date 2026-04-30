using System.Threading.Tasks;
using Godot;

public partial class RideMenuPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");

		Hide();
	}

	public void ShowMenu()
	{
		Show();
	}

	private void OnResumeButtonPressed()
	{
		Hide();
	}

	private async void OnBackToMenuButtonPressed()
	{
		if (_rideSessionNode is not null)
		{
			await _rideSessionNode.ExitToMainMenuAsync();
		}

		Hide();
		_hudController?.Hide();
		_startPanel?.Show();
	}

	private void OnQuitButtonPressed()
	{
		GetTree().Quit();
	}
}
