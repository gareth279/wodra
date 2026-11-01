"""Export the owner-supplied cycling helmet as a rigid Godot-ready assembly.

Run with Blender 5.2:
  blender --background --python tools/modeling/export_cycling_helmet_rigid.py
"""
from pathlib import Path

import bmesh
import bpy
from mathutils import Matrix, Vector


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "tools" / "modeling" / "source" / "helmet" / "sport_cycling_helmet_main.obj"
TEXTURE = SOURCE.parent / "sport_cycling_helmet_diffuse.png"
OUTPUT = ROOT / "src" / "IndoorCycling.Game" / "Assets" / "Models" / "CyclingHelmetRigid.glb"
SOURCE_CENTIMETRES_TO_METRES = 0.01


bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=str(SOURCE), forward_axis="NEGATIVE_Y", up_axis="Z")
meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
if len(meshes) != 1:
    raise RuntimeError(f"expected one supplied OBJ assembly, found {len(meshes)} meshes")
helmet = meshes[0]

# The supplied OBJ is one assembly. Recenter only by the largest connected
# shell island; keep every strap and retention island in that same source frame.
bm = bmesh.new()
bm.from_mesh(helmet.data)
bm.verts.ensure_lookup_table()
visited = set()
components = []
for start in bm.verts:
    if start.index in visited:
        continue
    visited.add(start.index)
    pending = [start]
    component = []
    while pending:
        current = pending.pop()
        component.append(current)
        for edge in current.link_edges:
            other = edge.other_vert(current)
            if other.index not in visited:
                visited.add(other.index)
                pending.append(other)
    components.append(component)
if not components:
    raise RuntimeError("supplied helmet OBJ has no vertices")
shell = max(components, key=len)
shell_world = [helmet.matrix_world @ vertex.co for vertex in shell]
shell_min = Vector(tuple(min(point[axis] for point in shell_world) for axis in range(3)))
shell_max = Vector(tuple(max(point[axis] for point in shell_world) for axis in range(3)))
shell_center = (shell_min + shell_max) * 0.5
bm.free()

# Independently inspected source geometry: the physical brow/front is OBJ -Y.
# Blender's NEGATIVE_Y import maps it to Blender +Y; glTF maps that to Godot -Z.
# Blender +Z up becomes Godot +Y up. Bake the import basis exactly once here.
for vertex in helmet.data.vertices:
    vertex.co = (helmet.matrix_world @ vertex.co - shell_center) * SOURCE_CENTIMETRES_TO_METRES
helmet.parent = None
helmet.matrix_world = Matrix.Identity(4)
helmet.location = (0.0, 0.0, 0.0)
helmet.rotation_euler = (0.0, 0.0, 0.0)
helmet.scale = (1.0, 1.0, 1.0)
helmet.data.update()

if not helmet.data.materials:
    material = bpy.data.materials.new("Supplied helmet diffuse")
    helmet.data.materials.append(material)
else:
    material = helmet.data.materials[0]
material.use_nodes = True
nodes = material.node_tree.nodes
shader = nodes.get("Principled BSDF")
if shader is None:
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    output_node = nodes.get("Material Output") or nodes.new("ShaderNodeOutputMaterial")
    material.node_tree.links.new(shader.outputs["BSDF"], output_node.inputs["Surface"])
texture = bpy.data.images.load(str(TEXTURE), check_existing=True)
texture.pack()
image_node = nodes.new("ShaderNodeTexImage")
image_node.image = texture
material.node_tree.links.new(image_node.outputs["Color"], shader.inputs["Base Color"])
shader.inputs["Roughness"].default_value = 0.55
shader.inputs["Metallic"].default_value = 0.02
for polygon in helmet.data.polygons:
    polygon.use_smooth = True

helmet.name = "OriginalSuppliedHelmet"
helmet["source"] = "Owner-supplied sport_cycling_helmet_main.obj"
helmet["physical_front_axis_obj"] = "-Y, independently identified by the brow lip and frontal vents"
helmet["godot_front_axis"] = "-Z"
helmet["godot_up_axis"] = "+Y"
helmet["units"] = "centimetres converted to metres"
helmet["shell_origin"] = "largest connected shell bounds center"
bpy.ops.object.select_all(action="DESELECT")
helmet.select_set(True)
bpy.context.view_layer.objects.active = helmet
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=str(OUTPUT), export_format="GLB", use_selection=True,
    export_animations=False, export_apply=True)
print("RIGID_HELMET_EXPORT", OUTPUT)
print("RIGID_HELMET_SOURCE_AXES", "OBJ physical front=-Y -> Blender +Y -> Godot -Z", "up=Blender +Z -> Godot +Y")
print("RIGID_HELMET_SHELL_BOUNDS_M", tuple(round(value, 4) for value in shell_max - shell_min))
