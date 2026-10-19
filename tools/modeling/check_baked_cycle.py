"""Focused Blender regression checks for the Wodra pedal cycle.

Run with:
  blender --background <candidate.blend> --python tools/modeling/check_baked_cycle.py
"""
from __future__ import annotations
import bpy
from mathutils import Vector


def main() -> None:
    rig = next((obj for obj in bpy.data.objects if obj.type == "ARMATURE"), None)
    if rig is None or rig.animation_data is None or rig.animation_data.action is None:
        raise AssertionError("weighted rider armature or baked action is missing")
    if rig.animation_data.action.name != "PedalCycle":
        raise AssertionError(f"unexpected action: {rig.animation_data.action.name}")
    scene = bpy.context.scene
    samples_per_turn = 72
    sample_count = samples_per_turn * 4
    max_knee_step = {"L": 0.0, "R": 0.0}
    knee_x = {"L": [], "R": []}
    previous = {}
    for index in range(sample_count + 1):
        # Quarter-frame samples inspect interpolation between the baked 5-degree
        # keys, including the seam from the last phase back to 0 degrees.
        offset = index / 4
        scene.frame_set(scene.frame_start + int(offset), subframe=offset % 1)
        bpy.context.view_layer.update()
        for side in ("L", "R"):
            bone = rig.pose.bones.get(f"lowerleg01.{side}")
            if bone is None:
                raise AssertionError(f"missing knee deformation bone for {side}")
            point = rig.matrix_world @ bone.head
            knee_x[side].append(point.x)
            if side in previous:
                max_knee_step[side] = max(max_knee_step[side], (point - previous[side]).length)
            previous[side] = point.copy()

    # The source coordinates are mirrored across the rider's sagittal plane.
    # These bands catch a knee jumping through the frame or crossing the midline.
    if min(knee_x["L"]) < -0.18 or max(knee_x["L"]) > -0.09:
        raise AssertionError(f"left knee left its stable tracking band: {min(knee_x['L']):.3f}..{max(knee_x['L']):.3f} m")
    if min(knee_x["R"]) < 0.09 or max(knee_x["R"]) > 0.21:
        raise AssertionError(f"right knee left its stable tracking band: {min(knee_x['R']):.3f}..{max(knee_x['R']):.3f} m")
    for side, step in max_knee_step.items():
        if step > 0.02:
            raise AssertionError(f"{side} knee interpolation jump is {step:.3f} m per 1.25 degrees")

    scene.frame_set(scene.frame_start)
    start = {bone.name: (bone.matrix.copy()) for bone in rig.pose.bones}
    scene.frame_set(scene.frame_start + samples_per_turn * 3)
    bpy.context.view_layer.update()
    seam_error = max((rig.pose.bones[name].matrix.translation - matrix.translation).length
                     for name, matrix in start.items())
    if seam_error > 1e-4:
        raise AssertionError(f"three-revolution loop seam differs by {seam_error:.6f} m")
    print("PEDAL_CYCLE_REGRESSION_PASS",
          f"max knee step L={max_knee_step['L']:.4f}m R={max_knee_step['R']:.4f}m",
          f"x bands L={min(knee_x['L']):.3f}..{max(knee_x['L']):.3f}m",
          f"R={min(knee_x['R']):.3f}..{max(knee_x['R']):.3f}m",
          f"seam={seam_error:.6f}m")


if __name__ == "__main__":
    main()
