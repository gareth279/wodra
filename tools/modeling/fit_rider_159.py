"""Fit the existing Wodra rider to the imported road bike, then export the GLB.

Run this on the editable integrated Blender checkpoint produced by
integrate_cgtrader_road_bike_158.py. The bicycle scene is treated as read-only;
only the rider armature, rider meshes and outfit details are changed.

  blender --background integrated.blend --python tools/modeling/fit_rider_159.py -- \
    output.glb output.blend
"""
from __future__ import annotations

import importlib
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


BIKE_CONTACTS = {
    "left": Vector((-.222, .502, .948)),
    "right": Vector((.222, .502, .948)),
}

# The control origin is offset from the palm base: the MPFB wrist/palm rests
# above and just aft of this point. These offsets put the palm heel on the
# measured top surface while leaving room for the fingers to curl around it.
HAND_CONTROL_OFFSETS = {
    "left": Vector((-.007, .051, -.086)),
    "right": Vector((.007, .051, -.086)),
}

ELBOW_TARGETS = {
    "left": Vector((-.255, .300, 1.13)),
    "right": Vector((.255, .300, 1.13)),
}


def action_curves(action):
    for layer in action.layers:
        for strip in layer.strips:
            for slot in action.slots:
                bag = strip.channelbag(slot)
                if bag:
                    yield from bag.fcurves


def add_forward_hip_hinge(action):
    """Distribute a modest forward hinge across the lower thoracic chain."""
    deltas = {"spine05": math.radians(26),
              "spine04": math.radians(9),
              "spine03": math.radians(3)}
    changed = set()
    for curve in action_curves(action):
        if not curve.data_path.endswith("].rotation_euler") or curve.array_index != 0:
            continue
        for name, delta in deltas.items():
            if f'pose.bones["{name}"]' in curve.data_path:
                for point in curve.keyframe_points:
                    point.co.y += delta
                    point.handle_left.y += delta
                    point.handle_right.y += delta
                curve.update()
                changed.add(name)
                break
    if changed != set(deltas):
        raise RuntimeError(f"missing spine rotation channels: {set(deltas) - changed}")
    print("RIDER159_SPINE_HINGE_DEGREES", {name: round(math.degrees(value), 2)
                                            for name, value in deltas.items()})


def set_control_world(rig, name: str, position: Vector, rotation: Matrix | None = None):
    control = rig.pose.bones[name]
    control.rotation_mode = "QUATERNION"
    matrix = control.matrix.copy()
    matrix.translation = rig.matrix_world.inverted() @ position
    if rotation is not None:
        armature_rotation = rig.matrix_world.to_3x3().inverted() @ rotation
        matrix = armature_rotation.to_4x4()
        matrix.translation = rig.matrix_world.inverted() @ position
    control.matrix = matrix
    return control


def enable_arm_ik(rig):
    try:
        mpfb = importlib.import_module("mpfb")
    except ModuleNotFoundError:
        import addon_utils
        modules = [module.__name__ for module in addon_utils.modules(refresh=True)
                   if module.__name__.split(".")[-1] == "mpfb"]
        if not modules:
            raise RuntimeError("MPFB is required to restore the authored arm IK rig")
        mpfb = importlib.import_module(modules[0])
        addon_utils.enable(mpfb.__name__)
    if mpfb.__name__ + ".services" not in sys.modules:
        import addon_utils
        addon_utils.enable(mpfb.__name__)
    helper_type = importlib.import_module(
        mpfb.__name__ + ".entities.rigging.righelpers.armhelpers.armhelpers").ArmHelpers
    settings = {
        "arm_helpers_type": "LOWERUPPER",
        "arm_parenting_strategy": "SPINE",
        "arm_target_rotates_hand": True,
        "arm_target_rotates_lower_arm": False,
        "hide_fk": False,
    }
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.mode_set(mode="EDIT")
    for name in ("left_hand_ik", "left_elbow_ik", "right_hand_ik", "right_elbow_ik"):
        bone = rig.data.edit_bones.get(name)
        if bone:
            rig.data.edit_bones.remove(bone)
    bpy.ops.object.mode_set(mode="OBJECT")
    for side in ("left", "right"):
        helper_type.get_instance(side, settings).apply_ik(rig)
    constraints = [constraint for bone in rig.pose.bones for constraint in bone.constraints
                   if constraint.type == "IK"]
    if not constraints:
        raise RuntimeError("MPFB arm IK helpers did not create constraints")
    for constraint in constraints:
        constraint.use_stretch = False
    print("RIDER159_ARM_IK_CONSTRAINTS", len(constraints))


