"""Build route-specific, Blender-authored Pine Hill intersection scenery.

Run from the repository root with Blender 5.2:
  blender --background --python tools/modeling/build_pine_hill_intersection_assets.py

Scene coordinates are route-local: X is road-right, Blender -Y is route-forward,
and Z is up. The GLB is a normal Godot-imported scene; there is no Blender runtime
dependency and no external texture or downloaded asset dependency.
"""
import math
import random
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "src/IndoorCycling.Game/Assets/World/PineHill"
OUT.mkdir(parents=True, exist_ok=True)
SOURCE = ROOT / "tools/modeling/source/PineHillGiveWayIntersection.blend"
FENCE_TEXTURE = OUT / "PineHillWeatheredFence_4k.jpg"
random.seed(1372)

bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials, bpy.data.cameras, bpy.data.lights):
    for datablock in list(datablocks):
        if datablock.users == 0:
            datablocks.remove(datablock)

def material(name, color, roughness=0.86):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Roughness"].default_value = roughness
    return mat

cladding = material("Warm off-white weatherboard", (0.66, 0.61, 0.52))
trim = material("Painted ivory trim", (0.84, 0.82, 0.75))
roof_base = material("Aged charcoal concrete tiles", (0.16, 0.18, 0.19))
roof_alt = material("Tile variation", (0.21, 0.22, 0.21))
window = material("Deep blue-grey glazing", (0.11, 0.17, 0.19), 0.27)
wood = [material(f"Weathered fence board {i}", c) for i, c in enumerate([
    (0.25, 0.26, 0.25), (0.30, 0.30, 0.28), (0.34, 0.33, 0.30),
    (0.28, 0.29, 0.27), (0.36, 0.35, 0.32), (0.23, 0.25, 0.24)])]
wood_dark = material("Fence rails and posts", (0.20, 0.21, 0.20))
bark = [material("Bark umber", (0.25, 0.17, 0.11)), material("Bark warm grey", (0.34, 0.25, 0.17))]
leaf_mats = [material(f"Foliage {i}", c) for i, c in enumerate([
    (0.14, 0.27, 0.16), (0.19, 0.34, 0.20), (0.24, 0.39, 0.23),
    (0.29, 0.43, 0.25), (0.17, 0.31, 0.22)])]
grass = [material("Lawn deep green", (0.25, 0.37, 0.19)), material("Lawn light", (0.34, 0.45, 0.24))]
tactile_yellow = material("Tactile kerb pad yellow", (0.88, 0.62, 0.10))
fence_back = material("Fence back - aged timber", (0.39, 0.40, 0.38), 0.94)
fence_photo = bpy.data.materials.new("Pine Hill photographed weathered paling face")
fence_photo.use_nodes = True
fence_bsdf = fence_photo.node_tree.nodes.get("Principled BSDF")
fence_bsdf.inputs["Roughness"].default_value = 0.94
fence_image = bpy.data.images.load(str(FENCE_TEXTURE), check_existing=True)
fence_image.pack()
fence_tex = fence_photo.node_tree.nodes.new("ShaderNodeTexImage")
fence_tex.image = fence_image
fence_photo.node_tree.links.new(fence_tex.outputs["Color"], fence_bsdf.inputs["Base Color"])

def mesh_obj(name, verts, faces, mat, loc=(0, 0, 0)):
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(verts, [], faces)
    mesh.materials.append(mat)
    mesh.validate(); mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.location = loc
    return obj

