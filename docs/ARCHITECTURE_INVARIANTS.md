# Wodra architectural invariants

These contracts protect accepted behavior while features change. Each PR should identify the affected contract and run its listed regression. A test passing does not replace review of contracts that have no automated guard.

## Authoritative rider simulation

`RideSessionNode` advances `RideState` from trainer telemetry, route gradient, elapsed time, and the active rider/bike profiles through `SimulationEngine` and `RidePauseGate`. Ambient scenery is a downstream observer. With identical inputs, enabled/disabled traffic must produce identical ride state, speed, distance, and recorded inputs. Cars must never clamp progress or speed, steer the rider, rewrite measurements, or alter trainer resistance. A narrow road may still show a visual overlap when no safe passing space exists.

Implementation: `src/IndoorCycling.Game/Scripts/RideSessionNode.cs`, `src/IndoorCycling.Core/Simulation/SimulationEngine.cs`, `src/IndoorCycling.Game/Scripts/World/RideWorldView.cs`.

Automated enforcement: `RidePhysicsAmbientTrafficInvarianceReviewTest.tscn` exercises the production `RideScreen`, `RideSessionNode`, steering controller and controlled ambient car with paired deterministic traces. `AmbientTrafficFollowingReviewTest.tscn` retains core traffic decision and simulation invariance checks. Both are run by `.github/workflows/windows-validation.yml`.

## Traffic follows the rider

Traffic may adapt its own speed, lane, yield/overtake decisions and spawn choices after observing the rider's state and predicted path. Traffic has no authority over local rider movement. This direction keeps decorative traffic from becoming a hidden part of trainer-derived physics.

Implementation: `src/IndoorCycling.Game/Scripts/World/AmbientActorManager.cs` and `AmbientTrafficFollowingMath.cs`.

Automated enforcement: ambient following/yield tests and the production RideScreen car interaction scenes in `src/IndoorCycling.Game/Tests/`. The paired RideScreen invariance regression protects traffic-to-rider directionality.

## Measured data stays truthful

Measured power, cadence and heart rate come from device telemetry; optional sensors remain optional. Never fabricate or rewrite sensor values to resolve a collision, animation, UI or pacing problem. Trainer resistance commands follow the ride's route grade/difficulty policy, never a scenery decision.

Implementation: `src/IndoorCycling.Trainer/`, `src/IndoorCycling.Game/Scripts/DeviceConnectionPanelController.cs`, and `RideSessionNode.cs`.

Automated enforcement: device readiness/optional-heart-rate checks in `DeviceConnectionAutomationReviewTest.tscn`, telemetry policy tests in the trainer/physics regression suites, and FIT-input comparison in `RidePhysicsAmbientTrafficInvarianceReviewTest.tscn`. Device-specific behavior with physical BLE/ANT+ equipment still needs hardware acceptance.

## Accepted cyclist behavior is protected

Preserve alpha.52 local/AI cyclist separation, AI following and accepted rider presentation while fixing ambient traffic. Do not alter cyclist movement as a shortcut for car spacing; traffic work stays within car decisions unless an owner explicitly broadens scope.

Implementation: `src/IndoorCycling.Game/Scripts/World/AiRiderPopulation.cs`, `RiderTrafficSeparation.cs`, and `RideWorldView.cs`.

Automated enforcement: `RiderTrafficRideScreenReviewTest.tscn` checks populated production-scene rider/actor envelopes. These sampled scene checks do not prove every route or owner visual acceptance; changes to cyclist behavior require focused review and acceptance evidence.

## Multiplayer authority is explicit

Remote rider messages own remote route position, speed, power, cadence and pause state. Local visual smoothing/presentation offsets may be derived for rendering, but must not mutate messages, interpolator samples, or authoritative remote state. Local collision handling cannot silently rewrite a remote rider.

