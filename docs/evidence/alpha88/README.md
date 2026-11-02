# Alpha.88 production RideScreen evidence

Captured with Godot 4.6.2 .NET and the production `RideScreen`, using the isolated test profile under `%LOCALAPPDATA%\Temp\WodraAlpha88-Isolated`.

- `side-full.png`, `front-full.png`, `three-quarter-full.png`: full rider and bicycle views.
- `legs-live-side-phase-*.png`: 85 rpm production side views at eight crank phases, each 45° apart.
- `pedals-side-phase-*.png`: close views of the moving pedal/shoe contact area at the same phases.
- `helmet-*.png`: production front, side, and three-quarter fit views; `helmet-pitch-0.png`, `helmet-pitch-3.png`, and `helmet-pitch-6.png` compare pitch only.
- `alternative-colours.png`: alternate jersey/helmet customization.
- `menu-alpha87-baseline.png`: reproduced alpha.87 ordering with media z-index 20.
- `menu-fixed.png` and `menu-closed-media-restored.png`: fixed modal ordering and media layout after closing.

The alpha.87 baseline and fixed menu screenshots show the same RideScreen state. In the baseline the media controls draw above the Settings content at the bottom edge. In the fixed image the modal dims and covers the media panel. The media panel returns to its original position after closing Settings.

The shoe test independently reads each skinned shoe mesh's vertices and uses the moving pedal forefoot contact to identify which physical end is toe. Across 72 cycle samples per side, the toe end is 117–118 mm closer to the forefoot contact than the heel end; mesh-axis error remains below 10°. No mesh/bone rotation was applied because the tested production geometry faces forward and remains on the pedal.

The 3° helmet pitch is retained: 0°/3°/6° comparisons plus the physical brow/eye/rear-shell markers show that alpha.87 pitched toward the rider's forehead; the helmet's accepted front/rear orientation remains unchanged.

