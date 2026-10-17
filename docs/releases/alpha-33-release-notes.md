# Wodra v0.1.0-alpha.33

## Changes

- Bounded the in-ride debug details inside a scrollable panel and reserved space for persistent pause, recording, menu, marker and jump controls.
- Positioned dummy trainer controls beside the route inset so the controls remain reachable without covering the inset.
- Corrected route inset projection so north is up and east is right. The rider marker and direction arrow use the same projection; the elevation profile is unchanged.
- Set `Wodra Ride` as the default name for manual and automatic Strava FIT uploads and add the Wodra releases link to descriptions. Explicit names and existing description text are retained.
- Applied the owner-approved cobalt helmet tortoiseshell cycling artwork to the Godot app/window, Windows executable, Velopack installer and packaged assets.

## Verification

- EOS-enabled Debug and Release solution builds passed.
- Existing regression suites passed, including route projection, HUD bounds and Strava metadata policies.
- Godot rendered 64 simulated layouts across 960×540, 1152×648, 1366×768 and 1920×1080 viewports, with 0, 1, 2 and 12 simulated riders, real/dummy modes, inset on/off, rider joins/leaves, resizing and scroll checks. This is rendered simulation evidence; no live online rider session or Strava upload was used.
- Owner visual acceptance of the exported icon/window, installer and upgraded shortcuts remains outstanding. No Strava activities were uploaded for testing.
