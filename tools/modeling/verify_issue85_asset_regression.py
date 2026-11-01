"""Verify the pedal-fix blend keeps the accepted issue #163 helmet and hands.

Run in Blender 5.2.2:
  blender --background --python verify_issue85_asset_regression.py -- <accepted-source.blend> <pedal-fix.blend>
"""
import bpy
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from retarget_pedals_163 import (  # noqa: E402
    helmet_semantics,
    require_helmet_physical_forward,
    rotate_helmet_mesh_180,
)

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


def verify_helmet_frame(rig, helmet, human, path):
    scene = bpy.context.scene
    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.update()
    metrics = helmet_semantics(rig, helmet, human)
    try:
        require_helmet_physical_forward(metrics, str(path))
    except RuntimeError as error:
        raise AssertionError(str(error)) from error

    bones = rig.data.bones
    head = bones.get("head")
    front_ref = bones.get("helmet_front_ref")
    rear_ref = bones.get("helmet_rear_ref")
    if head is None or front_ref is None or rear_ref is None or \
            front_ref.parent != head or rear_ref.parent != head:
        raise AssertionError(f"{path} is missing head-attached physical brow-lip and rear-tail references")
    front_marker = rig.matrix_world @ rig.pose.bones[front_ref.name].matrix.translation
    rear_marker = rig.matrix_world @ rig.pose.bones[rear_ref.name].matrix.translation
    if (front_marker - metrics["physical_front"]).length > 0.005 or \
            (rear_marker - metrics["physical_rear"]).length > 0.005:
        raise AssertionError(f"{path} exported helmet reference bones do not mark the independently measured OBJ features")

    original_vertices = [vertex.co.copy() for vertex in helmet.data.vertices]
    try:
        rotate_helmet_mesh_180(rig, helmet, metrics["head_center"], metrics["rider_up"])
        reversed_metrics = helmet_semantics(rig, helmet, human)
        try:
            require_helmet_physical_forward(reversed_metrics, "deliberately reversed control")
        except RuntimeError:
            pass
        else:
            raise AssertionError("helmet physical-feature regression accepted a deliberate 180-degree reverse")
    finally:
        for vertex, original in zip(helmet.data.vertices, original_vertices):
            vertex.co = original
        helmet.data.update()
        bpy.context.view_layer.update()

    print("PASS: source OBJ front lip is ahead of the face and rear shell tail is behind it",
          f"faceBikeDot={metrics['face_bike_alignment']:.3f} noseBikeDot={metrics['nose_bike_alignment']:.3f} "
          f"frontToEyes={metrics['front_to_eye']:.3f}m rearToEyes={metrics['rear_to_eye']:.3f}m "
          f"featureBikeDot={metrics['front_bike_alignment']:.3f} "
          f"sourceFitError={metrics['source_fit_error']:.9f}m; deliberate reverse rejected")


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
