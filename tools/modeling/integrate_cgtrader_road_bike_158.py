#!/usr/bin/env python3
"""Integrate the owner-supplied CGTrader road/gravel bike into Wodra's cyclist.

This deliberately consumes the owner's external GigantRoad.blend; it does not
rebuild any bicycle parts. It keeps the native Blender asset editable and only
normalizes its origin, hierarchy, animation pivots, contacts, image sizes and
excessive tessellation for the Godot runtime export.
"""
from __future__ import annotations

import argparse
import math
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


WHEEL_Z = 0.339
ROAD_FRONT_Y = 0.505891
ROAD_REAR_Y = -0.487107
BB = Vector((0.006121, -0.074943, -0.291019))
SOURCE_WHEEL_Z = -0.2241355
Z_OFFSET = WHEEL_Z - SOURCE_WHEEL_Z

FRONT_WHEEL_OBJECTS = {
    "Front_hub", "Front_rim.001", "Front_spokes", "Front_tire.001",
    "gigantDecal2.003",
}
REAR_WHEEL_OBJECTS = {
    "Rear_hub", "Rear_rim.001", "Rear_spokes", "Rear_tire",
    "Cassette", "gigantDecal2.002",
}
CRANK_OBJECTS = {
    "Cranks", "Pedals", "Front_chainring", "Crank_axle", "Crank_bolts",
}


def ancestor_of(node: bpy.types.Object, parent: bpy.types.Object) -> bool:
    current = node.parent
    while current:
        if current == parent:
            return True
        current = current.parent
    return False


def keep_world_parent(obj: bpy.types.Object, parent: bpy.types.Object) -> None:
    world = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.matrix_world = world


def remove_old_bike_meshes(bike: bpy.types.Object) -> None:
    for obj in list(bpy.data.objects):
        if obj.type == "MESH" and (obj == bike or ancestor_of(obj, bike)):
            bpy.data.objects.remove(obj, do_unlink=True)


def append_source_meshes(source: Path) -> list[bpy.types.Object]:
    with bpy.data.libraries.load(str(source), link=False) as (src, dst):
        mesh_names = [name for name in src.objects if name]
        dst.objects = mesh_names
    result: list[bpy.types.Object] = []
    for obj in dst.objects:
        if obj is None or obj.type != "MESH":
            continue
        bpy.context.scene.collection.objects.link(obj)
        # The native asset stores many meshes in centimetre-sized local data
        # with small object scales. Bake rotation/scale before modifiers and
        # joining so Blender/Godot retain the evaluated metre-scale geometry.
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        obj.select_set(False)
        # CGTrader source uses metres, X lateral, +Y forward and +Z up.
        obj.matrix_world = Matrix.Translation((0.0, 0.0, Z_OFFSET)) @ obj.matrix_world
        result.append(obj)
    return result


def decimate(obj: bpy.types.Object, ratio: float) -> None:
    before = len(obj.data.polygons)
    modifier = obj.modifiers.new("Wodra runtime polygon optimization", "DECIMATE")
    modifier.ratio = ratio
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    obj.select_set(False)
    print("DECIMATE", obj.name, before, "to", len(obj.data.polygons), "ratio", ratio)


def simplify_textures() -> None:
    # Keep the detailed tire color map at 2K; non-color/utility maps need less.
    limits = {"Tire_Color": 2048, "Tire_Bump": 1024, "Tire_Roughness": 1024}
    for image in list(bpy.data.images):
        if image.source != "FILE" or not image.size[0] or not image.size[1]:
            continue
        longest = max(image.size)
        limit = limits.get(image.name, 1024)
        if longest > limit:
            scale = limit / longest
            image.scale(max(1, round(image.size[0] * scale)),
                        max(1, round(image.size[1] * scale)))
            print("RESIZE_TEXTURE", image.name, tuple(image.size))
        image.pack()


def apply_modifiers(obj: bpy.types.Object) -> None:
    for modifier in list(obj.modifiers):
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        try:
            bpy.ops.object.modifier_apply(modifier=modifier.name)
        except RuntimeError as exc:
            raise RuntimeError(f"Could not bake {modifier.name} on {obj.name}: {exc}") from exc
        finally:
            obj.select_set(False)


def separate_loose(obj: bpy.types.Object) -> list[bpy.types.Object]:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    return [o for o in bpy.context.selected_objects if o.type == "MESH"]


def mesh_center_x(obj: bpy.types.Object) -> float:
    if not obj.data.vertices:
        return obj.matrix_world.translation.x
    return sum((obj.matrix_world @ v.co).x for v in obj.data.vertices) / len(obj.data.vertices)


