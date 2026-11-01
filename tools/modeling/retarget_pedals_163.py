"""Retarget the preserved issue #159 rider to the imported moving pedals.

This pass starts from the accepted sculpted-hands source blend. It preserves
the upper body and bicycle mesh while re-solving the bilateral leg chains
against the real crank-mounted pedal surface points.

  blender --background --python tools/modeling/retarget_pedals_163.py -- \
    --blend tools/modeling/source/WodraCyclist.issue159-sculpted-hands.blend \
    --output-blend tools/modeling/checkpoints/WodraCyclist.issue163-fit.blend \
    --output-glb tools/modeling/exports/WodraCyclist-issue163-fit.glb
"""
from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector
from mathutils.bvhtree import BVHTree


SAMPLES_PER_REVOLUTION = 72
BALL_VERTEX = 45  # underside vertex at the authored forefoot station, y=118 mm
CRANK_PHASE_OFFSET = -math.pi / 2  # imported crank starts 90 degrees ahead of PedalCycle
FOOT_PITCH_AMPLITUDE = math.radians(7)
# A road cyclist's knees track forward in the bike's sagittal plane. Do not
# derive this direction from the split lower-leg helper bones: the two sides'
# keyed helpers use mirrored local axes and can make one knee bend backwards.
MAX_KNEE_STEP = 0.022
MAX_BACKWARD_KNEE_OFFSET = 0.012
MIN_FORWARD_KNEE_OFFSET_WHEN_FLEXED = 0.006
SHOE_HEIGHT_SCALE = 0.62
TARGET_LEG_SEGMENT_LENGTH = 0.45
SIDES = {
    "L": ("left", "LeftPedal", "Contact_LeftPedal", "PedalBearingLeft",
          "Contact_LeftPedalSurface", "left_foot_ik", "left_knee_ik"),
    "R": ("right", "RightPedal", "Contact_RightPedal", "PedalBearingRight",
          "Contact_RightPedalSurface", "right_foot_ik", "right_knee_ik"),
}


def action_curves(action):
    for layer in action.layers:
        for strip in layer.strips:
            for slot in action.slots:
                bag = strip.channelbag(slot)
                if bag:
                    yield from bag.fcurves


def remove_control_animation(action, control_names, rig):
    """Keep accepted upper-body motion and release the full lower body for IK."""
    leg_bones = set()
    for side in SIDES:
        root = rig.data.bones.get(f"pelvis.{side}")
        if root is None:
            raise RuntimeError(f"the {side} pelvic anchor is missing")
        pending = [root]
        while pending:
            bone = pending.pop()
            leg_bones.add(bone.name)
            pending.extend(bone.children)
    removed = 0
    for layer in action.layers:
        for strip in layer.strips:
            for slot in action.slots:
                bag = strip.channelbag(slot)
                if bag is None:
                    continue
                for curve in list(bag.fcurves):
                    if any(f'pose.bones["{name}"]' in curve.data_path
                           for name in control_names | leg_bones):
                        bag.fcurves.remove(curve)
                        removed += 1
    for name in leg_bones:
        rig.pose.bones[name].matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    print("LOWER_BODY_CURVES_REMOVED", removed, "bones", sorted(leg_bones))


def world_pose(rig, pose_bone):
    return rig.matrix_world @ pose_bone.matrix


def evaluated_vertex_world(obj, index):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        return evaluated.matrix_world @ mesh.vertices[index].co
    finally:
        evaluated.to_mesh_clear()


