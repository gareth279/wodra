"""Regression check for the exported MPFB hand pose against runtime hood meshes.

Run with Blender 5.2 in background on the editable review blend. The helper
Mask modifier is disabled only to keep vertex indices stable; the Armature
modifier and the baked cadence action remain evaluated.
"""
import bpy
import statistics
from mathutils import Vector
from mathutils.bvhtree import BVHTree

rig = next(obj for obj in bpy.data.objects if obj.type == "ARMATURE")
human = bpy.data.objects.get("Human")
if human is None:
    raise RuntimeError("Weighted Human mesh is missing")
for modifier in human.modifiers:
    if modifier.type == "MASK":
        modifier.show_viewport = modifier.show_render = False

scene = bpy.context.scene
frames = sorted({scene.frame_start, scene.frame_start + (scene.frame_end-scene.frame_start)//4,
                 scene.frame_start + (scene.frame_end-scene.frame_start)//2,
                 scene.frame_start + 3*(scene.frame_end-scene.frame_start)//4, scene.frame_end})
depsgraph = bpy.context.evaluated_depsgraph_get()
for frame in frames:
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = human.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        points = [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
        for side in ("L", "R"):
            hood = bpy.data.objects.get("BrakeHood" + side)
            if hood is None:
                raise RuntimeError(f"BrakeHood{side} is missing")
            hood_eval = hood.evaluated_get(depsgraph)
            surface = BVHTree.FromObject(hood_eval, depsgraph)
            for finger in range(1, 6):
                group = human.vertex_groups.get(f"finger{finger}-3.{side}")
                if group is None:
                    raise RuntimeError(f"Finger group finger{finger}-3.{side} is missing")
                vertices = [index for index, vertex in enumerate(human.data.vertices)
                            if any(weight.group == group.index and weight.weight >= .1 for weight in vertex.groups)]
                distances = [surface.find_nearest(points[index])[3] for index in vertices]
                median = statistics.median(distances)
                maximum = max(distances)
                # Fingers 1 and 2 bridge toward the thumb/brake lever. Other
                # fingertips wrap the hood sides; these limits flag the prior
                # 30–50 mm hovering pose while allowing low-poly surface detail.
                limit = .035 if finger in (1, 2) else .030
                print(f"CONTACT frame={frame} side={side} finger={finger} median={median:.4f} max={maximum:.4f} limit={limit:.3f}")
                if median > limit:
                    raise AssertionError(f"finger{finger} {side} median hood gap {median:.3f}m exceeds {limit:.3f}m")
    finally:
        evaluated.to_mesh_clear()
print(f"PASS: both weighted hands remain near their hood surfaces over frames {frames}")
