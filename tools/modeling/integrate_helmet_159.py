"""Rebuild the selected fitted helmet and export the #159 rider model."""
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
    parser.add_argument("--use-supplied", action="store_true", help="Use the source OBJ instead of the fitted Wodra shell")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    bpy.ops.wm.open_mainfile(filepath=str(args.blend))
    rig = bpy.data.objects.get("Human.rig")
    body = bpy.data.objects.get("Human")
    if rig is None or body is None or not body.get("WodraSculptedHoodHandsV1"):
        raise RuntimeError("Expected the fitted rider and sculpted hands in the source blend")
    polish_rider.make_helmet(rig, use_supplied=args.use_supplied)
    args.blend.parent.mkdir(parents=True, exist_ok=True)
    args.glb.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(args.blend), compress=True)
    polish_rider.configure_gltf_vertex_color_material()
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=str(args.glb), export_format="GLB", use_selection=True,
        export_animations=True, export_frame_range=True, export_force_sampling=True,
        export_animation_mode="ACTIONS")
    print(f"ISSUE159_HELMET_EXPORTED {args.glb}")


if __name__ == "__main__":
    main()
