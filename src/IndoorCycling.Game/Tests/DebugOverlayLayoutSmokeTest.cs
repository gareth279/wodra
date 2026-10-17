using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;

/// <summary>Rendered viewport smoke coverage for the bounded in-ride debug and dummy overlays.</summary>
public partial class DebugOverlayLayoutSmokeTest : Node
{
	private static readonly Vector2I[] ViewportSizes =
	[
		new(960, 540), new(1152, 648), new(1366, 768), new(1920, 1080)
	];
	private readonly List<string> _failures = [];
	private int _screenshots;

	public override async void _Ready()
	{
		try
		{
			var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", "debug-overlay-alpha-polish");
			Directory.CreateDirectory(output);
			foreach (var size in ViewportSizes)
			foreach (var riderCount in new[] { 0, 1, 2, 12 })
			foreach (var dummyMode in new[] { false, true })
			foreach (var insetVisible in new[] { false, true })
			{
				var viewport = new SubViewport
				{
					Name = "OverlayPreview",
					Size = size,
					Disable3D = true,
					RenderTargetUpdateMode = SubViewport.UpdateMode.Always
				};
				AddChild(viewport);
				var background = new ColorRect { Color = new Color(.06f, .075f, .08f, 1) };
				background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
				viewport.AddChild(background);
				var hud = BuildHud();
				viewport.AddChild(hud);
				hud.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
				hud.SetDebugMode(true);
				hud.SetRecordingState(true, false);
				hud.UpdateDebugOverlay(CreateSnapshot(riderCount, dummyMode));

				var inset = hud.GetNode<Control>("RouteGradientInset");
				inset.SetProcess(false);
				inset.Visible = insetVisible;
				var insetCard = new PanelContainer { Name = "RenderedInsetPlaceholder" };
				insetCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
				insetCard.AddChild(new Label
				{
					Text = "ROUTE  ·  N ↑\nElevation profile and rider position",
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
					AutowrapMode = TextServer.AutowrapMode.WordSmart,
					MouseFilter = Control.MouseFilterEnum.Ignore
				});
				inset.AddChild(insetCard);
				var dummy = hud.GetNode<Control>("DummyControlPanel");
				dummy.Visible = dummyMode;
				ApplyRuntimeLayout(hud);
				await Settle();

				CheckRuntimeBounds(hud, inset, dummy, viewport, size, riderCount, dummyMode, insetVisible);
				// Exercise live membership changes and a resize on the same HUD instance.
				hud.UpdateDebugOverlay(CreateSnapshot(12, dummyMode));
				await Settle();
				var detailsScroll = hud.GetNode<ScrollContainer>("DebugRideOverlay/DebugLayout/DebugDetailsScroll");
				var debugText = hud.GetNode<Label>("DebugRideOverlay/DebugLayout/DebugDetailsScroll/DebugRideSummary").Text;
				if (!debugText.Contains("Human riders: 12", StringComparison.Ordinal) ||
					!debugText.Contains("Human Remote Rider 12", StringComparison.Ordinal) ||
					detailsScroll.GetVScrollBar().MaxValue <= detailsScroll.GetVScrollBar().Page)
					_failures.Add($"A newly joined rider set did not expand diagnostics at {size}.");
				hud.UpdateDebugOverlay(CreateSnapshot(0, dummyMode));
				await Settle();
				debugText = hud.GetNode<Label>("DebugRideOverlay/DebugLayout/DebugDetailsScroll/DebugRideSummary").Text;
				if (!debugText.Contains("Human riders: 0", StringComparison.Ordinal) ||
					debugText.Contains("Human Remote Rider", StringComparison.Ordinal))
					_failures.Add($"Departed rider rows remained in diagnostics at {size}.");
				hud.UpdateDebugOverlay(CreateSnapshot(riderCount, dummyMode));
				viewport.Size = new Vector2I(size.X + 32, size.Y + 24);
				await Settle();
				ApplyRuntimeLayout(hud);
				CheckRuntimeBounds(hud, inset, dummy, viewport, viewport.Size, riderCount, dummyMode, insetVisible);
				viewport.Size = size;
				await Settle();
				ApplyRuntimeLayout(hud);

				var scroll = hud.GetNode<ScrollContainer>("DebugRideOverlay/DebugLayout/DebugDetailsScroll");
				if (riderCount == 12)
				{
					if (scroll.GetVScrollBar().MaxValue <= scroll.GetVScrollBar().Page)
						_failures.Add($"Many-rider details did not scroll at {size} (dummy={dummyMode}, inset={insetVisible}).");
					SaveScreenshot(viewport, output, size, riderCount, dummyMode, insetVisible, "top");
					var oldScroll = scroll.ScrollVertical;
					scroll.ScrollVertical = (int)scroll.GetVScrollBar().MaxValue;
					await Settle();
					if (scroll.ScrollVertical <= oldScroll)
						_failures.Add($"Many-rider diagnostics could not be scrolled at {size}.");
				}

				SaveScreenshot(viewport, output, size, riderCount, dummyMode, insetVisible,
					riderCount == 12 ? "scrolled" : "default");
				_screenshots++;
				viewport.Free();
				await Settle();
			}

			if (_failures.Count == 0)
			{
				GD.Print($"[DebugOverlayLayoutSmoke] PASS: rendered {_screenshots} layouts across four sizes, zero/one/two/twelve remote riders, real/dummy modes, inset on/off; live rider changes, resizing, controls and scrolling passed. Screenshots: {output}");
				GetTree().Quit(0);
			}
			else
			{
				foreach (var failure in _failures) GD.PushError("[DebugOverlayLayoutSmoke] " + failure);
				GetTree().Quit(1);
			}
		}
		catch (Exception error)
		{
			GD.PushError($"[DebugOverlayLayoutSmoke] FAIL: {error}");
			GetTree().Quit(1);
		}
	}

