# Wodra cyclist prototype

This is the accepted alpha rider and road-bike implementation from PR #89 and
issues #84–#85. It contains a complete MPFB human mesh, the default weighted
skeleton, fitted road-bike geometry, a baked three-revolution pedal action,
and an in-game cadence-driven preview. The owner approved the rider and road
bike for this alpha on 20 October 2026. Broader non-road disciplines remain
future work.

## Visual direction

Wodra uses a **stylised low-poly** direction: clean geometry, strong readable
colours, restrained surface detail, and believable human and bicycle proportions.
The simplified world is intended to make riding accessible and clear; block-
shaped riders are not the target. The complete weighted human, fitted kit, and
optional helmet stay intact. Natural movement takes priority over polygon count.

## Verified authoring tools

- Blender 5.2.2 LTS, installed at `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`.
- MPFB 2.0.18, installed in Blender 5.2's persistent `user_default` extension
  repository at `%APPDATA%\Blender Foundation\Blender\5.2\extensions\user_default\mpfb`.
- Godot 4.6.2 .NET.
- FFmpeg 9.0.2 (`Gyan.FFmpeg.Shared`) for encoding the Blender PNG sequence.

Install the reviewed FFmpeg package with:

```powershell
winget install --id Gyan.FFmpeg.Shared --exact --source winget --accept-source-agreements --accept-package-agreements
```

MPFB 2.0.18 is the official extension release for Blender 4.2 and later. Its
official versioned package is 45,956,992 bytes with SHA-256
`c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad`.
The download URL and hash are published by Blender's official extension index.
The archive was verified before installation. To repeat the installation in
the current Windows account:

```powershell
$archive = Join-Path $env:TEMP 'add-on-mpfb-v2.0.18.zip'
$url = 'https://extensions.blender.org/download/sha256:c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad/add-on-mpfb-v2.0.18.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
$expected = 'c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad'
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'MPFB archive hash mismatch' }
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --command extension install-file -r user_default -e $archive
```

The dependency is installed in the current Blender user's persistent extension
directory; only the downloaded staging archive is temporary. MPFB initializes
its own user data at `%APPDATA%\Blender Foundation\Blender\5.2\extensions\.user\user_default\mpfb`.

## Rebuild and export

From the repository root, generate a separate WIP source blend, GLB, and preview
sequence without overwriting the accepted static source blend or its previews:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python tools/modeling/build_wodra_cyclist.py -- tools/modeling/exports/WodraCyclist-animated-candidate.glb tools/modeling/checkpoints/WodraCyclist.animated-candidate-01.blend
```

The script uses MPFB's documented `LOWERUPPER` leg and arm helpers, samples
pedal motion every 5 degrees with a temporally regularized, per-leg pole solve,
and saves a 72 fps `PedalCycle` action lasting about 3.03 seconds in GLB and
covering three complete revolutions. The 72 fps authoring rate preserves the
original 60 rpm reference duration while doubling knee trajectory samples.
It exports the full weighted skeleton and human mesh. The aero tube profiles,
wheel build, and contact anchors live in `build_model()` separately from the
shared baked cadence action.

The rider profile remains generated from MPFB 2.0.18. To reproduce the current
review asset without changing the accepted checkpoint until generation passes,
run:

```powershell
$env:WODRA_MPFB_SOURCE = Join-Path $env:APPDATA 'Blender Foundation\Blender\5.2\extensions\user_default\mpfb'
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  --python tools/modeling/build_wodra_cyclist.py -- `
  tools/modeling/exports/WodraCyclist.knee-bike-candidate.glb `
  tools/modeling/checkpoints/WodraCyclist.knee-bike-candidate.blend --skip-previews
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  tools/modeling/checkpoints/WodraCyclist.knee-bike-candidate.blend `
  --python tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist.knee-bike-final.glb `
  tools/modeling/source/WodraCyclist.blend tools/modeling/previews/knee-bike-final --skip-previews
Copy-Item tools/modeling/exports/WodraCyclist.knee-bike-final.glb `
  src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb -Force
```

The build uses MPFB's installed source and redirects MPFB's temporary user data
to the OS temp directory. It does not write to Blender's profile log folder.

The build script can also render pre-bake fit/phase views and a side animation
sequence. These are authoring previews; Godot review captures below use the
actual imported GLB under route lighting.

