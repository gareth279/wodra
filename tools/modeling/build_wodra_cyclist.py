"""Build the Wodra road-bike fit and full MPFB skinned cyclist with Blender.

Run from the repository root with:
  blender --background --python tools/modeling/build_wodra_cyclist.py

The resulting node names are a runtime contract consumed by CyclistVisual.cs.
The human mesh, standard skeleton, and weights come from MPFB's installed assets.
"""

from __future__ import annotations

import math
import os
import sys
import importlib
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


SIDES = 12
MPFB_EXPECTED_VERSION = "2.0.18"


def material(name: str, color: tuple[float, float, float], roughness: float = 0.82):
    value = bpy.data.materials.new(name)
    value.diffuse_color = (*color, 1.0)
    value.use_nodes = True
    shader = value.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (*color, 1.0)
    shader.inputs["Roughness"].default_value = roughness
    return value


def empty(name: str, parent, location=(0.0, 0.0, 0.0)):
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.06
    obj.location = location
    obj.parent = parent
    bpy.context.collection.objects.link(obj)
    return obj


def mesh_object(name: str, vertices, faces, mat, parent, smooth=False):
    # Catch accidental giant geometry while it is still easy to trace to its
    # primitive. Runtime-scaled animation targets intentionally use unit-sized
    # source meshes and are identified separately in validate_model().
    if not vertices or not faces:
        raise ValueError(f"{name}: mesh has no vertices or faces")
    if any(not math.isfinite(component) for vertex in vertices for component in vertex):
        raise ValueError(f"{name}: mesh contains a non-finite vertex")
    for face in faces:
        if len(face) < 3 or any(index < 0 or index >= len(vertices) for index in face):
            raise ValueError(f"{name}: mesh contains an invalid face")
    dimensions = tuple(max(v[axis] for v in vertices) - min(v[axis] for v in vertices) for axis in range(3))
    if any(dimension > 3.0 for dimension in dimensions):
        raise ValueError(f"{name}: implausibly large local mesh bounds {dimensions}")
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    if mat:
        mesh.materials.append(mat)
    if smooth:
        for polygon in mesh.polygons:
            polygon.use_smooth = True
    return obj


def validate_model():
    """Validate world-space envelope and print each mesh's export-space bounds."""
    expected_scaled_geometry = {"LeftCrank", "RightCrank", "LeftPedal", "RightPedal"}
    dynamic_templates = {
        "LeftCrank", "RightCrank", "LeftPedal", "RightPedal",
    }
    bpy.context.view_layer.update()
    model_min = [float("inf")] * 3
    model_max = [float("-inf")] * 3
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        corners = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
        mins = tuple(min(point[axis] for point in corners) for axis in range(3))
        maxs = tuple(max(point[axis] for point in corners) for axis in range(3))
        size = tuple(maxs[axis] - mins[axis] for axis in range(3))
        scale = tuple(float(value) for value in obj.matrix_world.to_scale())
        label = " [normalized runtime target]" if obj.name in dynamic_templates else ""
        print(f"Model mesh {obj.name}: bounds={size}, world scale={scale}{label}")
        for axis in range(3):
            model_min[axis] = min(model_min[axis], mins[axis])
            model_max[axis] = max(model_max[axis], maxs[axis])
        if any(not math.isfinite(value) for value in (*size, *scale)):
            raise ValueError(f"{obj.name}: invalid transformed bounds or scale")
        if obj.name not in expected_scaled_geometry and any(abs(value - 1.0) > 1e-4 for value in scale):
            raise ValueError(f"{obj.name}: unexpected non-unit world scale {scale}")
    dimensions = tuple(model_max[axis] - model_min[axis] for axis in range(3))
    if any(dimension <= 0 or dimension > 2.25 for dimension in dimensions):
        raise ValueError(f"Cyclist/bicycle world bounds are outside sanity limits: {dimensions}")
    print(f"Validated model envelope: {dimensions} m")


