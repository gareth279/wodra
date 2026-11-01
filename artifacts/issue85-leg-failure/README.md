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

## Accepted helmet and hands

The rebuilt GLB starts from the accepted issue #163 rider source. The owner-supplied vented shell and chin straps are visible in [side](after/head-side-helmet-on.png), [front](after/head-front-helmet-on.png), [three-quarter](after/head-three-quarter-helmet-on.png), and [rear](after/head-rear-helmet-on.png) production views. The Godot import reports 39,536 helmet vertices. Blender now derives face-forward from the weighted head and eye geometry, and identifies the separate central rear-retention component from the actual helmet mesh. A deliberately flipped source control put that hardware 23 mm ahead of the eyes; the exporter detected it and rotated the mesh in the rider's measured rest/world frame. The corrected Blender output put the hardware 211 mm behind the eyes. Godot independently measures the imported retention component 217 mm behind the eyes, a 0.845 face-to-bike-forward dot, and shell length 237 mm; the front/rear reference markers lie within 27/20 mm of the imported shell's measured end regions. The rendered front, side, and rear views show the brow facing travel and straps below the jaw. Helmet topology is unchanged. The editable Blender preservation check compares all 9,060 hand-weighted vertices and weights with the accepted source; the sculpted hands and fitted hood grip remain intact.

## Findings and validation

The alpha.85 lower-body rig used a five-bone chain whose helper split bones were treated as anatomical joints. Mirrored pole axes made the left knee bend behind the hip-to-ankle line, while old lower-body action curves moved the pelvis and leg anchors. The earlier fix corrected those knees and the pedal marker, but retained the source foot quaternion on the ankle/foot bone while a separate calibrated shoe bone carried the forefoot marker. In Godot, that left the actual foot-bone axis 36.5 degrees away from the shoe's heel-to-toe axis and its sole normal 155.5 degrees from the pedal normal. The old checks measured the shoe bone and forefoot marker, not the ankle/foot bone against the imported shoe, so the one-point contact passed while the foot still hung from the shin like a boot. The earlier rebuild also used the issue #159 blend, which did not carry the accepted issue #163 supplied helmet.

The correction keeps that knee solution and the accepted issue #163 rider source. It derives each shoe's heel-to-toe and outsole-up axes from the actual mesh and the pedal normal from imported pedal geometry. The ankle/foot and independently skinned shoe now share that measured frame; the toes follow bicycle-forward and the sole tracks the pedal platform with 4 degrees of phase-linked ankle pitch. The forefoot stays on its own moving pedal, while separate heel and toe markers check the rest of the sole through the full cycle. The source shoe's 128 mm thickness is scaled to 58.9 mm (58.88 mm in Godot's imported mesh bounds). The supplied vented helmet and chin straps, sculpted hands, clothing, upper body, and bicycle are retained.

Blender full-cycle metrics: 32.5–106.9 degree knee flexion, no backward knee bend, 17.2 mm maximum knee step, 4 degree maximum shoe pitch, foot-to-shoe axis error below 0.03 degrees, foot-to-pedal-normal error 4 degrees, 27 mm maximum heel-plane offset, 18 mm toe-plane offset, and forefoot/pedal contact below 0.002 mm. Godot's 72-phase imported review measures foot-to-shoe axis error at 0 degrees, foot-to-pedal-normal error at 4 degrees, toe and sole axes within 4 degrees of their targets, heel and toe outsole heights within 20/25 mm of the measured platform plane, and a 7.67 mm worst forefoot marker offset. Heel and toe remain above the platform plane throughout the cycle. Knee flexion remains 32.7–106.8 degrees with at most an 18.4 mm step and 0.05 mm leg-length variation. Live 85 rpm phase captures were reached through the production cadence update path on the requested route; all eight were visually inspected along with the side contact views, the accepted vented helmet and chin straps, and the rebuilt 480-frame, 16-second motion clip. The Godot importer reports 39,536 helmet vertices, and the Blender preservation check confirms unchanged helmet topology and all 9,060 hand-weighted vertex positions and weights.
