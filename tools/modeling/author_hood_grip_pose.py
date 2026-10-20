"""Author one measured MPFB metacarpal adduction and restrained grip increment.

Start from the preserved alpha48 mask-export source. The script changes only
finger joint rotations in the existing PedalCycle action, derives mirrored
local Z signs from evaluated bone motion toward the palm centre. It leaves bone
lengths, skin weights and lower-body animation intact; it adds only the measured
local wrist rotation needed to seat both hands on their hood surfaces.
"""
import bpy
import math
import sys
from pathlib import Path

args = sys.argv[sys.argv.index("--") + 1:]
if len(args) != 1:
    raise SystemExit("expected output blend path")
output = Path(args[0])
rig = bpy.data.objects.get("Human.rig") or next(o for o in bpy.data.objects if o.type == "ARMATURE")
human = bpy.data.objects.get("Human")
action = rig.animation_data.action if rig.animation_data else None
if human is None or action is None or action.name != "PedalCycle":
    raise RuntimeError("expected preserved animated Human with PedalCycle")
if action.get("WodraMeasuredGripV2", False):
    raise RuntimeError("grip pass is already applied; use the preserved source blend")

def action_curves(path):
    slot = next((slot for slot in action.slots if slot.identifier == f"OB{rig.name}"), action.slots[0])
    for layer in action.layers:
        for strip in layer.strips:
            bag = strip.channelbag(slot)
            if bag:
                curves = [curve for curve in bag.fcurves if curve.data_path == path]
                if curves:
                    return curves
    raise RuntimeError(f"missing animation channels: {path}")

# A controlled ±0.1 rad wrist-axis sweep after the PIP/DIP curl pass found that
# this small mirrored orientation brings the palm heel and all four fingers
# closer to the actual hood surface. It rotates the hand around its wrist
# pivot; it does not move or scale the wrist, forearm, or fingers.
for side, delta in (("L", 0.10), ("R", -0.10)):
    name = f"wrist.{side}"
    curves = action_curves(f'pose.bones["{name}"].rotation_euler')
    z_curve = next((curve for curve in curves if curve.array_index == 2), None)
    if z_curve is None:
        raise RuntimeError(f"no local-Z action curve for {name}")
    for point in z_curve.keyframe_points:
        point.co.y += delta
        point.handle_left.y += delta
        point.handle_right.y += delta
    z_curve.update()
    print("WRIST_FIT", name, "localZRadians", delta)

scene = bpy.context.scene
scene.frame_set(scene.frame_start)
bpy.context.view_layer.update()
# The 4–5 degree first pass moved the outer metacarpal heads only 5–7 mm and
# left the visible finger fan almost unchanged in Godot. These restrained
# inward angles (derived from the evaluated mirrored local-Z directions) close
# the fan by roughly 15–20 mm while preserving bone scale and finger length.
adduction = {
    # Signs are from probe_hand_adduction.py, which perturbs each local Z axis
    # and compares the weighted distal-finger fan after Armature evaluation.
    # 16–18 degrees collapsed adjacent digits in the isolated render; this
    # 10 degree intermediate correction narrows the visible fan while keeping
    # distinct fingers.
    "L": {1: -8, 2: -10, 3: 10, 4: 10},
    "R": {1: 8, 2: -10, 3: 10, 4: -10},
}
for side in ("L", "R"):
    bones = [rig.pose.bones[f"metacarpal{i}.{side}"] for i in range(1, 5)]
    for digit, bone in enumerate(bones, 1):
        amount = math.radians(adduction[side][digit])
        path = f'pose.bones["{bone.name}"].rotation_euler'
        curves = action_curves(path)
        z_curve = next((curve for curve in curves if curve.array_index == 2), None)
        if z_curve is None:
            raise RuntimeError(f"no local-Z action curve for {bone.name}")
        for point in z_curve.keyframe_points:
            point.co.y += amount
            point.handle_left.y += amount
            point.handle_right.y += amount
        z_curve.update()
        print("ADDUCT", bone.name, "measuredLocalZDegrees", round(math.degrees(amount), 2))

# A relaxed hood grip uses varied bends: the ring and little fingers close a
# little farther around the rubber; the index/middle stay light on the lever.
# The first runtime comparison still looked like fingers draped over the top
# of the hoods. Add a modest MCP bend as well as PIP/DIP curl so the fingers
# wrap from their bases instead of staying straight until the tips. The
# variation is small and digit-specific; all changes are rotations only.
curl = {
    1: (0.14, 0.14, 0.08),  # thumb stays opposed without folding into the palm
    2: (0.40, 0.60, 0.50),  # index rests lightly on the brake blade
    3: (0.48, 0.74, 0.68),  # middle follows the lever/hood transition
    4: (0.52, 0.78, 0.70),  # ring wraps around the hood rubber
    5: (0.46, 0.72, 0.66),  # little finger closes under the hood
}
for side in ("L", "R"):
    for finger, increments in curl.items():
        for joint, increment in enumerate(increments, 1):
            # The evaluated hand sweep found a small residual distal gap on
            # the left ring finger and right middle finger; keep the correction
            # local and symmetric in magnitude rather than shifting either
            # palm or wrist root.
            if side == "L" and finger == 4 and joint in (2, 3):
                increment += 0.08
            if side == "R" and finger == 3 and joint in (2, 3):
                increment += 0.08
            if increment == 0:
                continue
            name = f"finger{finger}-{joint}.{side}"
            curve = next((curve for curve in action_curves(f'pose.bones["{name}"].rotation_euler')
                          if curve.array_index == 0), None)
            if curve is None:
                raise RuntimeError(f"no local flex curve for {name}")
            for point in curve.keyframe_points:
                point.co.y += increment
                point.handle_left.y += increment
                point.handle_right.y += increment
            curve.update()
            print("FLEX", name, "deltaDegrees", round(math.degrees(increment), 2))

action["WodraMeasuredGripV2"] = True
scene.frame_set(scene.frame_start)
bpy.context.view_layer.update()
for side in ("L", "R"):
    for finger in range(1, 6):
        for joint in range(1, 4):
            bone = rig.pose.bones[f"finger{finger}-{joint}.{side}"]
            if max(abs(value - 1.0) for value in bone.scale) > 1e-6:
                raise RuntimeError(f"unexpected bone scale on {bone.name}: {tuple(bone.scale)}")
output.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(output))
print("SAVED_MEASURED_GRIP", output)
