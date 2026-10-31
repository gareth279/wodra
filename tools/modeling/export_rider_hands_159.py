"""Export the checked-in editable #159 hand-fit Blender source to GLB."""
import argparse
import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
import polish_rider


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--blend", type=Path, required=True)
    parser.add_argument("--glb", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])

    bpy.ops.wm.open_mainfile(filepath=str(args.blend))
    body = bpy.data.objects.get("Human")
    rig = bpy.data.objects.get("Human.rig")
    if body is None or not body.get("WodraSculptedHoodHandsV1"):
        raise RuntimeError("Source blend is missing its integrated sculpted hand mesh")
    if not body.get("WodraSculptedHoodHandsAuthor") or not body.get("WodraSculptedHoodHandsLicense"):
        raise RuntimeError("Source blend is missing hand-model attribution metadata")
    if rig is None or any(rig.data.bones.get(name) is None
                           for name in ("palm_contact.L", "palm_contact.R")):
        raise RuntimeError("Source blend is missing one or both palm contact frames")
    if rig.animation_data is None or rig.animation_data.action is None:
        raise RuntimeError("Source blend is missing the baked rider animation")

    args.glb.parent.mkdir(parents=True, exist_ok=True)
    polish_rider.configure_gltf_vertex_color_material()
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=str(args.glb), export_format="GLB", use_selection=True,
        export_animations=True, export_frame_range=True,
        export_force_sampling=True, export_animation_mode="ACTIONS")
    print(f"ISSUE159_RIDER_EXPORTED {args.glb}")


if __name__ == "__main__":
    main()