def add_pedal_bearings():
    """Add pivot-centered bearings without moving or changing pedal geometry."""
    crank = bpy.data.objects.get("CrankPivot")
    if crank is None:
        raise RuntimeError("imported CrankPivot is missing")
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for side, (_, pedal_name, contact_name, bearing_name, surface_name, *_rest) in SIDES.items():
        pedal = bpy.data.objects.get(pedal_name)
        contact = bpy.data.objects.get(contact_name)
        if pedal is None or contact is None:
            raise RuntimeError(f"imported {side} pedal or spindle contact is missing")
        bearing = bpy.data.objects.get(bearing_name)
        surface = bpy.data.objects.get(surface_name)
        if bearing is None:
            contact_local = crank.matrix_world.inverted() @ contact.matrix_world.translation
            pedal_world = pedal.matrix_world.copy()
            bearing = bpy.data.objects.new(bearing_name, None)
            bpy.context.scene.collection.objects.link(bearing)
            bearing.empty_display_type = "PLAIN_AXES"
            bearing.empty_display_size = .012
            bearing.parent = crank
            bearing.matrix_parent_inverse = Matrix.Identity(4)
            bearing.location = contact_local
            bearing.rotation_euler = (0.0, 0.0, 0.0)
            pedal.parent = bearing
            pedal.matrix_parent_inverse = Matrix.Identity(4)
            bpy.context.view_layer.update()
            pedal.matrix_basis = bearing.matrix_world.inverted() @ pedal_world

        if pedal.parent != bearing:
            raise RuntimeError(f"{pedal_name} is not parented to {bearing_name}")
        if surface is None:
            bpy.context.view_layer.update()
            depsgraph = bpy.context.evaluated_depsgraph_get()
            evaluated = pedal.evaluated_get(depsgraph)
            tree = BVHTree.FromObject(evaluated, depsgraph, epsilon=0.0)
            contact_world = contact.matrix_world.translation.copy()
            query_local = evaluated.matrix_world.inverted() @ (contact_world + Vector((0.0, 0.0, .04)))
            nearest = tree.find_nearest(query_local)
            if nearest is None:
                raise RuntimeError(f"could not locate the actual {side} pedal top surface")
            local_point, local_normal, _face, _distance = nearest
            world_point = evaluated.matrix_world @ local_point
            world_normal = (evaluated.matrix_world.to_3x3() @ local_normal).normalized()
            if world_normal.z < .75 or world_point.z <= contact_world.z:
                raise RuntimeError(f"{side} spindle marker does not resolve to the pedal's upper platform: "
                                   f"contact={tuple(round(v, 5) for v in contact_world)}, "
                                   f"point={tuple(round(v, 5) for v in world_point)}, "
                                   f"normal={tuple(round(v, 3) for v in world_normal)}")
            bearing_world = bearing.matrix_world.copy()
            surface = bpy.data.objects.new(surface_name, None)
            bpy.context.scene.collection.objects.link(surface)
            surface.empty_display_type = "SPHERE"
            surface.empty_display_size = .006
            surface.parent = bearing
            surface.matrix_parent_inverse = Matrix.Identity(4)
            surface.location = bearing_world.inverted() @ world_point
            surface.rotation_euler = (0.0, 0.0, 0.0)
            surface["contact_role"] = "measured upper pedal platform point"
            print("PEDAL_SURFACE_OFFSET", side,
                  tuple(round(v, 5) for v in surface.location),
                  "normal", tuple(round(v, 3) for v in world_normal))


def fit_road_shoes():
    """Reduce boot-like height while preserving the outsole and forefoot point."""
    for side in SIDES:
        shoe = bpy.data.objects.get(f"CyclingShoe{side}")
        if shoe is None or len(shoe.data.vertices) <= BALL_VERTEX:
            raise RuntimeError(f"CyclingShoe{side} is missing its authored forefoot point")
        minimum = min(vertex.co.z for vertex in shoe.data.vertices)
        maximum = max(vertex.co.z for vertex in shoe.data.vertices)
        original_height = maximum - minimum
        forefoot_height = shoe.data.vertices[BALL_VERTEX].co.z
        for vertex in shoe.data.vertices:
            vertex.co.z = forefoot_height + (vertex.co.z - forefoot_height) * SHOE_HEIGHT_SCALE
        shoe.data.update()
        new_height = max(vertex.co.z for vertex in shoe.data.vertices) - minimum
        print("ROAD_SHOE_HEIGHT", side, round(original_height, 4), "->", round(new_height, 4),
              "forefoot", tuple(round(v, 4) for v in shoe.data.vertices[BALL_VERTEX].co))


def add_pedal_contact_bones(rig, local_offsets):
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.mode_set(mode="EDIT")
    for side, (shoe_side, *_rest) in SIDES.items():
        name = f"pedal_contact.{side}"
        old = rig.data.edit_bones.get(name)
        if old:
            rig.data.edit_bones.remove(old)
        edit = rig.data.edit_bones.new(name)
        foot_name = f"foot.{side}"
        foot_rest = rig.data.bones[foot_name].matrix_local
        head = foot_rest @ local_offsets[side]
        edit.head = head
        edit.tail = head + Vector((0.0, 0.0, .008))
        edit.parent = rig.data.edit_bones[foot_name]
        edit.use_connect = False
        edit.use_deform = False
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.update()


def add_leg_ik_targets():
    targets = {}
    poles = {}
    for side in SIDES:
        target = bpy.data.objects.new(f"PedalFitTarget{side}", None)
        pole = bpy.data.objects.new(f"PedalKneeTarget{side}", None)
        bpy.context.scene.collection.objects.link(target)
        bpy.context.scene.collection.objects.link(pole)
        target.empty_display_type = "SPHERE"
        target.empty_display_size = .01
        pole.empty_display_type = "SPHERE"
        pole.empty_display_size = .01
        target.rotation_mode = "QUATERNION"
        targets[side] = target
        poles[side] = pole
    return targets, poles


def merge_vertex_groups(mesh_object, source_name, target_name):
    source = mesh_object.vertex_groups.get(source_name)
    if source is None:
        return
    target = mesh_object.vertex_groups.get(target_name)
    if target is None:
        target = mesh_object.vertex_groups.new(name=target_name)
    source_index = source.index
    for vertex in mesh_object.data.vertices:
        weight = next((entry.weight for entry in vertex.groups if entry.group == source_index), 0.0)
        if weight > 0.0:
            target.add([vertex.index], weight, "ADD")
    mesh_object.vertex_groups.remove(source)


