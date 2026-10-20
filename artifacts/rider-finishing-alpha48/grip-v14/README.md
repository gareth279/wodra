# Alpha.48 hand-grip and jitter review evidence

The hand captures are from the production `RideScreen` and imported GLB in Godot 4.6.2, at the same 1500×900 requested review size (the desktop renderer produced 1920×1080 frames), same route lighting, framing, and pose phase. `before/` is the v12 grip candidate; `after/` is the v14 candidate. Compare corresponding left/right close and elevated images. The after side view is included with debug HUD both enabled and disabled, plus a normal chase view.

The 12-second clip in `after/godot-motion-preview-v14.mp4` covers cadence pedalling, held-right steering, coasting, and restart. It was captured as 360 sequential production RideScreen frames at 30 fps.

V14 only changes authored finger, metacarpal, and wrist rotations. It preserves the weighted hand mesh, wrist topology, body and leg animation, and bike. No bone scale or skin weights changed. Blender 5.2.2 contact checks passed at frames 1, 55, 109, 163, and 217 for both hands. The GLB export regression passed for traced MPFB helper-face removal and 24 restored wrist faces per side.

`jitter/summary.txt` and `jitter/ride-jitter-realtime.csv` record a separate 36-second live variable-frame test on the reported route section with nine imported riders. The local native OpenGL run did not reproduce the reported judder. Skeletal sampling measurably reduces frame rate when enabled, but that is not proof of the owner’s root cause. The diagnostic ZIP had no per-frame timing series, and the reported ANGLE fallback was not active in this run. Issue #93 remains open pending reproduction under comparable renderer and real-trainer conditions.