Run focused Blender regressions on the saved source blend:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  tools/modeling/source/WodraCyclist.blend --python tools/modeling/check_baked_cycle.py
```

To regenerate the runtime GLB directly, pass
`src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb` as the first argument.
Open/import the Godot project after changing the GLB so its scene importer
refreshes the asset.

### Alpha.47 rider finishing

`source/WodraCyclist.alpha47.blend` is the editable full rig and bicycle source
for the alpha.47 hand/head polish. It preserves the accepted pedal action,
wheel/bike geometry, helmet accessory, and rider colours. The polish script
adds a 12 mm wrist-only adjustment against the existing runtime hood targets;
it does not alter elbow, leg, saddle, or pedal keys. The runtime shader adds a
small skin-only ambient contribution so facial planes remain readable in the
normal route shadows without changing the skin's base colour or the world
lighting. The head remains the full MPFB mesh. Export keeps the MPFB weighted
eye surfaces on their own warm ivory material, so the game applies rider
colour variants only to the outfit surface. Exploratory separate eye overlays
were rejected after their imported orientation looked detached.

Re-export the committed source without rebuilding the rider or changing the
source blend:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  tools/modeling/source/WodraCyclist.alpha47.blend --python `
  tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-alpha47-review.glb `
  tools/modeling/checkpoints/WodraCyclist.alpha47-review.blend `
  tools/modeling/previews/alpha47-review --skip-previews
```

`RiderFinishingSceneReviewTest.tscn` starts the production RideScreen with the
imported GLB and route HUD, checks settled side framing with debug on/off, and
captures actual Godot head/helmet, hood, side, and chase views. Redirect
`APPDATA` and `LOCALAPPDATA` into a writable local test directory when running
under a restricted account; Godot writes `user://` logs and automatic FIT data
during this ride-scene test.

### Focused helmet and contact polish

To apply the helmet/accessory and clothing/grip finish pass to the preserved
full-body rig and cadence action without regenerating the human or pose, run:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background tools/modeling/source/WodraCyclist.blend --python tools/modeling/polish_rider.py -- tools/modeling/exports/WodraCyclist-final.glb tools/modeling/checkpoints/WodraCyclist.final-helmet.blend tools/modeling/previews/helmet_toggle_02
```

This keeps the source blend unchanged. The script fits a separate head-bone
helmet and straps, preserves the pedal action, softens the clothing color hems,
adds toe coverage and compact brake hoods, and bakes a modest curl into the
existing finger bones across the action. It writes close helmet ON/OFF front,
side, and rear views; full-bike inspection views; and a two-revolution side
sequence with matching start/end crank poses. Add `--head-only` after the
preview directory to regenerate the six close helmet views and model export
without rerendering the bike or animation. Encode the clip with:

```powershell
$ffmpeg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build-shared\bin\ffmpeg.exe'
& $ffmpeg -y -framerate 12 -i tools/modeling/previews/helmet_toggle_02/animated_side/frames/frame_%03d.png -c:v libx264 -crf 20 -pix_fmt yuv420p -movflags +faststart tools/modeling/previews/helmet_toggle_02/animated_side/helmet_attached_two_cycles.mp4
```

`RiderBikePreview.tscn` provides a `Helmet` check button that hides the shell,
vent detail, rim, and straps together. It does not change the rider
rig, cadence playback, trainer data, or camera behavior.

### Alpha.47 owner-review follow-up — hand contact and camera framing

`source/WodraCyclist.alpha48-final-review.blend` and
`exports/WodraCyclist-alpha48-final-review.glb` are the editable and runtime
review candidates for the alpha.47 owner feedback. This is a review build, not
a published alpha. It starts from the preserved `WodraCyclist.alpha47.blend`,
keeps the full weighted human, bicycle, helmet toggle, pedal action, leg pose,
and accepted colours, then adjusts only the existing finger bones and wrist
root. Blender mesh-to-hood sampling identified the positive MPFB local X flex
direction and a 30 mm hand-root translation along the authored bike axis; no
elbow, leg, saddle, or pedal targets move. The eye surface receives a muted
warm-brown material; head geometry and world lighting are unchanged.

Reproduce the candidate export without regenerating the rider:

```powershell
$root = (Get-Location).Path
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  (Join-Path $root 'tools/modeling/source/WodraCyclist.alpha47.blend') --python `
  (Join-Path $root 'tools/modeling/polish_rider.py') -- `
  (Join-Path $root 'tools/modeling/exports/WodraCyclist-alpha48-final-review.glb') `
  (Join-Path $root 'tools/modeling/source/WodraCyclist.alpha48-final-review.blend') `
  (Join-Path $root 'tools/modeling/previews/alpha48-final-review') --skip-previews
```

Run the focused weighted-mesh contact regression on the saved blend. It samples
both hands at five frames from the baked pedal cycle and checks distal finger
segment distances against the actual hood surfaces:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  tools/modeling/source/WodraCyclist.alpha48-final-review.blend --python `
  tools/modeling/verify_hand_hood_contact.py
