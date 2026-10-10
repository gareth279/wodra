using System;
using System.Threading.Tasks;
using Godot;

public partial class RideMenuPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;

	private RouteSelectionPanelController? _routeSelectionPanel;
	private RideHistoryPanelController? _rideHistoryPanel;
	private Button? _keepRidingButton;
	private Button? _endRideButton;
	private bool _wasPausedBeforeConfirmation;
	private bool _endingRide;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_rideHistoryPanel = GetNodeOrNull<RideHistoryPanelController>("../RideHistoryPanel");

		_keepRidingButton = GetNode<Button>("PanelContainer/VBoxContainer/KeepRidingButton");
		_endRideButton = GetNode<Button>("PanelContainer/VBoxContainer/EndRideButton");
		_keepRidingButton.Pressed += OnKeepRidingButtonPressed;
		_endRideButton.Pressed += OnEndRideButtonPressed;

		Hide();
	}

	public void ShowEndRideConfirmation()
	{
		if (_rideSessionNode?.IsRideActive != true || _endingRide) return;
		_wasPausedBeforeConfirmation = _rideSessionNode.IsRidePaused;
		if (!_wasPausedBeforeConfirmation)
			_rideSessionNode.ToggleRidePause();
		_keepRidingButton!.Disabled = false;
		_endRideButton!.Disabled = false;
		Show();
		MoveToFront();
		GameLogger.Log("end_ride_confirmation_shown", new { wasPaused = _wasPausedBeforeConfirmation });
	}

	private void OnKeepRidingButtonPressed()
	{
		Hide();
		if (!_wasPausedBeforeConfirmation && _rideSessionNode?.IsRideActive == true && _rideSessionNode.IsRidePaused)
			_rideSessionNode.ResumeRide();
	}

	private void OnEndRideButtonPressed()
	{
		_ = EndRideAndShowSummaryAsync();
	}

	public async Task EndRideAndShowSummaryAsync()
	{
		if (_endingRide || _rideSessionNode?.IsRideActive != true) return;
		_endingRide = true;
		_keepRidingButton!.Disabled = true;
		_endRideButton!.Disabled = true;
		try { await _rideSessionNode.ExitToRouteSelectionAsync(); }
		catch (Exception ex)
		{
			_endingRide = false;
			_keepRidingButton.Disabled = false;
			_endRideButton.Disabled = false;
			GD.PushWarning($"Could not end ride: {ex.Message}");
			return;
		}
		_endingRide = false;
		Hide();
		_hudController?.Hide();
		if (_rideSessionNode?.LastCompletedActivityDraft is { } draft)
		{
			_rideHistoryPanel?.ShowCompletedSummary(draft, _rideSessionNode.LastCompletedActivityId,
				_rideSessionNode.LastRecordingError);
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

}