	private HudController BuildHud()
	{
		var hud = new HudController { Name = "HudController" };
		hud.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var bar = new PanelContainer { Name = "BottomBar" };
		bar.AnchorLeft = 0; bar.AnchorTop = bar.AnchorBottom = 1;
		bar.AnchorRight = 0;
		bar.OffsetLeft = 12; bar.OffsetRight = 350; bar.OffsetTop = -96; bar.OffsetBottom = -36;
		var margin = new MarginContainer { Name = "MarginContainer" };
		var row = new HBoxContainer { Name = "HBoxContainer" };
		row.AddChild(new Button { Name = "PauseRideButton", Text = "Pause" });
		row.AddChild(new Label { Name = "RecordingIndicator", Text = "● REC", Visible = true });
		row.AddChild(new Button { Name = "MenuButton", Text = "Menu" });
		margin.AddChild(row); bar.AddChild(margin); hud.AddChild(bar);
		var dummy = new Control { Name = "DummyControlPanel" };
		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var controls = new VBoxContainer();
		controls.AddChild(new Label { Text = "DUMMY TRAINER · controls remain clickable" });
		var power = new HBoxContainer(); power.AddChild(new Label { Text = "Power:" });
		foreach (var label in new[] { "−0.2", "−0.1", "+0.1", "+0.2" }) power.AddChild(new Button { Text = label });
		controls.AddChild(power);
		var cadence = new HBoxContainer(); cadence.AddChild(new Label { Text = "Cadence:" });
		cadence.AddChild(new Button { Text = "−5" }); cadence.AddChild(new Button { Text = "+5" });
		controls.AddChild(cadence); panel.AddChild(controls); dummy.AddChild(panel); hud.AddChild(dummy);
		return hud;
	}

	private static RideDebugSnapshot CreateSnapshot(int riderCount, bool dummy)
	{
		var riders = Enumerable.Range(0, riderCount).Select(index => new RemoteHumanRiderDebugSnapshot(
			$"remote-{index}",
			$"Remote Rider {index + 1} · long display name wraps across the diagnostics panel",
			index % 3 != 0, 100.5 - index * 12, index % 3 == 0 ? null : .8,
			index % 2 == 0, index % 3 != 0, true, false, true)).ToArray();
		return new RideDebugSnapshot
		{
			profileName = "A very long rider profile name for layout wrapping and scrolling",
			trainerMode = dummy ? "dummy" : "real",
			trainerKind = dummy ? "dummy" : "real",
			trainerConnected = true,
			fitRecordingState = "recording",
			routeName = "Dunedin Signal Hill Climbing Loop · long route title to verify wrapped route details",
			routeId = "dunedin-signal-hill-climbing-loop",
			routeSegment = "Signal Hill summit return descent",
			remoteHumanRiders = riders,
			aiRiderCount = 8,
			nearestAiRiderName = "Rider One",
			nearestAiRiderRelativeDistanceMeters = 123,
			ambientCars = 22,
			ambientPedestrians = 15,
			dummyMode = "W/kg",
			dummyWattsPerKg = 2.4,
			dummyTargetWatts = 180,
			trainerDifficultyPercent = 50,
			trainerGradeTargetPercent = 4.1,
			trainerGradeCommandedPercent = 3.8,
			trainerGradeTransitionActive = true,
			rawTrainerPowerWatts = 184,
			rawTrainerCadenceRpm = 83,
			rawTrainerSpeedKph = 24.5,
			telemetryAgeSeconds = .4,
			virtualSpeedKph = 24.5,
			totalRideDistanceMeters = 12500,
			elapsedRidingSeconds = 2100,
			lapNumber = 2,
			routeGradientPercent = 5.2,
			routeElevationMeters = 210
		};
	}