def stretch_along_segment(point, start, end, factor):
    axis = (end - start).normalized()
    along = (point - start).dot(axis)
    radial = point - (start + axis * along)
    return start + axis * (along * factor) + radial


def collapse_helper_leg_bones(rig):
    """Replace split thigh/shin helpers with one deform bone per anatomical segment."""
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    meshes = [obj for obj in bpy.data.objects if obj.type == "MESH" and
              any(mod.type == "ARMATURE" and mod.object == rig for mod in obj.modifiers)]
    transforms = {}
    for side in SIDES:
        thigh_head = rig.data.bones[f"upperleg01.{side}"].head_local.copy()
        knee = rig.data.bones[f"upperleg02.{side}"].tail_local.copy()
        ankle = rig.data.bones[f"lowerleg02.{side}"].tail_local.copy()
        thigh_factor = TARGET_LEG_SEGMENT_LENGTH / (knee - thigh_head).length
        shin_factor = TARGET_LEG_SEGMENT_LENGTH / (ankle - knee).length
        transforms[side] = (thigh_head, knee, ankle, thigh_factor, shin_factor)

    for mesh_object in meshes:
        mesh_to_rig = rig.matrix_world.inverted() @ mesh_object.matrix_world
        rig_to_mesh = mesh_to_rig.inverted()
        group_indexes = {}
        for side in SIDES:
            group_indexes[side] = tuple(
                mesh_object.vertex_groups.get(f"{name}.{side}").index
                if mesh_object.vertex_groups.get(f"{name}.{side}") is not None else -1
                for name in ("upperleg01", "upperleg02", "lowerleg01", "lowerleg02"))
        for vertex in mesh_object.data.vertices:
            influences = {entry.group: entry.weight for entry in vertex.groups}
            if not influences:
                continue
            total = sum(influences.values())
            if total <= 1e-6:
                continue
            point = mesh_to_rig @ vertex.co
            segment_influences = []
            leg_weight = 0.0
            for side in SIDES:
                upper_a, upper_b, lower_a, lower_b = group_indexes[side]
                weights = [influences.get(index, 0.0) if index >= 0 else 0.0
                           for index in (upper_a, upper_b, lower_a, lower_b)]
                thigh_weight = min(1.0, (weights[0] + weights[1]) / total)
                shin_weight = min(1.0 - thigh_weight, (weights[2] + weights[3]) / total)
                if thigh_weight <= 0.0 and shin_weight <= 0.0:
                    continue
                hip, knee, ankle, thigh_factor, shin_factor = transforms[side]
                segment_influences.append((side, thigh_weight, shin_weight))
                leg_weight += thigh_weight + shin_weight
            if leg_weight > 1.0:
                segment_influences = [(side, thigh / leg_weight, shin / leg_weight)
                                      for side, thigh, shin in segment_influences]
                leg_weight = 1.0
            result = point * (1.0 - leg_weight)
            for side, thigh_weight, shin_weight in segment_influences:
                hip, knee, ankle, thigh_factor, shin_factor = transforms[side]
                if thigh_weight > 0.0:
                    result += stretch_along_segment(point, hip, knee, thigh_factor) * thigh_weight
                if shin_weight > 0.0:
                    result += stretch_along_segment(point, knee, ankle, shin_factor) * shin_weight
            vertex.co = rig_to_mesh @ result
        mesh_object.data.update()

    for mesh_object in meshes:
        for side in SIDES:
            merge_vertex_groups(mesh_object, f"upperleg02.{side}", f"upperleg01.{side}")
            merge_vertex_groups(mesh_object, f"lowerleg02.{side}", f"lowerleg01.{side}")

    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for side in SIDES:
        thigh = rig.data.edit_bones[f"upperleg01.{side}"]
        thigh_knee = rig.data.edit_bones[f"upperleg02.{side}"].tail.copy()
        shin = rig.data.edit_bones[f"lowerleg01.{side}"]
        ankle = rig.data.edit_bones[f"lowerleg02.{side}"].tail.copy()
        foot = rig.data.edit_bones[f"foot.{side}"]
        hip = thigh.head.copy()
        knee_offset = (thigh_knee - hip).normalized() * TARGET_LEG_SEGMENT_LENGTH
        shin_offset = (ankle - thigh_knee).normalized() * TARGET_LEG_SEGMENT_LENGTH
        new_knee = hip + knee_offset
        new_ankle = new_knee + shin_offset
        thigh.tail = new_knee
        thigh.use_connect = True
        shin.head = new_knee
        shin.tail = new_ankle
        shin.parent = thigh
        shin.use_connect = True
        foot.head = new_ankle
        foot.parent = shin
        foot.use_connect = True
        rig.data.edit_bones.remove(rig.data.edit_bones[f"upperleg02.{side}"])
        rig.data.edit_bones.remove(rig.data.edit_bones[f"lowerleg02.{side}"])
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.update()
    for side in SIDES:
        thigh_length = rig.data.bones[f"upperleg01.{side}"].length
        shin_length = rig.data.bones[f"lowerleg01.{side}"].length
        if not (.42 <= thigh_length <= .47 and .42 <= shin_length <= .47):
            raise RuntimeError(f"collapsed {side} leg has implausible segment lengths: {thigh_length}, {shin_length}")
        print("ANATOMICAL_LEG_CHAIN", side, "thigh", round(thigh_length, 4),
              "shin", round(shin_length, 4), "bones", "upperleg01", "lowerleg01", "foot")


