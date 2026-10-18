using System;
using System.Linq;
using Godot;

/// <summary>Shared local diagnostic export panel opened from startup, trainer connection, and ride summary.</summary>
public partial class DiagnosticPanelController : Control
{
	private OptionButton _sessions = null!;
	private CheckButton _screenshots = null!;
	private Label _status = null!;
	private Button _export = null!;
	private Button _reduced = null!;
	private Button _openFolder = null!;
	private Button _copyPath = null!;
	private string? _preferredSession;
	private string? _lastPath;
	private Control? _origin;

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;
		var shade = new ColorRect { Color = new Color(0, 0, 0, .72f), MouseFilter = MouseFilterEnum.Stop };
		shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(shade);
		var panel = new PanelContainer { CustomMinimumSize = new Vector2(620, 430) };
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.OffsetLeft = -310; panel.OffsetRight = 310; panel.OffsetTop = -215; panel.OffsetBottom = 215; AddChild(panel);
		var margin = new MarginContainer(); margin.AddThemeConstantOverride("margin_left", 20); margin.AddThemeConstantOverride("margin_right", 20); margin.AddThemeConstantOverride("margin_top", 18); margin.AddThemeConstantOverride("margin_bottom", 18); panel.AddChild(margin);
		var column = new VBoxContainer(); margin.AddChild(column);
		var heading = new Label { Text = "Local diagnostics" }; heading.AddThemeFontSizeOverride("font_size", 24); column.AddChild(heading);
		column.AddChild(new Label { Text = "Choose one Wodra application session. Exports stay on this PC until you share them.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		_sessions = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill }; column.AddChild(_sessions);
		_screenshots = new CheckButton { Text = "Include debug screenshots (may show rider names)", ButtonPressed = true }; column.AddChild(_screenshots);
		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsVertical = SizeFlags.ExpandFill }; column.AddChild(_status);
		var actions = new HBoxContainer(); column.AddChild(actions);
		_export = new Button { Text = "Export selected session" }; _export.Pressed += () => _ = Export(false); actions.AddChild(_export);
		_reduced = new Button { Text = "Create reduced export", Visible = false }; _reduced.Pressed += () => _ = Export(true); actions.AddChild(_reduced);
		_openFolder = new Button { Text = "Open folder", Visible = false }; _openFolder.Pressed += OpenFolder; actions.AddChild(_openFolder);
		_copyPath = new Button { Text = "Copy path", Visible = false }; _copyPath.Pressed += CopyPath; actions.AddChild(_copyPath);
		var back = new Button { Text = "Back" }; back.Pressed += ClosePanel; actions.AddChild(back);
		Hide();
	}

	public void OpenFor(Control origin, string? sessionId = null)
	{
		_origin = origin; _preferredSession = sessionId; RefreshSessions();
		_origin.Hide(); Show(); MoveToFront();
	}

	private void RefreshSessions()
	{
		_sessions.Clear();
		var items = DiagnosticSessionExporter.ListSessions();
		var currentId = GameLogger.SessionId;
		foreach (var session in items)
		{
			var current = session.Id == currentId;
			_sessions.AddItem(session.DisplayLabel(current));
			_sessions.SetItemMetadata(_sessions.ItemCount - 1, session.Id);
			if (session.Id == (_preferredSession ?? currentId)) _sessions.Select(_sessions.ItemCount - 1);
		}
		if (_sessions.ItemCount > 0 && _sessions.GetSelectedId() < 0) _sessions.Select(0);
		_status.Text = items.Count == 0 ? "No diagnostic sessions are available." : "A full export includes the selected session logs, Godot logs, and available marker JSON. Screenshots are optional.";
		_reduced.Hide(); _openFolder.Hide(); _copyPath.Hide();
	}

	private async System.Threading.Tasks.Task Export(bool reduced)
	{
		if (_sessions.Selected < 0) return;
		var id = _sessions.GetItemMetadata(_sessions.Selected).AsString();
		_export.Disabled = true; _reduced.Disabled = true; _status.Text = reduced ? "Creating reduced export…" : "Creating full export…";
		try
		{
			var result = await DiagnosticSessionExporter.ExportAsync(id, _screenshots.ButtonPressed, reduced);
			_lastPath = result.Path;
			_status.Text = reduced && result.Success
				? "Reduced export created. Screenshots are omitted and oversized logs contain bounded tails.\n" + result.Message
				: result.Message;
			_reduced.Visible = result.RequiresReducedExport;
			_openFolder.Visible = result.Success;
			_copyPath.Visible = result.Success;
			if (result.Success && result.Path is not null)
			{
				var navigation = ExplorerNavigation.OpenAndVerify(result.Path);
				_status.Text += "\n" + navigation.Message;
			}
		}
		catch (Exception ex) { _status.Text = $"Could not export diagnostics: {ex.Message}"; }
		finally { _export.Disabled = false; _reduced.Disabled = false; }
	}

	private void OpenFolder()
	{
		if (string.IsNullOrWhiteSpace(_lastPath)) return;
		_status.Text = ExplorerNavigation.OpenFolderAndVerify(System.IO.Path.GetDirectoryName(_lastPath)!).Message;
	}
	private void CopyPath()
	{
		if (string.IsNullOrWhiteSpace(_lastPath)) return;
		DisplayServer.ClipboardSet(ExplorerNavigation.NormalizePath(_lastPath));
		_status.Text = "Export path copied to the clipboard.";
	}
	private void ClosePanel()
	{
		Hide();
		if (_origin is not null && GodotObject.IsInstanceValid(_origin) && _origin.IsInsideTree()) _origin.Show();
		_origin = null;
	}
}