def box(name, dims, loc, mat, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = bpy.context.object; obj.name = name
    obj.dimensions = dims
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    obj.data.materials.append(mat)
    if bevel:
        bevel_mod = obj.modifiers.new("Soft timber edges", "BEVEL")
        bevel_mod.width = bevel; bevel_mod.segments = 2
        obj.modifiers.new("Weighted corner normals", "WEIGHTED_NORMAL")
    return obj

def beam(name, a, b, radius, mat, sides=7):
    a, b = Vector(a), Vector(b)
    delta = b-a
    bpy.ops.mesh.primitive_cone_add(vertices=sides, radius1=radius, radius2=radius*.72,
                                    depth=delta.length, location=(a+b)/2)
    obj=bpy.context.object; obj.name=name
    obj.rotation_mode='QUATERNION'; obj.rotation_quaternion=delta.to_track_quat('Z','Y')
    obj.data.materials.append(mat)
    return obj

def create_house():
    house_objects_before = set(bpy.context.scene.objects)
    # Road-facing end and hip roof; low NZ suburban bungalow rather than a footprint extrusion.
    x0, x1, y0, y1 = -7.7, .3, -23.0, -15.0
    eave, ridge = 3.1, 5.35
    mesh_obj("Pine bungalow weatherboard shell", [
        (x0,y0,0),(x1,y0,0),(x1,y1,0),(x0,y1,0),
        (x0,y0,eave),(x1,y0,eave),(x1,y1,eave),(x0,y1,eave)],
        [(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)], cladding)
    # Hipped roof with short ridge along the street; four sloping planes.
    yf, yb = y1+.45, y0-.45
    rf, rb = y1-1.35, y0+1.35
    center_x=(x0+x1)/2
    verts=[(x0-.65,yf,eave),(x1+.65,yf,eave),(x1+.65,yb,eave),(x0-.65,yb,eave),
           (center_x,rf,ridge),(center_x,rb,ridge)]
    mesh_obj("Hipped tiled roof",verts,[(0,1,4),(1,2,5,4),(2,3,5),(3,0,4,5)],roof_base)
    # Parallel tile courses follow each roof slope, with restrained variation and eave fascia.
    for row in range(1,15):
        t=row/15
        yy=yf+(rf-yf)*t
        z=eave+(ridge-eave)*t+.018
        course_width=(x1-x0+1.3)-4.3*t
        box(f"Roof tile course {row}",(course_width,.026,.028),(center_x,yy,z),roof_alt,.008)
    box("Front eave fascia", (x1-x0+1.3,.17,.22),(center_x,yf,eave-.04),trim,.025)
    box("Back eave fascia", (x1-x0+1.3,.17,.22),(center_x,yb,eave-.04),trim,.025)
    # Front wall faces the dashcam (+Y); entry recess and two framed windows.
    front_y=y1-.055
    for wx in (-2.55,-.35):
        box(f"Window surround {wx}",(1.3,.13,1.4),(wx,front_y,1.88),trim,.035)
        box(f"Window glass {wx}",(1.02,.055,1.11),(wx,front_y+.08,1.9),window,.02)
        box(f"Window mullion vertical {wx}",(.055,.07,1.12),(wx,front_y+.12,1.9),trim)
        box(f"Window mullion horizontal {wx}",(1.02,.07,.055),(wx,front_y+.12,1.9),trim)
    box("Front entry door",(.96,.15,2.12),(-4.2,front_y,1.12),wood_dark,.04)
    box("Entry glazed panel",(.31,.035,.82),(-4.2,front_y-.09,1.55),window,.015)
    box("Entry porch lintel",(1.75,.22,.16),(-4.2,front_y+.1,2.35),trim,.025)
    # Attached two-bay garage door gives the frontage a recognisable suburban scale.
    # It sits behind the observed fence line and is an explicitly modelled approximation.
    garage_center=x0+1.45
    box("Attached garage opening surround",(2.8,.18,2.35),(garage_center,front_y,1.25),trim,.05)
    box("Garage door recessed panels",(2.48,.07,2.08),(garage_center,front_y-.11,1.16),material("Muted sage garage door",(.39,.45,.39)),.035)
    for panel_z in (.52,.96,1.40,1.78):
        box(f"Garage door horizontal reveal {panel_z}",(2.38,.035,.028),(garage_center,front_y-.16,panel_z),trim,.008)
    for panel_x in (garage_center-.72,garage_center,garage_center+.72):
        box(f"Garage door panel stile {panel_x}",(.026,.035,1.90),(panel_x,front_y-.16,1.14),trim,.008)
    # Fine horizontal weatherboard courses are actual narrow mesh strips.
    for z in [0.48+i*.22 for i in range(12)]:
        box(f"Weatherboard course {z:.2f}",(7.75,.035,.035),(center_x,front_y+.025,z),trim,.008)
    # Small chimney and downpipe establish a recognisable roof silhouette.
    mesh_obj("Masonry chimney",[(-1.2,-19.1,4.15),(-.55,-19.1,4.15),(-.55,-18.6,4.15),(-1.2,-18.6,4.15),
                                  (-1.2,-19.1,5.4),(-.55,-19.1,5.4),(-.55,-18.6,5.4),(-1.2,-18.6,5.4)],
             [(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)],roof_alt)
    # The identifiable hipped roof occupies substantially more of the source
    # frame than the mapped footprint's generic extrusion suggests. This explicit
    # video-guided model is enlarged and shifted toward the observed left-side plot;
    # footprint relation and facade dimensions remain approximate.
    group=bpy.data.objects.new("Video-informed Pine Hill bungalow placement",None)
    bpy.context.collection.objects.link(group); group.location.x=-8.0; group.scale.x=1.5
    for obj in set(bpy.context.scene.objects)-house_objects_before-{group}:
        obj.parent=group

def create_fence():
    # Individually modelled boards retain the rectified 4K source texture on each
    # front face. This is one mesh/object, not a camera-facing photographic panel.
    # 34 m is an image-perspective estimate; the OSM parcel/street edge constrains
    # its orientation, while exact surveyed fence endpoints are unavailable.
    y=-12.16; x0=-17.0; x1=17.0; pitch=.145; board_width=.133
    verts=[]; faces=[]; face_material=[]; face_uv=[]
    count=round((x1-x0)/pitch)
    for i in range(count):
        cx=x0+i*pitch+board_width/2
        u0=i/count; u1=(i+1)/count
        height=1.72 + .08*math.sin(i*1.71) + .04*math.sin(i*.43)
        zlo=.08; zhi=height
        # Front, back, sides and top of a tapered paling. Small spacing and varied
        # tops expose true board edges in oblique gameplay views.
        k=len(verts)
        verts.extend([(cx-board_width/2,y,zlo),(cx+board_width/2,y,zlo),
                      (cx+board_width/2-.012,y,zhi),(cx-board_width/2+.006,y,zhi),
                      (cx-board_width/2,y-.09,zlo),(cx+board_width/2,y-.09,zlo),
                      (cx+board_width/2-.012,y-.09,zhi),(cx-board_width/2+.006,y-.09,zhi)])
        faces.extend([(k,k+1,k+2,k+3),(k+7,k+6,k+5,k+4),
                      (k,k+4,k+5,k+1),(k+1,k+5,k+6,k+2),
                      (k+2,k+6,k+7,k+3),(k+3,k+7,k+4,k)])
        face_material.extend([0,1,1,1,1,1])
        face_uv.extend([[(u0,0),(u1,0),(u1,1),(u0,1)],
                        [(0,0),(0,1),(1,1),(1,0)],
                        [(0,0)]*4,[(0,0)]*4,[(0,0)]*4,[(0,0)]*4])
    mesh=bpy.data.meshes.new("Individually modelled photo-textured timber palings")
    mesh.from_pydata(verts,[],faces); mesh.materials.append(fence_photo); mesh.materials.append(fence_back)
    for p,mi in zip(mesh.polygons,face_material): p.material_index=mi
    uv=mesh.uv_layers.new(name="Rectified source detail by paling")
    for p,coords in zip(mesh.polygons,face_uv):
        for li,coord in zip(p.loop_indices,coords): uv.data[li].uv=coord
    fence=bpy.data.objects.new("Pine Hill 34m individually boarded timber fence",mesh)
    bpy.context.collection.objects.link(fence)
    # Supporting structure is behind the photographed fronts and fully 3D.
    for z in (.37,1.30):
        box(f"Fence continuous timber rail {z}",(34.2,.13,.14),(0,y-.17,z),wood_dark,.025)
    for x in [x0+i*1.8 for i in range(20)]:
        box(f"Fence timber post {x:.1f}",(.14,.17,1.96),(x,y-.20,.95),wood_dark,.022)
    box("Fence weathered top cap",(34.2,.16,.085),(0,y-.12,1.86),wood_dark,.02)
    # The source has one small vine patch on the boards. Rebuild it as leaves and a
    # stem on the fence surface so it remains a spatial object at oblique viewpoints.
    vine_x=4.1; vine_y=y+.075
    beam("Fence vine stem",(vine_x,vine_y,.84),(vine_x+.06,vine_y,1.43),.012,bark[1],5)
    for i in range(9):
        z=.94+i*.055
        side=-1 if i%2 else 1
        x=vine_x+side*(.10+(i%3)*.035)
        length=.17+(i%4)*.035
        verts=[(vine_x,vine_y,z),(x-.045,vine_y-.018,z+length*.42),
               (x,vine_y-.045,z+length),(x+.045,vine_y-.018,z+length*.42),
               (vine_x,vine_y-.045,z)]
        mesh_obj(f"Fence vine leaf {i}",verts,[(0,1,2,3,4),(4,3,2,1,0)],leaf_mats[i%len(leaf_mats)])

def create_pole():
    beam("Concrete roadside power pole",(-3.5,-12.3,0),(-3.42,-12.3,8.4),.14,material("Aged concrete pole",(.57,.55,.49)),10)
    box("Power pole crossarm",(1.5,.12,.11),(-3.42,-12.3,7.78),wood_dark,.025)

def create_garden():
    # Gently irregular planting banks and broad leaf shapes at the kerb return.
    mesh_obj("Grass berm near kerb",[(-6.2,-4.2,0),(-3.9,-4.1,.02),(-3.5,-8.4,.04),(-5.8,-9.1,.03),
                                    (-7.0,-7.2,.02)],[(0,1,2,3,4)],grass[0])
    box("Tactile kerb-ramp pad",(.86,.72,.035),(-6.05,-3.42,.105),tactile_yellow,.025)
    for i in range(7):
        for j in range(5):
            bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=.038,
                location=(-6.36+i*.103,-3.69+j*.135,.136))
            obj=bpy.context.object; obj.name="Tactile raised dot"
            obj.scale.z=.55; obj.data.materials.append(tactile_yellow)
    blade_mats = [material(f"Ornamental grass blade {i}", c) for i, c in enumerate([
        (0.24, 0.36, 0.16), (0.32, 0.43, 0.20), (0.39, 0.47, 0.24), (0.20, 0.31, 0.16)])]
    # The footage has a small tufted planting bed at the kerb. Build bent,
    # tapered blades with varied lean instead of the former row of ball shrubs.
    for clump in range(13):
        x=-6.3+random.random()*2.0; y=-4.9-random.random()*3.0
        for blade in range(11):
            angle=math.tau*blade/11+random.uniform(-.2,.2)
            height=random.uniform(.38,.92)
            lean=random.uniform(.12,.48)
            dx=math.cos(angle)*lean; dy=math.sin(angle)*lean*.65
            half=random.uniform(.018,.042)
            px=-math.sin(angle)*half; py=math.cos(angle)*half*.65
            verts=[(x-px,y-py,.06),(x+px,y+py,.06),
                   (x+dx*.42+px*.45,y+dy*.42+py*.45,height*.62),
                   (x+dx*.42-px*.45,y+dy*.42-py*.45,height*.62),
                   (x+dx,y+dy,height)]
            mesh_obj("Tapered ornamental grass leaf",verts,[(0,1,2,3),(2,4,3)],
                     blade_mats[random.randrange(len(blade_mats))])

