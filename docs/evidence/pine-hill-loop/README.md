# Pine Hill loop review evidence

Run date: 2026-11-04. Godot 4.6.2 stable mono; rendering device: NVIDIA Quadro P620, OpenGL 3.3 Compatibility.

The production `RideScreen` review loaded the built-in Pine Hill package, rode three laps in one uninterrupted active session, crossed into lap 4, kept FIT recording active at each seam, then ended through the normal Ride Menu. It passed 21 production assertions. The final FIT contains 214 samples and records 3,978.9 m; the ride telemetry trace is in `pine-three-lap-ride.csv`.

## Captures

- `pine-route-selection.png`: normal route catalogue and elevation preview.
- `pine-route-camera-*.png`: production chase-camera views at 0, 200, 302, 600, 682, 917, 1100 and 1268 m.
- `pine-loop-lap*-before-seam.png` and `pine-loop-lap*-after-seam.png`: views on both sides of each lap crossing. Lap 4 is the wrapped state after completing three full laps.
- `pine-loop-three-laps-complete.png`: active ride after three completed laps.
- `pine-render-performance.csv`: 101 frames recorded during the ride on the Quadro P620. Median frame time was 29.875 ms (33.47 FPS); maximum was 38.946 ms (25.68 FPS). Median draw calls were 1,287. These measurements are the baseline for scenery optimization.
- `fit/wodra-20261104-151736.fit`: finalized FIT from the graphical run.

## Geometry result

The transformation is reproducible with `python tools/Wodra.RouteBuilder/rebuild-pine-hill-loop.py --apply`. Exact source indices, removed spur, final fingerprint, seam and heading measurements are in [`routes/review/pine-hill-test-loop-transform.json`](../../../routes/review/pine-hill-test-loop-transform.json).

The headless and graphical production review logs are generated under the local Godot user data and Windows temporary directory; the committed CSV, FIT and screenshots preserve the review artifacts used for this result.