def render_previews(preview_dir: Path):
    """Render the source model in the same neutral seated pose used by runtime IK."""
    preview_dir.mkdir(parents=True, exist_ok=True)
    bpy.context.view_layer.update()
    object_by_name = {obj.name: obj for obj in bpy.context.scene.objects}
    saved_matrices = {}
    saved_hidden = {}

    def set_world_transform(name, position, rotation, scale):
        obj = object_by_name[name]
        saved_matrices[name] = obj.matrix_world.copy()
        saved_hidden[name] = obj.hide_render
        obj.hide_render = False
        saved = Matrix.Translation(Vector(position)) @ rotation.to_matrix().to_4x4() @ Matrix.Diagonal((*scale, 1.0))
        obj.matrix_world = saved

    def set_rod(name, start, end, radius):
        a, b = Vector(start), Vector(end)
        delta = b - a
        rotation = delta.normalized().to_track_quat("Z", "X")
        set_world_transform(name, (a + b) * 0.5, rotation, (radius * 2, radius * 2, delta.length))

    def set_sphere(name, position, dimensions):
        set_world_transform(name, position, QuaternionIdentity, dimensions)

    def set_box(name, position, dimensions):
        set_world_transform(name, position, QuaternionIdentity, dimensions)

    QuaternionIdentity = Matrix.Identity(3).to_quaternion()

    # GLB targets are normalized unit meshes. Pose and scale them exactly as the
    # Godot runtime does so that the Blender previews show the rider, not templates.
    crank_y, crank_z, crank_radius = -0.10, 0.39, 0.17  # Blender Y forward, Z up.
    left_pedal = (-0.13, crank_y - crank_radius, crank_z)
    right_pedal = (0.13, crank_y + crank_radius, crank_z)
    set_rod("LeftCrank", (left_pedal[0], crank_y, crank_z), left_pedal, .018)
    set_rod("RightCrank", (right_pedal[0], crank_y, crank_z), right_pedal, .018)
    set_box("LeftPedal", left_pedal, (.18, .075, .045))
    set_box("RightPedal", right_pedal, (.18, .075, .045))

    hip_left = (-.13, -.19, .98)
    hip_right = (.13, -.19, .98)
    ankle_left = (left_pedal[0] - .015, left_pedal[1] - .035, crank_z + .035)
    ankle_right = (right_pedal[0] + .015, right_pedal[1] - .035, crank_z + .035)

    def solve_knee(hip, ankle, bend):
        # Godot solver coordinates are (forward Z, vertical Y); Blender uses
        # (forward Y, vertical Z), so the forward axis changes sign.
        hx, hy = -hip[1], hip[2]
        ax, ay = -ankle[1], ankle[2]
        dx, dy = ax - hx, ay - hy
        actual = math.hypot(dx, dy)
        distance = min(.8599, max(.0001, actual))
        dx, dy = dx / actual, dy / actual
        along = (.43 * .43 + distance * distance - .43 * .43) / (2 * .43 * distance)
        perpendicular = math.sqrt(max(0, 1 - along * along))
        knee_forward = hx + dx * .43 * along - dy * .43 * perpendicular * bend
        knee_vertical = hy + dy * .43 * along + dx * .43 * perpendicular * bend
        return (hip[0], -knee_forward, knee_vertical)

    knee_left = solve_knee(hip_left, ankle_left, -1)
    knee_right = solve_knee(hip_right, ankle_right, -1)
    for prefix, hip, knee, ankle in (("Left", hip_left, knee_left, ankle_left),
                                    ("Right", hip_right, knee_right, ankle_right)):
        set_rod(prefix + "Thigh", hip, knee, .085)
        set_rod(prefix + "Shin", knee, ankle, .055)
        set_sphere(prefix + "Knee", knee, (.095, .095, .095))
        set_sphere(prefix + "Ankle", ankle, (.065, .065, .065))
        foot_position = (ankle[0], ankle[1] + .07, ankle[2] + .005)
        # Godot's (.15, .065, .22) dimensions map to Blender X, Z, Y.
        set_sphere(prefix + "CyclingShoe", foot_position, (.15, .22, .065))
        sole_position = (foot_position[0], foot_position[1], foot_position[2] - .05)
        set_box(prefix + "ShoeSole", sole_position, (.13, .19, .018))

    scene = bpy.context.scene
    old_engine = scene.render.engine
    old_resolution = (scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage)
    old_filepath = scene.render.filepath
    old_world_color = tuple(scene.world.color) if scene.world else None
    preview_objects = []
    target = Vector((0, 0, .87))
    views = {
        "side": (Vector((4.1, 0, 1.02)), 2.25),
        "rear": (Vector((0, -3.25, 1.05)), 2.25),
        "rear_three_quarter": (Vector((2.35, -2.65, 1.30)), 2.30),
    }
    try:
        available_engines = {item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in available_engines else "BLENDER_EEVEE"
        scene.render.resolution_x = 1200
        scene.render.resolution_y = 1200
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = "PNG"
        scene.render.film_transparent = False
        scene.world.color = (.018, .027, .038)

        def add_area(name, location, energy, size):
            data = bpy.data.lights.new(name, "AREA")
            data.energy = energy
            data.shape = "DISK"
            data.size = size
            light = bpy.data.objects.new(name, data)
            scene.collection.objects.link(light)
            light.location = location
            light.rotation_euler = (target - light.location).to_track_quat("-Z", "Y").to_euler()
            preview_objects.append(light)

        add_area("PreviewKey", (3.2, -3.0, 4.5), 520, 4.0)
        add_area("PreviewFill", (-3.0, 1.0, 2.8), 330, 3.5)
        add_area("PreviewRim", (0.0, 3.0, 3.6), 420, 3.0)
        for name, (location, ortho_scale) in views.items():
            data = bpy.data.cameras.new("PreviewCamera_" + name)
            data.type = "ORTHO"
            data.ortho_scale = ortho_scale
            camera = bpy.data.objects.new("PreviewCamera_" + name, data)
            scene.collection.objects.link(camera)
            camera.location = location
            camera.rotation_euler = (target - location).to_track_quat("-Z", "Y").to_euler()
            preview_objects.append(camera)
            scene.camera = camera
            scene.render.filepath = str(preview_dir / (name + ".png"))
            bpy.ops.render.render(write_still=True)
            print(f"Rendered {name} preview: {scene.render.filepath}")
    finally:
        for name, matrix in saved_matrices.items():
            object_by_name[name].matrix_world = matrix
            object_by_name[name].hide_render = saved_hidden[name]
        scene.camera = None
        for obj in preview_objects:
            bpy.data.objects.remove(obj, do_unlink=True)
        scene.render.engine = old_engine
        scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = old_resolution
        scene.render.filepath = old_filepath
        if old_world_color is not None:
            scene.world.color = old_world_color
        bpy.context.view_layer.update()


def render_fit_previews(preview_dir: Path):
    """Render close, clearly framed complete-rider fit views without pose edits."""
    preview_dir.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    old_engine = scene.render.engine
    old_resolution = (scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage)
    old_filepath = scene.render.filepath
    old_world = scene.world
    created = []
    target = Vector((0, 0.02, .93))
    try:
        engines = {item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x = scene.render.resolution_y = 1000
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = "PNG"
        scene.render.film_transparent = False
        world = bpy.data.worlds.new("FitReviewWorld")
        world.color = (.035, .045, .06)
        scene.world = world

        def light(name, location, energy, size):
            data = bpy.data.lights.new(name, "AREA")
            data.energy = energy
            data.shape = "DISK"
            data.size = size
            obj = bpy.data.objects.new(name, data)
            scene.collection.objects.link(obj)
            obj.location = location
            obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()
            created.append(obj)

        light("FitKey", (3.2, -2.5, 3.5), 650, 3.2)
        light("FitFill", (-2.8, .7, 2.3), 450, 2.8)
        light("FitRim", (0, 2.5, 3.0), 500, 2.8)
        views = {
            "fit_side": ((3.5, 0.02, 1.0), 2.5),
            "fit_front": ((0.0, 3.0, 1.0), 2.25),
            "fit_rear": ((0.0, -3.0, 1.0), 2.25),
        }
        for name, (location, scale) in views.items():
            data = bpy.data.cameras.new("FitCamera_" + name)
            data.type = "ORTHO"
            data.ortho_scale = scale
            camera = bpy.data.objects.new("FitCamera_" + name, data)
            scene.collection.objects.link(camera)
            camera.location = location
            camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
            created.append(camera)
            scene.camera = camera
            scene.render.filepath = str(preview_dir / (name + ".png"))
            bpy.ops.render.render(write_still=True)
            print(f"Rendered {name}: {scene.render.filepath}")
    finally:
        scene.camera = None
        for obj in created:
            bpy.data.objects.remove(obj, do_unlink=True)
        scene.world = old_world
        scene.render.engine = old_engine
        scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = old_resolution
        scene.render.filepath = old_filepath


def render_crank_phase_checks(rig, preview_dir: Path):
    """Render the full skinned rider at four crank phases before animation."""
    preview_dir.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    old_engine = scene.render.engine
    old_resolution = (scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage)
    old_filepath = scene.render.filepath
    old_world = scene.world
    created = []
    target = Vector((0, 0.02, .93))
    try:
        engines = {item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x = scene.render.resolution_y = 1000
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = "PNG"
        scene.render.film_transparent = False
        world = bpy.data.worlds.new("CrankPhaseReviewWorld")
        world.color = (.035, .045, .06)
        scene.world = world

        def light(name, location, energy, size):
            data = bpy.data.lights.new(name, "AREA")
            data.energy = energy
            data.shape = "DISK"
            data.size = size
            obj = bpy.data.objects.new(name, data)
            scene.collection.objects.link(obj)
            obj.location = location
            obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()
            created.append(obj)

        light("PhaseKey", (3.2, -2.5, 3.5), 650, 3.2)
        light("PhaseFill", (-2.8, .7, 2.3), 450, 2.8)
        data = bpy.data.cameras.new("CrankPhaseSideCamera")
        data.type = "ORTHO"
        data.ortho_scale = 2.5
        camera = bpy.data.objects.new("CrankPhaseSideCamera", data)
        scene.collection.objects.link(camera)
        camera.location = (3.5, .02, 1.0)
        camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
        created.append(camera)
        scene.camera = camera

        world_to_rig = rig.matrix_world.inverted()
        crank_center = Vector((0, -.10, .39))
        crank_radius = .17
        phase_config = {
            0: ((.13, -.35, .69, 135), (-.13, -.35, .88, 45)),
            90: (None, None),
            180: (None, None),
            270: (None, None),
        }

        def move_control(name, world_position):
            control = rig.pose.bones[name]
            matrix = control.matrix.copy()
            matrix.translation = world_to_rig @ Vector(world_position)
            control.matrix = matrix

        for degrees, calibrations in phase_config.items():
            phase = math.radians(degrees)
            desired_knees = {}
            for suffix, side, crank_angle in (("left", -1, phase + math.pi),
                                               ("right", 1, phase)):
                pedal = crank_center + Vector((0, crank_radius * math.sin(crank_angle),
                                                crank_radius * math.cos(crank_angle)))
                ankle = (side * .13, pedal.y - .08, pedal.z + .075)
                move_control(suffix + "_foot_ik", ankle)
                if degrees == 0:
                    pole = calibrations[0 if side < 0 else 1]
                    move_control(suffix + "_knee_ik", pole[:3])
                    leg_ik = next(c for c in rig.pose.bones["lowerleg02." + ("L" if side < 0 else "R")].constraints
                                  if c.type == "IK")
                    leg_ik.pole_angle = math.radians(pole[3])
                else:
                    progress = max(0.0, min(1.0, (ankle[2] - .295) / .34))
                    desired_knees[side] = Vector((side * .13,
                                                  -.11 + .21 * progress,
                                                  .50 + .667 * ankle[2]))
            if degrees != 0:
                # Find a stable mirrored pole plane for this crank position,
                # minimizing deviation from the expected forward knee path.
                for side, suffix in ((-1, "left"), (1, "right")):
                    side_name = "L" if side < 0 else "R"
                    leg_ik = next(c for c in rig.pose.bones["lowerleg02." + side_name].constraints
                                  if c.type == "IK")
                    desired = desired_knees[side]
                    best = None
                    for pole_x in (side * .13, -side * .13, side * .35, -side * .35):
                        for y_offset in (-.35, -.15, .05, .25):
                            for pole_degrees in range(0, 360, 45):
                                pole = (pole_x, desired.y + y_offset, desired.z)
                                move_control(suffix + "_knee_ik", pole)
                                leg_ik.pole_angle = math.radians(pole_degrees)
                                bpy.context.view_layer.update()
                                knee = rig.matrix_world @ rig.pose.bones["lowerleg01." + side_name].head
                                error = Vector((knee.x - desired.x, knee.y - desired.y, knee.z - desired.z))
                                score = (1.5 * error.x) ** 2 + error.y ** 2 + error.z ** 2
                                if best is None or score < best[0]:
                                    best = (score, pole, pole_degrees, knee.copy())
                    _, pole, pole_degrees, knee = best
                    move_control(suffix + "_knee_ik", pole)
                    leg_ik.pole_angle = math.radians(pole_degrees)
                    bpy.context.view_layer.update()
                    print(f"Phase {degrees} knee {side_name}: pole={tuple(round(v, 3) for v in pole)}, "
                          f"angle={pole_degrees}, joint={tuple(round(v, 3) for v in knee)}")
            bpy.context.view_layer.update()
            scene.render.filepath = str(preview_dir / f"crank_{degrees:03d}.png")
            bpy.ops.render.render(write_still=True)
            for side in ("L", "R"):
                ankle = rig.matrix_world @ rig.pose.bones["foot." + side].head
                knee = rig.matrix_world @ rig.pose.bones["lowerleg01." + side].head
                print(f"Crank {degrees:03d} {side}: knee={tuple(round(v, 3) for v in knee)} "
                      f"ankle={tuple(round(v, 3) for v in ankle)}")

        # Restore the reviewed static phase before baking the deliverable.
        for suffix, side, angle, pole, pole_angle in (
                ("left", -1, math.pi, (.13, -.35, .69), 135),
                ("right", 1, 0.0, (-.13, -.35, .88), 45)):
            pedal = crank_center + Vector((0, crank_radius * math.sin(angle), crank_radius * math.cos(angle)))
            move_control(suffix + "_foot_ik", (side * .13, pedal.y - .08, pedal.z + .075))
            move_control(suffix + "_knee_ik", pole)
            leg_ik = next(c for c in rig.pose.bones["lowerleg02." + ("L" if side < 0 else "R")].constraints
                          if c.type == "IK")
            leg_ik.pole_angle = math.radians(pole_angle)
        bpy.context.view_layer.update()
    finally:
        scene.camera = None
        for obj in created:
            bpy.data.objects.remove(obj, do_unlink=True)
        scene.world = old_world
        scene.render.engine = old_engine
        scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = old_resolution
        scene.render.filepath = old_filepath


def ellipsoid(name, center, radii, mat, parent, rings=8, sides=12):
    vertices, faces = [], []
    for row in range(rings + 1):
        latitude = -math.pi / 2 + math.pi * row / rings
        ring_radius = math.cos(latitude)
        height = math.sin(latitude)
        for side in range(sides):
            angle = math.tau * side / sides
            vertices.append((center[0] + radii[0] * ring_radius * math.cos(angle),
                             center[1] + radii[1] * ring_radius * math.sin(angle),
                             center[2] + radii[2] * height))
    for row in range(rings):
        for side in range(sides):
            a = row * sides + side
            b = row * sides + (side + 1) % sides
            faces.extend(((a, b, b + sides), (a, b + sides, a + sides)))
    return mesh_object(name, vertices, faces, mat, parent, smooth=True)


def loft_z(name, profiles, mat, parent, sides=16):
    """Smooth, closed elliptical rings: (z, center_y, radius_x, radius_y)."""
    vertices, faces = [], []
    for z, center_y, radius_x, radius_y in profiles:
        for side in range(sides):
            angle = math.tau * side / sides
            vertices.append((radius_x * math.cos(angle), center_y + radius_y * math.sin(angle), z))
    for row in range(len(profiles) - 1):
        for side in range(sides):
            a = row * sides + side
            b = row * sides + (side + 1) % sides
            c = (row + 1) * sides + (side + 1) % sides
            d = (row + 1) * sides + side
            faces.extend(((a, b, c), (a, c, d)))
    faces.append(tuple(range(sides - 1, -1, -1)))
    top = (len(profiles) - 1) * sides
    faces.append(tuple(top + side for side in range(sides)))
    return mesh_object(name, vertices, faces, mat, parent, smooth=True)


def tube_between(name, start, end, radius_start, radius_end, mat, parent, sides=10):
    a = Vector3(start)
    b = Vector3(end)
    axis = normalize(sub(b, a))
    reference = (0.0, 0.0, 1.0) if abs(axis[2]) < 0.9 else (1.0, 0.0, 0.0)
    u = normalize(cross(axis, reference))
    v = normalize(cross(axis, u))
    length = distance(a, b)
    rings = ((0.0, radius_start * 0.72), (0.16, radius_start),
             (0.82, radius_end), (1.0, radius_end * 0.72))
    vertices, faces = [], []
    for along, radius in rings:
        center = add(a, mul(axis, length * along))
        for side in range(sides):
            angle = math.tau * side / sides
            radial = add(mul(u, math.cos(angle)), mul(v, math.sin(angle)))
            vertices.append(add(center, mul(radial, radius)))
    for row in range(len(rings) - 1):
        for side in range(sides):
            a0 = row * sides + side
            b0 = row * sides + (side + 1) % sides
            c0 = (row + 1) * sides + (side + 1) % sides
            d0 = (row + 1) * sides + side
            faces.extend(((a0, b0, c0), (a0, c0, d0)))
    faces.append(tuple(range(sides - 1, -1, -1)))
    top = (len(rings) - 1) * sides
    faces.append(tuple(top + side for side in range(sides)))
    return mesh_object(name, vertices, faces, mat, parent, smooth=True)


def unit_rod(name, mat, parent, sides=10):
    vertices, faces = [], []
    # The runtime scales this mesh to the segment's diameter and length. Keep
    # its widest source diameter below one metre so it behaves like the fallback
    # cylinder instead of multiplying the requested segment thickness.
    profile = ((-0.50, .14), (-.43, .26), (-.31, .38), (-.10, .455),
               (.12, .43), (.29, .345), (.43, .23), (.50, .13))
    for z, radius in profile:
        for side in range(sides):
            angle = math.tau * side / sides
            vertices.append((radius * math.cos(angle), radius * math.sin(angle), z))
    for row in range(len(profile) - 1):
        for side in range(sides):
            a = row * sides + side
            b = row * sides + (side + 1) % sides
            faces.extend(((a, b, b + sides), (a, b + sides, a + sides)))
    faces.append(tuple(range(sides - 1, -1, -1)))
    top = (len(profile) - 1) * sides
    faces.append(tuple(top + side for side in range(sides)))
    return mesh_object(name, vertices, faces, mat, parent, smooth=True)


def unit_box(name, mat, parent):
    vertices = [(x, y, z) for z in (-0.5, 0.5) for y in (-0.5, 0.5) for x in (-0.5, 0.5)]
    faces = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6),
             (0, 2, 6, 4), (1, 5, 7, 3))
    return mesh_object(name, vertices, faces, mat, parent)


def torus_yz(name, major_radius, tube_radius, mat, parent, center=(0.0, 0.0, 0.0), major_steps=48, minor_steps=8):
    vertices, faces = [], []
    for major in range(major_steps):
        theta = math.tau * major / major_steps
        for minor in range(minor_steps):
            phi = math.tau * minor / minor_steps
            radial = major_radius + tube_radius * math.cos(phi)
            vertices.append((center[0] + tube_radius * math.sin(phi),
                             center[1] + radial * math.sin(theta),
                             center[2] + radial * math.cos(theta)))
    for major in range(major_steps):
        for minor in range(minor_steps):
            a = major * minor_steps + minor
            b = major * minor_steps + (minor + 1) % minor_steps
            c = ((major + 1) % major_steps) * minor_steps + (minor + 1) % minor_steps
            d = ((major + 1) % major_steps) * minor_steps + minor
            faces.extend(((a, b, c), (a, c, d)))
    return mesh_object(name, vertices, faces, mat, parent, smooth=True)


def add_spokes(name, mat, parent, center_y, center_z, radius=0.285, count=18):
    vertices, faces = [], []
    for side_x in (-0.012, 0.012):
        for spoke in range(count):
            theta = math.tau * (spoke + (0.5 if side_x > 0 else 0.0)) / count
            end = (side_x, center_y + radius * math.sin(theta), center_z + radius * math.cos(theta))
            start = (side_x, center_y, center_z)
            tube_vertices, tube_faces = tube_data(start, end, 0.0022, 0.0015, 6)
            base = len(vertices)
            vertices.extend(tube_vertices)
            faces.extend(tuple(base + i for i in face) for face in tube_faces)
    return mesh_object(name, vertices, faces, mat, parent)


def tube_data(start, end, r0, r1, sides):
    a, b = Vector3(start), Vector3(end)
    axis = normalize(sub(b, a))
    reference = (0.0, 0.0, 1.0) if abs(axis[2]) < 0.9 else (1.0, 0.0, 0.0)
    u, v = normalize(cross(axis, reference)), None
    v = normalize(cross(axis, u))
    vertices, faces = [], []
    for along, radius in ((0.0, r0), (1.0, r1)):
        center = add(a, mul(axis, distance(a, b) * along))
        for side in range(sides):
            angle = math.tau * side / sides
            vertices.append(add(center, mul(add(mul(u, math.cos(angle)), mul(v, math.sin(angle))), radius)))
    for side in range(sides):
        a0, b0 = side, (side + 1) % sides
        faces.extend(((a0, b0, b0 + sides), (a0, b0 + sides, a0 + sides)))
    faces.append(tuple(range(sides - 1, -1, -1)))
    faces.append(tuple(sides + side for side in range(sides)))
    return vertices, faces


def Vector3(value):
    return (float(value[0]), float(value[1]), float(value[2]))


def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, scalar): return (a[0] * scalar, a[1] * scalar, a[2] * scalar)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def distance(a, b): return math.sqrt(dot(sub(a, b), sub(a, b)))
def normalize(a):
    length = math.sqrt(dot(a, a))
    if length < 1e-8:
        raise ValueError("Cannot create a zero-length model part")
    return mul(a, 1.0 / length)