house_start = set(bpy.context.scene.objects)
create_house()
# Source framing shows only a partial bungalow roof beyond the fence, left of the pole.
# Move the full authored house behind the fence and left in the road-local composition.
for obj in set(bpy.context.scene.objects) - house_start:
    obj.location.x -= 4.0
    obj.location.y -= 15.0
create_fence()
create_pole()
create_garden()

# Blender's +Y converts to glTF/Godot -Z. The scene was sketched with forward
# objects at negative Y, which put it behind the matched camera. Reflect the
# authored placement across the road-frame Y axis before export; bake the
# reflection into vertices so normals/facing are correct in the runtime basis.
for obj in list(bpy.context.scene.objects):
    if obj.type != 'MESH':
        continue
    rotation_is_identity = (obj.rotation_mode == 'QUATERNION'
        and abs(obj.rotation_quaternion.w - 1.0) < 1e-7
        and abs(obj.rotation_quaternion.x) < 1e-7
        and abs(obj.rotation_quaternion.y) < 1e-7
        and abs(obj.rotation_quaternion.z) < 1e-7) or (
        obj.rotation_mode != 'QUATERNION' and all(abs(axis) < 1e-7 for axis in obj.rotation_euler))
    scale_is_identity = all(abs(axis - 1.0) < 1e-7 for axis in obj.scale)
    if not (rotation_is_identity and scale_is_identity):
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True); bpy.context.view_layer.objects.active=obj
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        obj.select_set(False)
    for vertex in obj.data.vertices:
        vertex.co.y = -vertex.co.y
    obj.location.y = -obj.location.y

