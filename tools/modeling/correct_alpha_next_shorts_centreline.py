"""Correct the local gluteal centreline in the editable Blender garment source."""
import bpy
import bmesh
import math
import os
import sys
from collections import defaultdict
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
if len(args) != 2:
    raise RuntimeError("Expected candidate editable blend path and exported garments GLB path")
blend_output, glb_output = map(os.path.abspath, args)
shorts = bpy.data.objects.get("RiderBibShortsShell")
rig = bpy.data.objects.get("Human.rig")
if shorts is None or rig is None:
    raise RuntimeError("Existing source is missing the authored shorts or production rig")

front = next((obj for obj in bpy.context.scene.objects if "FrontWheelAssembly" in obj.name), None)
rear = next((obj for obj in bpy.context.scene.objects if "RearWheelAssembly" in obj.name), None)
if front is None or rear is None:
    raise RuntimeError("Production bike is required to derive the rearward garment axis")
rearward = (shorts.matrix_world.to_3x3().inverted() @
            -(front.matrix_world.translation - rear.matrix_world.translation).normalized()).normalized()

# The source has the original 5 mm broad Gaussian lift. Remove only that known
# edit in the upper rear short panel, so the new centreline target is measured
# from the actual alpha.90 handoff geometry.
old_peak, old_sigma_x, old_center_z, old_sigma_z = .005, .027, .915, .105
for vertex in shorts.data.vertices:
    x, _, z = vertex.co
    if abs(x) <= .11 and .78 <= z <= 1.04:
        factor = math.exp(-((x / old_sigma_x) ** 2) - (((z - old_center_z) / old_sigma_z) ** 2))
        vertex.co -= rearward * (old_peak * factor)

# A small subset of centreline vertices forms the exaggerated crease. For each
# central vertex, find the closest matching left/right garment vertices in the
# same transverse row and place it 2.5 mm inside their averaged rear surface.
# This preserves the two fitted lobes while removing the deep pinched trough.
verts = list(shorts.data.vertices)
left_by_row = defaultdict(list)
right_by_row = defaultdict(list)
for vertex in verts:
    if vertex.co.x <= -.004 and vertex.co.x >= -.055 and .85 <= vertex.co.z <= .925 and vertex.co.y > 0:
        left_by_row[vertex.co.z].append(vertex)
    elif vertex.co.x >= .004 and vertex.co.x <= .055 and .85 <= vertex.co.z <= .925 and vertex.co.y > 0:
        right_by_row[vertex.co.z].append(vertex)

def nearest_row(candidates, z):
    options = [v for row, row_verts in candidates.items() if abs(row - z) <= .014 for v in row_verts]
    return min(options, key=lambda v: abs(abs(v.co.x) - .014) + 2.0 * abs(v.co.z - z)) if options else None

central = [v for v in verts if abs(v.co.x) < .0015 and .85 <= v.co.z <= .925 and v.co.y > 0]
if len(central) < 4:
    raise RuntimeError(f"Expected centerline crease vertices, found {len(central)}")
targets = []
for vertex in central:
    left = nearest_row(left_by_row, vertex.co.z)
    right = nearest_row(right_by_row, vertex.co.z)
    if left is None or right is None:
        continue
    print(f"  sides centre={vertex.index}: left={left.index}@{tuple(round(v,5) for v in left.co)} right={right.index}@{tuple(round(v,5) for v in right.co)}")
    target_y = (left.co.y + right.co.y) * .5 - .0025
    targets.append((vertex, target_y))
if len(targets) < 4:
    raise RuntimeError(f"Could not fit symmetric shorts rows to {len(targets)}/{len(central)} centerline vertices")

total_delta = 0.0
max_delta = 0.0
for vertex, target_y in targets:
    current = rearward.dot(vertex.co)
    # The authored bike is aligned to local +Y; use projection so the mesh edit
    # remains valid if the garment object has a local rotation.
    delta = target_y - current
    print(f"  centre vertex={vertex.index} z={vertex.co.z:.5f} current={current:.5f} target={target_y:.5f} delta={delta*1000:.2f}mm")
    vertex.co += rearward * delta
    total_delta += abs(delta)
    max_delta = max(max_delta, abs(delta))
shorts.data.update()

# Confirm no topology, face winding or weights changed during the local edit.
bm = bmesh.new()
bm.from_mesh(shorts.data)
bm.normal_update()
zero_area = sum(face.calc_area() <= 1e-10 for face in bm.faces)
duplicate_faces = len(bm.faces) - len({tuple(sorted(v.index for v in face.verts)) for face in bm.faces})
boundary_edges = sum(edge.is_boundary for edge in bm.edges)
bm.free()
if zero_area or duplicate_faces:
    raise RuntimeError(f"Shorts mesh invalid: zero_area={zero_area}, duplicate_faces={duplicate_faces}")
print(f"Shorts cleft centreline: corrected={len(targets)} vertices max_delta={max_delta*1000:.2f}mm "
      f"mean_delta={total_delta/len(targets)*1000:.2f}mm retained_groove=2.5mm "
      f"boundaries={boundary_edges} zero_area={zero_area} duplicate_faces={duplicate_faces}; weights unchanged")

bpy.ops.wm.save_as_mainfile(filepath=blend_output)
bpy.context.view_layer.update()
rig_world = rig.matrix_world.copy()
rig.parent = None
rig.matrix_world = rig_world
jersey = bpy.data.objects.get("RiderJerseyShell")
if jersey is None:
    raise RuntimeError("Existing source is missing RiderJerseyShell")
for obj in (shorts, jersey):
    world = obj.matrix_world.copy()
    obj.parent = rig
    obj.matrix_world = world
bpy.ops.object.select_all(action="DESELECT")
for obj in (rig, shorts, jersey):
    obj.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.export_scene.gltf(filepath=glb_output, export_format="GLB", use_selection=True,
    export_apply=True, export_skins=True, export_animations=False, export_yup=True,
    export_extras=True, export_materials="EXPORT", export_image_format="AUTO")
print(f"Saved centreline candidate source={blend_output} GLB={glb_output}")
