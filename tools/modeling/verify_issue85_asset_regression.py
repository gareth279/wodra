"""Verify the pedal-fix blend keeps the accepted issue #163 helmet and hands.

Run in Blender 5.2.2:
  blender --background --python verify_issue85_asset_regression.py -- <accepted-source.blend> <pedal-fix.blend>
"""
import bpy
import sys
from collections import defaultdict
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
if len(args) != 2:
    raise SystemExit("expected accepted issue #163 source blend and corrected issue #85 blend")
source_path, fixed_path = args


def snapshot_hand_region(human):
    groups = {group.index: group.name for group in human.vertex_groups}
    hand_names = {name for name in groups.values()
                  if name.startswith(("wrist.", "metacarpal", "finger"))}
    if not hand_names:
        raise AssertionError("accepted sculpted-hand vertex groups are missing")
    hand_indexes = {index for index, name in groups.items() if name in hand_names}
    result = {}
    for vertex in human.data.vertices:
        weights = {groups[entry.group]: round(entry.weight, 7)
                   for entry in vertex.groups if entry.group in hand_indexes and entry.weight > 1e-6}
        if weights:
            result[vertex.index] = (
                tuple(round(float(component), 7) for component in vertex.co),
                tuple(sorted(weights.items())))
    return result


def mesh_components(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        points = [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
        parent = list(range(len(points)))

        def find(index):
            while parent[index] != index:
                parent[index] = parent[parent[index]]
                index = parent[index]
            return index

        for edge in mesh.edges:
            first, second = find(edge.vertices[0]), find(edge.vertices[1])
            if first != second:
                parent[second] = first
        components = defaultdict(list)
        for index, point in enumerate(points):
            components[find(index)].append(point)
        return sorted(components.values(), key=len, reverse=True)
    finally:
        evaluated.to_mesh_clear()


def verify_helmet_frame(rig, helmet, human, path):
    scene = bpy.context.scene
    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.update()
    front_anchor = bpy.data.objects.get("FrontWheelSpin")
    rear_anchor = bpy.data.objects.get("RearWheelSpin")
    if front_anchor is None or rear_anchor is None:
        raise AssertionError(f"{path} is missing the actual front/rear bicycle anchors")
    bike_forward = (front_anchor.matrix_world.translation - rear_anchor.matrix_world.translation).normalized()
    rider_up = (rig.matrix_world.to_3x3() @ Vector((0.0, 0.0, 1.0))).normalized()
    bike_side = bike_forward.cross(rider_up).normalized()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated_human = human.evaluated_get(depsgraph)
    human_mesh = evaluated_human.to_mesh()
    try:
        head_group = human.vertex_groups.get("head")
        if head_group is None:
            raise AssertionError(f"{path} is missing actual head weights")
        head_indexes = {
            vertex.index for vertex in human.data.vertices
            if any(group.group == head_group.index and group.weight > 0.5 for group in vertex.groups)
        }
        head_points = [evaluated_human.matrix_world @ human_mesh.vertices[index].co
                       for index in head_indexes]
        eye_materials = {index for index, material in enumerate(human.data.materials)
                         if material and "Rider Eye" in material.name}
        eye_indexes = {index for polygon in human_mesh.polygons
                       if polygon.material_index in eye_materials for index in polygon.vertices}
        eye_points = [evaluated_human.matrix_world @ human_mesh.vertices[index].co
                      for index in eye_indexes]
    finally:
        evaluated_human.to_mesh_clear()
    if len(head_points) < 500 or len(eye_points) < 20:
        raise AssertionError(f"{path} actual face or weighted head geometry is incomplete")
    head_center = sum(head_points, Vector()) / len(head_points)
    eye_center = sum(eye_points, Vector()) / len(eye_points)
    face_forward = (eye_center - head_center).normalized()
    face_alignment = face_forward.dot(bike_forward)
    if face_alignment < 0.5:
        raise AssertionError(f"{path} rider eye/head geometry faces away from the actual front wheel: "
                             f"dot={face_alignment:.3f}")

    components = mesh_components(helmet)
    if not components or len(components[0]) < 20000:
        raise AssertionError(f"{path} accepted vented helmet shell component is missing")
    retention_candidates = []
    for component in components[1:]:
        if not 500 <= len(component) <= 4000:
            continue
        component_center = sum(component, Vector()) / len(component)
        lateral = (component_center - head_center).dot(bike_side)
        height = (component_center - head_center).dot(rider_up)
        if abs(lateral) <= 0.03 and height >= 0.05:
            retention_candidates.append((component_center, len(component), lateral, height))
    if not retention_candidates:
        raise AssertionError(f"{path} central rear-retention component is missing")
    retention_center, retention_count, _, _ = min(
        retention_candidates,
        key=lambda item: (abs(item[2]), -item[3], abs(item[1] - 1250)))
    retention_to_eyes = (retention_center - eye_center).dot(face_forward)
    if retention_to_eyes >= -0.05:
        raise AssertionError(f"{path} helmet's rear-retention hardware is not behind the rider's face: "
                             f"offset={retention_to_eyes:.3f}m")
    shell = components[0]
    shell_projections = [point.dot(face_forward) for point in shell]
    shell_rear = min(shell_projections)
    shell_front = max(shell_projections)

    bones = rig.data.bones
    head = bones.get("head")
    front_ref = bones.get("helmet_front_ref")
    rear_ref = bones.get("helmet_rear_ref")
    if head is None or front_ref is None or rear_ref is None or \
            front_ref.parent != head or rear_ref.parent != head:
        raise AssertionError(f"{path} is missing head-attached helmet geometry references")
    front_marker = rig.matrix_world @ rig.pose.bones[front_ref.name].matrix.translation
    rear_marker = rig.matrix_world @ rig.pose.bones[rear_ref.name].matrix.translation
    marker_axis = front_marker - rear_marker
    alignment = marker_axis.normalized().dot(face_forward)
    if not .15 <= marker_axis.length <= .35 or alignment < .75 or \
            marker_axis.normalized().dot(bike_forward) < .75:
        raise AssertionError(f"{path} helmet references disagree with the actual face/bicycle frame: "
                             f"length={marker_axis.length:.3f}m faceDot={alignment:.3f}")
    shell_front_points = [point for point in shell if point.dot(face_forward) >= shell_front - 0.015]
    shell_rear_points = [point for point in shell if point.dot(face_forward) <= shell_rear + 0.015]
    shell_front_center = sum(shell_front_points, Vector()) / len(shell_front_points)
    shell_rear_center = sum(shell_rear_points, Vector()) / len(shell_rear_points)
    if (front_marker - shell_front_center).length > .04 or \
            (rear_marker - shell_rear_center).length > .04:
        raise AssertionError(f"{path} helmet reference bones do not follow the measured shell ends")
    print("PASS: actual eye/head geometry faces the front wheel and rear-retention hardware sits behind it",
          f"faceBikeDot={face_alignment:.3f} retentionToEyes={retention_to_eyes:.3f}m "
          f"retentionVertices={retention_count} shellLength={shell_front - shell_rear:.3f}m "
          f"markerFaceDot={alignment:.3f}")


def inspect(path, require_shoes=False):
    bpy.ops.wm.open_mainfile(filepath=path)
    human = bpy.data.objects.get("Human")
    rig = bpy.data.objects.get("Human.rig")
    helmet = bpy.data.objects.get("CyclingHelmet")
    if human is None or rig is None:
        raise AssertionError(f"{path} is missing the accepted human rig")
    if (helmet is None or helmet.get("source") != "Owner-supplied sport_cycling_helmet OBJ" or
            len(helmet.data.vertices) < 30000):
        raise AssertionError(f"{path} is missing the supplied vented helmet and strap mesh")
    if require_shoes:
        for side in ("L", "R"):
            for name in (f"shoe.{side}", f"shoe_heel.{side}", f"shoe_toe.{side}", f"pedal_contact.{side}"):
                if rig.data.bones.get(name) is None:
                    raise AssertionError(f"{path} is missing {name}")
            shoe = bpy.data.objects.get(f"CyclingShoe{side}")
            if shoe is None or shoe.vertex_groups.get(f"shoe.{side}") is None:
                raise AssertionError(f"{path} shoe geometry is not bound to its independent shoe bone")
        verify_helmet_frame(rig, helmet, human, path)
    return snapshot_hand_region(human), len(helmet.data.vertices)


source_hands, source_helmet_vertices = inspect(source_path)
fixed_hands, fixed_helmet_vertices = inspect(fixed_path, require_shoes=True)
if source_hands != fixed_hands:
    differing = [index for index in source_hands.keys() | fixed_hands.keys()
                 if source_hands.get(index) != fixed_hands.get(index)]
    raise AssertionError(f"accepted sculpted hand geometry or weights changed at vertices {differing[:12]}")
if fixed_helmet_vertices != source_helmet_vertices:
    raise AssertionError(f"accepted helmet topology changed: {source_helmet_vertices} -> {fixed_helmet_vertices}")
print("PASS: accepted hand vertex positions and weights unchanged", len(source_hands), "vertices")
print("PASS: owner-supplied vented helmet and strap mesh retained", fixed_helmet_vertices, "vertices")
print("PASS: both independent shoe bones, heel/toe markers, and pedal contacts present")
