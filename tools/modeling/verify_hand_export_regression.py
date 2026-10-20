"""Fail if MPFB helper cubes leak or restored wrist seams are omitted.

Run in Blender with the editable grip-pass blend open:
  blender --background <source.blend> --python verify_hand_export_regression.py -- <production.glb>

The two helper faces are the traced MPFB control faces from the alpha48
diagnosis. The 48 wrist seams are identified by their full helper-tights mask
weight plus real wrist/finger deformation weights, then checked in both the
mask-baked export input and a fresh GLB reimport.
"""
import bpy
import importlib.util
import sys
from pathlib import Path

args = sys.argv[sys.argv.index("--") + 1:]
if len(args) != 1:
    raise SystemExit("expected exported GLB path")
glb = Path(args[0]).resolve()
if not glb.is_file():
    raise FileNotFoundError(glb)

def fail(message):
    raise AssertionError(message)

def point_key(point):
    return tuple(round(float(c), 6) for c in point)

def face_key(mesh, polygon):
    return tuple(sorted(point_key(mesh.vertices[index].co) for index in polygon.vertices))

human = bpy.data.objects.get("Human")
if human is None:
    fail("source blend has no Human mesh")

# The traced helper vertex sets are stable in the preserved source blend.
helper_ids = {13648: (13970, 13971, 13967, 13966),
              14016: (14460, 14456, 14457, 14461)}
helper_keys = {}
for face_id, expected_vertices in helper_ids.items():
    if face_id >= len(human.data.polygons):
        fail(f"traced helper face {face_id} is absent from the source mesh")
    polygon = human.data.polygons[face_id]
    if tuple(polygon.vertices) != expected_vertices:
        fail(f"traced helper face {face_id} topology changed: {tuple(polygon.vertices)}")
    helper_keys[face_id] = face_key(human.data, polygon)

groups = {group.name: group.index for group in human.vertex_groups}
proxy_group_indices = {name: groups[name] for name in ("JointCubes", "HelperGeometry") if name in groups}
if not proxy_group_indices:
    fail("source lost the MPFB proxy groups used to identify disconnected finger/wrist controls")

def weight(vertex, group_index):
    return next((assignment.weight for assignment in vertex.groups
                 if assignment.group == group_index), 0.0)

def full_proxy_face(polygon):
    vertices = [human.data.vertices[index] for index in polygon.vertices]
    for name, group_index in proxy_group_indices.items():
        if all(weight(vertex, group_index) >= .999 for vertex in vertices):
            return name
    return None

eye_material_indices = {index for index, material in enumerate(human.data.materials)
                        if material and "Rider Eye" in material.name}
eye_group_indices = {side: groups.get(f"eye.{side}") for side in ("L", "R")}
helper_geometry_index = groups.get("HelperGeometry")

def is_anatomical_eye(polygon):
    if polygon.material_index not in eye_material_indices or helper_geometry_index is None:
        return False
    vertices = [human.data.vertices[index] for index in polygon.vertices]
    helper_shell = all(weight(vertex, helper_geometry_index) >= .999 for vertex in vertices)
    eye_weighted = any(index is not None and all(weight(vertex, index) >= .95 for vertex in vertices)
                       for index in eye_group_indices.values())
    return helper_shell and eye_weighted

helper_tights = groups.get("helper-tights")
if helper_tights is None:
    fail("source lost helper-tights weights used to identify restored wrist seams")

wrist_faces = {"L": [], "R": []}
for side in ("L", "R"):
    deform_names = {name for name in groups
                    if name == f"wrist.{side}"
                    or name.startswith(("metacarpal", "finger")) and name.endswith("." + side)}
    deform_indices = {groups[name] for name in deform_names}
    wrist_index = groups.get(f"wrist.{side}")
    if wrist_index is None:
        fail(f"source lost wrist.{side}")
    for polygon in human.data.polygons:
        vertices = [human.data.vertices[index] for index in polygon.vertices]
        full_helper_weight = all(weight(vertex, helper_tights) >= .999 for vertex in vertices)
        deform_vertices = sum(any(assignment.group in deform_indices and assignment.weight > .01
                                  for assignment in vertex.groups) for vertex in vertices)
        wrist_weight = sum(weight(vertex, wrist_index) for vertex in vertices)
        if full_helper_weight and deform_vertices >= 2 and wrist_weight > .01:
            wrist_faces[side].append(polygon)
    if len(wrist_faces[side]) != 24:
        fail(f"expected 24 restored {side} wrist seam faces, found {len(wrist_faces[side])}")

restored_wrist_ids = {polygon.index for faces in wrist_faces.values() for polygon in faces}
proxy_face_ids = {name: [] for name in proxy_group_indices}
for polygon in human.data.polygons:
    proxy_name = full_proxy_face(polygon)
    if proxy_name and polygon.index not in restored_wrist_ids and not is_anatomical_eye(polygon):
        proxy_face_ids[proxy_name].append(polygon.index)
