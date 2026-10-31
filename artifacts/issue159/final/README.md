# Issue #159 — production rider and hood-grip review

All stills are captures from the production `RideScreen.tscn` with the imported
candidate in Godot 4.6.2 on the NVIDIA Quadro P620. The matched hand, helmet,
and whole-rider before/after captures use the same production review cameras at
1500×900. The motion test rendered 360 production frames at 30 fps at
1177×670. The accepted Alpha.84 CGTrader bicycle is unchanged.

## Rider fit

| Original baseline side | Current fitted rider side |
| --- | --- |
| ![Baseline side](before/fit-rider-side-full.png) | ![Current side](after/fit-rider-side-full.png) |

| Original baseline chase | Current fitted rider in gameplay chase |
| --- | --- |
| ![Baseline chase](before/ride-chase.png) | ![Current chase](after/ride-chase.png) |

![Current fitted rider from front three-quarter](after/ride-front-three-quarter.png)

The new model retains the PR's accepted forward cycling posture, fitted jersey,
bib shorts, shoes and helmet. This hand pass did not alter the bicycle or change
the rider's torso, pelvis, legs, or clothing mesh.

## Helmet fit

| Previous production helmet | Current fitted Wodra helmet |
| --- | --- |
| ![Previous helmet in RideScreen](before/head-three-quarter-helmet-on.png) | ![Current helmet in RideScreen](after/head-three-quarter-helmet-on.png) |

The supplied `sport_cycling_helmet` OBJ, MTL, diffuse map, and preview renders
were recovered from the owner's local `wodra_bikes` archive. I tested the mesh
in production RideScreen and retained an optional Blender import path, but did
not ship it in the production GLB: its shell landed over the rider's face in
the actual head views. The production GLB keeps the Wodra shell, with a smaller
crown, outward normals, vent patches, neutral materials, and head-following
chin straps. The supplied OBJ remains staged for further alignment work.

## Hands on the hoods

The prior Wodra hand pose had visibly stretched fingers hanging past the hood.
The current hand surfaces come from the closed road-control grip in the
owner-supplied Andy Woodhead cyclist model (CC BY 4.0). The actual Godot renders
below show the complete source grip geometry fitted to the Wodra hood surfaces.
The owner's photograph at `C:\Users\garet\OneDrive\wodra\images\bike\IMG_1465.JPEG`
was inspected as the primary grip reference and remains external; it is not
redistributed in this PR.

| Left hand, previous | Left hand, current |
| --- | --- |
| ![Previous left hand](before/hand-left-hood-close.png) | ![Current left hand](after/hand-left-hood-close.png) |

| Right hand, previous | Right hand, current |
| --- | --- |
| ![Previous right hand](before/hand-right-hood-close.png) | ![Current right hand](after/hand-right-hood-close.png) |

The elevated views show the palm support and opposed thumb around each control:

| Previous left elevated | Current left elevated | Previous right elevated | Current right elevated |
| --- | --- | --- | --- |
| ![Previous left elevated](before/hand-left-hood-elevated.png) | ![Current left elevated](after/hand-left-hood-elevated.png) | ![Previous right elevated](before/hand-right-hood-elevated.png) | ![Current right elevated](after/hand-right-hood-elevated.png) |

![Cockpit from rider viewpoint](after/cockpit-rider-view.png)

This is a visible improvement over the prior open, extended-finger silhouette:
the replacement fingers curl around the hood/lever body and the thumb closes
on the inboard side. The same closed-grip sculpture is scaled to 0.250 from
its previous 0.315 fit, reducing its palm and finger envelope while preserving
thumb opposition and hood contact. Each hand remains a single posed sculpture
rigidly attached to its wrist; its fingers do not articulate independently. The images are
provided for owner visual acceptance, which has not yet been granted.

## Motion

[12-second production RideScreen clip: pedalling, steering, coasting and restart](after/pedalling-steering-coast-restart.mp4)

| Pedalling | Steering | Coast | Restart |
| --- | --- | --- | --- |
| ![Pedalling](after/motion-pedalling.png) | ![Steering](after/motion-steering.png) | ![Coasting](after/motion-coasting.png) | ![Restart](after/motion-resuming.png) |

Godot's production `RiderMotionReviewTest.tscn` captured 360 frames at 30 fps
and passed its steering displacement and phase assertions. Both wrist-following
grips remain in the production ride during cadence, steering, coast and resume.

## Fit and performance checks

`RiderFinishingSceneReviewTest.tscn` passed in the production RideScreen and
sampled 72 phases across `PedalCycle`. It measured maximum sculpted
palm-contact-to-hood distance of 0 mm left and right, pelvis-to-saddle distance
of 96 mm, and ankle-origin travel of 5–340 mm. These are fit/continuity checks;
the screenshots remain the evidence for visual shape. The exported
`palm_contact.L/R` frames follow the wrist. The rider and accepted bicycle retain
their existing animation interfaces.

The final GLB contains 958,119 triangles across 25 render meshes and is
32,536,224 bytes. The pre-hand baseline contains 947,270 triangles across 25
meshes and is 36,912,232 bytes: the sculpture adds 10,849 triangles (+1.15%)
while the exported file is 4,376,008 bytes smaller (-11.86%). No separate
GPU-time or multiplayer frame-time benchmark was run for this rider-only
change, so the smaller file is not presented as a frame-rate improvement. The
owner should still check visual quality and physical riding acceptance on the
Windows gaming machine. No BLE/ANT+ hardware or real ride was tested here.

The Godot test processes report pre-existing renderer/Resource RID leak
warnings during shutdown after their PASS markers. They still exited zero on
the final graphical production runs. This is the isolated headless/scene
shutdown issue tracked by #56; no engine changes are included in this PR.
