using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

/// <summary>Captures the production rider through cadence, steering, coast, and restart.</summary>
public partial class RiderMotionReviewTest : Node
{
    public override async void _Ready()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(1500, 900));
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts",
                "alpha48-hand-correction", "grip-v2", "godot-motion-frames-v14");
            Directory.CreateDirectory(output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(12);

            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.DummyTrainer.Mode = "W/kg";
            GameBootstrap.Settings.DummyTrainer.WattsPerKg = 12;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 85;
            GameBootstrap.ConfigureFakeTrainerMode();
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-rolling-loop")))
                throw new InvalidOperationException("Could not start the production Rolling City ride.");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            hud.Show();
            hud.SetDebugDisplayEnabled(false);
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren())
                if (window is Window popup) popup.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();
            session.JumpToRouteDistance("720");
            session.ConfirmDebugDistanceJump();
            hud.HideDebugJumpConfirmation();
            hud.SetRecordingState(false, false);
            GameBootstrap.Settings.DebugModeEnabled = false;
            hud.SetDebugMode(false);
            world.SelectCameraInspectionView(RiderCameraView.GameplayRefined);
            await Settle(120);

            var index = 0;
            GD.Print("[RiderMotionReview] capture start: 85 rpm pedal, steer right, coast, restart at 85 rpm");
            index = await Capture(output, index, 3, 85);  // 85 rpm baseline
            SendSteering(Key.Right, true);
            index = await Capture(output, index, 2, 85);  // steering while pedalling
            var steeringOffset = world.CurrentLateralOffsetMeters;
            GD.Print($"[RiderMotionReview] held-right lateral offset={steeringOffset:F3} m");
            if (Math.Abs(steeringOffset) < .05)
                throw new InvalidOperationException($"Right steering event did not move the rider: {steeringOffset:F3} m.");
            SendSteering(Key.Right, false);
            index = await Capture(output, index, 1, 85);  // release / return
            session.AdjustDummyCadence(-85);
            index = await Capture(output, index, 2, 0);   // coasting
            session.AdjustDummyCadence(85);
            index = await Capture(output, index, 4, 85);  // restart, several cycles
            GD.Print($"[RiderMotionReview] PASS: {index} production RideScreen frames at 30 fps; output={output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[RiderMotionReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private async Task<int> Capture(string output, int index, int seconds, int cadence)
    {
        GD.Print($"[RiderMotionReview] phase cadence={cadence} rpm frames={seconds * 30}");
        for (var frame = 0; frame < seconds * 30; frame++)
        {
            await Settle(2);
            var image = GetViewport().GetTexture().GetImage();
            var path = Path.Combine(output, $"frame_{index++:0000}.png");
            var error = image.SavePng(path);
            if (error != Error.Ok) throw new IOException($"Could not save runtime frame: {error}");
        }
        return index;
    }

    private static void SendSteering(Key key, bool pressed)
    {
        Input.ParseInputEvent(new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            Pressed = pressed
        });
    }

    private async Task Settle(int frames)
    {
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
