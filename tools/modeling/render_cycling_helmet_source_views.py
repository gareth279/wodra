"""Render unmodified owner-supplied helmet source views with Blender.

Example (Blender 5.2):
  blender --background --python tools/modeling/render_cycling_helmet_source_views.py -- \
    --source-zip C:/Users/garet/OneDrive/wodra_bikes/sport_cycling_helmet.zip \
    --output-dir artifacts/issue164-helmet-orientation-source

The OBJ is parsed into Blender without applying an axis conversion or moving
vertices. Cameras alone frame the raw coordinates. The front/rear labels follow
the visibly inspected brow/vent end and occipital retention/tail end, not an
importer setting or a production transform.
"""

import argparse
import os
import sys
import tempfile
import zipfile
from pathlib import Path

import bpy
from mathutils import Vector


ROOT = Path(__file__).resolve().parents[2]
OBJ_NAME = "sport_cycling_helmet_main.obj"
TEXTURE_NAME = "sport_cycling_helmet_diffuse.png"


def blender_args():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source-zip",
        default=os.environ.get("WODRA_HELMET_SOURCE_ZIP"),
        help="authoritative owner ZIP (or set WODRA_HELMET_SOURCE_ZIP)",
    )
    parser.add_argument(
        "--output-dir",
        default=str(ROOT / "artifacts" / "issue164-helmet-orientation-source"),
    )
    parsed = parser.parse_args(args)
    if not parsed.source_zip:
        parser.error("--source-zip or WODRA_HELMET_SOURCE_ZIP is required")
    return parsed


def extract_owner_inputs(archive_path, destination):
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        obj_member = next((name for name in names if name.endswith(OBJ_NAME)), None)
        texture_member = next((name for name in names if name.endswith(TEXTURE_NAME)), None)
        mtl_member = next((name for name in names if name.lower().endswith(".mtl")), None)
        if not obj_member or not texture_member or not mtl_member:
            raise RuntimeError("owner ZIP must contain the original OBJ, MTL, and diffuse texture")
        extracted = {}
        for label, member in (("obj", obj_member), ("mtl", mtl_member), ("texture", texture_member)):
            target = destination / Path(member).name
            target.write_bytes(archive.read(member))
            extracted[label] = target
    return extracted


def parse_raw_obj(path):
    positions, texcoords, faces, face_texcoords = [], [], [], []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        fields = line.strip().split()
        if not fields:
            continue
        if fields[0] == "v":
            positions.append(tuple(map(float, fields[1:4])))
        elif fields[0] == "vt":
            texcoords.append(tuple(map(float, fields[1:3])))
        elif fields[0] == "f":
            face, face_uv = [], []
            for token in fields[1:]:
                parts = token.split("/")
                face.append(int(parts[0]) - 1)
                face_uv.append(int(parts[1]) - 1 if len(parts) > 1 and parts[1] else None)
            faces.append(face)
            face_texcoords.append(face_uv)
    if not positions or not faces:
        raise RuntimeError(f"no vertices or faces parsed from {path}")
    return positions, texcoords, faces, face_texcoords


def setup_material(mesh, texture_path):
    material = bpy.data.materials.new("Owner diffuse texture | raw source")
    material.use_nodes = True
    nodes = material.node_tree.nodes
    shader = nodes.get("Principled BSDF")
    shader.inputs["Roughness"].default_value = 0.34
    shader.inputs["Metallic"].default_value = 0.06
    image = bpy.data.images.load(str(texture_path), check_existing=True)
    image.pack()
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    material.node_tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])
    mesh.materials.append(material)


