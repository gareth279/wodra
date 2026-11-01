"""Retarget the preserved issue #159 rider to the imported moving pedals.

This pass starts from the accepted sculpted-hands source blend. It preserves
the body, torso, bicycle mesh, and hand animation while re-solving only the
four-bone legs against the real crank-mounted pedal surface points.

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
KNEE_POLE_ANGLE = {"L": 0.0, "R": 0.0}
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


def remove_control_animation(action, control_names):
    """Keep rider motion but free the leg IK targets for pedal retargeting."""
    removed = 0
    for layer in action.layers:
        for strip in layer.strips:
            for slot in action.slots:
                bag = strip.channelbag(slot)
                if bag is None:
                    continue
                for curve in list(bag.fcurves):
                    if any(f'pose.bones["{name}"]' in curve.data_path for name in control_names):
                        bag.fcurves.remove(curve)
                        removed += 1
    print("IK_TARGET_CURVES_REMOVED", removed, sorted(control_names))


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
        pole = bpy.data.objects.new(f"PedalFitPole{side}", None)
        bpy.context.scene.collection.objects.link(target)
        bpy.context.scene.collection.objects.link(pole)
        target.empty_display_type = "SPHERE"
        target.empty_display_size = .01
        pole.empty_display_type = "PLAIN_AXES"
        pole.empty_display_size = .01
        target.rotation_mode = "QUATERNION"
        targets[side] = target
        poles[side] = pole
    return targets, poles


def enable_leg_ik(rig, targets, poles):
    """Solve from the actual ankle/foot head, not the lower-leg tail helper."""
    for side in SIDES:
        for bone_name in (f"upperleg02.{side}", f"lowerleg02.{side}", f"foot.{side}"):
            bone = rig.pose.bones[bone_name]
            for constraint in list(bone.constraints):
                if constraint.type == "IK":
                    bone.constraints.remove(constraint)
        foot = rig.pose.bones[f"foot.{side}"]
        ik = foot.constraints.new("IK")
        ik.name = f"PedalContactIK_{side}"
        ik.target = targets[side]
        ik.chain_count = 5
        ik.use_tail = False
        ik.use_stretch = False
        ik.use_rotation = False
        ik.pole_target = poles[side]
        ik.pole_angle = KNEE_POLE_ANGLE[side]
        ik.influence = 1.0
        copy_rotation = foot.constraints.new("COPY_ROTATION")
        copy_rotation.name = f"PedalFootOrientation_{side}"
        copy_rotation.target = targets[side]
        copy_rotation.owner_space = "WORLD"
        copy_rotation.target_space = "WORLD"
        copy_rotation.mix_mode = "REPLACE"
    print("PEDAL_IK_READY", [(side, next(c.chain_count for c in rig.pose.bones[f"foot.{side}"].constraints
                                        if c.type == "IK")) for side in SIDES])


def stable_knee_pole(hip, ankle, bend_direction, side):
    """Keep a fixed, source-measured knee bend plane across the whole crank turn."""
    axis = (hip - ankle).normalized()
    bend = bend_direction - axis * bend_direction.dot(axis)
    if bend.length < .035:
        bend = Vector((-1.0 if side == "L" else 1.0, 0.0, 0.0))
        bend -= axis * bend.dot(axis)
        if bend.length < .035:
            bend = Vector((0.0, 1.0, 0.0)) - axis * axis.y
    return (hip + ankle) * .5 + bend.normalized() * .20


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
    })
    targets, poles = add_leg_ik_targets()
    enable_leg_ik(rig, targets, poles)
    scene.frame_set(start)
    bpy.context.view_layer.update()
    bend_directions = {}
    for side in SIDES:
        hip = rig.matrix_world @ rig.pose.bones[f"upperleg01.{side}"].head
        ankle = source_samples[start][side]["old_foot_target"]
        knee = source_samples[start][side]["old_knee_target"]
        axis = (hip - ankle).normalized()
        bend = knee - (ankle + axis * (knee - ankle).dot(axis))
        if bend.length < .035:
            bend = Vector((-1.0 if side == "L" else 1.0, 0.0, 0.0))
        bend_directions[side] = bend.normalized()
    print("KNEE_BEND_DIRECTIONS", {side: tuple(round(v, 4) for v in direction)
                                   for side, direction in bend_directions.items()})
    max_ik_contact = {side: 0.0 for side in SIDES}
    max_knee_step = {side: 0.0 for side in SIDES}
    previous_knee = {}
    max_knee_frame = {side: 0 for side in SIDES}
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
            foot_rotation = source[side]["foot_rotation"]
            # A small phase-linked toe roll gives the ankles visible, restrained
            # articulation while preserving the accepted shoe-to-pedal path.
            pitch = FOOT_PITCH_AMPLITUDE * math.sin(phase + (math.pi if side == "L" else 0.0))
            target_rotation = foot_rotation @ Quaternion((1.0, 0.0, 0.0), pitch)
            ball_world_offset = target_rotation @ local_offsets[side]
            target = surface.matrix_world.translation - ball_world_offset
            shift = target - source[side]["old_foot_target"]
            hip = rig.matrix_world @ rig.pose.bones[f"upperleg01.{side}"].head
            pole = stable_knee_pole(hip, target, bend_directions[side], side)
            targets[side].location = target
            targets[side].rotation_quaternion = target_rotation
            poles[side].location = pole
            for obj in (targets[side], poles[side]):
                obj.keyframe_insert(data_path="location", frame=frame)
            targets[side].keyframe_insert(data_path="rotation_quaternion", frame=frame)

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
                # The authored knee-pole control has discontinuous keyed jumps
                # at phase zero. Use the evaluated anatomical knee joint as the
                # measured bend reference instead.
                "old_knee_target": (rig.matrix_world @ rig.pose.bones[f"lowerleg02.{side}"].head).copy(),
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
    foot_start = {}
    knee_previous = {}
    knee_max_frame = {side: 0 for side in SIDES}
    pose_start = None

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
          "kneeStep", {side: (value, knee_max_frame[side]) for side, value in max_knee_step.items()})
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
    if max(max_knee_step.values()) > .045:
        raise RuntimeError(f"knee path has a visible jump: {max_knee_step}")
    print("PEDAL_FIT_163_PASS",
          "samples", end - start + 1,
          "marker-to-top", {side: round(value, 5) for side, value in max_marker_surface.items()},
          "sole-to-mesh", {side: round(value, 5) for side, value in max_ball_mesh.items()},
          "anklePitchMaxDeg", {side: round(math.degrees(value), 2) for side, value in max_foot_pitch.items()},
          "kneeStepMax", {side: round(value, 5) for side, value in max_knee_step.items()})


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
    add_pedal_bearings()
    add_pedal_contact_bones(rig, local_offsets)
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