if not proxy_face_ids.get("JointCubes") or not proxy_face_ids.get("HelperGeometry"):
    fail(f"expected disconnected MPFB finger and wrist controls in source for export regression: "
         f"{ {name: len(faces) for name, faces in proxy_face_ids.items()} }")

module_path = Path(__file__).with_name("polish_rider.py")
spec = importlib.util.spec_from_file_location("wodra_polish_rider", module_path)
polish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(polish)
export_mesh, authoring_mesh, authoring_name = polish.make_gltf_human_copy(human)
try:
    baked_keys = {face_key(export_mesh.data, polygon) for polygon in export_mesh.data.polygons}
    for face_id, signature in helper_keys.items():
        if signature in baked_keys:
            fail(f"traced MPFB helper face {face_id} returned in mask-baked export mesh")
    for side, faces in wrist_faces.items():
        missing = [polygon.index for polygon in faces if face_key(human.data, polygon) not in baked_keys]
        if missing:
            fail(f"mask-baked export mesh omitted restored {side} wrist faces {missing}")
    for name, face_ids in proxy_face_ids.items():
        for face_id in face_ids:
            polygon = human.data.polygons[face_id]
            signature = face_key(human.data, polygon)
            if signature in baked_keys:
                fail(f"full-weight {name} control face {face_id} returned in mask-baked export mesh")
finally:
    bpy.data.objects.remove(export_mesh, do_unlink=True)
    authoring_mesh.name = authoring_name

# Inspect the actual exported GLB in a fresh import operation. glTF import can
# triangulate quads, so each restored wrist quad must be represented by two
# triangles whose vertices collectively cover all four original corners.
before_objects = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=str(glb))
imported = [obj for obj in bpy.data.objects if obj not in before_objects and obj.type == "MESH"]
if not imported:
    fail("GLB reimport produced no meshes")
human_runtime = max(imported, key=lambda obj: len(obj.data.vertices))
runtime_eye_materials = {material.name for material in human_runtime.data.materials
                         if material and "Rider Eye" in material.name}
runtime_eye_faces = sum(1 for polygon in human_runtime.data.polygons
                        if polygon.material_index < len(human_runtime.data.materials)
                        and human_runtime.data.materials[polygon.material_index]
                        and "Rider Eye" in human_runtime.data.materials[polygon.material_index].name)
if not runtime_eye_materials or runtime_eye_faces == 0:
    fail("GLB reimport lost the anatomical eye material surface")
runtime_triangles = []
for polygon in human_runtime.data.polygons:
    if len(polygon.vertices) != 3:
        continue
    runtime_triangles.append([human_runtime.matrix_world @ human_runtime.data.vertices[i].co
                              for i in polygon.vertices])
runtime_wrist = {side: [] for side in ("L", "R")}
runtime_triangle_keys = {tuple(sorted(tuple(round(float(component), 5) for component in point)
                                     for point in triangle))
                         for triangle in runtime_triangles}

def reimport_contains_quad(corners):
    keys = [tuple(round(float(component), 5) for component in point) for point in corners]
    triangles = [tuple(sorted((keys[0], keys[1], keys[2]))),
                 tuple(sorted((keys[0], keys[2], keys[3]))),
                 tuple(sorted((keys[0], keys[1], keys[3]))),
                 tuple(sorted((keys[1], keys[2], keys[3])))]
    return (triangles[0] in runtime_triangle_keys and triangles[1] in runtime_triangle_keys) or \
           (triangles[2] in runtime_triangle_keys and triangles[3] in runtime_triangle_keys)

for face_id in helper_ids:
    polygon = human.data.polygons[face_id]
    corners = [human.matrix_world @ human.data.vertices[i].co for i in polygon.vertices]
    if reimport_contains_quad(corners):
        fail(f"traced MPFB helper face {face_id} returned in reimported GLB")

for name, face_ids in proxy_face_ids.items():
    for face_id in face_ids:
        polygon = human.data.polygons[face_id]
        corners = [human.matrix_world @ human.data.vertices[i].co for i in polygon.vertices]
        if reimport_contains_quad(corners):
            fail(f"full-weight {name} control face {face_id} returned in reimported GLB")

for side, faces in wrist_faces.items():
    for polygon in faces:
        corners = [human.matrix_world @ human.data.vertices[i].co for i in polygon.vertices]
        if reimport_contains_quad(corners):
            runtime_wrist[side].append(polygon.index)
    if len(runtime_wrist[side]) != 24:
        fail(f"GLB reimport retained {len(runtime_wrist[side])}/24 restored {side} wrist surfaces")

print("PASS: traced helper faces absent from baked export input and reimported GLB")
print("PASS: disconnected MPFB rig helpers absent", {name: len(faces) for name, faces in proxy_face_ids.items()})
print("PASS: restored wrist surfaces retained", {side: len(items) for side, items in runtime_wrist.items()})
print("PASS: anatomical eye surface retained", sorted(runtime_eye_materials), runtime_eye_faces)
print("GLB_REIMPORT", glb, "mesh", human_runtime.name,
      "vertices", len(human_runtime.data.vertices), "faces", len(human_runtime.data.polygons))