```

Refresh Godot's import, then run the production RideScreen review scenes. The
camera test records actual projected rider/bike bounds, HUD-safe center error,
debug state, window size, ride distance/speed, and collision clearance. It
checks settled Side and FrontThreeQuarter views at 1500×900 and 1920×1080 with
debug both on and off. The motion scene records 360 runtime frames at 30 fps
through pedalling, right steering, coasting, and restart:

```powershell
$env:APPDATA = (Resolve-Path '.godot-appdata').Path
$env:LOCALAPPDATA = (Resolve-Path '.godot-localappdata').Path
$godot = 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
& $godot --headless --path src/IndoorCycling.Game --editor --import
& $godot --path src/IndoorCycling.Game --scene res://Tests/RiderFinishingSceneReviewTest.tscn --rendering-method gl_compatibility
& $godot --path src/IndoorCycling.Game --scene res://Tests/RiderContactCameraReviewTest.tscn --rendering-method gl_compatibility
& $godot --path src/IndoorCycling.Game --scene res://Tests/RiderMotionReviewTest.tscn --rendering-method gl_compatibility
```

The in-game stills and matched alpha.47/candidate comparisons are under
`artifacts/alpha48-owner-review/`. The motion scene writes PNGs to
`artifacts/alpha47-owner-review/runtime-after/motion-frames/`; encode them with
the installed FFmpeg 9.0.2 shared build:

```powershell
$ffmpeg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build-shared\bin\ffmpeg.exe'
& $ffmpeg -y -framerate 30 -i 'artifacts/alpha47-owner-review/runtime-after/motion-frames/frame_%04d.png' `
  -c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p -movflags +faststart `
  'artifacts/alpha48-owner-review/after/pedalling-steering-coast-restart.mp4'
```

`ChaseCamera` retains its obstruction-resolution path. For fitted inspection
views, camera offset smoothing follows the current route basis, then a live
view-space correction centers the projected rider/bike bounds in the current
HUD-safe rectangle. Gameplay chase look-ahead is unchanged.

## Godot runtime and review

`CyclistVisual` imports the `Skeleton3D` and `PedalCycle` `AnimationPlayer` clip.
It samples the baked skeletal cycle from telemetry cadence; when cadence reaches
zero it holds the current pose and resumes from the same crank phase. Crank
rotation follows cadence independently, and both wheels continue to follow road
speed and wheel radius. The renderer consumes ride state and does not synthesize
trainer telemetry or change ride physics. The existing camera and route behavior
remain in place.

Run the actual Rolling City route preview:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn
```

It shows three riders at 55, 85, and 115 rpm; the left rider coasts and restarts,
and the production chase, side, and front three-quarter cameras cycle. For an
18-second local recording and actual in-game stills:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn --write-movie "$env:TEMP\Wodra-rider-preview.avi" --fixed-fps 30 --resolution 960x540 --quit-after 540 -- --capture-preview
```

The stills are saved in `src/IndoorCycling.Game/Tests/previews/` as
`RiderBikePreview-Chase.png`, `RiderBikePreview-Side.png`, and
`RiderBikePreview-FrontThreeQuarter.png`.

Capture a close, fixed-camera Godot clip with three complete cadence segments
(45, 85, and 120 rpm), then a coast and restart. Pick `rear`, `front`, or
`side`; outputs use the actual imported rider, animation, and route lighting:

```powershell
$godot = 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
$out = Join-Path (Get-Location).Path 'tools/modeling/previews/knee-review-rear.avi'
& $godot --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn `
  --write-movie $out --fixed-fps 30 --resolution 960x540 --quit-after 540 `
  -- --capture-knee-review --knee-view=rear
```

Repeat with AVI outputs `knee-review-front.avi` / `front` and
`knee-review-side.avi` / `side`, then encode to H.264:

```powershell
$ffmpeg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build-shared\bin\ffmpeg.exe'
& $ffmpeg -y -i tools/modeling/previews/knee-review-rear.avi -an -c:v libx264 -preset slow -crf 22 -pix_fmt yuv420p -movflags +faststart tools/modeling/previews/knee-review-rear.mp4
```

To compare all eight crank phases in Godot, pass `--quit-after 8000 -- --capture-rider-matrix`;
the option before `--` is an engine option and the matrix flag after it is a
preview argument. It writes `Rider-000` through `Rider-315` with rear, side,
and front captures.

### Leg helper and outfit export correction

`polish_rider.py` removes only the isolated, fully weighted MPFB
`helper-skirt` and `helper-tights` proxy face islands from the `Human` mesh.
The continuous weighted body and all skeleton weights remain. It bakes smooth
jersey/shorts/shoe masks to `RiderOutfitWeights`, marks that layer as Blender's
render color, and exports it as glTF `COLOR_0`. The Godot `Human` mesh shader
uses those three channels and computes skin as the residual; its jersey uniform
selects the rider variant while shorts, skin, and shoes keep fixed colors.

Regenerate an editable checkpoint and runtime GLB from the preserved helmet
candidate without rerendering Blender previews:

```powershell
$root = (Get-Location).Path
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  (Join-Path $root 'tools/modeling/checkpoints/WodraCyclist.final-helmet.blend') `
  --python (Join-Path $root 'tools/modeling/polish_rider.py') -- `
  (Join-Path $root 'tools/modeling/exports/WodraCyclist-fixed.glb') `
  (Join-Path $root 'tools/modeling/checkpoints/WodraCyclist.fixed.blend') `
  (Join-Path $root 'tools/modeling/previews/rider_export_fix') --skip-previews