def build_model():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)

    shorts = material("Wodra Bib Shorts · charcoal", (0.035, 0.047, 0.058))
    frame = material("Wodra Frame · deep teal", (0.055, 0.34, 0.39), 0.58)
    metal = material("Wodra Brushed Alloy", (0.34, 0.38, 0.39), 0.48)
    rubber = material("Road Tyre", (0.018, 0.022, 0.026), 0.92)
    glove = material("Glove", (0.045, 0.052, 0.055))

    root = empty("WodraCyclist", None)
    bike = empty("BicycleModel", root)
    rider = empty("RiderModel", root)

    # Blender +Y exports as Godot -Z. Keep the bottom bracket at Godot's
    # authored rearward Z=.10 anchor (Blender Y=-.10), matching runtime IK.
    rear_y, front_y, wheel_z, bb_y, bb_z = -0.51, 0.51, 0.35, -0.10, 0.39
    rear_spin = empty("RearWheelSpin", bike, (0.0, rear_y, wheel_z))
    front_spin = empty("FrontWheelSpin", bike, (0.0, front_y, wheel_z))
    for prefix, pivot in (("Rear", rear_spin), ("Front", front_spin)):
        torus_yz(prefix + "WheelTyre", 0.326, 0.024, rubber, pivot)
        torus_yz(prefix + "WheelRim", 0.295, 0.011, metal, pivot)
        add_spokes(prefix + "WheelSpokes", metal, pivot, 0.0, 0.0, 0.286, 14)
        ellipsoid(prefix + "WheelHub", (0, 0, 0), (0.035, 0.045, 0.045), metal, pivot, 6, 10)

    # Road frame tubes meet at shared BB, seat, head and hub coordinates.
    rear_hub = (0.0, rear_y, wheel_z)
    front_hub = (0.0, front_y, wheel_z)
    bb = (0.0, bb_y, bb_z)
    saddle_node = (0.0, -0.25, 0.92)
    head_low = (0.0, 0.32, 0.72)
    head_high = (0.0, 0.37, 0.84)
    frame_parts = []
    def frame_tube(name, a, b, radius):
        if name in {"down tube", "top tube", "head tube", "fork"}:
            # Elliptical, tapered sections read as molded carbon aero profiles
            # while remaining lightweight and easy to deform for future frames.
            axis = normalize(sub(b, a))
            reference = (0.0, 0.0, 1.0) if abs(axis[2]) < 0.9 else (1.0, 0.0, 0.0)
            u = normalize(cross(axis, reference))
            v = normalize(cross(axis, u))
            width_scale = {"down tube": 1.85, "top tube": 1.25,
                           "head tube": 1.30, "fork": 1.45}[name]
            depth_scale = {"down tube": .72, "top tube": .78,
                           "head tube": .88, "fork": .70}[name]
            sections = ((.0, .82), (.12, 1.0), (.34, 1.08),
                        (.72, 1.0), (.9, .94), (1.0, .82))
            length = distance(a, b)
            vertices, faces = [], []
            sides = 12
            for along, taper in sections:
                center = add(a, mul(axis, length * along))
                for section in range(sides):
                    theta = math.tau * section / sides
                    offset = add(mul(u, math.cos(theta) * radius * width_scale * taper),
                                 mul(v, math.sin(theta) * radius * depth_scale * taper))
                    vertices.append(add(center, offset))
            for row in range(len(sections) - 1):
                for section in range(sides):
                    a0 = row * sides + section
                    b0 = row * sides + (section + 1) % sides
                    faces.extend(((a0, b0, b0 + sides), (a0, b0 + sides, a0 + sides)))
            faces.extend((tuple(reversed(range(sides))),
                          tuple((len(sections) - 1) * sides + side for side in range(sides))))
            frame_parts.append((vertices, faces))
            return
        axis = normalize(sub(b, a))
        start = sub(a, mul(axis, radius * 0.85))
        end = add(b, mul(axis, radius * 0.85))
        frame_parts.append(tube_data(start, end, radius, radius * 0.92, 10))

    frame_tube("seat tube", bb, saddle_node, 0.031)
    frame_tube("down tube", bb, head_low, 0.034)
    frame_tube("top tube", saddle_node, head_high, 0.027)
    frame_tube("head tube", head_low, head_high, 0.041)
    for side in (-0.045, 0.045):
        frame_tube("chain stay", (side, rear_y, wheel_z), (side, bb_y, bb_z), 0.016)
        frame_tube("seat stay", (side, rear_y, wheel_z), (side, -0.25, 0.88), 0.015)
        frame_tube("fork", (side, front_y, wheel_z), (side, 0.32, 0.72), 0.017)
    frame_tube("seat post", saddle_node, (0.0, -0.20, 1.01), 0.018)
    # Merge tube meshes while keeping the shared named frame surface/material.
    vertices, faces = [], []
    for part_vertices, part_faces in frame_parts:
        base = len(vertices)
        vertices.extend(part_vertices)
        faces.extend(tuple(base + i for i in face) for face in part_faces)
    mesh_object("BikeFrame", vertices, faces, frame, bike, smooth=True)

    # Compact road saddle, tapered stem and familiar drop bars.
    ellipsoid("Saddle", (0, -0.25, 1.01), (0.12, 0.065, 0.022), shorts, bike, 5, 10)
    # Adult road-bike reach is kept compact enough for a relaxed bent-elbow fit.
    tube_between("Stem", head_high, (0.0, 0.22, 0.92), 0.021, 0.018, metal, bike)
    bar_parts = []
    def bar(a, b, radius=0.012):
        bar_parts.append(tube_data(a, b, radius, radius, 8))
    bar((-0.25, 0.22, 0.92), (0.25, 0.22, 0.92))
    for side in (-1.0, 1.0):
        bar((side * .25, .22, .92), (side * .25, .30, .91))
        bar((side * .25, .30, .91), (side * .22, .33, .85))
        bar((side * .22, .33, .85), (side * .17, .28, .82))
        ellipsoid("BrakeHood" + ("L" if side < 0 else "R"), (side * .25, .225, .955), (.047, .06, .055), glove, bike, 5, 9)
    vertices, faces = [], []
    for part_vertices, part_faces in bar_parts:
        base = len(vertices)
        vertices.extend(part_vertices)
        faces.extend(tuple(base + i for i in face) for face in part_faces)
    mesh_object("RoadDropHandlebars", vertices, faces, metal, bike, smooth=True)

    crank = empty("CrankPivot", bike, bb)
    left_crank = unit_rod("LeftCrank", metal, crank)
    right_crank = unit_rod("RightCrank", metal, crank)
    # CrankPivot is already at the bottom bracket: these are local offsets.
    left_crank.location = (-.13, 0.0, -.085)
    left_crank.scale = (.036, .036, .17)
    left_crank.rotation_euler.x = math.pi
    right_crank.location = (.13, 0.0, .085)
    right_crank.scale = (.036, .036, .17)
    left_pedal = unit_box("LeftPedal", metal, crank)
    right_pedal = unit_box("RightPedal", metal, crank)
    left_pedal.location = (-.13, 0.0, -.17)
    left_pedal.scale = (.06, .11, .025)
    right_pedal.location = (.13, 0.0, .17)
    right_pedal.scale = (.06, .11, .025)
    torus_yz("Chainring", .105, .006, metal, bike, (0.055, bb_y, bb_z), 24, 6)
    tube_between("ChainTop", (.11, bb_y, bb_z + .06), (.11, rear_y, wheel_z + .025), .004, .004, metal, bike, 6)
    tube_between("ChainBottom", (.11, bb_y, bb_z - .06), (.11, rear_y, wheel_z - .025), .004, .004, metal, bike, 6)

    return root


