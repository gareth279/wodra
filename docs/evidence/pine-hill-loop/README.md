# Pine Hill loop and scenery review evidence

Captured 2026-11-04 with Godot 4.6.2 stable Mono on an NVIDIA Quadro P620 (OpenGL 3.3 Compatibility).

## Production RideScreen proof

The normal route catalogue loaded Pine Hill Test and started the production `RideScreen` with the simulated trainer. One uninterrupted active session crossed the seam three times, advanced the HUD from lap 1 through lap 4, kept FIT recording active, and ended through the normal Ride Menu. The graphical review passed 21 production assertions. The latest FIT contains 214 samples and 3,978.7 m; the ride telemetry is in [`pine-three-lap-ride.csv`](pine-three-lap-ride.csv).

- `pine-route-selection.png`: route catalogue and elevation preview.
- `pine-loop-lap*-before-seam.png` and `pine-loop-lap*-after-seam.png`: production camera views around each crossing.
- `pine-loop-three-laps-complete.png`: active RideScreen after three completed laps and before manual end.
- [`fit/wodra-20261104-164726.fit`](fit/wodra-20261104-164726.fit): finalized FIT from the latest graphical run.

The deterministic loop transformation is `python tools/Wodra.RouteBuilder/rebuild-pine-hill-loop.py --apply`. Source indices, the removed out-and-back, final fingerprint, seam and heading measurements are in [`pine-hill-test-loop-transform.json`](../../../routes/review/pine-hill-test-loop-transform.json). The final route is 1,322.354 m with an exact seam and continuous direction/elevation.

## Full-route scenery comparison

Fourteen aligned footage checkpoints are paired with production Godot route-camera renders in:

- [`pine-source-game-comparison-1.jpg`](pine-source-game-comparison-1.jpg)
- [`pine-source-game-comparison-2.jpg`](pine-source-game-comparison-2.jpg)
- [`pine-source-game-comparison-3.jpg`](pine-source-game-comparison-3.jpg)

There are 18 rendered route-camera views in `pine-route-camera-*.png`, including the corrected corner and lap seam. The route package now has timestamp-linked, estimated building styles for all 357 footprints (28 existing reviewed overrides retained; 329 neighbourhood-level estimates added) and 25 added roadside features across nine video checkpoints. The added feature types cover verges, trees, hedges, a fence and utility poles. Existing authored Blender assets and the six road markings remain. The added individual house identities, materials, roof forms and feature offsets are explicitly estimates rather than surveyed facts; generation reports are in [`pine-hill-video-style-report.json`](../../../routes/review/pine-hill-video-style-report.json).

These comparisons do **not** support calling the result video-faithful. Houses and vegetation still read as simplified game geometry, and the current route scene does not reproduce several visible details in the footage, including parked cars, detailed kerbs and the full streetscape. These captures are included so visual acceptance can be judged directly; additional scenery iteration remains necessary.

## Quadro P620 performance

| RideScreen capture | Frames | Median frame time | P95 frame time | Median draw calls | Median primitives |
|---|---:|---:|---:|---:|---:|
| Before scenery styling | 101 | 29.875 ms | 33.801 ms | 1,287 | 1.21M |
| Current route scene | 118 | 25.264 ms | 29.644 ms | 1,345 | 1.36M |

Raw measurements are in [`pine-render-performance.csv`](pine-render-performance.csv) and [`pine-render-performance-final.csv`](pine-render-performance-final.csv). Median frame time improved in this capture while draw calls and primitives increased; the latter show the cost of additional scenery detail. [`pine-render-performance-visual-review.csv`](pine-render-performance-visual-review.csv) contains the separate 200-frame visual review sample.
