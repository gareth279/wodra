# Issue #159 production RideScreen review

All images below were captured from the production `RideScreen.tscn` in Godot
4.6.2 using the NVIDIA Quadro P620 renderer. Still views use a 1500×900 review
viewport; the motion review ran 360 frames at 30 fps with a 960×540 requested
window (the host produced a 1177×670 viewport). The bicycle is the accepted
Alpha.84 CGTrader model.

## Before and after

The matched production side and chase views compare the original upright rider
with the current fitted rider. Full-view captures are included below as well.

| Original baseline | Fitted production view |
| --- | --- |
| ![Original production side view](before/production-side.png) | ![Fitted production side view](after/production-side.png) |

| Original chase view | Fitted production chase view |
| --- | --- |
| ![Original chase](before/production-chase.png) | ![Fitted chase](after/production-chase.png) |

![Fitted rider and bicycle, front three-quarter](after/rider-bike-front-three-quarter.png)

The full side and front-three-quarter close views are from the final RideScreen
test. The original baseline RideScreen captures used a 1177×670 window while
the current fit review uses a 1500×900 SubViewport. The before/after side and
chase images use the same named production camera views, but their viewport
aspect ratios differ.

## Hood grip close-ups

These are matched production captures of the previous PR pose and the revised
hand pose, taken at the same hand-review camera and viewport size. The owner’s
hand-on-hood photograph is `images/bike/IMG_1465.JPEG` and remains outside the
repository.

| Left hood | Right hood |
| --- | --- |
| ![Left hand on hood](after/hand-left-hood.png) | ![Right hand on hood](after/hand-right-hood.png) |

| Previous left hand | Revised left hand |
| --- | --- |
| ![Previous left grip](before/hand-left-hood-close.png) | ![Revised left grip](after/hand-left-hood.png) |

| Previous right hand | Revised right hand |
| --- | --- |
| ![Previous right grip](before/hand-right-hood-close.png) | ![Revised right grip](after/hand-right-hood.png) |

The elevated hand views show the opposed thumb and the other four digits
wrapping the outside edge from above:

| Previous left elevated | Revised left elevated | Previous right elevated | Revised right elevated |
| --- | --- | --- | --- |
| ![Previous left elevated grip](before/hand-left-hood-elevated.png) | ![Revised left elevated grip](after/hand-left-hood-elevated.png) | ![Previous right elevated grip](before/hand-right-hood-elevated.png) | ![Revised right elevated grip](after/hand-right-hood-elevated.png) |

The source hand is a weighted 19,158-vertex mesh with distinct metacarpals and
three phalanges for every finger and thumb. Its modeled chain lengths are in a
human hand range (15–36 mm per phalanx); inspection showed the stretch was in
the pose, not missing finger geometry or weights. The prior common local-Euler
curl used the wrong direction on several mirrored phalanges because their
hinge rolls differ, leaving the fingers extended across the hood. The polish
pass narrows the metacarpal fan, resolves separate mirrored thumb/finger
points against the actual integrated cockpit mesh, and bakes the weighted
three-phalanx chains through the full 217-frame action. The thumb target is on
the inboard side; the four finger targets wrap the outboard hood edge.
Temporary Blender IK constraints and targets are removed after baking. Wrist,
arm, torso, pelvis, legs, bike geometry and runtime systems are unchanged.

`verify_hood_finger_pose_159.py` samples every frame and checks that all ten
fingertips stay within 15 mm of the cockpit, each finger chain has visible
flex, and the thumbs remain opposed to the fingers. The final canonical-source
run measured a worst fingertip-to-cockpit gap of 4.5 mm and minimum combined
finger-joint bend of 49.36°. This geometric check is paired with the close
production renders above; neither is used as a substitute for the other.

## Motion

[12-second production RideScreen clip: pedalling, steering, coasting and restart](pedalling-steering-coast-restart.mp4)

Selected production frames:

| Pedalling | Steering | Coast | Resume |
| --- | --- | --- | --- |
| ![Pedalling frame](after/motion-pedalling.png) | ![Steering frame](after/motion-steering.png) | ![Coasting frame](after/motion-coasting.png) | ![Resuming frame](after/motion-resuming.png) |

## Regression measurements

`RiderFinishingSceneReviewTest.tscn` sampled 72 phases across the imported
pedal cycle. Maximum palm-center distance from the hood targets was 20 mm on
each side; pelvis distance from saddle target was 96 mm; ankle origins moved
5–340 mm through the sampled cycle. These are fit/animation continuity checks,
not a substitute for visual contact or pedal-pressure validation.

The model verifier reports 947,270 triangles across 25 render meshes, versus
935,442 triangles and 24 meshes on the accepted bike-only Alpha.84 asset. This
is +1.3% triangles and one additional mesh (the merged three-surface garment
detail mesh). The final GLB grew from 36,563,576 to 36,912,212 bytes (+0.95%).
No separate GPU-time or multi-rider frame-time benchmark was run for this
rider-only change; these small geometry/draw-mesh deltas are the measured
overhead.

The rider remains stylised. The revised fingers curl around the hood edge and
the thumb opposes them, a closer visual match to the owner's relaxed grip than
the former open, extended pose. Close production views are included so the
remaining low-poly hand appearance can be judged directly; no claim of exact
photorealistic anatomy is made. Physical hardware and real-ride acceptance
have not been performed.
This PR does not change the bike geometry, trainer, ride physics, telemetry,
steering, recording, routes or multiplayer behavior.