def refresh_shoe_bone_offsets(rig, scene, local_offsets):
    """Measure the forefoot point again after rebuilding the leg rest matrices."""
    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.update()
    for side in SIDES:
        foot_world = world_pose(rig, rig.pose.bones[f"foot.{side}"])
        shoe = bpy.data.objects[f"CyclingShoe{side}"]
        ball_world = evaluated_vertex_world(shoe, BALL_VERTEX)
        local_offsets[side] = foot_world.inverted() @ ball_world
    print("REBUILT_SHOE_BALL_BONE_OFFSETS",
          {side: tuple(round(v, 5) for v in value) for side, value in local_offsets.items()})


def enable_leg_ik(rig, targets, poles):
    """Solve thigh and shin independently around an explicitly placed knee."""
    for side in SIDES:
        pelvis = rig.pose.bones[f"pelvis.{side}"]
        for constraint in list(pelvis.constraints):
            if constraint.type == "LIMIT_ROTATION" and constraint.name == "PinSeatedPelvis":
                pelvis.constraints.remove(constraint)
        pin = pelvis.constraints.new("LIMIT_ROTATION")
        pin.name = "PinSeatedPelvis"
        pin.owner_space = "LOCAL"
        pin.use_limit_x = pin.use_limit_y = pin.use_limit_z = True
        pin.min_x = pin.max_x = pin.min_y = pin.max_y = pin.min_z = pin.max_z = 0.0

        for bone_name in (f"upperleg01.{side}", f"lowerleg01.{side}", f"foot.{side}"):
            bone = rig.pose.bones[bone_name]
            for constraint in list(bone.constraints):
                if constraint.type == "IK":
                    bone.constraints.remove(constraint)
        for bone_name, target, label in (
            (f"upperleg01.{side}", poles[side], "ThighToKnee"),
            (f"lowerleg01.{side}", targets[side], "ShinToAnkle"),
        ):
            ik = rig.pose.bones[bone_name].constraints.new("IK")
            ik.name = f"{label}_{side}"
            ik.target = target
            ik.chain_count = 1
            ik.use_tail = True
            ik.use_stretch = False
            ik.use_rotation = False
            ik.influence = 1.0
        foot = rig.pose.bones[f"foot.{side}"]
        copy_rotation = foot.constraints.new("COPY_ROTATION")
        copy_rotation.name = f"PedalFootOrientation_{side}"
        copy_rotation.target = targets[side]
        copy_rotation.owner_space = "WORLD"
        copy_rotation.target_space = "WORLD"
        copy_rotation.mix_mode = "REPLACE"
    print("PEDAL_IK_READY", [(side,
          next(c.chain_count for c in rig.pose.bones[f"upperleg01.{side}"].constraints if c.type == "IK"),
          next(c.chain_count for c in rig.pose.bones[f"lowerleg01.{side}"].constraints if c.type == "IK"))
          for side in SIDES])


def solve_anatomical_knee(hip, ankle, bend_direction, upper_length, lower_length):
    """Place the knee by the two-segment law of cosines on the forward side."""
    leg_axis = ankle - hip
    distance = leg_axis.length
    if distance <= 1e-6 or distance >= upper_length + lower_length:
        raise RuntimeError(f"pedal target is unreachable from the seated hip: {distance:.4f} m")
    if distance < abs(upper_length - lower_length):
        raise RuntimeError(f"pedal target folds the leg beyond its anatomical range: {distance:.4f} m")
    direction = leg_axis / distance
    along = (upper_length * upper_length - lower_length * lower_length + distance * distance) / (2.0 * distance)
    perpendicular_length = math.sqrt(max(0.0, upper_length * upper_length - along * along))
    bend = bend_direction - direction * bend_direction.dot(direction)
    if bend.length < .035:
        raise RuntimeError("bike-forward knee plane is singular for this leg pose")
    return hip + direction * along + bend.normalized() * perpendicular_length


