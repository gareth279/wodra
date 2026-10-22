using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Implementations;

/// <summary>Exercises the selected offline pacer in the production RideScreen and compares player authority.</summary>
public partial class OfflinePacerRideScreenReviewTest : Node
{
    private const string RouteId = "dunedin-one-way-oval-gardens";
    private const int FrameCount = 180;
    private const double LowFrameRateSeconds = .65;

    public override async void _Ready()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(6);
            GameBootstrap.Settings.DebugModeEnabled = false;
            GameBootstrap.Settings.OnlineRidingEnabled = false;
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 1200;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 90;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(
                ProjectSettings.GlobalizePath("user://"), "offline-pacer-fit");
            GameBootstrap.ConfigureFakeTrainerMode();

            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var route = RouteCatalog.GetById(RouteId)
                ?? throw new InvalidOperationException($"Missing built-in production route {RouteId}.");

            var withPacer = await RunTrace(screen, session, world, route, "moderate");
            await session.ExitToRouteSelectionAsync();
            world.SetProcess(true);
            await Settle(2);
            world.SetProcess(false);
            var withoutPacer = await RunTrace(screen, session, world, route, PacerEffortPreset.DisabledId);

            AssertPlayerTraceEquivalent(withPacer, withoutPacer);
            if (!withPacer.PlayerPassedPacer)
                throw new InvalidOperationException("The high-power production RideScreen trace did not catch and pass its selected pacer.");
            if (withPacer.PacerFrames.Count < 3 || withPacer.PacerFrames[^1].RouteDistance <= withPacer.PacerFrames[0].RouteDistance)
                throw new InvalidOperationException("The production pacer did not advance continuously during the ride.");
            if (withoutPacer.PacerFrames.Count != 0)
                throw new InvalidOperationException("The disabled pacer selection still created production pacer state.");
            if (withPacer.AiRiderCount != 8 || withPacer.PacerVisualSamples == 0)
                throw new InvalidOperationException($"Pacer integration changed the accepted generic AI population or failed to create a visible rider model: AI={withPacer.AiRiderCount}, pacer visuals={withPacer.PacerVisualSamples}.");
            if (GameBootstrap.Settings.OnlineRidingEnabled || session.IsMultiplayerConnected)
                throw new InvalidOperationException("The selected pacer should be available without online riding or multiplayer.");

