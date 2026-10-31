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

### Alpha.48 relaxed hood-grip candidate

`source/WodraCyclist.alpha48-mask-export-fixed.blend` is the preserved starting
blend and `source/WodraCyclist.alpha48-relaxed-hood-grip-v14.blend` is the
editable v14 grip candidate. `author_hood_grip_pose.py` adjusts the existing
`PedalCycle` action only: it narrows the measured metacarpal fan, adds a varied
MCP/PIP/DIP curl so the fingers wrap from their bases, and applies a 0.1 rad
mirrored wrist rotation. It does not scale bones, move the forearms, change
skin weights/topology, or alter the legs and crank animation. Local
metacarpal signs were selected from evaluated distal finger-centre spacing;
the wrist signs came from a small wrist-axis sweep against the actual hood/lever
meshes. The weighted skin passed the five-phase contact regression and the
existing proxy/wrist-face export guard.

The visual reference is this close road-cycling hood grip photograph:
[Cyclist's road hood grip](https://unsplash.com/photos/a-close-up-of-a-person-riding-a-bike-Kg6bZocKRrE).
The in-game side/elevated captures under
`artifacts/rider-finishing-alpha48/grip-v14/after/` show both
hands against the actual imported brake hoods. The fixed chase view and
side-view debug on/off captures are beside them. The continuous 12 second
RideScreen motion clip (pedalling, steering, coasting, restart) is
`artifacts/rider-finishing-alpha48/grip-v14/after/godot-motion-preview-v14.mp4`.
The five-phase Blender regression is supporting evidence; owner visual review
remains necessary.

Re-export the editable candidate without rebuilding the rider:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  tools/modeling/source/WodraCyclist.alpha48-relaxed-hood-grip-v14.blend --python `
  tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-alpha48-relaxed-hood-grip.glb `
  tools/modeling/source/WodraCyclist.alpha48-relaxed-hood-grip-export.blend `
  tools/modeling/previews/alpha48-relaxed-hood-grip --export-only
```

Open the resulting GLB in the production RideScreen using
`RiderFinishingSceneReviewTest.tscn`; continuous pedal, steering, coast, and
restart frames are captured by `RiderMotionReviewTest.tscn`.

### Alpha.48 cockpit reference comparison (v22, owner review pending)

The next cockpit pass used one modern road-control family throughout. SRAM's
[AXS controls guide](https://www.sram.com/en/learn/tech-deep-dives/axs-controls)
describes the narrower, longer hood, the raised/inward-flared support, and the
distinct brake paddle. Its [road setup guide](https://www.sram.com/en/learn/road-axs-welcome-guide)
provides the matching production photographs. For grip anatomy, compare the
[SRAM AXS hand-on-hood photograph](https://www.sram.com/globalassets/publicsites/03_learn/sr/road-axs-2x/welcome-guide-fival-update/04_fps_force-rival-shifter-update.jpg?format=jpg&quality=90&w=950)
and a [full rider on the hoods](https://www.cyclingweekly.com/fitness/marginal-gains-everybody-318503).
These are photographic modelling references; no third-party images are stored
in this repository.

The current editable candidate is
`source/WodraCyclist.alpha48-cockpit-v22.blend`, exported as
`exports/WodraCyclist-alpha48-cockpit-v22.glb`. It replaces the separated bar
segments with a continuous swept drop-bar loop, narrows/tapers the hood shell
to approximately 102 x 33 x 36 mm with a raised nose, and adds a separate
flattened curved lever blade. The current close Godot captures are in
`artifacts/alpha48-hand-correction/cockpit-v22/godot-candidate/`; the runtime
motion clip is `artifacts/alpha48-hand-correction/cockpit-v22/godot-motion-v22.mp4`.

**Visual review remains failed/pending.** Compared with the SRAM photographs,
the in-game hood still reads too much like a rounded capsule and the fingers
still look long and flat rather than curled naturally around the grip. The
five-phase contact and mask-export regressions pass, but neither establishes
anatomically convincing appearance. Do not merge or publish this candidate
until a further hand/cockpit correction passes owner visual review. Issue #93
rendering jitter is a separate investigation and is not claimed fixed here.

Reproduce the v22 export from the preserved v21 editable source:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  tools/modeling/source/WodraCyclist.alpha48-cockpit-v21.blend --python `
  tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-alpha48-cockpit-v22.glb `
  tools/modeling/source/WodraCyclist.alpha48-cockpit-v22.blend `
  tools/modeling/previews/alpha48-cockpit-v22 --skip-previews
```

Then copy the GLB to `src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb`,
run a Godot import, and capture `RiderFinishingSceneReviewTest.tscn` and
`RiderMotionReviewTest.tscn` in a fresh Godot process.

### Issue #93 rendering jitter investigation

The local real-time RideScreen comparison is in
`artifacts/rider-finishing-alpha48/grip-v14/jitter/`. On the Quadro P620 with
Godot 4.6.2 OpenGL Compatibility at 1920x1080/59.97 Hz, three 12 second
variable-frame samples at the reported route section recorded zero frames at
or above 33.33 ms. Animated baseline p50/p95/p99/max was 9.17/9.52/10.00/14.58
ms; disabling rider skeletal sampling reduced it to 4.55/5.20/9.17/9.17 ms.
Thus animation sampling has measurable cost here, but this run did not
reproduce the reported judder and does not establish that sampling is its
cause. The supplied diagnostic ZIP has no per-frame timing stream, and the
local execution did not use its ANGLE fallback path. Issue #93 remains open
pending a comparable native capture/timing series from the affected renderer
or machine; do not treat the local no-reproduction result as a jitter fix.

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

### MPFB hand export diagnosis and correction

The controlled hand comparison established that the source is sound through the
authored grip. The first failure is Blender's glTF export: MPFB's `Mask`
modifier is active in the editable scene but omitted from the GLB. The unmasked
export therefore included rig-control helper faces around fingertips and
wrists. The GLB-reimport image first shows those fragments; production
RideScreen loads that same geometry. The four-influence change was not the
cause: Blender 5.2.2 emitted only `JOINTS_0`/`WEIGHTS_0` and warned that it
selected and normalized the four largest influences itself.

Earlier helper cleanup had also removed 24 genuine wrist seam quads per hand.
The preserved source repairs those faces from a matching MPFB 2.0.18 mesh after
checking identical vertex coordinates, topology, and wrist/finger weights.
`remove_mp_fb_leg_proxy_panels` now protects faces carrying deformation-bone
weights from being classified as leg helpers again. The repair does not change
the armature or bone scales.

The exporter now makes a temporary Human duplicate, bakes the MPFB body mask
into that duplicate, retains the explicit unweighted eye-material faces, and
exports the duplicate. The editable weighted Human, 291 groups, 19 shape keys,
171-bone rig, and animation remain intact. The source mask evaluates to 13,378
body faces; the export adds its 152 eye faces and excludes the 1,774 helper
faces. The eye surface must be retained explicitly because MPFB's body mask
also removes those intentionally unweighted vertices.

Reproduce the final export from its editable checkpoint without rebuilding the
character:

```powershell
$root = (Get-Location).Path
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background `
  (Join-Path $root 'tools/modeling/source/WodraCyclist.alpha48-mask-export-fixed.blend') `
  --python (Join-Path $root 'tools/modeling/polish_rider.py') -- `
  (Join-Path $root 'tools/modeling/exports/WodraCyclist-alpha48-hand-mask-fixed.glb') `
  (Join-Path $root 'tools/modeling/source/WodraCyclist.alpha48-mask-export-fixed.blend') `
  (Join-Path $root 'tools/modeling/previews/alpha48-hand-mask-fixed') --export-only
```

The stage renders are under `tools/modeling/diagnostics/alpha48-control/`;
Godot close hand captures and the ride motion preview are under
`artifacts/alpha48-hand-correction/`. `HandGlbMinimalReviewTest.tscn` checks a
direct GLB import and embedded animation without Wodra rider scripts.
`RiderFinishingSceneReviewTest.tscn` and `RiderMotionReviewTest.tscn` exercise
the production RideScreen, route lighting, HUD and imported rider.

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

### Road bicycle geometry pass (#158)

The current road bicycle geometry is authored in `source/WodraCyclist.blend`;
`build_road_bike_158.py` rebuilds only the bicycle subtree and preserves the
weighted human, skeleton, `PedalCycle` action, root, and animation nodes. It also
restores the 48 existing wrist seam quads from the documented matching
`WodraCyclist.alpha48-mask-export-fixed.blend` checkpoint when the raw canonical
blend does not contain them, and restores the checkpoint's 152 eye-face
material assignments. It verifies identical source vertex coordinates and
weights; no rider vertices, weights, bones, shape keys, or animation are changed.
The historical cockpit evidence from issue #95 / PR #94 in
`artifacts/alpha48-hand-correction/cockpit-v22/README.md` and the owner-provided
photos/videos under `images/bike/` were reviewed. The owner's IMG_1459 and
IMG_1474 photographs show the Trek Émonda and Shimano 105 brifters from side,
front, and hand-on-hood angles. In review, the prior superellipse loft produced
a high domed canopy, and a flat silhouette extrusion looked like a tapered
fin. The current iteration replaces both with an editable rounded 3D body
intersecting the wrapped bar, a mostly level palm surface, and a raised nose.
The brake blade and inner shift paddle remain separate curved forms; the blade
overlaps the control body at a visible axle/pivot. This remains a stylised
approximation: its palm-body transition and nose still read more like a capsule
than the reference control and require further visual review. No Shimano marks
or product artwork are copied. Drop-bar return ends are now 290 mm apart,
previously 180 mm.

The committed, editable result is `source/WodraCyclist.issue158.blend`;
`source/WodraCyclist.blend` remains the preserved canonical rider source. The
builder verifies that human vertices, weights, skeleton, and `PedalCycle` remain
unchanged before saving the rebuilt bike into the result blend.

The geometry target follows published size-54 road-bike relationships rather
than proprietary graphics. The [Specialized Tarmac SL8 geometry chart](https://www.specialized.com/ca/en/tarmac-sl8-comp-sram-rival-axs-/p/4292996)
provides a 978 mm wheelbase, 410 mm chainstay, 74 mm bottom-bracket drop,
420 mm handlebar, 100 mm stem, 172.5 mm crank, and 73 degree head angle. This
model uses a 170 mm crank and the same 978/410/74 mm frame relationships. Its
700×28C wheel has a 622 mm bead-seat diameter and 678 mm outer diameter; the
carbon rim is 37 mm deep. The drivetrain is a plausible 50/34 double with an
11-sprocket cassette, a closed chain path, two disc rotors, calipers, and a
rear derailleur. Frame, carbon, alloy, rubber, tape, and hood materials use a
consistent restrained stylised finish.

All dimensions below are in metres in the `BicycleModel` local Blender basis:
X is lateral, +Y points forward, and +Z is up. The GLB/Godot basis maps Blender
+Y forward to Godot -Z. The front/rear wheel pivots remain `FrontWheelSpin` and
`RearWheelSpin`; the cadence-controlled `CrankPivot`, crank, and pedal node
names are unchanged.

| Contact or datum | Blender bike-local position |
| --- | --- |
| Rear wheel centre | `(0, -0.489, 0.339)` |
| Front wheel centre | `(0, 0.489, 0.339)` |
| Bottom bracket | `(0, -0.079, 0.265)` |
| Seat cluster | `(0, -0.240, 0.845)` |
| Saddle/pelvis target | `(0, -0.253, 0.951)` |
| Left hood palm shelf | `(-0.210, 0.421, 0.900)` |
| Right hood palm shelf | `(0.210, 0.421, 0.900)` |
| Left pedal target, crank local | `(-0.130, 0, -0.170)` |
| Right pedal target, crank local | `(0.130, 0, 0.170)` |

`Contact_LeftHood`, `Contact_RightHood`, `Contact_Saddle`,
`Contact_LeftPedal`, and `Contact_RightPedal` export as named empty nodes. The
pedal targets are children of `CrankPivot` so they rotate with the pedals. The
hood, saddle, and pedal coordinates are intended as stable #159 fit references;
the existing rider's hands and pelvis need refitting to these revised points in
the separate posture task. On the current baked riding pose, the measured palm
median is 221 mm from each hood and the 90th percentile is 268 mm; finger
medians range from 141 to 198 mm from their corresponding control surfaces.
That is a confirmed fit mismatch intentionally deferred to #159. No hand, body,
clothing, bone, or animation data was changed in #158. Do not distort the
finished bicycle to preserve the old hand/pelvis pose.

Rebuild the editable source and runtime model with Blender 5.2.2, then reimport
in Godot 4.6.2:

```powershell
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
& $blender --background tools/modeling/source/WodraCyclist.blend `
  --python tools/modeling/build_road_bike_158.py -- `
  tools/modeling/source/WodraCyclist.issue158.blend
& $blender --background tools/modeling/source/WodraCyclist.issue158.blend `
  --python tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-issue158.glb `
  tools/modeling/checkpoints/WodraCyclist-issue158-export.blend `
  tools/modeling/previews/issue158 --export-only
Copy-Item tools/modeling/exports/WodraCyclist-issue158.glb `
  src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb -Force
python tools/modeling/verify_road_bike_158.py
```

`verify_road_bike_158.py` is a standard-library GLB regression check for wheel
spacing and diameter, BB drop, bar width, crank radius, component/contact nodes,
lever-to-hood relation, animation import, triangle budget, and mesh budget. The
production Godot review harness captures matched 1920x1080
side/top/front-three-quarter, rider, cockpit, and control views in
`artifacts/issue158-road-bike/`. The close cockpit rebuild captures are under
`artifacts/issue158-road-bike/evidence/cockpit-profile-rebuild/`, beside the
earlier canopy-style production views for comparison. `RiderMotionReviewTest.tscn` supplies matched
production RideScreen captures for pedalling, steering, coasting, and restart.
`--capture-road-bike-motion --motion-view=chase`
uses the imported production model, pedal cycle, road, and chase camera for the
ride-motion review; it is a route-following visual, not a physical steering
hardware test.
The hood and lever silhouette was also reviewed against the ergonomic shape
and distinct brake-blade layout shown in [SRAM's AXS control overview](https://www.sram.com/en/learn/tech-deep-dives/axs-controls);
no brand marks or copied product artwork are included.

The #160 cockpit iteration replaces the pointed side extrusion with a rounded,
editable 3D control body intersecting the wrapped bar, and reshapes the brake
blade to overlap the front underside at its pivot. The photographed owner
Shimano 105 controls are in `images/bike` and are not redistributed; a cropped
owner reference and current production Godot comparisons are preserved in
`artifacts/issue158-road-bike/evidence/cockpit-profile-rebuild/`. The current
render is not being represented as visually accepted: the hood still reads
too capsule-like and does not yet match the reference palm shelf/nose clearly.
The hand targets remain at ±210 mm lateral, 421 mm forward and 900 mm high in
Blender coordinates, rotated to the palm surface.

## Current #158 source: owner-supplied CGTrader road/gravel bike

The procedural road-bike sections above document the earlier failed iterations. For the current PR #160 candidate, the production bicycle geometry has been replaced with the owner-supplied native Blender asset from `C:\Users\garet\OneDrive\wodra_bikes\road_gravel\BLEND.zip`. The selected archive member is `GigantRoad.blend`; its relative `Textures` directory is required. The original downloads remain outside the repository and are not modified. The road and gravel disciplines share this source. MTB archives were inspected but are not integrated by #158.

The native file contains separately modelled frame tubes, fork, front/rear wheels, hubs, rims, spokes, tyres, cockpit, brakes, saddle, cranks, pedals, chain, cassette, derailleurs and small hardware. Materials and image maps are present in the archive. There is no licence, attribution, or redistribution metadata in the supplied archive; the repository owner must confirm the CGTrader licence permits committing and redistributing the resulting runtime GLB. The source includes GIGANT logo decals; the current Wodra runtime asset removes the separate logo/decal meshes and unused logo-only materials/maps. No licence terms are inferred from the download source or altered here.

`tools/modeling/integrate_cgtrader_road_bike_158.py` imports the original Blender scene, retains the canonical Wodra rider mesh, skeleton, and pedal action, replaces only bicycle geometry, bakes source transforms, scales maps, decimates highly tessellated hardware, and establishes the runtime pivots. The cleanup removes only the two oversized disconnected cockpit cable loops from the source cable mesh, retaining its frame-following/control runs. It tones down the source tape color and bump map while retaining the wrapped-bar surface and seams. It joins static parts by material into stable frame/cockpit/saddle groups and keeps front and rear wheels plus cranks/pedals independently animatable. The integrated editable `.blend` is an intermediate working file and remains outside Git alongside the original download; the GLB is self-contained for gameplay and does not read OneDrive at runtime.

Source measurements after normalization are approximately 993 mm wheelbase and 665 mm tyre outer diameter. Hubs are grounded at 339 mm. The polished production GLB contains 935,442 triangles across 24 render meshes and is 36,563,576 bytes. Relative to the first integrated candidate (1,695,994 triangles, 67,015,424 bytes), that is 44.8% fewer triangles and 45.4% less asset data; compared with the 3.71-million-triangle source it is about 74.8% fewer triangles. The visual cleanup keeps the frame, fork, cockpit, hood/lever, rim, tyre and hub silhouettes intact. It chiefly reduces duplicated high-subdivision brake calipers, bolts, cassette, chain and spokes. The earlier Wodra model was 68,894 triangles/49 meshes and 6,974,720 bytes; the imported model remains more detailed by design. Godot reports 24 runtime mesh instances and zero non-unit scales. The existing Godot import setting `meshes/generate_lods=true` remains enabled; this pass did not add hand-authored duplicate LOD assets.

Bike-local contact frames (Blender axes: X lateral, Y fore/aft, Z up):

| Contact | Position (m) | Orientation |
| --- | --- | --- |
| Left hood palm shelf | `(-0.222, 0.502, 0.948)` | local surface normal `(0, -0.08, 1)` |
| Right hood palm shelf | `(0.222, 0.502, 0.948)` | local surface normal `(0, -0.08, 1)` |
| Saddle / pelvis | `(0, -0.285, 0.978)` | bike frame |
| Left pedal | `(-0.128, -0.164, 0)` | child of `CrankPivot` |
| Right pedal | `(0.128, 0.164, 0)` | child of `CrankPivot` |

The pedal contacts rotate with their crank pivot. They are bike-local fitting references for issue #159; no rider pose or hand rig changes are included.

To reproduce the integration on the owner machine, extract a working copy of `BLEND.zip` and its `Textures` folder into a temporary directory, then run:

```powershell
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$source = "$env:TEMP\WodraBikeSource\GigantRoad.blend"
$integrated = "$env:TEMP\WodraBikeSource\WodraCyclist.issue158-cgtrader.blend"
& $blender --background --python tools/modeling/integrate_cgtrader_road_bike_158.py -- `
  --source $source --rider tools/modeling/source/WodraCyclist.blend --output $integrated
& $blender --background $integrated --python tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-issue158-cgtrader.glb `
  tools/modeling/checkpoints/WodraCyclist-issue158-cgtrader-export.blend `
  tools/modeling/previews/issue158-cgtrader --export-only
python tools/modeling/verify_road_bike_158.py `
  tools/modeling/exports/WodraCyclist-issue158-cgtrader.glb
```

The owner’s Trek photographs were reviewed directly from `C:\Users\garet\OneDrive\wodra\images\bike`, including side/cockpit and grip views; they remain external and are not copied into the repository. The comparison informed the compact cable routing and restrained, visible tape seams; the unbranded model keeps its own frame design. The production RideScreen renders captured after cleanup are in `artifacts/issue158-road-bike/polished-cgtrader-production-review/`; the preceding imported model’s matched renders are in `artifacts/issue158-road-bike/cgtrader-production-review/`. The rider’s hand fit remains deferred to #159.

Godot 4.6.2 imported the optimized GLB and the actual RideScreen test loaded both one and twelve visible cyclists with the 24-mesh model. Animated-baseline plus disabled-skeletal-sampling and steer/coast/restart phases completed. The matched 2026-10-31 windowed A/B run against the exact pre-import Wodra GLB is documented in [`final-bike-a-b`](../../artifacts/issue158-road-bike/performance/final-bike-a-b/README.md). On an i7-9700 / Quadro P620 at 1152×648, the 12-rider run measured median frame delta 30.30 ms original versus 31.94 ms optimized; median draw calls rose from 1,528 to 1,740, render objects from 2,216 to 2,448, and Godot's whole-process render-video-memory counter from 50.4 MB to 143.8 MB. Thus the imported model has a modest but measurable load/memory regression in the 12-rider case; no performance improvement is claimed. GPU frame time was not available. The importer setting `meshes/generate_lods=true` is enabled, but effective LOD switches/triangle reduction by distance were not measured. The benchmark's RideScreen phases validated actual runtime model loading and active pedaling/steering/coast/restart, but they do not substitute for a final owner visual review.

The installed `RiderBikePreview` motion-capture scene fails to save frames in headless mode because Godot’s dummy renderer returns a null viewport texture. During the #158 final validation, the graphical RideScreen launched on the NVIDIA renderer and reported live draw/object counters, but the available desktop capture surface showed a solid grey viewport. No valid #158 final-state screenshot could be captured, so its existing production screenshots document the cleanup before the final small tape-bump adjustment only. Issue #158 did not change rider rig, bike pivots, rider positioning, trainer/simulation, recording, route, or multiplayer code. Third-party licensing and distribution permission remain owner review items.

## Issue #159 — fitting the rider to the accepted bicycle

`fit_rider_159.py` starts from the editable integrated bike/rider blend produced
by `integrate_cgtrader_road_bike_158.py`. It changes only the rider: a measured
forward torso hinge is distributed over the thoracic spine, the existing MPFB
arm IK targets are placed at the two bicycle hood contacts and relaxed elbow
targets, and the resulting arm/finger pose is baked over every frame of the
existing `PedalCycle` action. The bike mesh, component hierarchy, animation
hooks and runtime rider binding are not regenerated by the `--rider-only`
finishing pass.

The finish pass in `polish_rider.py` fits the helmet shell from the evaluated
head bone, adds jersey collar/zip/cuff and bib-gripper details to the evaluated
skinned surface, and exports the complete weighted rider. These details reuse
the existing rider mesh and skeleton; no cloth simulation, wardrobe system,
runtime retargeting, or change to cycling physics is introduced. Bike-local
fit references remain:

| Contact | Position (m) |
| --- | --- |
| Left hood palm shelf | `(-0.222, 0.502, 0.948)` |
| Right hood palm shelf | `(0.222, 0.502, 0.948)` |
| Saddle / pelvis | `(0, -0.285, 0.978)` |
| Left pedal | `(-0.128, -0.164, 0)` relative to `CrankPivot` |
| Right pedal | `(0.128, 0.164, 0)` relative to `CrankPivot` |

To reproduce from the owner-supplied `GigantRoad.blend`, extract the original
archive and its `Textures` directory to a temporary working folder. Then run
the integration, rider-fit, and rider-only finishing steps. The supplied
source archives remain external and unchanged:

```powershell
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$source = "$env:TEMP\WodraBikeSource\GigantRoad.blend"
$integrated = "$env:TEMP\WodraBikeSource\WodraCyclist.issue159-integrated.blend"
$fitBlend = "$env:TEMP\WodraBikeSource\WodraCyclist.issue159-fit.blend"
& $blender --background --python tools/modeling/integrate_cgtrader_road_bike_158.py -- `
  --source $source --rider tools/modeling/source/WodraCyclist.blend --output $integrated
& $blender --background $integrated --python tools/modeling/fit_rider_159.py -- `
  tools/modeling/exports/WodraCyclist-issue159-fit.glb $fitBlend
& $blender --background $fitBlend --python tools/modeling/polish_rider.py -- `
  tools/modeling/exports/WodraCyclist-issue159-final.glb `
  tools/modeling/checkpoints/WodraCyclist.issue159-final.blend `
  tools/modeling/previews/issue159-final --rider-only --skip-previews
Copy-Item tools/modeling/exports/WodraCyclist-issue159-final.glb `
  src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb -Force
```

After opening the Godot project to refresh the GLB import, run
`RiderFinishingSceneReviewTest.tscn` for production RideScreen views and 72
phase fit samples, then `RiderMotionReviewTest.tscn` for cadence, steering,
coasting, and restart. The fit test verifies that the palm centers remain
within 45 mm of each hood target, the pelvis remains within 180 mm of the
saddle target, and the feet animate through a full cadence cycle. These
measurements establish fit and motion continuity, not finger-to-hood contact.
For the latter, run `verify_hood_finger_pose_159.py` on the final editable
blend with Blender 5.2. It evaluates all three phalanges of both thumbs and
all eight fingers over every baked `PedalCycle` frame against the integrated
cockpit surface.

The production captures and matched baseline are in `artifacts/issue159/`.
The new rider has a forward cycling posture compared with the prior upright
pose. The photo-guided grip correction bakes each weighted finger chain onto
the appropriate side of the hood, correcting the mirrored hinge-axis issue
that left fingers extended in the earlier pose. Close production captures and
the motion clip are included for owner visual review. The hand mesh remains
stylised and physical riding acceptance is still required. The bicycle remains
the accepted #158 model and its geometry is unchanged by this task.
