# Wodra 0.1.0-alpha.12

This alpha completes the in-ride menu and End Ride confirmation layout work from issue #32. The alpha.11 route and gradient inset remains included.

## Changes

- Ride Menu content now fills the dialog's usable width, with a centered heading, balanced padding, and consistent action widths.
- Ride Menu and End Ride confirmation recalculate their own content size after Godot's container layout settles. Repeated view changes and viewport resizing no longer carry a previous view's sizing constraints forward.
- End Ride confirmation is compact and centered. Its explanation reflects whether a FIT recording is active, previously saved ride segments exist, or FIT recording is off.
- Modal focus, Escape/cancel handling, and the input-blocking backdrop remain in place.
- The alpha.11 route and gradient inset and responsive ride-summary layout are preserved.

## Verification

Debug and Release game builds and the Godot modal layout smoke checks passed. The modal regression exercised settled container sizing, repeated menu/confirmation/cancel transitions, keyboard focus, input blocking, and viewport resizing at 960×540, 1152×648, and 1920×1080. Windows display-scaling and full in-ride graphical acceptance remain for rider retest.