def build_skinned_rider(rider_model):
    """Create a full weighted MPFB human and fit its real skeleton to this bike."""
    mpfb_source = os.environ.get("WODRA_MPFB_SOURCE")
    if mpfb_source:
        sys.path.insert(0, str(Path(mpfb_source).resolve()))
    try:
        try:
            mpfb = importlib.import_module("mpfb")
        except ModuleNotFoundError:
            import addon_utils
            modules = [m.__name__ for m in addon_utils.modules(refresh=True)
                       if m.__name__.split(".")[-1] == "mpfb"]
            if not modules:
                raise RuntimeError("MPFB is not installed in Blender")
            mpfb = importlib.import_module(modules[0])
        services_module = mpfb.__name__ + ".services"
        if services_module not in sys.modules:
            if not mpfb_source:
                import addon_utils
                addon_utils.enable(mpfb.__name__)
            else:
                temp_home = os.environ.get("TEMP", ".")
                preferences = {
                    "mpfb_user_data": os.path.join(temp_home, "wodra-mpfb-build"),
                    "mpfb_second_root": None, "mh_user_data": None,
                    "mh_auto_user_data": False, "mpfb_shelf_label": "MPFB",
                }
                mpfb.get_preference = lambda key: preferences.get(key)
                bpy.utils.extension_path_user = lambda package, path="", create=False: os.path.join(
                    preferences["mpfb_user_data"], package, path)
                mpfb.register()
        manifest = Path(mpfb.__file__).with_name("blender_manifest.toml")
        version_line = next((line for line in manifest.read_text(encoding="utf-8").splitlines()
                             if line.strip().startswith("version =")), "")
        if f'"{MPFB_EXPECTED_VERSION}"' not in version_line:
            raise RuntimeError(f"Expected MPFB {MPFB_EXPECTED_VERSION}; found {version_line or 'no extension manifest'}")
        mpfb_prefix = mpfb.__name__
        HumanService = importlib.import_module(
            mpfb_prefix + ".services.humanservice").HumanService
        TargetService = importlib.import_module(
            mpfb_prefix + ".services.targetservice").TargetService
    except Exception as exc:
        raise RuntimeError(
            "The cyclist export needs MPFB 2.0.18 and its bundled default rig/weights. "
            "Install and enable MPFB in Blender Preferences > Get Extensions."
        ) from exc

    # Remove only the old placeholder rider subtree; the authored road bike remains.
    def remove_children(parent):
        for child in list(parent.children):
            remove_children(child)
            bpy.data.objects.remove(child, do_unlink=True)
    remove_children(rider_model)

    macro = TargetService.get_default_macro_info_dict()
    macro.update({"gender": 1.0, "age": 0.35, "muscle": 0.50,
                  "weight": 0.16, "proportions": 0.55, "height": 0.58})
    human = HumanService.create_human(scale=0.1, feet_on_ground=True,
                                      mask_helpers=True, macro_detail_dict=macro)
    rig = HumanService.add_builtin_rig(human, "default", import_weights=True)
    if rig is None or not any(mod.type == "ARMATURE" and mod.object == rig for mod in human.modifiers):
        raise RuntimeError("MPFB did not attach its default skin weights to the character mesh")
    if len(human.vertex_groups) < 100:
        raise RuntimeError(f"Expected MPFB weighted vertex groups, found {len(human.vertex_groups)}")

    skin = material("MPFB CC0 Skin", (0.50, 0.33, 0.24), 0.73)
    jersey = material("Wodra Cycling Jersey", (0.46, 0.10, 0.075), 0.78)
    shorts = material("Wodra Cycling Bib Shorts", (0.035, 0.045, 0.055), 0.86)
    shoe = material("Wodra Road Shoes", (0.12, 0.16, 0.19), 0.72)
    mesh = human.data
    mesh.materials.clear()
    for value in (skin, jersey, shorts, shoe):
        mesh.materials.append(value)
    material_counts = [0, 0, 0, 0]
    for polygon in mesh.polygons:
        p = polygon.center
        abs_x = abs(p.x)
        if p.z < 0.12 and p.y < -0.055:
            group = 3
        elif 0.72 < p.z < 1.02 and abs_x < 0.29:
            group = 2
        elif 0.97 < p.z < 1.405 and (abs_x < .26 or (abs_x < .36 and p.z < 1.31)):
            group = 1
        else:
            group = 0
        polygon.material_index = group
        polygon.use_smooth = True
        material_counts[group] += 1
    if min(material_counts) == 0:
        raise RuntimeError(f"MPFB body material regions are incomplete: {material_counts}")

    rider_model.rotation_euler.z = math.pi
    rider_model.location = (0.0, -0.263, 0.195)
    rig.parent = rider_model
    rig.matrix_parent_inverse = Matrix.Identity(4)
    rig.location = (0.0, 0.0, 0.0)
    rig.rotation_euler = (0.0, 0.0, 0.0)

    # A light road helmet is weighted to the real head bone, so it follows the
    # character instead of floating above the manually replaced torso.
    helmet = loft_z("CyclingHelmet", (
        (1.595, -.035, .078, .090), (1.615, -.025, .104, .120),
        (1.650, -.010, .112, .130), (1.685, .008, .087, .100),
        (1.712, .014, .045, .060), (1.722, .010, .006, .010)),
        material("Road Helmet", (.74, .47, .13), .46), rig, 16)
    brim = ellipsoid("HelmetBrim", (0.0, -.126, 1.612), (.103, .034, .012),
                     helmet.data.materials[0], rig, 5, 12)
    for obj in (helmet, brim):
        obj.vertex_groups.new(name="head")
        for vertex in obj.data.vertices:
            obj.vertex_groups["head"].add([vertex.index], 1.0, "REPLACE")
        modifier = obj.modifiers.new("Head skin", "ARMATURE")
        modifier.object = rig

    # Lean the whole spine chain from the pelvis. This changes joint rotation,
    # never bone or limb length. The pelvis remains above the saddle anchor.
    spine = rig.pose.bones["spine05"]
    spine.rotation_mode = "XYZ"
    spine.rotation_euler.x = math.radians(24)
    neck = rig.pose.bones["neck01"]
    neck.rotation_mode = "XYZ"
    neck.rotation_euler.x = math.radians(-9)

    targets = []
    # Use MPFB's documented segmented helper rig. Its two-segment lower/upper
    # controls are designed for this default skeleton; ad-hoc IK on a wrist or
    # lower-leg bone bypasses the rig's joint limits and helper hierarchy.
    LegHelpers = importlib.import_module(
        mpfb.__name__ + ".entities.rigging.righelpers.leghelpers.leghelpers").LegHelpers
    ArmHelpers = importlib.import_module(
        mpfb.__name__ + ".entities.rigging.righelpers.armhelpers.armhelpers").ArmHelpers
    helper_settings = {
        "leg_helpers_type": "LOWERUPPER",
        "leg_parenting_strategy": "ROOT",
        "leg_target_rotates_foot": True,
        "leg_target_rotates_lower_leg": False,
        "arm_helpers_type": "LOWERUPPER",
        "arm_parenting_strategy": "SPINE",
        "arm_target_rotates_hand": True,
        "arm_target_rotates_lower_arm": False,
        "hide_fk": False,
    }
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="OBJECT")
    for side in ("left", "right"):
        LegHelpers.get_instance(side, helper_settings).apply_ik(rig)
        ArmHelpers.get_instance(side, helper_settings).apply_ik(rig)

    # MPFB control bones are in armature space, so convert bike-world targets
    # through rig.matrix_world. The target sits at the foot pivot; the shoe's
    # pedal contact is forward and slightly below that pivot.
    bpy.context.view_layer.update()
    bb_y, bb_z, crank = -.10, .39, .17
    world_to_rig = rig.matrix_world.inverted()
    def set_helper_world(name, position, world_rotation=None):
        helper = rig.pose.bones[name]
        matrix = helper.matrix.copy()
        matrix.translation = world_to_rig @ Vector(position)
        if world_rotation is not None:
            armature_rotation = rig.matrix_world.to_3x3().inverted() @ world_rotation
            matrix = armature_rotation.to_4x4()
            matrix.translation = world_to_rig @ Vector(position)
        helper.matrix = matrix
        return helper

    # Solve at a simple opposing-crank phase: left at 180 degrees, right at 0.
    for suffix, side, angle in (("left", -1, math.pi), ("right", 1, 0.0)):
        pedal_y = bb_y + crank * math.sin(angle)
        pedal_z = bb_z + crank * math.cos(angle)
        set_helper_world(suffix + "_foot_ik",
                         (side * .13, pedal_y - .08, pedal_z + .075))
        # The knee helper is a bend control (not the knee joint itself).
        knee_z = .62 if angle == math.pi else .76
        set_helper_world(suffix + "_knee_ik", (side * .13, .10, knee_z))

    # A single full-length foot IK chain plus the documented MPFB knee helper
    # as its pole gives one unambiguous solver for the joints. MPFB's default
    # two-chain setup puts a second IK constraint on upperleg02; that competes
    # with the full leg chain when the pelvis is fixed on the saddle.
    for side in ("L", "R"):
        upper = rig.pose.bones["upperleg02." + side]
        for constraint in list(upper.constraints):
            if constraint.type == "IK":
                upper.constraints.remove(constraint)
        lower = rig.pose.bones["lowerleg02." + side]
        leg_ik = next(c for c in lower.constraints if c.type == "IK")
        leg_ik.chain_count = 4
        leg_ik.use_stretch = False
        leg_ik.pole_target = rig
        leg_ik.pole_subtarget = "left_knee_ik" if side == "L" else "right_knee_ik"
        leg_ik.pole_angle = 0.0
    # The two MPFB leg rolls are mirrored. These measured pole offsets keep
    # each knee in its own forward tracking plane at the opposing crank phase.
    for side, suffix, pole_position, degrees in (
            ("L", "left", (.13, -.35, .69), 135),
            ("R", "right", (-.13, -.35, .88), 45)):
        leg_ik = next(c for c in rig.pose.bones["lowerleg02." + side].constraints if c.type == "IK")
        set_helper_world(suffix + "_knee_ik", pole_position)
        leg_ik.pole_angle = math.radians(degrees)
        bpy.context.view_layer.update()
        knee = rig.matrix_world @ rig.pose.bones["lowerleg01." + side].head
        print(f"Fitted knee {side}: pole={pole_position}, angle={degrees}, "
              f"joint={tuple(round(v, 3) for v in knee)}")

    # Set the MPFB hand targets behind the brake hoods so the weighted palm and
    # fingers, rather than the wrist pivot, make contact at the hood tops.
    for suffix, side in (("left", -1), ("right", 1)):
        side_name = "L" if side < 0 else "R"
        wrist = rig.pose.bones["wrist." + side_name]
        wrist_world = rig.matrix_world @ wrist.matrix
        hand_axis = rig.matrix_world.to_3x3() @ (wrist.tail - wrist.head)
        hand_rotation = hand_axis.normalized().rotation_difference(Vector((0, 1, 0))) @ wrist_world.to_quaternion()
        set_helper_world(suffix + "_hand_ik", (side * .25, .145, .965), hand_rotation.to_matrix())
        set_helper_world(suffix + "_elbow_ik", (side * .25, -.025, 1.13))
    bpy.context.view_layer.update()

    print("MPFB left-leg helpers:", [(c.name, c.type, getattr(c, "chain_count", None))
                                     for c in rig.pose.bones["lowerleg02.L"].constraints])
    for side, target_name, foot_name in (("L", "left_foot_ik", "foot.L"),
                                         ("R", "right_foot_ik", "foot.R")):
        target_world = rig.matrix_world @ rig.pose.bones[target_name].matrix
        foot_world = rig.matrix_world @ rig.pose.bones[foot_name].matrix
        print(f"Foot {side} target/head world:", tuple(round(v, 3) for v in target_world.translation),
              tuple(round(v, 3) for v in foot_world.translation))
    for side in ("L", "R"):
        wrist_world = rig.matrix_world @ rig.pose.bones["wrist." + side].matrix
        print(f"Wrist {side} world:", tuple(round(v, 3) for v in wrist_world.translation))
        knee_world = rig.matrix_world @ rig.pose.bones["lowerleg01." + side].head
        target_name = "left_knee_ik" if side == "L" else "right_knee_ik"
        target_world = rig.matrix_world @ rig.pose.bones[target_name].matrix
        print(f"Knee {side} target/joint world:", tuple(round(v, 3) for v in target_world.translation),
              tuple(round(v, 3) for v in knee_world))

    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    print(f"MPFB rig: {len(rig.data.bones)} bones, {len(human.vertex_groups)} weighted groups, "
          f"material face counts={material_counts}")
    for target in targets:
        print(f"Fit target {target.name}: {tuple(round(value, 3) for value in target.location)}")
    return rig, targets


