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
    private const string DefaultRouteId = "dunedin-one-way-oval-gardens";
    private const int FrameCount = 180;
    private const double LowFrameRateSeconds = .65;
    private Dictionary<string, bool>? _originalGameFeaturePreferences;
    private string? _originalPacerPresetId;
    private bool _originalDebugModeEnabled;
    private bool _originalOnlineRidingEnabled;
    private bool _originalRideSettingsCaptured;

    public override async void _Ready()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(6);
            _originalDebugModeEnabled = GameBootstrap.Settings.DebugModeEnabled;
            _originalOnlineRidingEnabled = GameBootstrap.Settings.OnlineRidingEnabled;
            _originalPacerPresetId = GameBootstrap.Settings.PacerPresetId;
            _originalRideSettingsCaptured = true;
            GameBootstrap.Settings.DebugModeEnabled = false;
            GameBootstrap.Settings.OnlineRidingEnabled = false;
            _originalGameFeaturePreferences = new Dictionary<string, bool>(GameBootstrap.Settings.GameFeaturesByProfile);
            GameBootstrap.Settings.GameFeaturesByProfile.Clear();
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 1200;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 90;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(
                ProjectSettings.GlobalizePath("user://"), "offline-pacer-fit");
            GameBootstrap.ConfigureFakeTrainerMode();

            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var routeId = System.Environment.GetEnvironmentVariable("WODRA_PACER_REVIEW_ROUTE") ?? DefaultRouteId;
            var route = RouteCatalog.GetById(routeId)
                ?? throw new InvalidOperationException($"Missing production review route {routeId}.");
            if (route.Id == "dunedin-south-coastal-loop")
                AssertRendezvousAcrossRouteTurnaround(route);

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
            RestoreGameFeaturePreferences();
            GD.Print($"[OfflinePacerRideScreenReview] PASS: production RideScreen route={route.Id}; Moderate Pacer 2.2 W/kg ({PacerEffortPresets.Find("moderate")!.TargetPowerWatts} W at the assumed 75 kg body mass) remained visible and progressed for {withPacer.PacerFrames.Count} ticks; a 1,200 W player caught/overtook it; pausing/restarts are covered by Core tests; {FrameCount} identical player ticks and {withPacer.RecordedSamples.Count} production FIT inputs matched pacer-off exactly while offline; generic AI count={withPacer.AiRiderCount}.");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            RestoreGameFeaturePreferences();
            GD.PushError($"[OfflinePacerRideScreenReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private void RestoreGameFeaturePreferences()
    {
        if (!_originalRideSettingsCaptured || GameBootstrap.Settings is null) return;
        if (_originalGameFeaturePreferences is not null)
        {
            GameBootstrap.Settings.GameFeaturesByProfile.Clear();
            foreach (var item in _originalGameFeaturePreferences)
                GameBootstrap.Settings.GameFeaturesByProfile[item.Key] = item.Value;
        }
        GameBootstrap.Settings.DebugModeEnabled = _originalDebugModeEnabled;
        GameBootstrap.Settings.OnlineRidingEnabled = _originalOnlineRidingEnabled;
        if (_originalPacerPresetId is not null)
            GameBootstrap.Settings.PacerPresetId = _originalPacerPresetId;
        GameBootstrap.Settings.Save();
        _originalRideSettingsCaptured = false;
        _originalGameFeaturePreferences = null;
        _originalPacerPresetId = null;
    }

    private static void AssertRendezvousAcrossRouteTurnaround(RouteDefinition route)
    {
        if (route.Points is not { Count: >= 3 })
            throw new InvalidOperationException("The out-and-back route review requires sampled production route geometry.");
        var bestDistance = 0d;
        var bestTurnDegrees = 0d;
        for (var distance = 25d; distance < route.DistanceMeters - 25; distance += 10)
        {
            var before = route.GetSampleAt(distance - 20);
            var middle = route.GetSampleAt(distance);
            var after = route.GetSampleAt(distance + 20);
            if (before.Latitude is not { } beforeLat || before.Longitude is not { } beforeLon
                || middle.Latitude is not { } middleLat || middle.Longitude is not { } middleLon
                || after.Latitude is not { } afterLat || after.Longitude is not { } afterLon) continue;
            var firstBearing = BearingDegrees(beforeLat, beforeLon, middleLat, middleLon);
            var secondBearing = BearingDegrees(middleLat, middleLon, afterLat, afterLon);
            var turn = Math.Abs((secondBearing - firstBearing + 540) % 360 - 180);
            if (turn > bestTurnDegrees) { bestTurnDegrees = turn; bestDistance = distance; }
        }
        if (bestTurnDegrees < 120)
            throw new InvalidOperationException($"The South Coastal production route no longer exposes an authored turnaround to review (largest sampled turn {bestTurnDegrees:0}°).");

        var playerDistance = bestDistance - 8;
        var player = new RiderState("player", "You", route.Id, playerDistance,
            route.GetLoopDistance(playerDistance), (int)Math.Floor(playerDistance / route.DistanceMeters) + 1,
            0, 5, 180, 90, false, 60);
        var pacer = new PacerRiderController(PacerEffortPresets.Find("moderate")!);
        pacer.Start(player, route.DistanceMeters, -90);
        pacer.BringTo(player.RouteDistanceMeters, player.LateralOffsetMeters);
        var rendezvous = pacer.State;
        if (Math.Abs(rendezvous.RouteDistanceMeters - (playerDistance + 12)) > .01
            || rendezvous.WrappedRouteDistanceMeters != route.GetLoopDistance(playerDistance + 12)
            || rendezvous.RouteId != route.Id)
            throw new InvalidOperationException($"Rendezvous did not preserve the South Coastal route direction across its {bestTurnDegrees:0}° turnaround at {bestDistance:0} m.");
        pacer.Update(route.DistanceMeters, route.GetGradientAt, .5, false);
        if (pacer.State.RouteDistanceMeters <= rendezvous.RouteDistanceMeters
            || route.GetSampleAt(pacer.State.WrappedRouteDistanceMeters).Latitude is null)
            throw new InvalidOperationException("The South Coastal pacer did not resume normal simulation along the routed turnaround geometry.");
    }

    private static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var meanLatitude = (lat1 + lat2) * .5 * Math.PI / 180;
        var x = (lon2 - lon1) * Math.Cos(meanLatitude);
        var y = lat2 - lat1;
        return (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360;
    }

    private async Task<TraceResult> RunTrace(Node screen, RideSessionNode session,
        RideWorldView world, RouteDefinition route, string presetId)
    {
        GameBootstrap.Settings.PacerPresetId = presetId;
        if (!await session.StartTrainerSessionAsync(route))
            throw new InvalidOperationException("Could not start the production RideScreen pacer review session.");
        screen.GetNode<HudController>("HudController").Show();
        screen.GetNode<StartPanelController>("StartPanel").Hide();
        session._Process(.05);
        session.SetProcess(false);
        world.SetProcess(true);
        await Settle(8);
        if (world.RouteGeometry is null) throw new InvalidOperationException("The production route geometry did not load.");

        CaptureRenderedReviewFrame(presetId == PacerEffortPreset.DisabledId
            ? "pacer-none-control" : $"pacer-{presetId}-near-debug-hidden");
        if (presetId != PacerEffortPreset.DisabledId)
        {
            var hud = screen.GetNode<HudController>("HudController");
            hud.SetDebugDisplayEnabled(true);
            await Settle(2);
            CaptureRenderedReviewFrame("pacer-debug-visible-default-window");
            DisplayServer.WindowSetSize(new Vector2I(960, 540));
            await Settle(2);
            CaptureRenderedReviewFrame("pacer-debug-visible-minimum-window");
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            await Settle(2);
            CaptureRenderedReviewFrame("pacer-debug-visible-fullscreen");
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            hud.SetDebugDisplayEnabled(false);
            await Settle(2);
        }
        world.SetProcess(false);

        if (presetId == PacerEffortPreset.DisabledId)
        {
            if (world.SelectedPacerPreset is not null || world.PacerRiderState is not null || world.PacerVisualVisible)
                throw new InvalidOperationException("No-pacer selection should leave the production world without pacer state or model.");
            if (!screen.GetNode<Control>("HudController/OfflinePacerPanel").IsVisibleInTree())
                throw new InvalidOperationException("The production ride HUD must keep its pacer selector available when no pacer is selected.");
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
            if (world.PacerRiderStates.Count != PacerEffortPresets.All.Count
                || world.PacerRiderStates.Select(state => state.RiderId).Distinct().Count() != PacerEffortPresets.All.Count)
                throw new InvalidOperationException("The production world must retain all three independently identified effort pacers.");
            var playerForGaps = new RiderState("player", "You", route.Id, session.CurrentRideState.DistanceMeters,
                world.RouteGeometry!.WrapDistance(session.CurrentRideState.DistanceMeters), 1,
                world.CurrentLateralOffsetMeters, 0, 0, 0, false, 0);
            var initialGaps = world.PacerRiderStates.Select(state =>
                PacerRiderController.SignedGapMeters(state, playerForGaps)).Order().ToArray();
            if (initialGaps[0] > 40 || initialGaps[^1] < 90)
                throw new InvalidOperationException($"Pacer routes should visibly cover near, middle and far gaps: {string.Join(", ", initialGaps.Select(gap => $"{gap:0} m"))}.");
            world.SelectPacerPreset("easy");
            if (world.SelectedPacerPreset?.Id != "easy")
                throw new InvalidOperationException("Changing pacer from the production selector did not track the requested pacer identity.");
            world._Process(.05);
            world.BringSelectedPacerToPlayer();
            world._Process(.05);
            await Settle(2);
            CaptureRenderedReviewFrame("pacer-easy-selected-after-sync");
            world.SelectPacerPreset("strong");
            world._Process(.05);
            world.BringSelectedPacerToPlayer();
            world._Process(.05);
            await Settle(2);
            CaptureRenderedReviewFrame("pacer-strong-selected-after-sync");
            world.SelectPacerPreset(preset.Id);
            if (world.SelectedPacerPreset?.Id != preset.Id)
                throw new InvalidOperationException("Returning to the selected production pacer silently changed its identity.");
            world._Process(.05);
            await Settle(2);
            var playerBeforeSync = session.CurrentRideState;
            world.BringSelectedPacerToPlayer();
            world._Process(.05);
            var synced = world.PacerRiderState
                ?? throw new InvalidOperationException("Bringing the selected pacer to the player cleared its selection.");
            if (Math.Abs(PacerRiderController.SignedGapMeters(synced, new RiderState("player", "You", route.Id,
                    playerBeforeSync.DistanceMeters, world.RouteGeometry!.WrapDistance(playerBeforeSync.DistanceMeters),
                    (int)Math.Floor(playerBeforeSync.DistanceMeters / world.RouteGeometry.LengthMeters) + 1,
                    world.CurrentLateralOffsetMeters, playerBeforeSync.VirtualSpeedKph / 3.6,
                    playerBeforeSync.CurrentPowerWatts, playerBeforeSync.CurrentCadenceRpm, session.IsRidePaused,
                    playerBeforeSync.ElapsedSeconds))) > 20)
                throw new InvalidOperationException("The production rendezvous did not place the pacer close to the player.");
            if (session.CurrentRideState != playerBeforeSync || synced.RiderId != initial.RiderId)
                throw new InvalidOperationException("Pacer rendezvous changed player ride state or selected pacer identity.");
            var syncedGap = PacerRiderController.SignedGapMeters(synced, new RiderState("player", "You", route.Id,
                playerBeforeSync.DistanceMeters, world.RouteGeometry!.WrapDistance(playerBeforeSync.DistanceMeters),
                (int)Math.Floor(playerBeforeSync.DistanceMeters / world.RouteGeometry.LengthMeters) + 1,
                world.CurrentLateralOffsetMeters, playerBeforeSync.VirtualSpeedKph / 3.6,
                playerBeforeSync.CurrentPowerWatts, playerBeforeSync.CurrentCadenceRpm, session.IsRidePaused,
                playerBeforeSync.ElapsedSeconds));
            var hudGap = screen.GetNode<HudController>("HudController").PacerGapText;
            if (Math.Abs(syncedGap - 12) > 3 || !hudGap.Contains($"{Math.Abs(syncedGap):0} m", StringComparison.Ordinal))
                throw new InvalidOperationException($"Rendezvous must produce a 12 m near-field gap and report the same value in the HUD; gap={syncedGap:0.0}, HUD='{hudGap}'.");
            await Settle(2);
            CaptureRenderedReviewFrame("pacer-selected-after-sync");
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
        if (presetId != PacerEffortPreset.DisabledId && world.SelectedPacerPreset?.Id != presetId)
            throw new InvalidOperationException("The selected pacer silently changed after the player passed nearby AI riders.");
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

    private void CaptureRenderedReviewFrame(string name)
    {
        if (OS.GetCmdlineArgs().Contains("--headless"))
        {
            GD.Print($"[OfflinePacerRideScreenReview] screenshot skipped: headless renderer ({name})");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "WodraPacerReview");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        var image = GetViewport().GetTexture()?.GetImage();
        if (image is null)
        {
            GD.Print($"[OfflinePacerRideScreenReview] screenshot unavailable in headless renderer: {name}");
            return;
        }
        var error = image.SavePng(path);
        if (error != Error.Ok)
            throw new IOException($"Could not save production RideScreen visual review frame ({name}): {error}.");
        GD.Print($"[OfflinePacerRideScreenReview] screenshot={path}");
    }

    private sealed record TraceResult(IReadOnlyList<PlayerFrame> Frames, IReadOnlyList<PacerFrame> PacerFrames,
        IReadOnlyList<RecordingSample> RecordedSamples, int AiRiderCount, bool PlayerPassedPacer, int PacerVisualSamples);
    private readonly record struct PlayerFrame(RideState State, double LateralOffset, double Gradient,
        double? TrainerGradeCommand, int InputWatts, int InputCadence);
    private readonly record struct PacerFrame(double RouteDistance, double GapMeters, double SpeedMps, int LapNumber);
    private readonly record struct RecordingSample(RideState State, int PowerWatts, int CadenceRpm,
        int? HeartRateBpm, double Gradient);
}

