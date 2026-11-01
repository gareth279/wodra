# PR #164 helmet fit comparison

These are production `RideScreen.tscn` captures from Godot 4.6.2, rendered
with the OpenGL Quadro P620 renderer. The run used the Dummy Trainer at 85 rpm
on Dunedin One-way: Oval to Gardens Loop and the live cadence update path.
The before images are from the `de77ef8` baseline; after images are from the
rebuilt rigid helmet attached to the existing head bone. The rider, skeleton,
animation, bicycle, and helmet-forward transform were not changed.

## Before and after production head views

| View | `de77ef8` baseline | Fitted helmet |
|---|---|---|
| Side | ![Before side](../issue164-helmet-rigid/ride-screen-head-side.png) | ![After side](ride-screen-head-side.png) |
| Front | ![Before front](../issue164-helmet-rigid/ride-screen-head-front.png) | ![After front](ride-screen-head-front.png) |
| Rear | ![Before rear](../issue164-helmet-rigid/ride-screen-head-rear.png) | ![After rear](ride-screen-head-rear.png) |
| Three-quarter | ![Before three-quarter](../issue164-helmet-rigid/ride-screen-head-three-quarter.png) | ![After three-quarter](ride-screen-head-three-quarter.png) |

## Full rider at live crank phases

| Phase | `de77ef8` baseline | Fitted helmet |
|---|---|---|
| 0° | ![Before full bike 0](../issue164-helmet-rigid/ride-screen-full-bike-phase-000.png) | ![After full bike 0](ride-screen-full-bike-phase-000.png) |
| 90° | ![Before full bike 90](../issue164-helmet-rigid/ride-screen-full-bike-phase-090.png) | ![After full bike 90](ride-screen-full-bike-phase-090.png) |
| 180° | ![Before full bike 180](../issue164-helmet-rigid/ride-screen-full-bike-phase-180.png) | ![After full bike 180](ride-screen-full-bike-phase-180.png) |
| 270° | ![Before full bike 270](../issue164-helmet-rigid/ride-screen-full-bike-phase-270.png) | ![After full bike 270](ride-screen-full-bike-phase-270.png) |

## Measured source fit

The production head mesh measures 172.6 mm from eye level to the crown and
68.5 mm forward at the brow. The supplied shell bounds are 177.8 × 273.7 ×
149.6 mm. The shell now scales to 95% in each imported Blender axis and moves
31.5 mm rearward, keeping its front about 25 mm beyond the brow and its crown
about 10 mm above the measured scalp. Its existing origin and forward axis stay
unchanged. The molded front lip is fitted with the shell; strap vertices blend
from the existing under-jaw clasp to their fitted shell anchors, independently
of the shell surface.

The post-change views show the same forward-facing helmet with a shorter,
snugger shell, improved rear coverage, and straps meeting beneath the jaw. A
small amount of skin color remains visible through the model's open vent slots;
these captures should be reviewed for visual acceptance.

## Reproduction

From the repository root, export with Blender 5.2:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python tools/modeling/export_cycling_helmet_rigid.py
```

Then import and run `res://Tests/RiderFinishingSceneReviewTest.tscn` with Godot
4.6.2. Set `WODRA_VISUAL_REVIEW_OUTPUT` and
`WODRA_VISUAL_REVIEW_RESOLUTION=1920x1080` to choose the capture directory and
size. The harness records the eight live crank phases and the four full-rider
phase views above.

## Verification

- Blender 5.2 export completed and repeated with identical GLB SHA-256.
- Godot 4.6.2 imported the rebuilt GLB; the production RideScreen review scene
  reported `PASS` at 85 rpm.
- Debug and Release game builds passed (281 existing warnings, no errors).
- The existing rider-fit headless regression passed. The production rider GLB
  hash and runtime attachment/orientation code are unchanged.
- Shutdown RID diagnostics matched the `de77ef8` baseline counts: the same
  Canvas, CanvasItem, viewport, textures, shaped-text, font, and ObjectDB
  warnings appeared in the review runs. The helmet fit change did not add to
  those reported leak counts.