def bake_actual_pedal_cycle(rig, scene, source_samples, local_offsets):
    crank = bpy.data.objects["CrankPivot"]
    start, end = scene.frame_start, scene.frame_end
    if end - start + 1 != SAMPLES_PER_REVOLUTION * 3 + 1:
        raise RuntimeError(f"unexpected source PedalCycle frame range {start}-{end}")
    old_action = rig.animation_data.action if rig.animation_data else None
    if old_action is None or "PedalCycle" not in old_action.name:
        raise RuntimeError("the accepted rider blend must contain its PedalCycle action")

    # Pre-sampled source targets provide stable knee poles and the original
    # ankle articulation. Only the target locations are retargeted to the
    # moving CGTrader pedal surface points.
    remove_control_animation(old_action, {
        name for side in SIDES.values() for name in (side[5], side[6])
    }, rig)
    collapse_helper_leg_bones(rig)
    refresh_shoe_bone_offsets(rig, scene, local_offsets)
    add_pedal_contact_bones(rig, local_offsets)
    targets, poles = add_leg_ik_targets()
    enable_leg_ik(rig, targets, poles)
    scene.frame_set(start)
    bpy.context.view_layer.update()
    front = bpy.data.objects.get("FrontWheelSpin")
    rear = bpy.data.objects.get("RearWheelSpin")
    if front is None or rear is None:
        raise RuntimeError("bike forward anchors are required for anatomical knee poles")
    bike_forward = (front.matrix_world.translation - rear.matrix_world.translation).normalized()
    upper_lengths = {side: rig.data.bones[f"upperleg01.{side}"].length for side in SIDES}
    lower_lengths = {side: rig.data.bones[f"lowerleg01.{side}"].length for side in SIDES}
    print("ANATOMICAL_KNEE_PLANE_FORWARD", tuple(round(v, 4) for v in bike_forward))
    max_ik_contact = {side: 0.0 for side in SIDES}
    max_knee_step = {side: 0.0 for side in SIDES}
    previous_knee = {}
    max_knee_frame = {side: 0 for side in SIDES}
    foot_reference = {side: source_samples[start][side]["foot_rotation"] for side in SIDES}
    for frame in range(start, end + 1):
        index = frame - start
        phase = (index % SAMPLES_PER_REVOLUTION) * math.tau / SAMPLES_PER_REVOLUTION
        # The source action contains three pedal revolutions; the knee-pole
        # controls have a small discontinuity at the repeated-cycle boundaries.
        # Use the first authored revolution as the stable one-cycle source and
        # repeat it exactly across the exported clip.
        source_frame = start + (index % SAMPLES_PER_REVOLUTION)
        source = source_samples[source_frame]
        crank.rotation_euler.x = -(phase + CRANK_PHASE_OFFSET)
        for side, (_suffix, _pedal, _contact, bearing_name, *_rest) in SIDES.items():
            bpy.data.objects[bearing_name].rotation_euler.x = phase + CRANK_PHASE_OFFSET
        scene.frame_set(frame)
        bpy.context.view_layer.update()

        for side, (suffix, _pedal, _contact_name, _bearing, surface_name, foot_ik_name, knee_ik_name) in SIDES.items():
            surface = bpy.data.objects[surface_name]
            foot_rotation = foot_reference[side]
            # A small phase-linked toe roll gives the ankles visible, restrained
            # articulation while preserving the accepted shoe-to-pedal path.
            pitch = FOOT_PITCH_AMPLITUDE * math.sin(phase + (math.pi if side == "L" else 0.0))
            target_rotation = foot_rotation @ Quaternion((1.0, 0.0, 0.0), pitch)
            ball_world_offset = target_rotation @ local_offsets[side]
            target = surface.matrix_world.translation - ball_world_offset
            hip = rig.matrix_world @ rig.pose.bones[f"upperleg01.{side}"].head
            knee_target = solve_anatomical_knee(hip, target, bike_forward,
                                                 upper_lengths[side], lower_lengths[side])
            targets[side].location = target
            targets[side].rotation_quaternion = target_rotation
            poles[side].location = knee_target
            for obj in (targets[side], poles[side]):
                obj.keyframe_insert(data_path="location", frame=frame)
            targets[side].keyframe_insert(data_path="rotation_quaternion", frame=frame)

        bpy.context.view_layer.update()
        if frame == start:
            # The first solve follows a freshly-created IK target and the
            # source action's prior evaluated frame. Re-evaluate frame 1 so
            # Blender's constraint graph starts the baked cycle from the same
            # converged state used for every subsequent sample.
            scene.frame_set(frame)
            bpy.context.view_layer.update()
        for side, (_suffix, _pedal, _contact, _bearing, surface_name, *_rest) in SIDES.items():
            marker = rig.matrix_world @ rig.pose.bones[f"pedal_contact.{side}"].head
            surface_point = bpy.data.objects[surface_name].matrix_world.translation
            error = (marker - surface_point).length
            max_ik_contact[side] = max(max_ik_contact[side], error)
            knee = rig.matrix_world @ rig.pose.bones[f"lowerleg01.{side}"].head
            if side in previous_knee:
                step = (knee - previous_knee[side]).length
                if step > max_knee_step[side]:
                    max_knee_step[side] = step
                    max_knee_frame[side] = frame
            previous_knee[side] = knee.copy()
        if frame in (start, start + 18, start + 36, start + 54, end):
            print("PEDAL_IK_SAMPLE", frame, "phase", round(math.degrees(phase), 1),
                  "contact", {side: round(max_ik_contact[side], 5) for side in SIDES})

    if max(max_ik_contact.values()) > .012:
        raise RuntimeError(f"leg IK cannot reach the actual pedal surface: {max_ik_contact}")
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.nla.bake(frame_start=start, frame_end=end, step=1,
                     only_selected=False, visual_keying=True, clear_constraints=True,
                     use_current_action=False, bake_types={"POSE"})
    bpy.ops.object.mode_set(mode="OBJECT")
    baked = rig.animation_data.action if rig.animation_data else None
    if baked is None:
        raise RuntimeError("could not bake the actual-pedal-fit animation")
    baked.name = "PedalCycle"
    for curve in action_curves(baked):
        for point in curve.keyframe_points:
            point.interpolation = "LINEAR"
    print("PEDAL_IK_BAKED", len(list(action_curves(baked))), "curves",
          start, end, "kneeStepMax", {side: (round(value, 5), max_knee_frame[side])
                                      for side, value in max_knee_step.items()})
    for obj in list(targets.values()) + list(poles.values()):
        bpy.data.objects.remove(obj, do_unlink=True)