def retarget_and_bake(rig):
    action = rig.animation_data.action if rig.animation_data else None
    if action is None or "PedalCycle" not in action.name:
        raise RuntimeError("integrated source must contain PedalCycle")
    start, end = bpy.context.scene.frame_start, bpy.context.scene.frame_end
    if end - start < 100:
        raise RuntimeError(f"unexpected pedal cycle frame range {start}-{end}")
    add_forward_hip_hinge(action)
    enable_arm_ik(rig)
    scene = bpy.context.scene
    for frame in range(start, end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        for side in ("left", "right"):
            suffix = side
            wrist = rig.pose.bones["wrist." + ("L" if side == "left" else "R")]
            wrist_world = rig.matrix_world @ wrist.matrix
            hand_axis = rig.matrix_world.to_3x3() @ (wrist.tail - wrist.head)
            hand_rotation = (hand_axis.normalized().rotation_difference(Vector((0, 1, 0))
                             ) @ wrist_world.to_quaternion())
            hand_target = BIKE_CONTACTS[side] + HAND_CONTROL_OFFSETS[side]
            hand = set_control_world(rig, suffix + "_hand_ik", hand_target,
                                     hand_rotation.to_matrix())
            elbow = set_control_world(rig, suffix + "_elbow_ik", ELBOW_TARGETS[side])
            hand.keyframe_insert(data_path="location", frame=frame)
            hand.keyframe_insert(data_path="rotation_quaternion", frame=frame)
            elbow.keyframe_insert(data_path="location", frame=frame)
            elbow.keyframe_insert(data_path="rotation_quaternion", frame=frame)
            bpy.context.view_layer.update()

    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.nla.bake(frame_start=start, frame_end=end, step=1,
                     only_selected=False, visual_keying=True, clear_constraints=True,
                     use_current_action=False, bake_types={"POSE"})
    bpy.ops.object.mode_set(mode="OBJECT")
    baked = rig.animation_data.action if rig.animation_data else None
    if baked is None:
        raise RuntimeError("could not bake the fitted rider animation")
    baked.name = "PedalCycle"
    for curve in action_curves(baked):
        for point in curve.keyframe_points:
            point.interpolation = "LINEAR"
    worst = {"left": 0.0, "right": 0.0}
    for sample in range(17):
        frame = round(start + (end - start) * sample / 16)
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        for side in ("left", "right"):
            suffix = "L" if side == "left" else "R"
            palms = [rig.pose.bones[f"metacarpal{i}.{suffix}"] for i in range(1, 5)]
            palm = sum(((rig.matrix_world @ bone.matrix).translation for bone in palms),
                       Vector()) / len(palms)
            error = (palm - BIKE_CONTACTS[side]).length
            worst[side] = max(worst[side], error)
    print("RIDER159_FULL_CYCLE_PALM_MAX_M", {side: round(value, 4)
                                            for side, value in worst.items()})
    if max(worst.values()) > .045:
        raise RuntimeError(f"full-cycle palm misses hood contact envelope: {worst}")
    scene.frame_set(start)
    bpy.context.view_layer.update()
    print("RIDER159_BAKED", len(list(action_curves(baked))), "curves", start, end)


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if len(args) != 2:
        raise SystemExit("expected output GLB and editable output blend")
    glb, blend = map(Path, args)
    glb.parent.mkdir(parents=True, exist_ok=True)
    blend.parent.mkdir(parents=True, exist_ok=True)
    rig = bpy.data.objects.get("Human.rig")
    bike = bpy.data.objects.get("BicycleModel")
    human = bpy.data.objects.get("Human")
    if not rig or not bike or not human:
        raise RuntimeError("integrated cyclist must retain Human.rig, Human and BicycleModel")
    retarget_and_bake(rig)
    bpy.ops.wm.save_as_mainfile(filepath=str(blend))
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = bike
    bpy.ops.export_scene.gltf(filepath=str(glb), export_format="GLB", use_selection=True,
                              export_animations=True, export_frame_range=True,
                              export_force_sampling=True, export_animation_mode="ACTIONS")
    print("EXPORTED_RIDER159", glb)


if __name__ == "__main__":
    main()
