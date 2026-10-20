# Alpha.47 owner-review follow-up

These are before/after captures from the production `RideScreen` and imported rider asset, using the same route, capture code, lighting, and window sizes. `before/` uses the alpha.47 GLB; `after/` uses the focused hand-pose and camera update. They are local review artifacts, not release assets.

## Evidence

- `after/hand-left-hood-close.png` and `hand-right-hood-close.png`: actual runtime hand/hood views from each side. The wrists and hand pose were adjusted locally; fingers now curl toward and overlap the hood grip surfaces. The low-poly fingers still read somewhat stiff in macro view, so owner visual review remains appropriate. Evaluated palm/metacarpal vertices have a 12 mm median and 28 mm 90th-percentile gap to hood surfaces across all five checked cadence phases; distal finger groups have 6–26 mm medians.
- `after/pedalling-steering-coast-restart.mp4`: 12 seconds at 30 fps from the production RideScreen; includes pedalling, held right steering, coast, and restart.
- `after/camera-marker003-*`: Side and FrontThreeQuarter views after settling at the reproduced Rolling City segment, at 1500x900 and 1920x1080 with debug on/off.
- `after/ride-side-debug-*.png` and `ride-chase.png`: normal route-lighting views after centring and chase composition.
- `after/head-*-helmet-*.png`: helmet on/off head views. Inspection did not support a safe geometry edit in this pass; the existing face remains deliberately low-poly and simple.
- `after/camera-centering.jsonl`: projected subject bounds, intended HUD-safe center, measured center offset, speed, camera drift and clearance for each settled sample. Final settled offsets were below 0.02 horizontally and 0.025 vertically, with the entire projected bound inside the safe rectangle.

## Checks

- Blender evaluated weighted-hand-to-runtime-hood surface regression: both hands, five crank phases, all five distal finger groups.
- Godot review exercised the production `RideScreen`, marker 003 and the 740m segment, both inspection views, two window sizes, debug on/off, and 6/30/60/120-frame settling samples.
- Motion capture exercised 85 rpm, steering right, coast at zero cadence, and restart at 85 rpm over 360 frames.

No alpha package was built or published for this review.
