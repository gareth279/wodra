# Wodra v0.1.0-alpha.34

## Changes

- Fixed dummy trainer power and cadence controls appearing beside the route inset over the riding view. They now stay in the right-side inset column, below its complete route and elevation area. At shorter heights the bounded controls panel scrolls so the power and cadence actions remain reachable.
- Preserved the bounded left debug panel and its persistent ride controls.

## Verification

- EOS-enabled Debug and Release solution builds and regression checks passed.
- Godot rendered 64 simulated layouts at 960×540, 1152×648, 1366×768 and 1920×1080 with 0, 1, 2 and 12 simulated riders, real/dummy modes, inset on/off, rider joins/leaves, resizing, scrolling and dummy control hit-area checks. Screenshots were reviewed for the reported window size, compact-height scrolling, inset visibility and fullscreen-sized output.
- These are rendered simulated layouts; owner visual acceptance on the installed client remains outstanding for issue #34.
