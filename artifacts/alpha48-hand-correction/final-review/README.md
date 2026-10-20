# PR #94 hand-export review

These captures compare the previous exported rider with the corrected export.
They are diagnostic evidence for the helper-geometry regression; they do not
constitute owner acceptance.

## First failing stage

`blender-left-hand-solid.jpg` and `blender-left-hand-wire.jpg` show the six
controlled Blender stages. Stages 1–3 use the unposed hand to compare the fresh
MPFB source, production mesh with the armature off, and production rig reset.
Stages 4–6 show the authored grip, the previous GLB reimport, and the
mask-baked GLB reimport. The previous GLB first shows sharp faces at fingertips
and the wrist; the authored source and corrected reimport do not.

The polygons are MPFB rig-control helpers, not a deformation of the hand
skeleton. One traced left fingertip face (source face 13648; vertices
13970, 13971, 13967, 13966) is weighted to `JointCubes`, `Left`,
`joint-l-finger-3-1`, and `metacarpal2.L`. A right wrist example (face 14016;
vertices 14460, 14456, 14457, 14461) is weighted to `JointCubes`, `Right`,
`joint-r-hand-3`, and `wrist.R`. The Blender authoring `Mask` removes these
helper faces, but Blender 5.2.2's glTF export omitted that modifier. The prior
export consequently sent the helpers to both Blender reimport and Godot.

The earlier helper-panel cleanup had also removed 24 real wrist seam quads per
hand because those vertices had full `helper-tights` weights. The repair restores
those 48 faces from a matching MPFB 2.0.18 Human mesh after verifying identical
vertex coordinates and deformation weights. The export keeps the eye material
explicitly: MPFB's body mask hides those 152 intentionally unweighted eye faces
along with its helpers.

The source contains 15,200 Human polygons. The MPFB mask evaluates to 13,378
body polygons; 1,774 rig-helper polygons are excluded from export and 152 eye
polygons are retained. The export keeps 19 shape keys and 291 weight groups.
Blender's exact GLB contains one 171-joint skin, `PedalCycle`, and one
`JOINTS_0`/`WEIGHTS_0` set. The exporter itself warned that it selected and
normalized the four highest weights, so the earlier four-influence edit was not
the source of the fragments.

## Runtime evidence

`godot-hand-contact-before-after.jpg` pairs matching close/elevated captures
from the production RideScreen before and after the export correction. Both use
the same review harness, camera placement, ride scene, and lighting. It shows
the finger/wrist fragments disappearing while the accepted hand pose and
runtime hoods remain in place.

`godot-ride-motion.webp` is a 12-second, 30-fps frame capture from the production
RideScreen on Rolling City. It shows pedalling at 85 rpm, right steering,
coasting, and restarting. The rider uses the corrected GLB. Separate runtime
close stills are in `../godot-phase-zero/hand-left-hood-close.png`,
`hand-left-hood-elevated.png`, `hand-right-hood-close.png`, and
`hand-right-hood-elevated.png`.

For a direct import check without Wodra rider scripts, run
`res://Tests/HandGlbMinimalReviewTest.tscn`. It asserts both Human material
surfaces and the embedded animation load, then plays `PedalCycle`. The
production RideScreen was also run through
`res://Tests/RiderFinishingSceneReviewTest.tscn`.

## Reproduce

The editable source is `tools/modeling/source/WodraCyclist.alpha48-mask-export-fixed.blend`.
The exporter can regenerate the GLB without rerunning character or bike edits:

```powershell
$root = (Get-Location).Path
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  (Join-Path $root 'tools/modeling/source/WodraCyclist.alpha48-mask-export-fixed.blend') `
  --python (Join-Path $root 'tools/modeling/polish_rider.py') -- `
  (Join-Path $root 'tools/modeling/exports/WodraCyclist-alpha48-hand-mask-fixed.glb') `
  (Join-Path $root 'tools/modeling/source/WodraCyclist.alpha48-mask-export-fixed.blend') `
  (Join-Path $root 'tools/modeling/previews/alpha48-hand-mask-fixed') --export-only
```
