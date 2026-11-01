# Alpha.85 rider leg and pedalling correction

These are genuine captures from the Godot 4.6.2 production `RideScreen` on the NVIDIA Quadro P620. The after images use Dummy Trainer at 85 rpm on `Dunedin One-way: Oval to Gardens Loop`; the eight close side views are reached through `CyclistVisual`'s live cadence update. The motion clip contains 480 production frames at 30 fps (16 seconds), including low and high cadence, steering and camera changes, coasting, stopping, and restart.

## Before and after

| Alpha.85 production baseline | Corrected production model |
| --- | --- |
| ![Alpha.85 rider side view](before/fit-rider-side-full.png) | ![Corrected rider side view](after/fit-rider-side-full.png) |

The before captures were taken from the unmodified alpha.85 GLB and runtime in an earlier RideScreen review. The route scenery differs from the corrected run; both are actual production render captures.

## Live 85 rpm crank phases

- [0 degrees](after/legs-live-side-phase-000.png)
- [45 degrees](after/legs-live-side-phase-045.png)
- [90 degrees](after/legs-live-side-phase-090.png)
- [135 degrees](after/legs-live-side-phase-135.png)
- [180 degrees](after/legs-live-side-phase-180.png)
- [225 degrees](after/legs-live-side-phase-225.png)
- [270 degrees](after/legs-live-side-phase-270.png)
- [315 degrees](after/legs-live-side-phase-315.png)

## Complete production motion capture

[Download or preview the 16 second RideScreen clip](after/ride-motion-review.mp4).

## Findings and validation

The alpha.85 lower-body rig used a five-bone chain whose helper split bones were treated as anatomical joints. Mirrored pole axes made the left knee bend behind the hip-to-ankle line, while old lower-body action curves moved the pelvis and leg anchors. The test only checked the shoe-ball marker, so it passed despite broken knees and boot-like shoes. Runtime selection also chose the first `PedalCycle` clip, whose feet were static, while the correct clip was imported alongside it.

The fix collapses helper bones into anatomical thigh and shin bones, rebuilds a deterministic two-segment knee solve on the forward side, removes stale pelvis/leg action channels, keeps hips seated, scales the shoe height while preserving the outsole point, and explicitly exports the `RiderOutfitWeights` mask as glTF `COLOR_0`. Runtime animation selection now requires the fully keyed bilateral foot clip.

Blender full-cycle metrics: 32.5–106.9 degree knee flexion, no backward knee bend, 17.2 mm maximum knee step, 7 degree maximum ankle articulation, and outsole/pedal contact below 0.002 mm. Godot imported-model review: 32.7–106.7 degree knee flexion, 19.2 mm maximum knee step including cycle seam, 0.05 mm maximum thigh/shin length change, 7.61 mm worst ball marker offset, and verified jersey/shorts/shoe/skin vertex masks. The exact side-phase views above are also part of manual visual review; numeric markers are not treated as a substitute.
