# Alpha.87 rider visual review

These captures come from Godot 4.6.2 Compatibility rendering of the production `RideScreen` with Dummy Trainer at 85 rpm. The pitch sequence holds the production side camera and compares the existing 0° fit with 3° and 6° extra forward pitch. The 3° result is the smallest visible brow-fit improvement; 6° lowers the front farther without a useful fit gain. The accepted front/rear alignment is preserved.

`fit-rider-side-full.png` shows the default teal jersey, dark shorts, and charcoal helmet. The navy/red and white/original images show alternative combinations. `fit-rider-front-three-quarter-full.png` shows the default combination from the front three-quarter camera. The live phase captures show the rigid helmet following the rider through top, forward, bottom, and rear pedal positions.

The helmet retains its supplied textured GLB and mesh. Shell tint preserves texture shading; dark atlas details retain the straps, clasp, and vents. Exposed skin remains under the existing separate skin mask in `RiderOutfitShader`.
