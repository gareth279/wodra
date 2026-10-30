# PR #160 cockpit iteration evidence

These stills were captured from the imported production GLB in
`RiderFinishingSceneReviewTest.tscn` at 1920×1080 using Godot 4.6.2 and the
production RideScreen camera setup. The matching full-resolution captures are
in `artifacts/issue158-road-bike/production-ridescreen/final-review-v4/`.

`reference-before-after.jpg` places a crop of the owner-provided Shimano 105
photograph beside the prior canopy and this candidate render. The source
photograph remains in the owner's `images/bike` folder and is not copied here.
This view shows that the canopy is gone and the lever now overlaps the rounded
control body at its pivot. The present candidate still looks too capsule-like
and lacks the reference's clear, sloped palm shelf and nose. It is included as
working review evidence, not as a claim of visual acceptance.

Other captures include side, top, front three-quarter, rider perspective,
both no-hands controls, hands on each hood, complete bike side/front views and
the normal chase camera. `tools/modeling/build_road_bike_158.py` rebuilds the
editable source and GLB. Stable hand targets are ±210 mm lateral, 421 mm
forward, 900 mm high in Blender bike-local coordinates, with rotation matching
the shallow palm surface. The existing rider pose and hand geometry are not
changed; final hand fit remains for issue #159.
