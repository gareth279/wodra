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
failures = []
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
            hood_surface = BVHTree.FromObject(hood_eval, depsgraph)
            # Sample the weighted palm/metacarpal skin itself. This is the
            # palm-seating check; the wrist bone origin is not a contact point.
            palm_indices = set()
            for metacarpal in range(1, 5):
                group = human.vertex_groups.get(f"metacarpal{metacarpal}.{side}")
                if group is None:
                    raise RuntimeError(f"Metacarpal group metacarpal{metacarpal}.{side} is missing")
                palm_indices.update(index for index, vertex in enumerate(human.data.vertices)
                                    if any(weight.group == group.index and weight.weight >= .1
                                           for weight in vertex.groups))
            palm_distances = [hood_surface.find_nearest(points[index])[3] for index in palm_indices]
            palm_median = statistics.median(palm_distances)
            palm_p90 = sorted(palm_distances)[int(.9 * (len(palm_distances) - 1))]
            print(f"PALM frame={frame} side={side} median={palm_median:.4f} p90={palm_p90:.4f}")
            if palm_median > .035 or palm_p90 > .060:
                failures.append(f"Palm {side} frame {frame}: median={palm_median:.3f}m p90={palm_p90:.3f}m")
            for finger in range(1, 6):
                group = human.vertex_groups.get(f"finger{finger}-3.{side}")
                if group is None:
                    raise RuntimeError(f"Finger group finger{finger}-3.{side} is missing")
                vertices = [index for index, vertex in enumerate(human.data.vertices)
                            if any(weight.group == group.index and weight.weight >= .1 for weight in vertex.groups)]
                surfaces = [hood_surface]
                # In a relaxed road hood grip, the index and middle fingers
                # can rest on the brake blade while the other digits wrap the
                # rubber hood. Measure their contact against either real mesh.
                if finger in (2, 3):
                    lever = bpy.data.objects.get("BrakeLever" + side)
                    if lever is None:
                        raise RuntimeError(f"BrakeLever{side} is missing")
                    lever_eval = lever.evaluated_get(depsgraph)
                    surfaces.append(BVHTree.FromObject(lever_eval, depsgraph))
                distances = [min(surface.find_nearest(points[index])[3] for surface in surfaces)
                             for index in vertices]
                median = statistics.median(distances)
                maximum = max(distances)
                # Fingers 1 and 2 bridge toward the thumb/brake lever. Other
                # fingertips wrap the hood sides; these limits flag the prior
                # 30–50 mm hovering pose while allowing low-poly surface detail.
                limit = .035 if finger in (1, 2) else .030
                print(f"CONTACT frame={frame} side={side} finger={finger} median={median:.4f} max={maximum:.4f} limit={limit:.3f}")
                if median > limit:
                    failures.append(f"finger{finger} {side} frame {frame}: median={median:.3f}m limit={limit:.3f}m")
    finally:
        evaluated.to_mesh_clear()
if failures:
    raise AssertionError("hood contact failures: " + "; ".join(failures))
print(f"PASS: both weighted hands remain near their hood surfaces over frames {frames}")