def sample_source_targets(rig, scene):
    source = {}
    local_offsets = {}
    for frame in range(scene.frame_start, scene.frame_end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        source[frame] = {}
        for side, (suffix, _pedal, _contact, _bearing, _surface, foot_ik_name, knee_ik_name) in SIDES.items():
            foot = rig.pose.bones[f"foot.{side}"]
            foot_world = world_pose(rig, foot)
            shoe = bpy.data.objects.get(f"CyclingShoe{side}")
            if shoe is None or len(shoe.data.vertices) <= BALL_VERTEX:
                raise RuntimeError(f"CyclingShoe{side} lacks its authored forefoot contact vertex")
            ball_world = evaluated_vertex_world(shoe, BALL_VERTEX)
            ball_local = foot_world.inverted() @ ball_world
            if side not in local_offsets:
                local_offsets[side] = ball_local
            elif (ball_local - local_offsets[side]).length > .002:
                raise RuntimeError(f"{side} shoe contact point is not rigidly weighted to foot.{side}")
            foot_control = rig.pose.bones[foot_ik_name]
            knee_control = rig.pose.bones[knee_ik_name]
            source[frame][side] = {
                "foot_rotation": foot_world.to_quaternion().normalized(),
                "old_foot_target": (rig.matrix_world @ foot_control.matrix).translation.copy(),
                # Keep the actual knee joint available for diagnostics. The
                # lowerleg02 head is below the knee and is only a shin helper.
                "source_knee": (rig.matrix_world @ rig.pose.bones[f"lowerleg01.{side}"].head).copy(),
            }
    print("SHOE_BALL_BONE_OFFSETS", {side: tuple(round(v, 5) for v in value) for side, value in local_offsets.items()})
    return source, local_offsets


def validate_full_cycle(rig, scene):
    start, end = scene.frame_start, scene.frame_end
    crank = bpy.data.objects["CrankPivot"]
    depsgraph = bpy.context.evaluated_depsgraph_get()
    max_marker_surface = {side: 0.0 for side in SIDES}
    max_ball_mesh = {side: 0.0 for side in SIDES}
    max_sole_delta = {side: 0.0 for side in SIDES}
    max_knee_step = {side: 0.0 for side in SIDES}
    max_foot_pitch = {side: 0.0 for side in SIDES}
    min_knee_flex = {side: math.inf for side in SIDES}
    max_knee_flex = {side: 0.0 for side in SIDES}
    min_forward_knee = {side: math.inf for side in SIDES}
    min_flexed_forward_knee = {side: math.inf for side in SIDES}
    max_backward_knee = {side: 0.0 for side in SIDES}
    max_upper_leg_delta = {side: 0.0 for side in SIDES}
    max_lower_leg_delta = {side: 0.0 for side in SIDES}
    foot_start = {}
    knee_previous = {}
    knee_max_frame = {side: 0 for side in SIDES}
    pose_start = None

    front = bpy.data.objects.get("FrontWheelSpin")
    rear = bpy.data.objects.get("RearWheelSpin")
    if front is None or rear is None:
        raise RuntimeError("bike forward anchors are required for leg validation")
    bike_forward = (front.matrix_world.translation - rear.matrix_world.translation).normalized()
    reference_lengths = {}

    for frame in range(start, end + 1):
        index = frame - start
        phase = (index % SAMPLES_PER_REVOLUTION) * math.tau / SAMPLES_PER_REVOLUTION
        crank.rotation_euler.x = -(phase + CRANK_PHASE_OFFSET)
        for side, (_suffix, _pedal, _contact, bearing_name, *_rest) in SIDES.items():
            bpy.data.objects[bearing_name].rotation_euler.x = phase + CRANK_PHASE_OFFSET
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        depsgraph = bpy.context.evaluated_depsgraph_get()

        pose_now = {}
        for side, (_suffix, pedal_name, _contact_name, _bearing, surface_name, *_rest) in SIDES.items():
            marker_bone = rig.pose.bones[f"pedal_contact.{side}"]
            ball = rig.matrix_world @ marker_bone.head
            shoe = bpy.data.objects[f"CyclingShoe{side}"]
            shoe_ball = evaluated_vertex_world(shoe, BALL_VERTEX)
            surface = bpy.data.objects[surface_name].matrix_world.translation
            marker_error = (ball - surface).length
            sole_error = (shoe_ball - ball).length
            max_marker_surface[side] = max(max_marker_surface[side], marker_error)
            max_sole_delta[side] = max(max_sole_delta[side], sole_error)

            pedal = bpy.data.objects[pedal_name]
            pedal_eval = pedal.evaluated_get(depsgraph)
            tree = BVHTree.FromObject(pedal_eval, depsgraph, epsilon=0.0)
            ball_local = pedal_eval.matrix_world.inverted() @ shoe_ball
            nearest = tree.find_nearest(ball_local)
            if nearest is None:
                raise RuntimeError(f"could not sample actual {side} pedal geometry")
            max_ball_mesh[side] = max(max_ball_mesh[side], nearest[3])

            foot_rotation = world_pose(rig, rig.pose.bones[f"foot.{side}"]).to_quaternion().normalized()
            if side not in foot_start:
                foot_start[side] = foot_rotation.copy()
            dot = abs(sum(a * b for a, b in zip(foot_start[side], foot_rotation)))
            angle = 2.0 * math.acos(min(1.0, max(0.0, dot)))
            max_foot_pitch[side] = max(max_foot_pitch[side], angle)
            knee = rig.matrix_world @ rig.pose.bones[f"lowerleg01.{side}"].head
            hip = rig.matrix_world @ rig.pose.bones[f"upperleg01.{side}"].head
            ankle = rig.matrix_world @ rig.pose.bones[f"foot.{side}"].head
            upper_length = (hip - knee).length
            lower_length = (knee - ankle).length
            if side not in reference_lengths:
                reference_lengths[side] = (upper_length, lower_length)
            max_upper_leg_delta[side] = max(max_upper_leg_delta[side],
                                             abs(upper_length - reference_lengths[side][0]))
            max_lower_leg_delta[side] = max(max_lower_leg_delta[side],
                                             abs(lower_length - reference_lengths[side][1]))
            flexion = math.pi - (hip - knee).angle(ankle - knee)
            flexion_degrees = math.degrees(flexion)
            min_knee_flex[side] = min(min_knee_flex[side], flexion_degrees)
            max_knee_flex[side] = max(max_knee_flex[side], flexion_degrees)
            leg_axis = ankle - hip
            closest = hip + leg_axis * max(0.0, min(1.0, (knee - hip).dot(leg_axis) / leg_axis.length_squared))
            forward_offset = (knee - closest).dot(bike_forward)
            min_forward_knee[side] = min(min_forward_knee[side], forward_offset)
            max_backward_knee[side] = max(max_backward_knee[side], max(0.0, -forward_offset))
            if flexion_degrees > 15.0:
                min_flexed_forward_knee[side] = min(min_flexed_forward_knee[side], forward_offset)
            if side in knee_previous:
                step = (knee - knee_previous[side]).length
                if step > max_knee_step[side]:
                    max_knee_step[side] = step
                    knee_max_frame[side] = frame
            knee_previous[side] = knee.copy()
            pose_now[side] = (ball.copy(), foot_rotation.copy(), knee.copy())

        if frame == start:
            pose_start = pose_now
        elif frame == end:
            for side in SIDES:
                seam = (pose_now[side][0] - pose_start[side][0]).length
                if seam > .002:
                    raise RuntimeError(f"{side} foot/pedal contact loop seam is {seam:.4f} m")

    print("PEDAL_FIT_METRICS", "marker", max_marker_surface, "mesh", max_ball_mesh,
          "soleDelta", max_sole_delta, "footAngle", max_foot_pitch,
          "kneeStep", {side: (value, knee_max_frame[side]) for side, value in max_knee_step.items()},
          "kneeFlexDeg", {side: (round(min_knee_flex[side], 1), round(max_knee_flex[side], 1)) for side in SIDES},
          "kneeForwardOffset", {side: (round(min_forward_knee[side], 4), round(max_backward_knee[side], 4)) for side in SIDES},
          "legLengthDelta", {side: (round(max_upper_leg_delta[side], 5), round(max_lower_leg_delta[side], 5)) for side in SIDES})
    if max(max_marker_surface.values()) > .012:
        raise RuntimeError(f"shoe ball markers leave the actual moving pedal top: {max_marker_surface}")
    if max(max_sole_delta.values()) > .002:
        raise RuntimeError(f"shoe contact marker no longer matches the authored outsole: {max_sole_delta}")
    if max(max_ball_mesh.values()) > .012:
        raise RuntimeError(f"shoe sole floats above or intersects the pedal surface: {max_ball_mesh}")
    if min(max_foot_pitch.values()) < math.radians(3.0):
        raise RuntimeError(f"ankles remain too rigid through PedalCycle: {max_foot_pitch}")
    if max(max_foot_pitch.values()) > math.radians(18.0):
        raise RuntimeError(f"ankle articulation is excessive: {max_foot_pitch}")
    if max(max_knee_step.values()) > MAX_KNEE_STEP:
        raise RuntimeError(f"knee path has a visible jump: {max_knee_step}")
    if min(min_knee_flex.values()) < 2.0 or max(max_knee_flex.values()) > 125.0:
        raise RuntimeError(f"knee flexion leaves the road-cycling range: {min_knee_flex}, {max_knee_flex}")
    if max(max_backward_knee.values()) > MAX_BACKWARD_KNEE_OFFSET:
        raise RuntimeError(f"a knee bends behind the hip-to-ankle line: {max_backward_knee}")
    for side in SIDES:
        if min_flexed_forward_knee[side] < MIN_FORWARD_KNEE_OFFSET_WHEN_FLEXED:
            raise RuntimeError(f"{side} knee does not track forward through flexion: {min_flexed_forward_knee[side]}")
    if max(max_upper_leg_delta.values()) > .003 or max(max_lower_leg_delta.values()) > .003:
        raise RuntimeError(f"IK stretched a leg chain: upper={max_upper_leg_delta}, lower={max_lower_leg_delta}")
    print("PEDAL_FIT_163_PASS",
          "samples", end - start + 1,
          "marker-to-top", {side: round(value, 5) for side, value in max_marker_surface.items()},
          "sole-to-mesh", {side: round(value, 5) for side, value in max_ball_mesh.items()},
          "anklePitchMaxDeg", {side: round(math.degrees(value), 2) for side, value in max_foot_pitch.items()},
          "kneeStepMax", {side: round(value, 5) for side, value in max_knee_step.items()},
          "kneeFlexDeg", {side: (round(min_knee_flex[side], 1), round(max_knee_flex[side], 1)) for side in SIDES},
          "kneeBackwardMax", {side: round(max_backward_knee[side], 4) for side in SIDES})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--blend", type=Path, required=True, help="accepted sculpted-hands source blend")
    parser.add_argument("--output-blend", type=Path, required=True)
    parser.add_argument("--output-glb", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])

    bpy.ops.wm.open_mainfile(filepath=str(args.blend))
    rig = bpy.data.objects.get("Human.rig")
    scene = bpy.context.scene
    if rig is None or bpy.data.objects.get("Human") is None:
        raise RuntimeError("accepted fitted rider and weighted human mesh are required")
    source, local_offsets = sample_source_targets(rig, scene)
    fit_road_shoes()
    add_pedal_bearings()
    bake_actual_pedal_cycle(rig, scene, source, local_offsets)
    validate_full_cycle(rig, scene)

    # Restore the production model's neutral transform. CyclistVisual supplies
    # the measured phase offset and pedal bearing rotation at runtime.
    bpy.data.objects["CrankPivot"].rotation_euler = (0.0, 0.0, 0.0)
    for _side, (_suffix, _pedal, _contact, bearing_name, *_rest) in SIDES.items():
        bpy.data.objects[bearing_name].rotation_euler = (0.0, 0.0, 0.0)
    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.update()

    args.output_blend.parent.mkdir(parents=True, exist_ok=True)
    args.output_glb.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(args.output_blend), compress=True)
    # Keep the editable blend's authoring graph intact, but explicitly connect
    # the weighted outfit mask on this export pass. Without this glTF's COLOR_0
    # falls back to white after the body mesh is rebuilt and Godot renders the
    # jersey, shorts, and shoes as skin.
    human = bpy.data.objects["Human"]
    outfit = next((material for material in human.data.materials
                   if material and "Rider Outfit" in material.name), None)
    if outfit is None or human.data.color_attributes.get("RiderOutfitWeights") is None:
        raise RuntimeError("weighted rider outfit color attribute is required for glTF export")
    outfit.use_nodes = True
    nodes = outfit.node_tree.nodes
    links = outfit.node_tree.links
    bsdf = next((node for node in nodes if node.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        raise RuntimeError("Rider Outfit material has no Principled BSDF for COLOR_0 export")
    color = next((node for node in nodes if node.type == "VERTEX_COLOR" and
                  node.layer_name == "RiderOutfitWeights"), None)
    if color is None:
        color = nodes.new("ShaderNodeVertexColor")
        color.layer_name = "RiderOutfitWeights"
    for link in list(bsdf.inputs["Base Color"].links):
        links.remove(link)
    links.new(color.outputs["Color"], bsdf.inputs["Base Color"])
    print("GLTF_EXPORT_COLOR_ATTRIBUTE", color.layer_name, "directly connected to base color")
    bpy.ops.object.select_all(action="SELECT")
    bike = bpy.data.objects["BicycleModel"]
    bpy.context.view_layer.objects.active = bike
    bpy.ops.export_scene.gltf(filepath=str(args.output_glb), export_format="GLB",
                              use_selection=True, export_animations=True,
                              export_frame_range=True, export_force_sampling=True,
                              export_animation_mode="ACTIONS")
    print("EXPORTED_PEDAL_FIT_163", args.output_glb)


if __name__ == "__main__":
    main()
