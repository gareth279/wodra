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

# Fit measured in the production RideScreen frame against the weighted Human
# head mesh at the PedalCycle pose. The head crown is +0.1726 m above eye level;
# the current shell center is +0.112 m, and the shell half-height is 0.0748 m.
# A 0.95 vertical fit leaves about 0.010 m above the crown while raising the
# lower brow edge slightly for forehead clearance. The brow's most forward
# point is +0.0685 m; the scaled shell front is kept 0.025 m beyond it. The
# rearward shift covers the exposed rear of the skull without changing the
# helmet's verified forward direction.
SHELL_SCALE = Vector((0.95, 0.95, 0.95))  # Blender frame: lateral, forward, up.
SHELL_OFFSET_CM = Vector((0.0, -3.15, -0.05))  # rearward, nearly level with the old origin.
STRAP_CLASP_SOURCE_Z_CM = -11.6
STRAP_SHELL_ANCHOR_SOURCE_Z_CM = 1.25


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
shell_component = max(range(len(components)), key=lambda index: len(components[index]))
shell = components[shell_component]
component_by_vertex = {
    vertex.index: component_index
    for component_index, component in enumerate(components)
    for vertex in component
}
source_points = [helmet.matrix_world @ vertex.co for vertex in bm.verts]
component_bounds = []
for component in components:
    points = [source_points[vertex.index] for vertex in component]
    component_bounds.append((
        Vector(tuple(min(point[axis] for point in points) for axis in range(3))),
        Vector(tuple(max(point[axis] for point in points) for axis in range(3))),
    ))

# Raw renders of the owner ZIP (unmodified OBJ, MTL and texture) establish that
# OBJ -Y is the forehead/brow-lip end: it has the short brow edge and frontal
# vents. OBJ +Y is the longer occipital tail. Blender's NEGATIVE_Y import maps
# that physical front to Blender +Y. The bounds below locate the already
# identified brow-lip island; they do not decide which end is the front.
front_lip_components = [
    index for index, (minimum, maximum) in enumerate(component_bounds)
    if index != shell_component
    and minimum.y > 8.0 and maximum.y > 8.0
    and minimum.z > 0.0 and maximum.z < 5.0
]
if len(front_lip_components) != 1:
    raise RuntimeError(f"expected one visually identified front brow lip island, found {front_lip_components}")
front_lip_component = front_lip_components[0]
shell_components = {shell_component, *front_lip_components}

# The central clasp is the small, centered, lowest physical hardware island.
# Keeping it fixed preserves its measured under-jaw position while the strap
# paths are reshaped to meet the shell's new anchor locations.
clasp_components = [
    index for index, (minimum, maximum) in enumerate(component_bounds)
    if index not in shell_components
    and maximum.z < -10.0
    and max(abs(minimum.x), abs(maximum.x)) < 1.5
    and minimum.y > -2.0 and maximum.y < 2.0
]
if len(clasp_components) != 1:
    raise RuntimeError(f"expected one central under-jaw clasp island, found {clasp_components}")
clasp_component = clasp_components[0]
front_strap_components = [
    index for index, (minimum, maximum) in enumerate(component_bounds)
    if index not in shell_components and index != clasp_component
    and min(abs(minimum.x), abs(maximum.x)) > 6.0
    and maximum.y < 0.0 and minimum.y < -5.0
    and minimum.z > -6.0 and maximum.z < 0.0
]
if len(front_strap_components) != 2:
    raise RuntimeError(f"expected the two supplied forward strap branches for a focused routing correction, found {front_strap_components}")

shell_world = [source_points[vertex.index] for vertex in shell]
shell_min = Vector(tuple(min(point[axis] for point in shell_world) for axis in range(3)))
shell_max = Vector(tuple(max(point[axis] for point in shell_world) for axis in range(3)))
shell_center = (shell_min + shell_max) * 0.5

# Export two non-rendering landmarks from independently inspected physical
# features. They let the production RideScreen regression check the brow lip
# and rear tail against the rider's actual face, instead of assuming a helmet
# local axis means "forward". The rear marker is taken from the raw OBJ +Y end,
# which the configured Blender import places at the shell's minimum Y.
def fit_shell_point(point):
    return ((point - shell_center) * SHELL_SCALE + SHELL_OFFSET_CM) * SOURCE_CENTIMETRES_TO_METRES