def join_meshes(objects: list[bpy.types.Object], name: str,
                parent: bpy.types.Object) -> bpy.types.Object:
    if not objects:
        raise RuntimeError(f"No geometry available for {name}")
    for obj in objects:
        keep_world_parent(obj, parent)
    if len(objects) > 1:
        bpy.ops.object.select_all(action="DESELECT")
        for obj in objects:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = objects[0]
        bpy.ops.object.join()
    result = objects[0]
    result.name = name
    result.data.name = name + "Mesh"
    result.select_set(False)
    return result


def contact(name: str, bike: bpy.types.Object, position: tuple[float, float, float],
            normal: tuple[float, float, float] = (0.0, 0.0, 1.0)) -> bpy.types.Object:
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_type = "ARROWS"
    obj.empty_display_size = 0.035
    bike.users_collection[0].objects.link(obj)
    obj.parent = bike
    obj.location = position
    direction = Vector(normal).normalized()
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = direction.to_track_quat("Z", "Y")
    obj["WodraContactTargetV1"] = True
    return obj


def optimize_asset(meshes: list[bpy.types.Object]) -> None:
    # This source includes several tiny parts with hundreds of thousands of
    # triangles. Ratios preserve the silhouette while avoiding unnecessary
    # tessellation; cockpit controls and wheel silhouettes remain untouched.
    ratios = {
        "Brake_caliper_front": 0.08,
        "Brake_caliper_rear": 0.08,
        "Brake_bolts": 0.08,
        "Cassette": 0.18,
        "Chain": 0.32,
        "Front_spokes": 0.65,
        "Rear_spokes": 0.65,
        "Seat": 0.32,
        "Seatclamp": 0.45,
        "Seatclamp_bolts": 0.45,
        "Headtube_spacers": 0.45,
    }
    for obj in meshes:
        ratio = ratios.get(obj.name)
        if ratio is not None:
            decimate(obj, ratio)
        apply_modifiers(obj)


