# Wodra cyclist prototype

This is an owner-review prototype for PR #89 and issues #84–#86. It contains a
complete MPFB human mesh, the default weighted skeleton, fitted road-bike
geometry, a baked three-revolution pedal action, and an in-game cadence-driven
preview. It remains a work in progress pending owner visual approval.

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

## Current review state

The static candidate and sampled 0°, 90°, 180°, and 270° phases are provided for
visual inspection. The MPFB rig has 171 bones and the human mesh has 291 weight
groups. The side render shows saddle contact, forward torso lean, bent elbows,
hood contact, and feet on the pedals without bone stretching. The current asset
is a prototype; owner visual review remains required before merging or releasing.

### Carbon road-bike polish (PR #89 review)

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