def add_skinned_road_shoes(rig):
    """Add fitted shoe shells weighted to each MPFB foot deformation bone."""
    shoe = material("Road Shoe Upper", (.045, .065, .075), .58)
    sole = material("Road Shoe Sole", (.018, .021, .023), .82)
    world_to_rig = rig.matrix_world.inverted()
    bpy.context.view_layer.update()
    for side in ("L", "R"):
        bone_name = "foot." + side
        pose_bone = rig.pose.bones[bone_name]
        ankle = rig.matrix_world @ pose_bone.head
        # y increases toward the cyclist's toes. Cross-sections follow the
        # measured MPFB foot bounds, with a small clearance over the skin.
        stations = ((-.080, .034, -.065, -.035),
                    (-.045, .045, -.070, .025),
                    (.015, .048, -.070, .036),
                    (.118, .052, -.086, .022),
                    (.190, .048, -.092, .002))
        ring_count = 12
        world_vertices = []
        for dy, half_width, bottom, top in stations:
            center_z = (bottom + top) * .5
            radius_z = (top - bottom) * .5
            for index in range(ring_count):
                angle = math.tau * index / ring_count
                world_vertices.append((ankle.x + half_width * math.cos(angle),
                                       ankle.y + dy,
                                       ankle.z + center_z + radius_z * math.sin(angle)))
        faces = []
        material_indices = []
        for station in range(len(stations) - 1):
            for index in range(ring_count):
                a = station * ring_count + index
                b = station * ring_count + (index + 1) % ring_count
                faces.extend(((a, b, b + ring_count, a + ring_count),))
                material_indices.append(1 if station in (0, 1, 2, 3) and index in (3, 4, 5, 6, 7, 8) else 0)
        faces.append(tuple(range(ring_count - 1, -1, -1)))
        material_indices.append(1)
        last = (len(stations) - 1) * ring_count
        faces.append(tuple(last + index for index in range(ring_count)))
        material_indices.append(0)

        # Convert the posed world shell back to rest armature space so the
        # Armature modifier applies the same foot deformation as the body.
        rest_to_pose = pose_bone.matrix
        pose_to_rest = rig.data.bones[bone_name].matrix_local @ rest_to_pose.inverted()
        vertices = [pose_to_rest @ (world_to_rig @ Vector(point)) for point in world_vertices]
        mesh = bpy.data.meshes.new("CyclingShoe" + side + "Mesh")
        mesh.from_pydata([tuple(vertex) for vertex in vertices], [], faces)
        mesh.materials.append(shoe)
        mesh.materials.append(sole)
        for polygon, material_index in zip(mesh.polygons, material_indices):
            polygon.material_index = material_index
            polygon.use_smooth = True
        obj = bpy.data.objects.new("CyclingShoe" + side, mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = rig
        obj.matrix_parent_inverse = Matrix.Identity(4)
        obj.vertex_groups.new(name=bone_name)
        obj.vertex_groups[bone_name].add(list(range(len(vertices))), 1.0, "REPLACE")
        modifier = obj.modifiers.new("MPFB foot skin", "ARMATURE")
        modifier.object = rig


def build_cadence_animation(rig):
    """Sample three full crank revolutions through nonstretching MPFB IK, then bake bones."""
    scene = bpy.context.scene
    # Five-degree source samples keep the baked knee trajectory smooth at the
    # fastest supported cadence. The higher authoring frame rate keeps the
    # exported cycle at one second per revolution (60 rpm).
    cycle_intervals = 72
    cycle_count = 3
    scene.frame_start, scene.frame_end = 1, cycle_intervals * cycle_count + 1
    scene.render.fps = 72  # One full turn per second at 60 rpm.
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="OBJECT")
    rig.animation_data_clear()
    crank_center = Vector((0, -.10, .39))
    radius = .17
    inverse_armature = rig.matrix_world.inverted()
    start_states = {
        "L": {"pole": Vector((.13, -.35, .69)), "angle": math.radians(135)},
        "R": {"pole": Vector((-.13, -.35, .88)), "angle": math.radians(45)},
    }
    previous = {side: {"pole": state["pole"].copy(), "angle": state["angle"]}
                for side, state in start_states.items()}

    def set_control_world(name, point):
        control = rig.pose.bones[name]
        control.rotation_mode = "QUATERNION"
        matrix = control.matrix.copy()
        matrix.translation = inverse_armature @ Vector(point)
        control.matrix = matrix
        return control

    def key_control(control, frame):
        control.keyframe_insert(data_path="location", frame=frame)
        control.keyframe_insert(data_path="rotation_quaternion", frame=frame)

    for index in range(cycle_intervals * cycle_count + 1):
        frame = index + 1
        phase_index = index % cycle_intervals
        degrees = phase_index * (360 / cycle_intervals)
        phase = math.tau * phase_index / cycle_intervals
        scene.frame_set(frame)
        if phase_index == 0:
            previous = {side: {"pole": state["pole"].copy(), "angle": state["angle"]}
                        for side, state in start_states.items()}
        desired = {}
        for suffix, side, crank_angle in (("left", -1, phase + math.pi),
                                           ("right", 1, phase)):
            pedal = crank_center + Vector((0, radius * math.sin(crank_angle),
                                            radius * math.cos(crank_angle)))
            ankle = (side * .13, pedal.y - .08, pedal.z + .075)
            set_control_world(suffix + "_foot_ik", ankle)
            if phase_index == 0:
                side_name = "L" if side < 0 else "R"
                state = start_states[side_name]
                pole = state["pole"]
                set_control_world(suffix + "_knee_ik", pole)
                leg_ik = next(c for c in rig.pose.bones["lowerleg02." + side_name].constraints if c.type == "IK")
                leg_ik.pole_angle = state["angle"]
                continue
            progress = max(0.0, min(1.0, (ankle[2] - .295) / .34))
            desired[side] = Vector((side * .13, -.11 + .21 * progress,
                                    .50 + .667 * ankle[2]))

        for side, suffix in ((-1, "left"), (1, "right")):
            side_name = "L" if side < 0 else "R"
            leg_ik = next(c for c in rig.pose.bones["lowerleg02." + side_name].constraints if c.type == "IK")
            if phase_index != 0:
                target = desired[side]
                best = None
                # Keep each pole on its own leg's lateral bend plane. The old
                # coarse search could switch between opposite-side planes and
                # 45-degree pole-angle branches every 10 degrees of crank
                # motion. Search only small, continuous changes around the
                # previous solution and regularize them against that solution.
                previous_pole = previous[side_name]["pole"]
                pole_x = previous_pole.x
                previous_angle = previous[side_name]["angle"]
                cycle_progress = phase_index / cycle_intervals
                seam_weight = max(0.0, (cycle_progress - .75) / .25) ** 2
                seam_state = start_states[side_name]
                angle_offsets = (-math.radians(5), -math.radians(2.5), 0.0,
                                 math.radians(2.5), math.radians(5))
                for y_delta in (-.025, -.0125, 0.0, .0125, .025):
                    pole_y = previous_pole.y + y_delta
                    for angle_delta in angle_offsets:
                        angle = (previous_angle + angle_delta + math.pi) % math.tau - math.pi
                        pole = Vector((pole_x, pole_y, target.z))
                        set_control_world(suffix + "_knee_ik", pole)
                        leg_ik.pole_angle = angle
                        bpy.context.view_layer.update()
                        joint = rig.matrix_world @ rig.pose.bones["lowerleg01." + side_name].head
                        error = Vector((joint.x - target.x, joint.y - target.y, joint.z - target.z))
                        angular_delta = (angle - previous_angle + math.pi) % math.tau - math.pi
                        pole_delta = pole - previous[side_name]["pole"]
                        seam_pole_delta = pole - seam_state["pole"]
                        seam_angle_delta = (angle - seam_state["angle"] + math.pi) % math.tau - math.pi
                        score = ((1.5 * error.x) ** 2 + error.y ** 2 + error.z ** 2
                                 + .35 * angular_delta ** 2 + 2.5 * pole_delta.length_squared
                                 + seam_weight * (2.5 * seam_pole_delta.length_squared
                                                  + .35 * seam_angle_delta ** 2))
                        if best is None or score < best[0]:
                            best = (score, pole.copy(), angle, joint.copy())
                _, pole, angle, joint = best
                set_control_world(suffix + "_knee_ik", pole)
                leg_ik.pole_angle = angle
                previous[side_name] = {"pole": pole, "angle": angle}
                if phase_index % 6 == 0:
                    print(f"Cycle frame {frame} {side_name}: knee={tuple(round(v, 3) for v in joint)} "
                          f"poleAngle={round(math.degrees(angle))}")
            control = rig.pose.bones[suffix + "_foot_ik"]
            key_control(control, frame)
            control = rig.pose.bones[suffix + "_knee_ik"]
            key_control(control, frame)
            leg_ik.keyframe_insert(data_path="pole_angle", frame=frame)
        for suffix in ("left", "right"):
            key_control(rig.pose.bones[suffix + "_hand_ik"], frame)
            key_control(rig.pose.bones[suffix + "_elbow_ik"], frame)
        bpy.context.view_layer.update()

    # The final frame is identical to frame 1 so the exported animation loops.
    scene.frame_set(scene.frame_end)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.nla.bake(frame_start=scene.frame_start, frame_end=scene.frame_end, step=1,
                     only_selected=False, visual_keying=True, clear_constraints=True,
                     use_current_action=False, bake_types={"POSE"})
    bpy.ops.object.mode_set(mode="OBJECT")
    action = rig.animation_data.action if rig.animation_data else None
    if action is None:
        raise RuntimeError("Blender did not bake an action from the evaluated cycling rig")
    action.name = "PedalCycle"
    curves = [curve for layer in action.layers for strip in layer.strips
              for channelbag in strip.channelbags for curve in channelbag.fcurves]
    for curve in curves:
        for point in curve.keyframe_points:
            point.interpolation = "LINEAR"
    scene.frame_set(scene.frame_start)
    print(f"Baked {action.name}: {len(curves)} curves, "
          f"frames {scene.frame_start}-{scene.frame_end}, {scene.render.fps} fps")


