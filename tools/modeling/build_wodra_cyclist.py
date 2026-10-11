"""Create the Wodra low-poly cyclist GLB using Blender's bundled Python API.

Run from the repository root with:
  blender --background --python tools/modeling/build_wodra_cyclist.py

The resulting node names are a runtime contract consumed by CyclistVisual.cs.
This file intentionally uses no downloaded assets or third-party Python packages.
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


SIDES = 12


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
    dynamic_templates = {
        "LeftCrank", "RightCrank", "LeftPedal", "RightPedal", "LeftThigh", "RightThigh",
        "LeftShin", "RightShin", "LeftCyclingShoe", "RightCyclingShoe", "LeftShoeSole",
        "RightShoeSole", "LeftAnkle", "RightAnkle", "LeftKnee", "RightKnee",
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
        if any(abs(value - 1.0) > 1e-4 for value in scale):
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
    for z, radius in ((-0.5, 0.38), (-0.42, 0.5), (0.42, 0.5), (0.5, 0.38)):
        for side in range(sides):
            angle = math.tau * side / sides
            vertices.append((radius * math.cos(angle), radius * math.sin(angle), z))
    for row in range(3):
        for side in range(sides):
            a = row * sides + side
            b = row * sides + (side + 1) % sides
            faces.extend(((a, b, b + sides), (a, b + sides, a + sides)))
    faces.append(tuple(range(sides - 1, -1, -1)))
    top = 3 * sides
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

    jersey = material("Wodra Jersey · rust red", (0.44, 0.105, 0.075))
    shorts = material("Wodra Bib Shorts · charcoal", (0.035, 0.047, 0.058))
    skin = material("Wodra Skin", (0.52, 0.32, 0.22))
    helmet = material("Wodra Helmet · warm ochre", (0.72, 0.43, 0.16))
    frame = material("Wodra Frame · deep teal", (0.055, 0.34, 0.39), 0.58)
    metal = material("Wodra Brushed Alloy", (0.34, 0.38, 0.39), 0.48)
    rubber = material("Road Tyre", (0.018, 0.022, 0.026), 0.92)
    shoe = material("Cycling Shoe", (0.73, 0.70, 0.61))
    glove = material("Glove", (0.045, 0.052, 0.055))

    root = empty("WodraCyclist", None)
    bike = empty("BicycleModel", root)
    rider = empty("RiderModel", root)
    torso_rig = empty("TorsoRig", rider)
    left_arm = empty("LeftArmRig", torso_rig)
    right_arm = empty("RightArmRig", torso_rig)
    left_leg = empty("LeftLegRig", rider)
    right_leg = empty("RightLegRig", rider)

    # Blender +Y exports as Godot -Z. Keep the bottom bracket at Godot's
    # authored rearward Z=.10 anchor (Blender Y=-.10), matching runtime IK.
    rear_y, front_y, wheel_z, bb_y, bb_z = -0.51, 0.51, 0.35, -0.10, 0.39
    rear_spin = empty("RearWheelSpin", bike, (0.0, rear_y, wheel_z))
    front_spin = empty("FrontWheelSpin", bike, (0.0, front_y, wheel_z))
    for prefix, pivot in (("Rear", rear_spin), ("Front", front_spin)):
        torus_yz(prefix + "WheelTyre", 0.326, 0.024, rubber, pivot)
        torus_yz(prefix + "WheelRim", 0.295, 0.006, metal, pivot)
        add_spokes(prefix + "WheelSpokes", metal, pivot, 0.0, 0.0, 0.286, 18)
        ellipsoid(prefix + "WheelHub", (0, 0, 0), (0.035, 0.045, 0.045), metal, pivot, 6, 10)

    # Road frame tubes meet at shared BB, seat, head and hub coordinates.
    rear_hub = (0.0, rear_y, wheel_z)
    front_hub = (0.0, front_y, wheel_z)
    bb = (0.0, bb_y, bb_z)
    saddle_node = (0.0, -0.25, 0.82)
    head_low = (0.0, 0.32, 0.72)
    head_high = (0.0, 0.37, 0.84)
    frame_parts = []
    def frame_tube(name, a, b, radius):
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
        frame_tube("seat stay", (side, rear_y, wheel_z), (side, -0.25, 0.80), 0.015)
        frame_tube("fork", (side, front_y, wheel_z), (side, 0.32, 0.72), 0.017)
    frame_tube("seat post", saddle_node, (0.0, -0.20, 0.91), 0.018)
    # Merge tube meshes while keeping the shared named frame surface/material.
    vertices, faces = [], []
    for part_vertices, part_faces in frame_parts:
        base = len(vertices)
        vertices.extend(part_vertices)
        faces.extend(tuple(base + i for i in face) for face in part_faces)
    mesh_object("BikeFrame", vertices, faces, frame, bike, smooth=True)

    # Compact road saddle, tapered stem and familiar drop bars.
    ellipsoid("Saddle", (0, -0.25, 0.91), (0.12, 0.065, 0.022), shorts, bike, 5, 10)
    tube_between("Stem", head_high, (0.0, 0.56, 0.94), 0.021, 0.018, metal, bike)
    bar_parts = []
    def bar(a, b, radius=0.012):
        bar_parts.append(tube_data(a, b, radius, radius, 8))
    bar((-0.30, 0.56, 0.94), (0.30, 0.56, 0.94))
    for side in (-1.0, 1.0):
        bar((side * .30, .56, .94), (side * .30, .64, .93))
        bar((side * .30, .64, .93), (side * .27, .67, .87))
        bar((side * .27, .67, .87), (side * .20, .62, .84))
        ellipsoid("BrakeHood" + ("L" if side < 0 else "R"), (side * .30, .565, .975), (.047, .06, .055), glove, bike, 5, 9)
    vertices, faces = [], []
    for part_vertices, part_faces in bar_parts:
        base = len(vertices)
        vertices.extend(part_vertices)
        faces.extend(tuple(base + i for i in face) for face in part_faces)
    mesh_object("RoadDropHandlebars", vertices, faces, metal, bike, smooth=True)

    crank = empty("CrankPivot", bike, bb)
    unit_rod("LeftCrank", metal, crank)
    unit_rod("RightCrank", metal, crank)
    unit_box("LeftPedal", metal, crank)
    unit_box("RightPedal", metal, crank)
    torus_yz("Chainring", .105, .006, metal, bike, (0.055, bb_y, bb_z), 24, 6)
    tube_between("ChainTop", (.11, bb_y, bb_z + .06), (.11, rear_y, wheel_z + .025), .004, .004, metal, bike, 6)
    tube_between("ChainBottom", (.11, bb_y, bb_z - .06), (.11, rear_y, wheel_z - .025), .004, .004, metal, bike, 6)

    # Sculpted jersey rings create a compact, forward-leaning torso.
    loft_z("JerseyTorso", ((.91, -.18, .15, .12), (.99, -.20, .16, .13),
        (1.08, -.15, .17, .14), (1.18, -.06, .19, .13), (1.27, .02, .18, .115),
        (1.315, .05, .14, .10)), jersey, torso_rig, 16)
    ellipsoid("PelvisAndShorts", (0, -.19, .99), (.118, .103, .073), shorts, torso_rig, 7, 12)
    tube_between("Neck", (0, .045, 1.285), (0, .10, 1.37), .058, .055, skin, torso_rig, 12)
    ellipsoid("Head", (0, .13, 1.475), (.102, .103, .118), skin, torso_rig, 9, 14)
    loft_z("CyclingHelmet", ((1.52, .10, .105, .135), (1.55, .10, .125, .15),
        (1.60, .08, .116, .145), (1.65, .035, .09, .12),
        (1.69, -.015, .048, .075), (1.705, -.03, .012, .025)), helmet, torso_rig, 14)
    ellipsoid("HelmetBrim", (0, .245, 1.54), (.105, .04, .012), helmet, torso_rig, 5, 10)

    for side, suffix, arm_rig in ((-1, "L", left_arm), (1, "R", right_arm)):
        shoulder = (side * .18, .045, 1.29)
        elbow = (side * .29, .335, 1.105)
        wrist = (side * .30, .565, .955)
        side_name = "Left" if side < 0 else "Right"
        tube_between(side_name + "JerseySleeve", shoulder, elbow, .071, .053, jersey, arm_rig, 12)
        tube_between(side_name + "Forearm", elbow, wrist, .050, .040, skin, arm_rig, 12)
        ellipsoid("ElbowJoint" + suffix, elbow, (.044, .044, .044), skin, arm_rig, 6, 10)
        ellipsoid("GlovedHand" + suffix, wrist, (.045, .045, .045), glove, arm_rig, 6, 10)

    # These normalized meshes are deliberately named animation targets. The existing C#
    # cadence/IK controller sets their transforms exactly as it does for its fallback mesh.
    for side, suffix, leg in ((-1, "Left", left_leg), (1, "Right", right_leg)):
        unit_rod(suffix + "Thigh", shorts, leg)
        unit_rod(suffix + "Shin", skin, leg)
        ellipsoid(suffix + "CyclingShoe", (0, 0, 0), (.5, .5, .5), shoe, leg, 7, 10)
        unit_box(suffix + "ShoeSole", shorts, leg)
        ellipsoid(suffix + "Ankle", (0, 0, 0), (.5, .5, .5), skin, leg, 6, 9)
        ellipsoid(suffix + "Knee", (0, 0, 0), (.5, .5, .5), shorts, leg, 6, 9)

    return root


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if args:
        requested_output = Path(args[0])
        output = requested_output if requested_output.is_absolute() else Path(__file__).resolve().parents[2] / requested_output
    else:
        output = Path(__file__).resolve().parents[2] / "src" / "IndoorCycling.Game" / "Assets" / "Models" / "WodraCyclist.glb"
    output.parent.mkdir(parents=True, exist_ok=True)
    root = build_model()
    validate_model()
    render_previews(Path(__file__).resolve().parent / "previews")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.gltf(filepath=str(output), export_format="GLB", use_selection=True)
    print(f"Exported Wodra cyclist to {output}")


if __name__ == "__main__":
    main()
