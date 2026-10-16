# Wodra v0.1.0-alpha.31

## Changes

- Renamed the steering device card to **Controller**.
- Added a saved **Device type** preference with Auto-detect (currently supports Elite Sterzo Smart) and explicit Elite Sterzo Smart choices. Both use the existing Sterzo adapter; this is separate from protocol preference.
- Shows the connected controller name, connection state and live steering angle in degrees, with Connect, Centre and Disconnect actions.
- Fixed pre-ride Settings navigation so backing out of Advanced ANT+ setup returns to the connection screen. Settings opened from the active ride menu still returns to Ride Menu and Continue Riding.
- Escape and controller cancel follow the Settings back path, and focus returns to an enabled action on the caller screen.

## Verification

- Windows Debug and Release builds and the regression suites passed.
- Rendered RideScreen smoke checks passed at 1138×675 and fullscreen, including controller choices, settings persistence, pre-ride back navigation, Escape, repeated opens, and the active-ride menu path.
- The controller live angle in the visual smoke check is simulated UI data. Physical Sterzo and heart-rate hardware acceptance remain outstanding.
- No ANT+ or trainer transport behavior was changed.
