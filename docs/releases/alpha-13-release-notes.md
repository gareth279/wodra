# Wodra 0.1.0-alpha.13

This alpha completes the End Ride confirmation details from issue #32. It preserves the alpha.11 route and gradient inset and the alpha.12 modal sizing fixes.

## Changes

- End Ride now describes both an active FIT recording that will be finalized and any FIT segments already saved during the ride.
- When recording is off, the confirmation says whether saved FIT segments will be kept or that ending the ride will not create a FIT file.
- The compact, centered Ride Menu and End Ride layouts, settled container sizing, modal focus, Escape/cancel behavior, and input blocking remain in place.

## Verification

Debug and Release game builds and the rendered Godot modal layout smoke checks passed. The modal regression covers the no-recording, saved-segment, and active-recording copy states, settled control bounds, repeated menu/confirmation/cancel transitions, focus, input blocking, and resizing at 960×540, 1152×648, and 1920×1080.
