using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Windows;

/// <summary>Reusable ANT+ network-key and receiver setup for Settings and trainer connection.</summary>
public partial class AntNetworkSetupPanel : VBoxContainer
{
	internal Func<bool>? ConfigurationInUseForTesting { get; set; }
	internal Func<string>? StatusForTesting { get; set; }
	internal Action<string>? SaveForTesting { get; set; }
	internal Action? ClearForTesting { get; set; }
	internal Func<Task>? InitializeForTesting { get; set; }
	internal LineEdit? KeyInputForTesting => _keyInput;
	internal Label? StatusForTestingLabel => _status;
	internal Button? SaveForTestingButton => _saveButton;
	internal Button? ClearForTestingButton => _clearButton;
	internal Button? InitializeForTestingButton => _initializeButton;

	public Func<bool>? IsConfigurationInUse { get; set; }

	private LineEdit? _keyInput;
	private Label? _status;
	private Button? _saveButton;
	private Button? _clearButton;
	private Button? _initializeButton;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 7);
		AddChild(new Label
		{
			Text = "Enter the ANT+ network key as 16 hexadecimal digits or eight byte values. It stays in this Windows account's private app-data storage.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		});
		_status = new Label { Name = "AntNetworkStatus", Text = GetStatus(), AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddChild(_status);
		_keyInput = new LineEdit
		{
			Name = "AntNetworkKeyInput",
			PlaceholderText = "16 hexadecimal digits or eight byte values",
			ClearButtonEnabled = true,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Secret = true
		};
		AddChild(_keyInput);
		var actions = new HBoxContainer { Name = "AntNetworkActions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_saveButton = new Button { Name = "SaveAntNetworkKey", Text = "Save / replace key", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_clearButton = new Button { Name = "ClearAntNetworkKey", Text = "Clear key", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var help = new Button { Text = "Garmin key page", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_initializeButton = new Button { Name = "InitializeAntNetwork", Text = "Check receiver and initialize ANT+", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		actions.AddChild(_saveButton);
		actions.AddChild(_clearButton);
		actions.AddChild(help);
		AddChild(actions);
		AddChild(_initializeButton);
		AddChild(new Label
		{
			Text = "ANT+ FE-C can be selected without a receiver or key. To connect, configure the 64-bit network key, connect an ANTUSB-m receiver with its Windows driver, then wake a compatible FE-C trainer nearby.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		});

		_saveButton.Pressed += SaveKey;
		_clearButton.Pressed += ClearKey;
		help.Pressed += () => OS.ShellOpen(AntNetworkKeyMaterial.GarminNetworkKeysUrl);
		_initializeButton.Pressed += () => _ = InitializeNetworkAsync();
	}

	private string GetStatus() => StatusForTesting?.Invoke() ?? AntNetworkKeyMaterial.GetStatus();
	private bool IsInUse() => ConfigurationInUseForTesting?.Invoke() ?? IsConfigurationInUse?.Invoke() == true;

	private void SaveKey()
	{
		if (IsInUse()) { _status!.Text = "Disconnect ANT+ before changing its setup."; return; }
		try
		{
			if (SaveForTesting is { } save) save(_keyInput!.Text);
			else AntNetworkKeyMaterial.Save(_keyInput!.Text);
			_keyInput.Clear();
			_status!.Text = "ANT+ network key saved. You can replace it at any time when ANT+ is disconnected.";
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException)
		{ _status!.Text = ex.Message; }
	}

	private void ClearKey()
	{
		if (IsInUse()) { _status!.Text = "Disconnect ANT+ before clearing its setup."; return; }
		try
		{
			if (ClearForTesting is { } clear) clear();
			else AntNetworkKeyMaterial.Clear();
			_keyInput!.Clear();
			_status!.Text = GetStatus();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SecurityException)
		{ _status!.Text = ex.Message; }
	}

	private async Task InitializeNetworkAsync()
	{
		if (IsInUse())
		{
			_status!.Text = "Disconnect ANT+ before reinitializing its network.";
			return;
		}
		_initializeButton!.Disabled = true;
		_status!.Text = "Checking the ANT+ receiver and network key…";
		try
		{
			if (InitializeForTesting is { } initialize) await initialize();
			else await AntFeCTrainerClient.InitializeNetworkAsync(diagnostic: diagnostic => GameLogger.Log("ant_diagnostic", diagnostic));
			_status!.Text = "Receiver opened and ANT+ network initialized. Connect an awake FE-C trainer next.";
		}
		catch (Exception ex)
		{
			GameLogger.Log("ant_network_initialization_ui_failed", new { exceptionType = ex.GetType().Name, message = ex.Message });
			_status!.Text = ex.Message;
		}
		finally { _initializeButton.Disabled = false; }
	}
}
