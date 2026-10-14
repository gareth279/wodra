# Wodra 0.1.0-alpha.11

This alpha adds the in-ride route and elevation overview and tightens the ride menu dialogs.

## Changes

- The ride HUD includes a compact route map with a direction marker and a lap-aware elevation profile. Position, direction, elevation and gradient follow the active route and ride distance, including lap wraps and catch-up repositioning.
- The route and gradient inset can be hidden from HUD & Display; that preference is saved between launches.
- Ride Menu and End Ride dialogs size to their content, stay within the viewport and scroll only when needed. Continue Riding and End Ride actions are aligned, keyboard focus starts on the safe action, Escape returns to riding, and the overlay blocks ride controls.
- Alpha.10's summary scrolling and responsive window defaults remain in place (1152×648 default, 960×540 minimum).

## Verification limits

Debug and Release builds, route overview regressions across all four built-in routes, summary-scroll regressions and headless Godot startup passed. This session cannot capture the Windows desktop, so visual inspection at laptop sizes, Windows display scaling checks and cycling-PC performance checks remain pending.
