# Wodra cyclist model workflow

`build_wodra_cyclist.py` creates an original low-poly rider and road bike using Blender's built-in Python API. It uses no external assets or add-on dependencies. The generated GLB is deliberately split into named rider and bicycle presentation trees, with the same named transform targets the C# ride animation already drives.

## Generate and preview

Run this from the repository root in PowerShell. The script validates every mesh's finite vertices, face indices, local dimensions, world scale, and overall model envelope; applies the game's seated leg pose temporarily; renders three previews; restores neutral animation templates; and exports the GLB.

On the Windows machine used for this asset, call Blender by its full path:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python tools/modeling/build_wodra_cyclist.py
```

The script writes:

```text
src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb
tools/modeling/previews/side.png
tools/modeling/previews/rear.png
tools/modeling/previews/rear_three_quarter.png
```

The GLB can be independently checked using its exported mesh and transformed bounds, target-node contract, and normalized animation templates:

```powershell
python tools/modeling/inspect_wodra_glb.py
```

To export to another destination, pass an absolute path after `--`:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python tools/modeling/build_wodra_cyclist.py -- 'D:\models\WodraCyclist.glb'
```

Open `src/IndoorCycling.Game/project.godot` in Godot once so it imports the new GLB, then run the game. `CyclistVisual` loads the GLB when present and its required target nodes are found; otherwise the existing procedural visual remains the fallback. Both representations use the same cadence, wheel, pedal, and two-bone leg update code.

## Animation target contract

The imported scene must contain these nodes with the stated types:

- `BicycleModel`, `RiderModel`, `TorsoRig`, `FrontWheelSpin`, `RearWheelSpin`, `CrankPivot`: `Node3D`
- `LeftCrank`, `RightCrank`, `LeftPedal`, `RightPedal`, `LeftThigh`, `LeftShin`, `RightThigh`, `RightShin`, `LeftCyclingShoe`, `RightCyclingShoe`, `LeftShoeSole`, `RightShoeSole`, `LeftAnkle`, `RightAnkle`, `LeftKnee`, `RightKnee`: `MeshInstance3D`
- `BikeFrame`, `JerseyTorso`, `LeftJerseySleeve`, `RightJerseySleeve`: `MeshInstance3D` material targets

The procedural visual remains in `CyclistVisual.cs` as a fallback. It does not own a separate animation loop; `UpdatePedalingGeometry` updates either model using these shared targets.

## Preview in a debug ride

In a Debug build, start a ride and press F6 to cycle the camera views. The small camera label appears briefly even when the debug panel is hidden. Press Shift+F12 in each view to save a HUD screenshot under Pictures/Wodra; the label identifies the captured view. Inspect Side, Rear three-quarter, and Front three-quarter for posture, hand reach, articulation, wheel clearance, and frame silhouette.

The previews are Blender renders of the authored model with the runtime animation targets posed and scaled to the game's neutral seated pedal position. They are a geometry review aid; check import orientation and motion in Godot after generating the asset.
