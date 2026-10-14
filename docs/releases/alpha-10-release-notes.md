# Wodra 0.1.0-alpha.10

This public Windows x64 alpha improves window sizing and responsive layout.

## Changes

- Ride Summary now keeps route, date, duration, distance and laps visible while detailed statistics, FIT paths and screenshot filenames scroll in a bounded panel.
- Launch, trainer connection, route selection and in-ride menu content scroll within the available panel space, keeping actions reachable at smaller window sizes.
- The default window is 1152×648 with a 960×540 minimum. The output pixel area is about 1.2% lower than the previous 1024×738 viewport.
- Windowed size and fullscreen preference are remembered. F11 and Alt+Enter toggle fullscreen; the in-ride display menu also provides the toggle.

## Verification limits

Numerical layout regressions, Windows builds and Godot headless startup passed. Actual screenshot inspection, Windows 125%/150% display scaling checks and performance checks on the AMD Radeon R7 350 cycling PC remain pending.