Implementation: `src/IndoorCycling.Game/Scripts/RideSessionNode.cs`, `src/IndoorCycling.Game/Scripts/Multiplayer/RemoteRiderInterpolator.cs`, and remote presentation in `RideWorldView.cs`.

Automated enforcement: multiplayer protocol/integration tests under `tests/Wodra.Multiplayer.*.Tests`. The boundary between visual smoothing and authoritative multiplayer state remains an architectural review requirement.

## Ride display and recorded activity agree

HUD progress, lap/elapsed summaries, actual ridden distance, FIT samples and uploaded activity inputs must derive from the authoritative session state and truthful telemetry. Presentation or scenery cannot introduce a second progression value. FIT finalization/segmentation must preserve recorded ride data across normal stop and recovery paths.

Implementation: `RideSessionNode.cs`, `RideActivityAccumulator.cs`, `RideFitRecorder.cs`, and `StravaIntegrationClient.cs`.

Automated enforcement: physics, recording, persistence and Strava regression suites, plus recording-input comparisons in the RideScreen invariance test. Actual third-party service acceptance and real-device FIT contents remain external acceptance checks.

## Camera and rendering do not own physics

The accepted alpha.49 camera framing and obstruction recovery must remain stable through route, world and actor changes. Camera smoothing, rendering interpolation and rider visuals may not move the authoritative rider or feed presentation offsets back into ride progress or steering.

Implementation: `src/IndoorCycling.Game/Scripts/World/ChaseCamera.cs`, `RideWorldView.cs`, and `RideSessionNode.cs`.

Automated enforcement: camera/framing and obstruction recovery review scenes in the Windows workflow. The accepted owner framing/visual result remains subject to in-game review; CI is not visual approval.

## Device connectivity is recoverable and optional where specified

Trainer readiness is required to start a trainer ride; optional heart-rate and steering devices must not block riding. Discovery, reconnect, cancellation, retry and recovery must not leak handlers, duplicate callbacks or leave stale actions. ANT+ FE-C behavior remains conditional on selected mode and available hardware.

Implementation: `src/IndoorCycling.Game/Scripts/DeviceConnectionPanelController.cs`, trainer connection/recovery code in `src/IndoorCycling.Trainer/`, and `docs/trainer-connectivity.md`.

Automated enforcement: trainer recovery and production device-connection automation scenes in `.github/workflows/windows-validation.yml`. Real Windows Bluetooth/ANT+ devices are still required for physical acceptance.

## Release integrity

Use the pinned Windows toolchain, approved EOS SDK/configuration and existing release workflow in `docs/windows-alpha-release.md`. Do not publish from an environment missing those capabilities or write-capable authentication. Preflight a clean reviewed commit and matching reachable tag; never move/overwrite a release tag. Keep secrets out of source, arguments and logs. Distinguish built, uploaded, published and anonymously verified artifacts. Verify ZIP, Setup, full package, bundled routes, offline startup, feed metadata and public hashes before claiming delivery. Documentation/test-only issue #107 does not authorize a release.

Implementation/operations: `publish.ps1`, `tools/GitHubReleaseAuth.ps1`, `.github/workflows/windows-alpha-draft.yml`, and `docs/windows-alpha-release.md`.

Automated enforcement: Windows validation runs `tests/GitHubReleaseAuth.Tests.ps1`; the release workflow/publisher checks toolchain, tag, package and release preflight. Public downloads and owner hardware/visual acceptance remain release-specific checks, not claims inferred from a successful build.

## Enforcement summary

Windows CI automatically checks the deterministic RideScreen traffic/physics invariant, ambient car behavior, device automation, regression suites, builds, release-auth safety, and selected production camera/rider scenes. Sensor truth, multiplayer authority boundaries, accepted alpha.52 cyclist behavior, general HUD/FIT/Strava coherence, owner visual acceptance and physical hardware behavior also require architectural review; only the named scenarios are automated, not every possible route, device or service interaction. Keep this distinction explicit in PRs.