            await session.ExitToRouteSelectionAsync();
            GD.Print($"[OfflinePacerRideScreenReview] PASS: production RideScreen; Moderate Pacer 2.2 W/kg ({PacerEffortPresets.Find("moderate")!.TargetPowerWatts} W at the assumed 75 kg body mass) remained visible and progressed for {withPacer.PacerFrames.Count} ticks; a 1,200 W player caught/overtook it; pausing/restarts are covered by Core tests; {FrameCount} identical player ticks and {withPacer.RecordedSamples.Count} production FIT inputs matched pacer-off exactly while offline; generic AI count={withPacer.AiRiderCount}.");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[OfflinePacerRideScreenReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private async Task<TraceResult> RunTrace(Node screen, RideSessionNode session,
        RideWorldView world, RouteDefinition route, string presetId)
    {
        GameBootstrap.Settings.PacerPresetId = presetId;
        if (!await session.StartTrainerSessionAsync(route))
            throw new InvalidOperationException("Could not start the production RideScreen pacer review session.");
        screen.GetNode<HudController>("HudController").Show();
        screen.GetNode<StartPanelController>("StartPanel").Hide();
        session.SetProcess(false);
        world.SetProcess(true);
        await Settle(8);
        world.SetProcess(false);
        if (world.RouteGeometry is null) throw new InvalidOperationException("The production route geometry did not load.");

        if (presetId == PacerEffortPreset.DisabledId)
        {
            if (world.SelectedPacerPreset is not null || world.PacerRiderState is not null || world.PacerVisualVisible)
                throw new InvalidOperationException("No-pacer selection should leave the production world without pacer state or model.");
        }
        else
        {
            var preset = PacerEffortPresets.Find(presetId)
                ?? throw new InvalidOperationException($"Unknown test pacer preset {presetId}.");
            if (world.SelectedPacerPreset?.Id != preset.Id || world.PacerRiderState is not { } initial
                || initial.RiderId != $"ai-pacer-{preset.Id}" || initial.PowerWatts != preset.TargetPowerWatts
                || !world.PacerVisualVisible)
                throw new InvalidOperationException("The selected pacer preset, target watts, stable identity or visible CyclistVisual was missing.");
            if (!world.GetRiderCollisionReviewSamples().Any(sample => sample.RiderId == initial.RiderId))
                throw new InvalidOperationException("The selected pacer does not appear in production rider visual samples.");
        }
        if (session.IsMultiplayerConnected || GameBootstrap.Settings.OnlineRidingEnabled)
            throw new InvalidOperationException("Offline pacer review unexpectedly enabled multiplayer.");

        var frames = new List<PlayerFrame>(FrameCount);
        var pacerFrames = new List<PacerFrame>();
        var samples = new List<RecordingSample>();
        void OnRecordingSample(RideState state, TrainerTelemetry telemetry, double gradient) =>
            samples.Add(new RecordingSample(state, telemetry.PowerWatts, telemetry.CadenceRpm,
                telemetry.HeartRateBpm, gradient));
        session.RecordingSampleObservedForReview += OnRecordingSample;
        try
        {
            var trainer = GameBootstrap.TrainerClient as FakeTrainerClient
                ?? throw new InvalidOperationException("Production RideScreen did not use its controlled fake trainer.");
            for (var frame = 0; frame < FrameCount; frame++)
            {
                var delta = frame % 24 == 23 ? LowFrameRateSeconds : frame % 12 == 11 ? .25 : .05;
                var watts = frame % 60 < 42 ? 1200 : 1000;
                var cadence = 88 + frame % 5;
                trainer.SetDummyOutput(watts, cadence);
                var left = frame % 72 is >= 10 and < 26;
                var right = frame % 72 is >= 42 and < 58;
                world.SetDeterministicSteeringInputForReview(left, right);
                var before = session.CurrentRideState;
                var gradient = route.GetSampleAt(before.DistanceMeters).GradientPercent;
                session._Process(delta);
                world._Process(delta);
                frames.Add(new PlayerFrame(session.CurrentRideState, world.CurrentLateralOffsetMeters,
                    gradient, trainer.LastRequestedSimulationGradePercent, watts, cadence));
                if (world.PacerRiderState is { } pacer)
                {
                    var player = new RiderState("player", "You", route.Id,
                        session.CurrentRideState.DistanceMeters,
                        world.RouteGeometry!.WrapDistance(session.CurrentRideState.DistanceMeters),
                        (int)Math.Floor(session.CurrentRideState.DistanceMeters / world.RouteGeometry.LengthMeters) + 1,
                        world.CurrentLateralOffsetMeters, session.CurrentRideState.VirtualSpeedKph / 3.6,
                        session.CurrentRideState.CurrentPowerWatts, session.CurrentRideState.CurrentCadenceRpm,
                        session.IsRidePaused, session.CurrentRideState.ElapsedSeconds);
                    pacerFrames.Add(new PacerFrame(pacer.RouteDistanceMeters,
                        PacerRiderController.SignedGapMeters(pacer, player), pacer.VirtualSpeedMps, pacer.LapNumber));
                }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        finally { session.RecordingSampleObservedForReview -= OnRecordingSample; }

        var aiCount = world.GetDiagnosticState().AiRiders;
        return new TraceResult(frames, pacerFrames, samples, aiCount,
            frames.Count > 1 && pacerFrames.Count > 1 && pacerFrames[0].GapMeters > 0
                && pacerFrames[^1].GapMeters < 0,
            presetId == PacerEffortPreset.DisabledId ? 0 : world.GetRiderCollisionReviewSamples()
                .Count(sample => sample.RiderId == $"ai-pacer-{presetId}"));
    }

    private static void AssertPlayerTraceEquivalent(TraceResult pacer, TraceResult none)
    {
        if (pacer.Frames.Count != none.Frames.Count || pacer.RecordedSamples.Count != none.RecordedSamples.Count)
            throw new InvalidOperationException("Pacer on/off RideScreen traces produced different frame or recording sample counts.");
        for (var index = 0; index < pacer.Frames.Count; index++)
        {
            var a = pacer.Frames[index];
            var b = none.Frames[index];
            if (a.State != b.State || a.LateralOffset != b.LateralOffset || a.Gradient != b.Gradient
                || a.TrainerGradeCommand != b.TrainerGradeCommand || a.InputWatts != b.InputWatts
                || a.InputCadence != b.InputCadence)
                throw new InvalidOperationException($"Offline pacer changed authoritative RideScreen output at frame {index}: with={a}, without={b}.");
        }
        for (var index = 0; index < pacer.RecordedSamples.Count; index++)
        {
            var a = pacer.RecordedSamples[index];
            var b = none.RecordedSamples[index];
            if (a.State != b.State || a.Gradient != b.Gradient || a.PowerWatts != b.PowerWatts
                || a.CadenceRpm != b.CadenceRpm || a.HeartRateBpm != b.HeartRateBpm)
                throw new InvalidOperationException($"Offline pacer changed production FIT input {index}: with={a}, without={b}.");
        }
    }

    private async Task Settle(int frames)
    {
        for (var frame = 0; frame < frames; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private sealed record TraceResult(IReadOnlyList<PlayerFrame> Frames, IReadOnlyList<PacerFrame> PacerFrames,
        IReadOnlyList<RecordingSample> RecordedSamples, int AiRiderCount, bool PlayerPassedPacer, int PacerVisualSamples);
    private readonly record struct PlayerFrame(RideState State, double LateralOffset, double Gradient,
        double? TrainerGradeCommand, int InputWatts, int InputCadence);
    private readonly record struct PacerFrame(double RouteDistance, double GapMeters, double SpeedMps, int LapNumber);
    private readonly record struct RecordingSample(RideState State, int PowerWatts, int CadenceRpm,
        int? HeartRateBpm, double Gradient);
}