def integrate(source: Path, rider: Path, output: Path) -> None:
    bpy.ops.wm.open_mainfile(filepath=str(rider))
    bike = bpy.data.objects.get("BicycleModel")
    if bike is None:
        raise RuntimeError("Canonical cyclist source has no BicycleModel")
    pivots = {name: bpy.data.objects.get(name) for name in
              ("FrontWheelSpin", "RearWheelSpin", "CrankPivot")}
    if any(value is None for value in pivots.values()):
        raise RuntimeError("Canonical cyclist is missing wheel/crank animation pivots")
    remove_old_bike_meshes(bike)

    # Keep/reposition the stable Wodra animation nodes. The source asset itself
    # already matches the intended 700C wheelbase and BB coordinates closely.
    pivots["FrontWheelSpin"].location = (0.0, ROAD_FRONT_Y, WHEEL_Z)
    pivots["RearWheelSpin"].location = (0.0, ROAD_REAR_Y, WHEEL_Z)
    pivots["CrankPivot"].location = tuple(BB + Vector((0.0, 0.0, Z_OFFSET)))

    meshes = append_source_meshes(source)
    simplify_textures()
    optimize_asset(meshes)

    by_name = {o.name: o for o in meshes}
    missing = (FRONT_WHEEL_OBJECTS | REAR_WHEEL_OBJECTS | CRANK_OBJECTS |
               {"Handlebars.001", "Brakes", "Frame1", "Frame2", "Frame3", "welds"}) - by_name.keys()
    if missing:
        raise RuntimeError("Source bicycle is missing expected meshes: " + ", ".join(sorted(missing)))

    front_parts = [by_name[name] for name in FRONT_WHEEL_OBJECTS]
    rear_parts = [by_name[name] for name in REAR_WHEEL_OBJECTS]
    # The front/rear decal and cassette are physically part of the wheel system.
    join_meshes(front_parts, "RoadBikeFrontWheelAssembly", pivots["FrontWheelSpin"])
    join_meshes(rear_parts, "RoadBikeRearWheelAssembly", pivots["RearWheelSpin"])

    # Separate artist-authored crank and pedal islands, then group each side.
    crank_islands = separate_loose(by_name["Cranks"])
    left_cranks = [o for o in crank_islands if mesh_center_x(o) < 0]
    right_cranks = [o for o in crank_islands if mesh_center_x(o) >= 0]
    if not left_cranks or not right_cranks:
        raise RuntimeError("Could not classify the imported left/right crank geometry")
    # The BB spider belongs to the drivetrain-side/right crank group.
    join_meshes(left_cranks, "LeftCrank", pivots["CrankPivot"])
    join_meshes(right_cranks, "RightCrank", pivots["CrankPivot"])
    rotating_drive = [by_name[name] for name in ("Front_chainring", "Crank_bolts")]
    join_meshes(rotating_drive, "RoadBikeRotatingDrive", pivots["CrankPivot"])

    pedal_islands = separate_loose(by_name["Pedals"])
    left_pedals = [o for o in pedal_islands if mesh_center_x(o) < 0]
    right_pedals = [o for o in pedal_islands if mesh_center_x(o) >= 0]
    if not left_pedals or not right_pedals:
        raise RuntimeError("Could not classify the imported left/right pedal geometry")
    join_meshes(left_pedals, "LeftPedal", pivots["CrankPivot"])
    join_meshes(right_pedals, "RightPedal", pivots["CrankPivot"])

    # Move the remaining bike assemblies into the source bike's stable frame.
    static = [o for o in list(bpy.data.objects)
              if o.type == "MESH" and o.name not in
              {"RoadBikeFrontWheelAssembly", "RoadBikeRearWheelAssembly",
               "LeftCrank", "RightCrank", "LeftPedal", "RightPedal",
               "RoadBikeRotatingDrive"}
              and o != bike and (o in meshes or o.name in {"Crank_axle", "Crank_bolts"})]
    # The source frame stays grouped under its established semantic name,
    # allowing current material variation to remain a runtime presentation.
    frame_names = {"Frame1", "Frame2", "Frame3", "welds"}
    cockpit_names = {"Handlebars.001", "Brakes", "Lines.001", "Front_spokes.001", "Steam", "Cap",
                     "Stem_bolts", "Stem_bolts2"}
    saddle_names = {"Seat", "Seatclamp", "Seatclamp_bolts", "Seatpost.001"}
    frame_parts = [o for o in static if o.name in frame_names]
    cockpit_parts = [o for o in static if o.name in cockpit_names]
    saddle_parts = [o for o in static if o.name in saddle_names]
    for o in frame_parts:
        static.remove(o)
    for o in cockpit_parts + saddle_parts:
        static.remove(o)
    frame = join_meshes(frame_parts, "BikeFrame", bike)
    join_meshes(cockpit_parts, "RoadBikeCockpit", bike)
    join_meshes(saddle_parts, "RoadBikeSaddle", bike)
    # Keep the imported source material finish. CyclistVisual's old palette
    # override was for the generated prototype and would erase this material.
    join_meshes(static, "RoadBikeStatic", bike)

    # Remove now-empty import pivots; retain them as Wodra's runtime contract.
    for name, value in pivots.items():
        value.empty_display_type = "PLAIN_AXES"
        value.empty_display_size = 0.025

    # Contacts reference the actual palm shelf plane and asset contact geometry.
    # The imported brifter supports each palm near the highest forward shelf.
    for name in ("Contact_LeftHood", "Contact_RightHood", "Contact_Saddle",
                 "Contact_LeftPedal", "Contact_RightPedal"):
        old = bpy.data.objects.get(name)
        if old:
            bpy.data.objects.remove(old, do_unlink=True)
    contact("Contact_LeftHood", bike, (-0.222, 0.502, 0.948), (0.0, -0.08, 1.0))
    contact("Contact_RightHood", bike, (0.222, 0.502, 0.948), (0.0, -0.08, 1.0))
    contact("Contact_Saddle", bike, (0.0, -0.285, 0.978))
    contact("Contact_LeftPedal", pivots["CrankPivot"], (-0.128, -0.164, 0.0))
    contact("Contact_RightPedal", pivots["CrankPivot"], (0.128, 0.164, 0.0))

    # Source asset views and game should not inherit its missing HDRI path.
    world = bpy.context.scene.world
    if world:
        world.use_nodes = True
        background = world.node_tree.nodes.get("Background")
        if background:
            background.inputs["Color"].default_value = (0.045, 0.055, 0.07, 1)
            background.inputs["Strength"].default_value = 0.35
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    triangles = 0
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH":
            obj.data.calc_loop_triangles()
            triangles += len(obj.data.loop_triangles)
    print("WODRA_CGTRADER_ROAD_INTEGRATION", "triangles", triangles,
          "mesh_objects", sum(o.type == "MESH" for o in bpy.context.scene.objects),
          "source", source)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True,
                        help="External owner-supplied GigantRoad.blend")
    parser.add_argument("--rider", type=Path, required=True,
                        help="Canonical Wodra cyclist source blend")
    parser.add_argument("--output", type=Path, required=True,
                        help="Integrated editable working blend")
    args = parser.parse_args(sys_argv())
    for path in (args.source, args.rider):
        if not path.is_file():
            raise FileNotFoundError(path)
    integrate(args.source.resolve(), args.rider.resolve(), args.output.resolve())


def sys_argv() -> list[str]:
    import sys
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]


if __name__ == "__main__":
    main()
