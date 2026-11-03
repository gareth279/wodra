# PR #174 shorts review

The before/after close-ups are full-resolution 1920×1080 Godot 4.6.2 RideScreen captures from the same rear and rear three-quarter cameras, at identical 85 rpm crank phases (0°, 90°, 180°, 270°). The after mesh starts with the prior five-vertex correction, then blends the connected posterior fabric around the cleft toward a bilateral contour with a gradual lateral and vertical falloff. It adjusts only existing shorts vertices; weights, topology, pelvis, saddle, leg mesh, grippers and animation remain unchanged.

The first broad smoothing attempt moved some neighbors inward and produced visible shading patches; it was rejected and retained in the worktree evidence. The accepted pass moves only the local cleft surface outward toward the adjacent lobes and retains a subtle center inset. Full rider captures show the fitted result at normal viewing distances in Teal, Navy and White jersey colors.

The collar remains deferred to issue #173. Its failed rebuild and split-material cause evidence remain outside the production asset.

Rebuild the accepted source and GLB from the previous centreline source with Blender 5.2.2:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b tools/modeling/source/WodraCyclist-Garments-shortspatch-input.blend --python tools/modeling/correct_alpha_next_shorts_surface.py -- tools/modeling/source/WodraCyclist-Garments-accepted-shorts.blend src/IndoorCycling.Game/Assets/Models/RiderGarments.glb
```

The Blender script verifies that vertex weights and face topology are unchanged before it exports.