Copy-Item tools/modeling/exports/WodraCyclist-fixed.glb `
  src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb -Force
```

Refresh the Godot import, then capture one rider at eight evenly spaced crank
phases from rear, side, and front-three-quarter views. This uses the actual
Rolling City route lighting and the review-only close framing; it does not
change the production camera:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' `
  --headless --editor --import --path src/IndoorCycling.Game
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' `
  --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn --resolution 960x540 `
  --quit-after 360 -- --capture-rider-matrix
```

The 24 stills are written to `src/IndoorCycling.Game/Tests/previews/` as
`Rider-000` through `Rider-315` with `Rear`, `Side`, and `Front` suffixes.
For the normal three-rider chase/side/front preview and an 18-second in-game
recording, use the regular movie command above with `--capture-preview`.

## Provenance and licensing

- MPFB code is GPL-3.0-or-later. Its base mesh, default rig, and related bundled
  assets are CC0-1.0. MPFB is an authoring dependency only and is not shipped in
  the game.
- The generated human and weighted rig come from MPFB; the cycling pose, fitted
  shoes, bicycle, clothing materials, helmet geometry, and export scripts are
  Wodra-authored. No external human model or motion-capture animation is used.
- The game ships the GLB only and has no Blender or MPFB runtime dependency.

## Accepted alpha implementation

The static candidate and sampled 0°, 90°, 180°, and 270° phases are provided for
visual inspection. The MPFB rig has 171 bones and the human mesh has 291 weight
groups. The side render shows saddle contact, forward torso lean, bent elbows,
hood contact, and feet on the pedals without bone stretching. The owner accepted
the rider and refined road bike for this alpha. The model remains intentionally
stylised low-poly; MTB/gravel/track geometry and broader OpenSim model research
are outside this release.

### Carbon road-bike polish (PR #89)

The focused road-bike finish is authored in `source/WodraCyclist.blend`; it
preserves the existing weighted rider, helmet accessory, and `PedalCycle`
action. `polish_rider.py` can be rerun on that saved blend without rebuilding
the human or changing the wheelbase, bottom bracket, saddle, crank length, or
pedal anchors. The head tube/stem junction is slimmed, platform pedals are
darkened and thinned at their existing anchors, and the hoods use a tapered
grip profile with separate brake blades. The rim profile is 40 mm radial depth,
dark carbon, with 18 fine dark spokes per wheel. The source generator has
matching frame, cockpit, and wheel dimensions.

Re-export a candidate and preserve an editable blend checkpoint before
promoting it to the runtime asset:

```powershell
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
& $blender --background tools/modeling/source/WodraCyclist.blend `
  --python tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-road-bike-review.glb `
  tools/modeling/checkpoints/WodraCyclist-road-bike-review.blend `
  tools/modeling/previews/road-bike-review --skip-previews
```

`inspect_wodra_glb.py` checks the normalized crank/pedal animation templates;
the complete thighs, shins, shoes, and knees are skinned parts of `Human`, not
separate runtime-scaled meshes. Run `check_baked_cycle.py` against the saved
blend to check the three-revolution seam and knee motion.

For bike-only and rider-on-bike side/front-three-quarter stills under the
Godot route lighting:

```powershell
$env:APPDATA = Join-Path (Get-Location).Path 'tools/modeling/previews/godot-user-data'
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' `
  --path src/IndoorCycling.Game --quit-after 640 `
  res://Tests/RiderBikePreview.tscn -- --capture-road-bike-review
```

Capture chase, fixed front, or fixed rear motion with
`--capture-road-bike-motion --motion-view=chase|front|rear`. Godot writes the
PNG sequence to `src/IndoorCycling.Game/Tests/previews/RoadBikeMotion/<view>/`.
Encode the first 300 frames as a 10-second 30 fps MP4 using the installed
FFmpeg package:

```powershell
$ffmpeg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build-shared\bin\ffmpeg.exe'
& $ffmpeg -y -framerate 30 -start_number 0 `
  -i src/IndoorCycling.Game/Tests/previews/RoadBikeMotion/front/frame%04d.png `
  -frames:v 300 -c:v libx264 -preset fast -crf 22 -pix_fmt yuv420p `
  tools/modeling/previews/road-bike-front-10s.mp4
```