def render_cadence_preview(rig, preview_dir: Path):
    """Render an animated side preview and contact-sheet source frames."""
    preview_dir.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    old_engine = scene.render.engine
    old_resolution = (scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage)
    old_filepath = scene.render.filepath
    old_format = scene.render.image_settings.file_format
    old_world = scene.world
    created = []
    target = Vector((0, .02, .93))
    try:
        engines = {item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x = scene.render.resolution_y = 800
        scene.render.resolution_percentage = 100
        scene.render.film_transparent = False
        world = bpy.data.worlds.new("CadencePreviewWorld")
        world.color = (.035, .045, .06)
        scene.world = world
        for name, location, energy, size in (
                ("CycleKey", (3.2, -2.5, 3.5), 650, 3.2),
                ("CycleFill", (-2.8, .7, 2.3), 450, 2.8)):
            data = bpy.data.lights.new(name, "AREA")
            data.energy, data.shape, data.size = energy, "DISK", size
            obj = bpy.data.objects.new(name, data)
            scene.collection.objects.link(obj)
            obj.location = location
            obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()
            created.append(obj)
        data = bpy.data.cameras.new("CadenceSideCamera")
        data.type, data.ortho_scale = "ORTHO", 2.5
        camera = bpy.data.objects.new("CadenceSideCamera", data)
        scene.collection.objects.link(camera)
        camera.location = (3.5, .02, 1.0)
        camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
        created.append(camera)
        scene.camera = camera

        frame_dir = preview_dir / "frames"
        frame_dir.mkdir(parents=True, exist_ok=True)
        scene.render.image_settings.file_format = "PNG"
        scene.render.filepath = str(frame_dir / "frame_")
        bpy.ops.render.render(animation=True)
    finally:
        scene.camera = None
        for obj in created:
            bpy.data.objects.remove(obj, do_unlink=True)
        scene.world = old_world
        scene.render.engine = old_engine
        scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = old_resolution
        scene.render.image_settings.file_format = old_format
        scene.render.filepath = old_filepath
    scene.frame_set(scene.frame_start)


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    skip_previews = "--skip-previews" in args
    args = [arg for arg in args if arg != "--skip-previews"]
    if args:
        requested_output = Path(args[0])
        output = requested_output if requested_output.is_absolute() else Path(__file__).resolve().parents[2] / requested_output
    else:
        output = Path(__file__).resolve().parents[2] / "src" / "IndoorCycling.Game" / "Assets" / "Models" / "WodraCyclist.glb"
    output.parent.mkdir(parents=True, exist_ok=True)
    root = build_model()
    rider_model = bpy.data.objects.get("RiderModel")
    rig, rig_targets = build_skinned_rider(rider_model)
    add_skinned_road_shoes(rig)
    validate_model()
    if not skip_previews:
        render_fit_previews(Path(__file__).resolve().parent / "previews" / "static_fit_candidate_04")
        render_crank_phase_checks(rig, Path(__file__).resolve().parent / "previews" / "crank_phase_checks_04")
    build_cadence_animation(rig)
    if not skip_previews:
        render_cadence_preview(rig, Path(__file__).resolve().parent / "previews" / "cadence_preview_02")
    for target in rig_targets:
        bpy.data.objects.remove(target, do_unlink=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root
    source_path = (Path(args[1]) if len(args) > 1 else
                   Path(__file__).resolve().parent / "source" / "WodraCyclist.blend")
    source_path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(source_path))
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.gltf(filepath=str(output), export_format="GLB", use_selection=True,
                              export_animations=True, export_frame_range=True,
                              export_force_sampling=True, export_animation_mode="ACTIONS")
    print(f"Exported Wodra cyclist to {output}")


if __name__ == "__main__":
    main()