	private void CheckRuntimeBounds(HudController hud, Control inset, Control dummy, SubViewport viewport,
		Vector2I size, int riderCount, bool dummyMode, bool insetVisible)
	{
		var debug = hud.GetNode<PanelContainer>("DebugRideOverlay");
		var actions = hud.GetNode<Control>("DebugRideOverlay/DebugLayout/DebugControls");
		var pause = hud.GetNode<Button>("BottomBar/MarginContainer/HBoxContainer/PauseRideButton");
		var menu = hud.GetNode<Button>("BottomBar/MarginContainer/HBoxContainer/MenuButton");
		var bottomBar = hud.GetNode<Control>("BottomBar");
		var scroll = hud.GetNode<ScrollContainer>("DebugRideOverlay/DebugLayout/DebugDetailsScroll");
		CheckInside(debug.GetGlobalRect(), size, "debug panel", riderCount, dummyMode, insetVisible);
		CheckInside(actions.GetGlobalRect(), size, "debug actions", riderCount, dummyMode, insetVisible);
		CheckInside(pause.GetGlobalRect(), size, "pause control", riderCount, dummyMode, insetVisible);
		CheckInside(menu.GetGlobalRect(), size, "menu control", riderCount, dummyMode, insetVisible);
		if (debug.GetGlobalRect().Intersects(bottomBar.GetGlobalRect()) || actions.GetGlobalRect().End.Y > debug.GetGlobalRect().End.Y + .5f ||
			scroll.GetGlobalRect().End.Y > actions.GetGlobalRect().Position.Y + .5f)
			_failures.Add($"Debug content or sticky actions overlap at {size}, riders={riderCount}.");
		if (insetVisible) CheckInside(inset.GetGlobalRect(), size, "route inset", riderCount, dummyMode, insetVisible);
		var recording = hud.GetNode<Label>("BottomBar/MarginContainer/HBoxContainer/RecordingIndicator");
		if (!recording.Visible) _failures.Add($"Recording status is not accessible at {size}.");
		foreach (var action in hud.GetNode("DebugRideOverlay/DebugLayout/DebugControls").FindChildren("*", "Button", true, false).Cast<Button>())
			if (action.Disabled || action.MouseFilter == Control.MouseFilterEnum.Ignore)
				_failures.Add($"Debug action {action.Text} is not clickable at {size}.");
		if (!dummyMode || !insetVisible) return;
		var d = dummy.GetGlobalRect(); var i = inset.GetGlobalRect();
		if (d.Intersects(i))
			_failures.Add($"Dummy controls overlap route inset at {size}, riders={riderCount}.");
		if (d.Intersects(bottomBar.GetGlobalRect()))
			_failures.Add($"Dummy controls overlap persistent ride controls at {size}, riders={riderCount}.");
		foreach (var button in dummy.FindChildren("*", "Button", true, false).Cast<Button>())
		{
			CheckInside(button.GetGlobalRect(), size, "dummy control", riderCount, dummyMode, insetVisible);
			if (button.Disabled) _failures.Add($"Dummy control is disabled at {size}.");
		}
	}

	private void CheckInside(Rect2 rectangle, Vector2I size, string label,
		int riderCount, bool dummyMode, bool insetVisible)
	{
		if (rectangle.Position.X < -1 || rectangle.Position.Y < -1 || rectangle.End.X > size.X + 1 || rectangle.End.Y > size.Y + 1)
			_failures.Add($"{label} escaped viewport {size}, riders={riderCount}, dummy={dummyMode}, inset={insetVisible}: {rectangle}.");
	}

	private static void ApplyRuntimeLayout(HudController hud) =>
		typeof(HudController).GetMethod("UpdateDebugOverlayLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(hud, null);

	private void SaveScreenshot(SubViewport viewport, string output, Vector2I size, int riderCount,
		bool dummyMode, bool insetVisible, string scrollState)
	{
		var image = viewport.GetTexture().GetImage();
		var file = Path.Combine(output, $"{size.X}x{size.Y}-riders{riderCount}-{(dummyMode ? "dummy" : "real")}-inset{(insetVisible ? "on" : "off")}-{scrollState}.png");
		if (image.SavePng(file) != Error.Ok) _failures.Add($"Could not save rendered preview {file}.");
	}

	private async Task Settle()
	{
		for (var frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private void Check(bool condition, string message)
	{
		if (!condition) _failures.Add(message);
	}
}