def setup_scene(output_dir, positions, faces, texcoords, face_texcoords, texture_path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mesh = bpy.data.meshes.new("OwnerSuppliedOBJ_UnmodifiedCoordinates")
    mesh.from_pydata(positions, [], faces)
    mesh.update()
    obj = bpy.data.objects.new("OwnerOriginalOBJ_NoAxisTransform", mesh)
    bpy.context.collection.objects.link(obj)
    uv_layer = mesh.uv_layers.new(name="UVMap")
    for polygon, uv_indices in zip(mesh.polygons, face_texcoords):
        polygon.use_smooth = True
        for loop_index, uv_index in zip(polygon.loop_indices, uv_indices):
            if uv_index is not None:
                uv_layer.data[loop_index].uv = texcoords[uv_index]
    setup_material(mesh, texture_path)

    minimum = Vector(tuple(min(vertex[axis] for vertex in positions) for axis in range(3)))
    maximum = Vector(tuple(max(vertex[axis] for vertex in positions) for axis in range(3)))
    center = (minimum + maximum) * 0.5
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.eevee.taa_render_samples = 64
    scene.render.resolution_x = 1400
    scene.render.resolution_y = 900
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast"
    scene.world = bpy.data.worlds.new("Neutral studio background")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.18, 0.18, 0.18, 1)
    scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.32

    def area(name, location, power, size, color):
        data = bpy.data.lights.new(name, "AREA")
        data.energy, data.shape, data.size, data.color = power, "DISK", size, color
        light = bpy.data.objects.new(name, data)
        scene.collection.objects.link(light)
        light.location = location
        light.rotation_euler = (Vector(center) - light.location).to_track_quat("-Z", "Y").to_euler()

    area("Key", (22, -25, 34), 12000, 18, (1.0, 0.88, 0.76))
    area("Fill", (-25, -4, 20), 8000, 20, (0.63, 0.78, 1.0))
    area("Rim", (4, 28, 27), 12000, 16, (0.77, 0.69, 1.0))

    label_material = bpy.data.materials.new("View label")
    label_material.use_nodes = True
    label_shader = label_material.node_tree.nodes.get("Principled BSDF")
    label_shader.inputs["Base Color"].default_value = (1, 1, 1, 1)
    label_shader.inputs["Emission Color"].default_value = (1, 1, 1, 1)
    label_shader.inputs["Emission Strength"].default_value = 1.0

    views = (
        ("raw-side-from-minus-x", (-45, center.y, center.z), "SIDE VIEW -X | visible short brow/front is right; long tail is rear"),
        ("raw-side-from-plus-x", (45, center.y, center.z), "SIDE VIEW +X | visible short brow/front is left; long tail is rear"),
        ("raw-front-from-minus-y", (center.x, -45, center.z), "FRONT | short brow edge and frontal vent layout"),
        ("raw-rear-from-plus-y", (center.x, 45, center.z), "REAR | occipital coverage and retention shape"),
        ("raw-above", (center.x, center.y, 45), "ABOVE | front is the short brow end; tail is rear"),
    )
    for name, location, label in views:
        camera_data = bpy.data.cameras.new(f"Camera_{name}")
        camera_data.type = "ORTHO"
        camera_data.ortho_scale = 48
        camera = bpy.data.objects.new(f"Camera_{name}", camera_data)
        scene.collection.objects.link(camera)
        camera.location = location
        camera.rotation_euler = (Vector(center) - camera.location).to_track_quat("-Z", "Y").to_euler()
        scene.camera = camera

        text_data = bpy.data.curves.new(f"Label_{name}", "FONT")
        text_data.body = label
        text_data.size = 0.8
        text_data.materials.append(label_material)
        text = bpy.data.objects.new(f"Label_{name}", text_data)
        scene.collection.objects.link(text)
        text.parent = camera
        text.location = (-22.5, 13.7, -8.0)
        text.rotation_euler = (0, 0, 0)

        scene.render.filepath = str(output_dir / f"{name}.png")
        bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(text, do_unlink=True)
        bpy.data.objects.remove(camera, do_unlink=True)
        bpy.data.cameras.remove(camera_data)

    return minimum, maximum, len(positions), len(faces)


def main():
    args = blender_args()
    source_zip = Path(args.source_zip).resolve()
    output_dir = Path(args.output_dir).resolve()
    if not source_zip.is_file():
        raise FileNotFoundError(source_zip)
    output_dir.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="wodra-helmet-raw-") as temporary:
        inputs = extract_owner_inputs(source_zip, Path(temporary))
        positions, texcoords, faces, face_texcoords = parse_raw_obj(inputs["obj"])
        minimum, maximum, vertex_count, face_count = setup_scene(
            output_dir, positions, faces, texcoords, face_texcoords, inputs["texture"]
        )
        print("OWNER_ZIP", source_zip)
        print("RAW_OBJ_NO_VERTEX_TRANSFORM", inputs["obj"])
        print("OWNER_MTL_PRESENT", inputs["mtl"])
        print("RAW_BOUNDS", tuple(minimum), tuple(maximum))
        print("RAW_COUNTS", vertex_count, "vertices", face_count, "faces")
        print("PHYSICAL_FRONT", "short brow/forehead lip and frontal vents; visually identified as -Y")
        print("PHYSICAL_REAR", "long occipital tail and rear retention coverage; visually identified as +Y")
        print("RENDERED_TO", output_dir)


if __name__ == "__main__":
    main()
