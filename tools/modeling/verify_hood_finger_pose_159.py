"""Validate the articulated two-hand hood grip across the baked pedal cycle.

Run with Blender 5.2 on the editable post-polish source blend:
  blender --background <rider.blend> --python verify_hood_finger_pose_159.py

This checks the weighted finger skeleton against the actual integrated cockpit
mesh. It also rejects a straight-finger pose and verifies the thumb is opposed
to the four fingers; palm-center proximity alone is not considered a grip.
"""
import math

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree


rig = bpy.data.objects.get("Human.rig")
bike = bpy.data.objects.get("BicycleModel")
cockpit = bpy.data.objects.get("RoadBikeCockpit")
if not rig or not bike or not cockpit or cockpit.type != "MESH":
    raise RuntimeError("expected Human.rig, BicycleModel and RoadBikeCockpit")
action = rig.animation_data.action if rig.animation_data else None
if action is None or not action.get("WodraHoodFingerContactV1", False):
    raise RuntimeError("the editable action has no baked hood-finger contact pass")

depsgraph = bpy.context.evaluated_depsgraph_get()
cockpit_eval = cockpit.evaluated_get(depsgraph)
surface = BVHTree.FromObject(cockpit_eval, depsgraph, epsilon=0.0)
world_to_cockpit = cockpit.matrix_world.inverted()
bike_inverse = bike.matrix_world.inverted()
scene = bpy.context.scene
worst_contact = (0.0, None)
minimum_bend = (math.inf, None)
start, end = scene.frame_start, scene.frame_end

for frame in range(start, end + 1):
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    for side in ("L", "R"):
        side_sign = -1.0 if side == "L" else 1.0
        tips = {}
        for digit in range(1, 6):
            chain = [rig.pose.bones.get(f"finger{digit}-{joint}.{side}") for joint in (1, 2, 3)]
            if any(bone is None for bone in chain):
                raise RuntimeError(f"missing finger{digit}.{side} phalanx chain")
            points = [rig.matrix_world @ bone.head for bone in chain]
            points.append(rig.matrix_world @ chain[-1].tail)
            directions = [(points[index + 1] - points[index]).normalized() for index in range(3)]
            bend = sum(math.degrees(directions[index].angle(directions[index + 1]))
                       for index in range(2))
            if bend < minimum_bend[0]:
                minimum_bend = (bend, (frame, side, digit))
            if bend < 30.0:
                raise AssertionError(f"finger{digit}.{side} is extended at frame {frame}: {bend:.1f} degrees")
            tip_world = points[-1]
            tip_local = world_to_cockpit @ tip_world
            nearest = surface.find_nearest(tip_local)
            if nearest is None:
                raise RuntimeError("could not sample the integrated cockpit surface")
            distance = nearest[3]
            if distance > worst_contact[0]:
                worst_contact = (distance, (frame, side, digit))
            if distance > 0.015:
                raise AssertionError(f"finger{digit}.{side} misses the cockpit at frame {frame}: {distance:.3f} m")
            tips[digit] = bike_inverse @ tip_world

        thumb_inboard = tips[1].x * side_sign
        for digit in range(2, 6):
            finger_outboard = tips[digit].x * side_sign
            if finger_outboard < thumb_inboard + 0.005:
                raise AssertionError(f"thumb{side} is not opposed to finger{digit} at frame {frame}")

scene.frame_set(start)
bpy.context.view_layer.update()
print("PASS: all 10 finger tips stay within 15 mm of the imported cockpit over the full cycle")
print("PASS: every finger chain flexes at least 30 degrees and each thumb opposes its fingers")
print("WORST_CONTACT_M", round(worst_contact[0], 5), worst_contact[1])
print("MINIMUM_TOTAL_BEND_DEGREES", round(minimum_bend[0], 2), minimum_bend[1])
