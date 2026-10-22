using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using NVector3 = System.Numerics.Vector3;

/// <summary>Exercises local-player/car interaction in the production RideScreen and route model.</summary>
public partial class LocalPlayerCarInteractionRideScreenReviewTest : Node
{
    [Export] public bool ExpectSafeInteraction { get; set; }
    [Export] public bool CaptureEvidence { get; set; } = true;
    [Export] public string RouteId { get; set; } = "dunedin-signal-hill-climbing-loop";

    private static readonly FieldInfo RideStateField = typeof(RideSessionNode)
        .GetField("_rideState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RideSessionNode test state field was not found.");

    public override async void _Ready()
    {
        try
        {
            GD.Print($"[LocalPlayerCarInteractionReview] configuration expectSafe={ExpectSafeInteraction} capture={CaptureEvidence} scene={SceneFilePath}");
            VerifyConstraintMath();
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            var side = ExpectSafeInteraction ? "after" : "before";
            var output = Path.Combine(Path.GetTempPath(), "Wodra-alpha54-local-player-car-review", side);
            if (CaptureEvidence) Directory.CreateDirectory(output);

            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(6);
            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 220;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 86;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(Path.GetTempPath(), "Wodra-alpha54-review-fit");
            GameBootstrap.ConfigureFakeTrainerMode();
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            world.AiRiderTargetCount = 0;
            world.AiRiderMaximumCount = 0;
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById(RouteId)))
                throw new InvalidOperationException($"Could not start production route {RouteId}.");
            var hud = screen.GetNode<HudController>("HudController");
            hud.Show();
            var startPanel = screen.GetNode<StartPanelController>("StartPanel");
            startPanel.Hide();
            (typeof(StartPanelController).GetField("_profileWindow", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(startPanel) as Window)?.Hide();
            await WaitForWorld(world, 180);
            session.JumpToRouteDistance("8670");
            session.ConfirmDebugDistanceJump();
            hud.HideDebugJumpConfirmation();
            hud.SetRecordingState(false, false);
            await Settle(6);

            SetSpeed(session, 82);
            var worldRoute = world.RouteGeometry ?? throw new InvalidOperationException("Production route geometry was not loaded.");
            var player = PlayerSample(world);
            var predictedPose = world.GetLocalPlayerCarPoseForTesting();
            var renderedBaseError = NVector3.Distance(predictedPose.Position, ToNumerics(player.WorldPosition));
            if (renderedBaseError > .05f)
                throw new InvalidOperationException($"Predictive rider envelope origin differs from the rendered RideScreen bounds base by {renderedBaseError:F3}m.");
            GD.Print($"[LocalPlayerCarInteractionReview] predictive/rendered envelope origin match: error={renderedBaseError:F4}m bounds={player.ModelLengthMeters:F2}x{player.ModelWidthMeters:F2}x{player.WorldBounds.Size.Y:F2}m");
            var points = worldRoute.Samples.Select(sample => ToNumerics(world.ToGlobal(ProceduralRoad.ToGodot(sample.Position)))).ToArray();
            var manager = ReplaceTrafficManager(world);
            var carPath = new AmbientActorPath(AmbientActorKind.Car, 54001, 1, points,
                new AmbientSpeedRange(4, 4), "controlled local-player review car", "residential",
                roadWidthMeters: 7.5, baseLaneOffsetMeters: (float)player.LateralOffsetMeters);
            var carDistance = Math.Min(player.RouteDistanceMeters + 38, carPath.LengthMeters - 80);
            manager.AddTrafficTestFixture(AmbientActorKind.Car, carPath, carDistance, 4);

            var collisionCount = 0;
            var minimumClearance = float.PositiveInfinity;
            var closestFollowerGap = double.PositiveInfinity;
            var passed = false;
            var captures = 0;
            var headless = DisplayServer.GetName() == "headless";
            var returningFromPass = false;
            var startTime = Time.GetTicksMsec();
            var totalFrames = 900;
            for (var frame = 0; frame < totalFrames; frame++)
            {
                if (headless)
                {
                    if (frame == 60) world.SetKeyboardSteeringForTesting(leftHeld: false, rightHeld: true);
                    if (frame == 280) world.SetKeyboardSteeringForTesting(leftHeld: false, rightHeld: false);
                    if (frame == 500) world.SetKeyboardSteeringForTesting(leftHeld: true, rightHeld: false);
                    if (frame == 700) world.SetKeyboardSteeringForTesting(leftHeld: false, rightHeld: false);
                }
                else
                {
                    if (frame == 210) SendSteering(Key.Right, true);
                    if (frame == 430) SendSteering(Key.Right, false);
                    if (frame == 560) SendSteering(Key.Left, true);
                }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (headless) AdvanceAtControlledSpeed(session, world);
                else SetSpeed(session, 82);

                var playerNow = PlayerSample(world);
                if (!headless && returningFromPass && playerNow.LateralOffsetMeters >= 1.35)
                {
                    SendSteering(Key.Left, false);
                    returningFromPass = false;
                }
                if (!headless && frame == 560) returningFromPass = true;
                if (!headless && frame is 209 or 429 or 559 or 779 or 899)
                    GD.Print($"[LocalPlayerCarInteractionReview] steering checkpoint frame={frame} lateral={playerNow.LateralOffsetMeters:F2} route={playerNow.RouteDistanceMeters:F1}");
                var carNow = manager.FirstCarState ?? throw new InvalidOperationException("Controlled review car disappeared.");
                var clearance = AmbientActorEnvelopeMath.SignedClearance(carNow.WorldPosition,
                    carNow.Heading, AmbientActorEnvelopeMath.CarLengthMeters,
                    AmbientActorEnvelopeMath.CarWidthMeters, AmbientActorEnvelopeMath.CarHeightMeters,
                    new AmbientTrafficPose(0, ToNumerics(playerNow.WorldPosition), ToNumerics(playerNow.WorldForward)),
                    playerNow.ModelLengthMeters, playerNow.ModelWidthMeters, playerNow.WorldBounds.Size.Y);
                minimumClearance = Math.Min(minimumClearance, clearance);
                if (clearance < 0)
                {
                    collisionCount++;
                    if (collisionCount <= 5)
                        GD.Print($"[LocalPlayerCarInteractionReview] overlap frame={frame} clear={clearance:F3} route={playerNow.RouteDistanceMeters:F3} offset={playerNow.LateralOffsetMeters:F3} rider=({playerNow.WorldPosition.X:F3},{playerNow.WorldPosition.Y:F3},{playerNow.WorldPosition.Z:F3}) car=({carNow.WorldPosition.X:F3},{carNow.WorldPosition.Y:F3},{carNow.WorldPosition.Z:F3}) speed={session.CurrentRideState.VirtualSpeedKph:F2}");
                }
                var gap = NVector3.Dot(carNow.WorldPosition - ToNumerics(playerNow.WorldPosition),
                    ToNumerics(playerNow.WorldForward));
                var carLateralOffset = manager.FirstCarLaneOffset ?? playerNow.LateralOffsetMeters;
                var sameLane = Math.Abs(carLateralOffset - playerNow.LateralOffsetMeters)
                    < AmbientActorEnvelopeMath.CarWidthMeters * .5 + playerNow.ModelWidthMeters * .5 + .25;
                if (gap > 0 && sameLane) closestFollowerGap = Math.Min(closestFollowerGap, gap);
                if (gap < -5) passed = true;

                if (CaptureEvidence && DisplayServer.GetName() != "headless" && frame % 12 == 0 && captures < 65)
                {
                    var path = Path.Combine(output, $"frame_{frame:D4}.png");
                    var error = GetViewport().GetTexture().GetImage().SavePng(path);
                    if (error != Error.Ok) throw new IOException($"Could not save RideScreen evidence frame: {error}.");
                    captures++;
                }
            }
            var elapsedSeconds = (Time.GetTicksMsec() - startTime) / 1000.0;
            var finalPlayer = PlayerSample(world);
            var finalCar = manager.FirstCarState!.Value;
            var finalGap = NVector3.Dot(finalCar.WorldPosition - ToNumerics(finalPlayer.WorldPosition),
                ToNumerics(finalPlayer.WorldForward));
            var returnedOffset = Math.Abs(finalPlayer.LateralOffsetMeters - RiderLaneMath.RiderLateralOffsetMeters);

            if (ExpectSafeInteraction)
            {
                if (collisionCount != 0)
                    throw new InvalidOperationException($"Local player intersected the rendered car envelope {collisionCount} frames; minimum clearance={minimumClearance:F3}m.");
                if (closestFollowerGap < 3.0)
                    throw new InvalidOperationException($"Same-lane following gap became too short: {closestFollowerGap:F2}m.");
                var requireVisualPass = !headless;
                if (requireVisualPass && !passed)
                    throw new InvalidOperationException($"Player did not pass after steering into the wide, clear passing side; final gap={finalGap:F2}m, offset={finalPlayer.LateralOffsetMeters:F2}m.");
                if (requireVisualPass && returnedOffset > .35)
                    throw new InvalidOperationException($"Steering did not return smoothly after the pass; final offset={finalPlayer.LateralOffsetMeters:F2}m.");
            }
            else if (collisionCount == 0)
                throw new InvalidOperationException("Alpha.53 baseline did not reproduce a local-player/car rendered-envelope overlap.");

            GD.Print($"[LocalPlayerCarInteractionReview] {(ExpectSafeInteraction ? "PASS" : "BASELINE REPRODUCED")}: route={RouteId}, collisionFrames={collisionCount}, minimumClearance={minimumClearance:F3}m, closestFollowGap={closestFollowerGap:F2}m, passed={passed}, lateralReturnError={returnedOffset:F2}m, routeDistance={finalPlayer.RouteDistanceMeters:F1}m, headless={headless}, elapsed={elapsedSeconds:F2}s, evidence={output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[LocalPlayerCarInteractionReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private static AmbientActorManager ReplaceTrafficManager(RideWorldView world)
    {
        var field = typeof(RideWorldView).GetField("_ambientActorManager", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RideWorldView ambient manager field was not found.");
        if (field.GetValue(world) is AmbientActorManager previous && GodotObject.IsInstanceValid(previous))
        {
            previous.GetParent()?.RemoveChild(previous);
            previous.QueueFree();
        }
        var manager = new AmbientActorManager { Name = "ControlledLocalPlayerCarManager", RandomSeed = 54001 };
        world.AddChild(manager);
        field.SetValue(world, manager);
        return manager;
    }

    private static void VerifyConstraintMath()
    {
        static AmbientTrafficPose StraightRider(double distance, float offset) =>
            new(0, new NVector3((float)distance, 0, offset), NVector3.UnitX);
        static AmbientCarInteractionSample Car(int id, double roadWidth, float x, float z, float speed) =>
            new(id, roadWidth, new AmbientTrafficRider($"car-{id}", new NVector3(x, 0, z),
                NVector3.UnitX, speed, AmbientActorEnvelopeMath.CarLengthMeters,
                AmbientActorEnvelopeMath.CarWidthMeters, AmbientActorEnvelopeMath.CarHeightMeters));

        var widePass = LocalPlayerCarInteractionMath.ClampRouteProgress(0, 12, 1, 1.7f,
            1.72f, .5f, 1.61f, StraightRider, [Car(1, 7.5, 5, 0, 2)]);
        if (widePass.BlockingVehicleId is not null || widePass.AllowedRouteDistanceMeters < 11.99)
            throw new InvalidOperationException($"Wide-road clear pass was blocked: {widePass}.");

        var narrowFollow = LocalPlayerCarInteractionMath.ClampRouteProgress(0, 10, 1, 0,
            1.72f, .5f, 1.61f, StraightRider, [Car(2, 5, 5, 0, .5f)]);
        if (narrowFollow.BlockingVehicleId != 2 || !narrowFollow.FollowOnly
            || narrowFollow.AllowedRouteDistanceMeters >= 2)
            throw new InvalidOperationException($"Narrow-road following did not retain a safe gap: {narrowFollow}.");

        var steeringBlock = LocalPlayerCarInteractionMath.ClampLateralOffset(0, 0, 2, 1,
            1.72f, .5f, 1.61f, StraightRider, [Car(3, 7.5, 0, 0, 0)]);
        if (steeringBlock.BlockingVehicleId != 3 || steeringBlock.AllowedOffsetMeters >= 1.2)
            throw new InvalidOperationException($"Steering was allowed into an occupied car envelope: {steeringBlock}.");

        static AmbientTrafficPose ElevatedRider(double distance, float offset) =>
            new(0, new NVector3((float)distance, 4, offset), NVector3.UnitX);
        var gradeSeparated = LocalPlayerCarInteractionMath.ClampRouteProgress(0, 10, 1, 0,
            1.72f, .5f, 1.61f, ElevatedRider, [Car(4, 5, 5, 0, 0)]);
        if (gradeSeparated.BlockingVehicleId is not null || gradeSeparated.AllowedRouteDistanceMeters < 9.99)
            throw new InvalidOperationException($"Vertically separated road actor blocked the player: {gradeSeparated}.");

        AmbientTrafficPose CurvedRider(double distance, float offset)
        {
            var x = (float)distance;
            var z = .002f * x * x;
            var forward = NVector3.Normalize(new NVector3(1, 0, .004f * x));
            var side = new NVector3(-forward.Z, 0, forward.X);
            return new AmbientTrafficPose(0, new NVector3(x, .001f * x, z) + side * offset, forward);
        }
        var prediction = Enumerable.Range(0, 7).Select(index =>
        {
            var time = index * .25;
            var x = 6 + (float)(3 * time);
            var forward = NVector3.Normalize(new NVector3(1, 0, .004f * x));
            return new AmbientTrafficPose(time, new NVector3(x, .001f * x, .002f * x * x), forward);
        }).ToArray();
        var curvedCar = new AmbientCarInteractionSample(5, 7.5,
            new AmbientTrafficRider("car-5", prediction[0].Position, prediction[0].Forward, 3,
                AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
                AmbientActorEnvelopeMath.CarHeightMeters, prediction));
        var largeStep = LocalPlayerCarInteractionMath.ClampRouteProgress(0, 24, 1.5, 0,
            1.72f, .5f, 1.61f, CurvedRider, [curvedCar]);
        if (largeStep.BlockingVehicleId != 5 || largeStep.AllowedRouteDistanceMeters >= 7.2)
            throw new InvalidOperationException($"Curved high-speed large-step interaction was not swept safely: {largeStep}.");

        GD.Print("[LocalPlayerCarInteractionReview] constraint math: wide pass, narrow follow, occupied steering, vertical separation, curved large delta PASS");
    }

    private static void SetSpeed(RideSessionNode session, double speedKph)
    {
        var state = session.CurrentRideState with { VirtualSpeedKph = speedKph };
        RideStateField.SetValue(session, state);
    }

    private static void SendSteering(Key key, bool pressed) => Input.ParseInputEvent(new InputEventKey
    {
        Keycode = key, PhysicalKeycode = key, Pressed = pressed
    });

    private static void AdvanceAtControlledSpeed(RideSessionNode session, RideWorldView world)
    {
        const double deltaSeconds = 1.0 / 60.0;
        var previous = session.CurrentRideState;
        var proposed = previous with
        {
            DistanceMeters = previous.DistanceMeters + 82 / 3.6 * deltaSeconds,
            VirtualSpeedKph = 82
        };
        var constrained = world.ConstrainLocalCarRideState(previous, proposed, deltaSeconds);
        RideStateField.SetValue(session, constrained);
    }

    private static RiderCollisionReviewSample PlayerSample(RideWorldView world) =>
        world.GetRiderCollisionReviewSamples().Single(sample => sample.RiderId == "player");

    private async Task WaitForWorld(RideWorldView world, int maximumFrames)
    {
        for (var frame = 0; frame < maximumFrames && world.RouteGeometry is null; frame++)
            await ToSignal(world.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (world.RouteGeometry is null) throw new InvalidOperationException("Production RideScreen did not finish loading the route.");
    }

    private async Task Settle(int frames)
    {
        for (var frame = 0; frame < frames; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static NVector3 ToNumerics(Godot.Vector3 value) => new(value.X, value.Y, value.Z);
}
