# Minimal 3D ride view

The existing `RideScreen.tscn` now has a `RideWorldView` child. The menus and HUD
remain ordinary 2D controls drawn over the main viewport. There are no external
assets, physics bodies, network services, or additional trainer responsibilities.

## Data flow and responsibilities

`RideSessionNode` exposes read-only `CurrentRideState`, `CurrentRoute` and
`IsRideActive` properties. Its simulation remains the sole authority for distance.
`RideWorldView` reads these properties after the session's frame update. It never
reads trainer telemetry or accumulates its own progress.

- `VisualRoute`: pure .NET geometry and `GetPoseAtDistance`. Uses existing
  `RouteDefinition`, segments and GPS points; it is a rendering cache, not another
  route definition or simulation.
- `ProceduralRoad`: builds seven-metre road strips, markings and green shoulders.
  Subdivisions are at most ten authored metres apart and include every route point
  and segment boundary. Width is horizontal/unbanked. Geometry is built at ride
  start and disposed/replaced at the next route load, never rebuilt per frame.
- `RideWorldView`: builds ground, light, environment and a primitive bicycle/rider;
  positions and pitches the rider from the sampled pose. Hides the world and
  releases the current camera when the ride ends.
- `ChaseCamera`: follows eight metres behind and four metres above the rider,
  looks six metres ahead and uses frame-rate-independent exponential smoothing.
  It snaps into place at ride start, so it does not fly in from the previous route.

## Coordinates, distance and loops

One world unit is one metre. +X is east, +Y is up and -Z is north. The rider faces
Godot's local -Z direction. GPS coordinates use the first ordered route point as
origin (including its elevation) and a small-area equirectangular conversion:

```
X = (longitude - originLongitude) * radiansPerDegree * 6371000 * cos(originLatitude)
Z = -(latitude - originLatitude) * radiansPerDegree * 6371000
Y = elevation - originElevation
```

There is no horizontal or vertical scale multiplier and no hill exaggeration.
GPS spans interpolate by their existing authored `DistanceMeters` keys, not by
newly measured 3D arc length. Approximate GPS data therefore need not produce a
geometric length equal to the route's declared length; visible speed can differ
between spans while authored progress remains correct.

Without at least two GPS points, a circle of radius `routeLength / (2 * pi)`
provides the horizontal centreline. Elevation is integrated from the original
segment grades, beginning at zero. This is a placeholder layout, not real geography.

Some existing data is not a closed elevation profile: Test Route ends 70 metres
above its origin; Climb Test Route integrates to a positive net rise. Rendering
subtracts the endpoint-minus-start displacement using a smoothstep over the final
quarter of the route. `VisualRoute.ClosureStartFraction` controls that interval.
Thus these routes have a visual recovery descent to close the lap; the visual
slope in that portion can differ from the HUD/trainer grade. GPS routes that are
already closed, including the Dunedin loop, need no correction. No route data,
FIT elevation, simulation grades or trainer payloads are changed by this operation.
The correction vector is logged once on route load.

Sampling wraps only its local copy of distance modulo route length. Total ride
distance is never reset. Position is interpolated on the same centreline used to
build the road. An eight-metre symmetric lookaround gives a smoothed forward
direction across corners and the lap seam. Road cross-sections use that direction
to meet at the seam. The rider follows position exactly, without physics or lag;
only camera position and look target are smoothed.

The green shoulders follow road height. A large flat ground plane lies below the
lowest route point; this is deliberately simple scenery, not a terrain engine.

## Validation

From the repository root:

```powershell
dotnet build gwift.sln -c Debug -m:1
dotnet build gwift.sln -c Release -m:1
dotnet run --project tests/IndoorCycling.VisualRoute.Tests -c Debug
dotnet run --project tests/IndoorCycling.Recording.Tests -c Debug
```

Both focused test executables also support `-c Release`. The visual tests link the
production route definitions and sampler; they need neither Godot nor BLE hardware.

## Manual test in the already-open Godot editor

1. Stop a currently running game, accept/reload the externally changed scene if
   prompted, and run the project with F6/F5 as appropriate (F6 with RideScreen open,
   F5 for the configured main scene). Do not start a second editor.
2. Choose **Test with Dummy Trainer**, then **Flat Test Route**. Expect a grey road
   with pale edges, yellow centre line, green ground, a blue primitive cyclist with
   dark wheels and an orange head, and a camera above/behind it. The existing HUD
   should remain visible. Distance should increase and the rider should follow the
   road automatically.
3. Click **+ Grade** and **- Grade**. HUD grade/manual offset and the existing dummy
   simulation should respond. The road's geometry remains the selected route;
   manual offset changes trainer/simulation conditions, not geography.
4. Click recording **Start**, **Pause**, **Start** to resume, then **Stop**. The rider
   continues to follow session progress during a recording pause, as before.
   Expect the saved FIT path in status. **Save to Strava** should still identify
   the last file and report that upload is not wired.
5. Click **Menu**, **Resume**. The existing menu is an overlay, not a ride pause;
   progress continues. Then **Menu**, **Back To Menu** returns to route selection
   with pairing retained. Select **Climb Test Route**; expect a fresh start and
   rising/falling road height. The opening climb begins at 400 authored metres.
6. Repeat with **Test Route** and **Dunedin One-way: Oval to Gardens Loop**. Their
   GPS layouts should produce different road shapes. Let Flat Test Route run past
   3,000 total metres (roughly six minutes at 30 km/h): wrapped HUD distance should
   restart while total distance continues; rider/camera should cross the closed seam.
7. To test the real trainer, return through the existing navigation to the start
   screen, choose **Connect Trainer**, press one **Pair** button, wait for pairing,
   then **Continue**, select a route, and pedal. Expect the same view, now driven
   by the existing trainer/session telemetry. Test manual grade adjustment and
   route grade transitions using the already-proven FTMS setup.
8. Quit through **Menu**, **Quit**, including once with an active recording. Also
   try closing the running game window. Expect normal recording finalization and
   trainer disconnect. The editor Stop button is not a graceful application quit.

Optional: enable `DebugPoseLogging` on the scene's `RideWorldView` node in the
Inspector before running. Output prints total/wrapped distance, world position,
segment and route grade at most once per second. It defaults off and adds no HUD.

Rendering/lighting, camera behaviour and physical BLE operations require manual
runtime testing. Current zero-power speed fallback, recording-only pause semantics,
unfinished FIT replay and Strava upload remain unchanged. This view adds no camera
collision avoidance, terrain, animated pedalling, traffic or assets.
