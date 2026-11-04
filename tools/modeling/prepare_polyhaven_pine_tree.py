"""Optimize the reviewed CC0 Poly Haven broadleaf model for the Wodra asset pipeline.

Usage: blender --background --python tools/modeling/prepare_polyhaven_pine_tree.py -- <source.gltf>
The generated GLB is metre-scaled and decimated for the single-scene route asset.
"""
import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "src/IndoorCycling.Game/Assets/World/PineHill/PineHillMatureBroadleaf.glb"
BLEND = ROOT / "tools/modeling/source/PolyHavenTreeSmall02.blend"
args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(args) != 1:
    raise SystemExit("Pass the downloaded Poly Haven tree_small_02.gltf path after --")
SOURCE = Path(args[0]).resolve()

bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(SOURCE))
tree = next((obj for obj in bpy.context.scene.objects
             if obj.type == "MESH" and "tree_small_02" in obj.name.lower()), None)
if tree is None:
    raise RuntimeError("Poly Haven tree_small_02 glTF mesh was not imported")
for obj in list(bpy.context.scene.objects):
    if obj is not tree:
        bpy.data.objects.remove(obj, do_unlink=True)

# Scale the source's 4.6 m sapling into a mature street tree, then reduce the
# high-density foliage-card mesh while retaining its UVs, texture alpha and materials.
tree.scale = (2.0, 2.0, 2.0)
bpy.context.view_layer.objects.active = tree
tree.select_set(True)
bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
decimate = tree.modifiers.new("Wodra runtime triangle budget", "DECIMATE")
# A reduced 0.05 candidate was compared in the production RideScreen. It produced
# no repeatable frame-time or draw-call improvement and slightly softened leaf-card
# silhouettes, so retain the reviewed 0.06 asset until a measured LOD wins.
decimate.ratio = 0.06
decimate.use_collapse_triangulate = True
bpy.ops.object.modifier_apply(modifier=decimate.name)
tree.name = "PolyHaven Tree Small 02 • Wodra broadleaf"
tree.data.name = "Mature broadleaf foliage with preserved leaf cards"

# Keep colors natural and slightly restrained for Wodra daylight materials.
for mat in tree.data.materials:
    if not mat or not mat.use_nodes:
        continue
    shader = mat.node_tree.nodes.get("Principled BSDF")
    if shader and not shader.inputs["Base Color"].is_linked:
        color = shader.inputs["Base Color"].default_value
        if "leav" in mat.name.lower():
            shader.inputs["Base Color"].default_value = (color[0] * 0.88, color[1] * 0.96, color[2] * 0.78, color[3])

tree.parent = None
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
bpy.ops.object.select_all(action="DESELECT")
tree.select_set(True)
bpy.context.view_layer.objects.active = tree
bpy.ops.export_scene.gltf(filepath=str(OUTPUT), export_format="GLB", export_apply=True,
    export_materials="EXPORT", export_image_format="AUTO", use_selection=True,
    export_cameras=False, export_lights=False)
triangles = sum(len(poly.vertices) - 2 for poly in tree.data.polygons)
print(f"Prepared CC0 tree asset: {OUTPUT}; mesh faces={len(tree.data.polygons)} triangles={triangles}")