# Convert legend text and combine static geometry by material before export. This keeps
# the route-local asset to a small set of mesh instances while retaining material groups.
for obj in list(bpy.context.scene.objects):
    if obj.type == 'FONT':
        bpy.context.view_layer.objects.active=obj
        obj.select_set(True)
        bpy.ops.object.convert(target='MESH')
        bpy.context.object.select_set(False)
by_material={}
for obj in bpy.context.scene.objects:
    if obj.type == 'MESH':
        key=tuple(sorted(mat.name for mat in obj.data.materials if mat))
        by_material.setdefault(key,[]).append(obj)
for key, objects in by_material.items():
    if len(objects)<2: continue
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects: obj.select_set(True)
    bpy.context.view_layer.objects.active=objects[0]
    bpy.ops.object.join()
    bpy.context.object.name='PineHillAsset_'+'_'.join(key)
    if key and (key[0].startswith("Foliage") or key[0].startswith("Bark")):
        bpy.ops.object.shade_smooth()

# Group everything under a predictable route-local root for GLB users and DCC inspection.
root=bpy.data.objects.new("PineHillGiveWayIntersection",None); bpy.context.collection.objects.link(root)
for obj in list(bpy.context.scene.objects):
    if obj is not root:
        obj.parent=root
root.location=(0,0,0)

bpy.ops.object.select_all(action='DESELECT')
for obj in bpy.context.scene.objects:
    if obj is root or obj.parent is root:
        obj.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE))
bpy.ops.export_scene.gltf(filepath=str(OUT/"PineHillGiveWayIntersection.glb"),export_format='GLB',
    export_apply=True,export_cameras=False,export_lights=False,export_materials='EXPORT',
    export_yup=True,export_image_format='AUTO',use_selection=True)
print(f"Exported authored Pine Hill intersection assets to {OUT}")