front_lip_points = [source_points[vertex.index] for vertex in components[front_lip_component]]
front_brow_marker = fit_shell_point(sum(front_lip_points, Vector()) / len(front_lip_points))
rear_tail_vertices = [
    vertex for vertex in components[shell_component]
    if source_points[vertex.index].y <= shell_min.y + 0.01
]
if not rear_tail_vertices:
    raise RuntimeError("could not locate the independently identified OBJ +Y rear tail")
rear_tail_marker = fit_shell_point(
    sum((source_points[vertex.index] for vertex in rear_tail_vertices), Vector()) / len(rear_tail_vertices)
)
bm.free()

# Keep the assembly origin and coordinate conversion unchanged. Fit the shell
# and its front lip around the measured skull landmarks. The two strap branches,
# ear adjusters, and rear retention pieces blend from the existing jaw clasp to
# their newly fitted shell anchors, preventing detached ends or a loose loop.
# The source OBJ is in centimetres and this imported Blender frame is lateral,
# physical-front, up; the existing glTF/Godot axis conversion is kept intact.
for vertex in helmet.data.vertices:
    point = source_points[vertex.index]
    local = point - shell_center
    component_index = component_by_vertex.get(vertex.index)
    if component_index is None:
        raise RuntimeError(f"helmet vertex {vertex.index} was not assigned to a connected component")

    fitted_shell = local * SHELL_SCALE + SHELL_OFFSET_CM
    if component_index in shell_components:
        fitted = fitted_shell
    elif component_index == clasp_component:
        fitted = local
    else:
        vertical_span = STRAP_SHELL_ANCHOR_SOURCE_Z_CM - STRAP_CLASP_SOURCE_Z_CM
        anchor_weight = max(0.0, min(1.0, (point.z - STRAP_CLASP_SOURCE_Z_CM) / vertical_span))
        fitted = local.lerp(fitted_shell, anchor_weight)
        if component_index in front_strap_components:
            # Candidate B moves the helmet shell forward and pitches it down;
            # that pulls the supplied front branches into the cheek. Keep their
            # shell ends attached, then ease only the lower run 10 mm rearward
            # and 5 mm outward toward the ear/junction. The rear branch and
            # chin clasp remain the original supplied geometry.
            fitted.y -= 1.0 * (1.0 - anchor_weight)
            lateral_sign = 1.0 if point.x > 0 else -1.0
            fitted.x += lateral_sign * 1.0 * (1.0 - anchor_weight)
    vertex.co = fitted * SOURCE_CENTIMETRES_TO_METRES
helmet.parent = None
helmet.matrix_world = Matrix.Identity(4)
helmet.location = (0.0, 0.0, 0.0)
helmet.rotation_euler = (0.0, 0.0, 0.0)
helmet.scale = (1.0, 1.0, 1.0)
helmet.data.update()

landmarks = []
for name, position, description in (
    ("HelmetPhysicalFrontBrowMarker", front_brow_marker, "owner OBJ -Y brow lip, confirmed in raw front and side renders"),
    ("HelmetPhysicalRearTailMarker", rear_tail_marker, "owner OBJ +Y occipital tail, confirmed in raw rear and side renders"),
):
    marker = bpy.data.objects.new(name, None)
    marker.empty_display_type = "SPHERE"
    marker.empty_display_size = 0.004
    marker.location = position
    marker["physical_feature"] = description
    bpy.context.collection.objects.link(marker)
    landmarks.append(marker)

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
helmet["fit_method"] = "measured production head fit: shell 0.95/0.95/0.95; rearward 31.5 mm; strap anchors blend to fixed jaw clasp"
bpy.ops.object.select_all(action="DESELECT")
helmet.select_set(True)
for marker in landmarks:
    marker.select_set(True)
bpy.context.view_layer.objects.active = helmet
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=str(OUTPUT), export_format="GLB", use_selection=True,
    export_animations=False, export_apply=True)
print("RIGID_HELMET_EXPORT", OUTPUT)
print("RIGID_HELMET_SOURCE_AXES", "OBJ physical front=-Y -> Blender +Y -> Godot -Z", "up=Blender +Z -> Godot +Y")
print("RIGID_HELMET_SHELL_BOUNDS_M", tuple(round(value, 4) for value in shell_max - shell_min))
print("RIGID_HELMET_FIT", "scaleXYZ", tuple(SHELL_SCALE), "offsetXYZ_cm", tuple(SHELL_OFFSET_CM),
      "front lip island", front_lip_components, "jaw clasp island", clasp_component,
      "rearward-routed front strap islands", front_strap_components)
print("RIGID_HELMET_PHYSICAL_LANDMARKS_M", "front brow", tuple(front_brow_marker), "rear tail", tuple(rear_tail_marker))
