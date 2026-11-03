"""Build the route-specific, Blender-authored Pine Hill give-way scene.

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
    (0.48, 0.46, 0.40), (0.54, 0.52, 0.46), (0.60, 0.57, 0.50),
    (0.51, 0.50, 0.46), (0.64, 0.61, 0.54), (0.43, 0.43, 0.39)])]
wood_dark = material("Fence rails and posts", (0.35, 0.34, 0.30))
bark = [material("Bark umber", (0.25, 0.17, 0.11)), material("Bark warm grey", (0.34, 0.25, 0.17))]
leaf_mats = [material(f"Foliage {i}", c) for i, c in enumerate([
    (0.14, 0.27, 0.16), (0.19, 0.34, 0.20), (0.24, 0.39, 0.23),
    (0.29, 0.43, 0.25), (0.17, 0.31, 0.22)])]
grass = [material("Lawn deep green", (0.25, 0.37, 0.19)), material("Lawn light", (0.34, 0.45, 0.24))]
tactile_yellow = material("Tactile kerb pad yellow", (0.88, 0.62, 0.10))
metal = material("Galvanised sign post", (0.53, 0.56, 0.55), 0.55)
red = material("NZ sign red", (0.74, 0.06, 0.045), 0.55)
white = material("Sign enamel ivory", (0.91, 0.89, 0.82), 0.6)
letter = material("Sign legend charcoal", (0.08, 0.09, 0.09), 0.6)

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
    # Road-facing end and hip roof; low NZ suburban bungalow rather than a footprint extrusion.
    x0, x1, y0, y1 = -6.7, -.7, -22.4, -15.4
    eave, ridge = 3.0, 5.0
    mesh_obj("Pine bungalow weatherboard shell", [
        (x0,y0,0),(x1,y0,0),(x1,y1,0),(x0,y1,0),
        (x0,y0,eave),(x1,y0,eave),(x1,y1,eave),(x0,y1,eave)],
        [(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)], cladding)
    # Hipped roof with short ridge along the street; four sloping planes.
    yf, yb = y1+.45, y0-.45
    rf, rb = y1-1.35, y0+1.35
    verts=[(x0-.65,yf,eave),(x1+.65,yf,eave),(x1+.65,yb,eave),(x0-.65,yb,eave),
           (-3.7,rf,ridge),( -3.7,rb,ridge)]
    mesh_obj("Hipped tiled roof",verts,[(0,1,4),(1,2,5,4),(2,3,5),(3,0,4,5)],roof_base)
    # Parallel tile courses follow each roof slope, with restrained variation and eave fascia.
    for row in range(1,15):
        t=row/15
        yy=yf+(rf-yf)*t
        z=eave+(ridge-eave)*t+.018
        course_width=7.3-4.3*t
        box(f"Roof tile course {row}",(course_width,.026,.028),(-3.7,yy,z),roof_alt,.008)
    box("Front eave fascia", (7.3,.17,.22),(-3.7,yf,eave-.04),trim,.025)
    box("Back eave fascia", (7.3,.17,.22),(-3.7,yb,eave-.04),trim,.025)
    # Front wall faces the dashcam (+Y); entry recess and two framed windows.
    front_y=y1-.055
    for wx in (-5.55,-1.85):
        box(f"Window surround {wx}",(1.3,.13,1.4),(wx,front_y,1.88),trim,.035)
        box(f"Window glass {wx}",(1.02,.055,1.11),(wx,front_y+.08,1.9),window,.02)
        box(f"Window mullion vertical {wx}",(.055,.07,1.12),(wx,front_y+.12,1.9),trim)
        box(f"Window mullion horizontal {wx}",(1.02,.07,.055),(wx,front_y+.12,1.9),trim)
    box("Front entry door",(.96,.15,2.12),(-3.7,front_y,1.12),wood_dark,.04)
    box("Entry glazed panel",(.31,.035,.82),(-3.7,front_y-.09,1.55),window,.015)
    box("Entry porch lintel",(1.75,.22,.16),(-3.7,front_y+.1,2.35),trim,.025)
    # Fine horizontal weatherboard courses are actual narrow mesh strips.
    for z in [0.55+i*.22 for i in range(11)]:
        box(f"Weatherboard course {z:.2f}",(5.85,.035,.035),(-3.7,front_y+.025,z),trim,.008)
    # Small chimney and downpipe establish a recognisable roof silhouette.
    mesh_obj("Masonry chimney",[(-1.2,-19.1,4.15),(-.55,-19.1,4.15),(-.55,-18.6,4.15),(-1.2,-18.6,4.15),
                                  (-1.2,-19.1,5.4),(-.55,-19.1,5.4),(-.55,-18.6,5.4),(-1.2,-18.6,5.4)],
             [(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)],roof_alt)

def create_fence():
    # Individually weathered, irregular paling boards with three stringers and stout posts.
    y=-12.2; x_start=-15.0; total=30.0; count=182
    for i in range(count):
        x=x_start+total*i/(count-1)
        w=random.uniform(.135,.18); h=random.uniform(1.52,1.82)
        lean=random.uniform(-.018,.018)
        verts=[(x-w/2,y,h),(x+w/2,y,h*random.uniform(.97,1.02)),
               (x+w/2,y,.08),(x-w/2,y,.08),
               (x-w/2,y+.075,h),(x+w/2,y+.075,h),(x+w/2,y+.075,.08),(x-w/2,y+.075,.08)]
        obj=mesh_obj(f"Weathered paling {i:03d}",verts,[(0,1,2,3),(4,7,6,5),(0,4,5,1),(1,5,6,2),(2,6,7,3)],wood[i%len(wood)])
        obj.rotation_euler[2]=lean
    for z in (.34,1.08):
        box(f"Fence continuous rail {z}",(30.25,.12,.13),(0,y+.11,z),wood_dark,.018)
    for x in range(-15,16,2):
        box(f"Fence post {x}",(.17,.19,1.98),(x,y+.10,.92),wood_dark,.025)

def create_tree(cx, cy, height, crown_radius, seed):
    rng=random.Random(seed)
    # Tapered trunk with visible forked scaffold branches and asymmetric crown.
    lean=rng.uniform(-.28,.28)
    beam("Mature broadleaf trunk",(cx,cy,0),(cx+lean,cy-.05,height*.58),.29,bark[seed%2],10)
    branch_tips=[]
    for n in range(9):
        angle=math.tau*n/9+rng.uniform(-.22,.22)
        start_z=height*rng.uniform(.40,.64)
        length=crown_radius*rng.uniform(.62,1.02)
        tip=(cx+lean+math.cos(angle)*length,cy+math.sin(angle)*length*.72,height*rng.uniform(.70,.91))
        start=(cx+lean*.6,cy,start_z)
        beam("Forked canopy branch",start,tip,rng.uniform(.075,.14),bark[(seed+n)%2],7)
        branch_tips.append(tip)
    # Layer many deformed low-poly leaf masses over the branches, with varied scales/colors.
    for j in range(60):
        tip=branch_tips[j%len(branch_tips)]
        spread=crown_radius*rng.uniform(.03,.48)
        px=tip[0]+rng.gauss(0,spread); py=tip[1]+rng.gauss(0,spread*.72)
        pz=tip[2]+rng.gauss(0,height*.065)
        r=rng.uniform(.40,.88)
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2,radius=1,location=(px,py,pz))
        obj=bpy.context.object; obj.name="Irregular foliage cluster"
        for v in obj.data.vertices:
            noise=1+rng.uniform(-.12,.12)
            v.co.x*=r*rng.uniform(.88,1.12)*noise
            v.co.y*=r*.72*noise
            v.co.z*=r*.66*noise
        obj.data.materials.append(leaf_mats[rng.randrange(len(leaf_mats))])

def create_sign():
    # Face plane points toward approaching traffic (+Y in this route-local asset).
    x,y=-5.3,-6.6
    box("Give-way steel post",(.085,.085,2.42),(x,y,1.21),metal,.025)
    w,h=.78,.86; z=2.13
    # Extruded inverted triangle sign plate with coloured border and cream inset.
    outer=[(x-w/2,y+.08,z+h/2),(x+w/2,y+.08,z+h/2),(x,y+.08,z-h/2)]
    inner=[(x-w*.405,y+.095,z+h*.39),(x+w*.405,y+.095,z+h*.39),(x,y+.095,z-h*.36)]
    mesh_obj("Give-way red triangular sign",outer+[(a,b-.11,c) for a,b,c in outer],[(0,1,2),(3,5,4)],red)
    # After reflecting the Blender route frame, this face's outward normal is -Y,
    # toward the approaching dashcam. Keep its rear winding reversed.
    mesh_obj("Give-way enamel face",inner+[(a,b-.025,c) for a,b,c in inner],[(0,2,1),(3,4,5)],white)
    # Build crisp approach-facing letter strokes directly in the sign plane. This
    # avoids font-atlas/runtime variation and keeps the small legend readable in GLB.
    verts=[]; faces=[]
    def stroke(ax,az,bx,bz,thickness=.013):
        dx,dz=bx-ax,bz-az; length=math.hypot(dx,dz)
        px,pz=-dz/length*thickness/2,dx/length*thickness/2
        front_y=y+.15; back_y=front_y+.018
        start=len(verts)
        verts.extend([(ax+px,front_y,az+pz),(bx+px,front_y,bz+pz),(bx-px,front_y,bz-pz),(ax-px,front_y,az-pz),
                      (ax+px,back_y,az+pz),(bx+px,back_y,bz+pz),(bx-px,back_y,bz-pz),(ax-px,back_y,az-pz)])
        faces.extend([(start,start+1,start+2,start+3),(start+4,start+7,start+6,start+5),
                      (start,start+4,start+5,start+1),(start+1,start+5,start+6,start+2),
                      (start+2,start+6,start+7,start+3),(start+3,start+7,start+4,start)])
    glyphs={
        "G":list("abcdefg"), "I":list("ahd"), "V":["v1","v2"], "E":list("afged"),
        "W":["w1","w2","w3","w4"], "A":list("abgfec"), "Y":["y1","y2","y3"]
    }
    for text,center_z in (("GIVE",2.25),("WAY",2.02)):
        width,height=.105,.145; gap=.025
        total=len(text)*width+(len(text)-1)*gap; cursor=x-total/2
        top,bottom=center_z+height/2,center_z-height/2; mid=center_z
        for char in text:
            cx=cursor+width/2; left,right=cx-width/2,cx+width/2
            paths={
                "a":(left,top,right,top),"g":(left,mid,right,mid),"d":(left,bottom,right,bottom),
                "f":(left,mid,left,top),"e":(left,bottom,left,mid),"b":(right,mid,right,top),"c":(right,bottom,right,mid),
                "h":(cx,bottom,cx,top),"v1":(left,top,cx,bottom),"v2":(right,top,cx,bottom),
                "w1":(left,top,left,bottom),"w2":(left,bottom,cx,mid-.018),"w3":(cx,mid-.018,right,bottom),"w4":(right,bottom,right,top),
                "y1":(left,top,cx,mid),"y2":(right,top,cx,mid),"y3":(cx,mid,cx,bottom)
            }
            for segment in glyphs[char]:
                ax,az,bx,bz=paths[segment]; stroke(ax,az,bx,bz)
            cursor+=width+gap
    mesh_obj("Approach-facing GIVE WAY legend",verts,faces,letter)

def create_pole():
    beam("Concrete roadside power pole",(-1.55,-12.3,0),(-1.48,-12.3,8.4),.14,material("Aged concrete pole",(.57,.55,.49)),10)
    box("Power pole crossarm",(1.5,.12,.11),(-1.48,-12.3,7.78),wood_dark,.025)

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
    for i in range(19):
        x=-6.4+random.random()*2.4; y=-4.8-random.random()*3.6
        h=random.uniform(.28,.85); r=random.uniform(.18,.45)
        # Short woody garden shrubs with angular but dense leaf branches.
        for k in range(6):
            a=math.tau*k/6
            beam("Garden shrub stem",(x,y,.03),(x+math.cos(a)*r,y+math.sin(a)*r*.7,h),.025,wood_dark,5)
        for k in range(7):
            bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=r*.62,
                location=(x+random.uniform(-r,r),y+random.uniform(-r*.7,r*.7),h*random.uniform(.65,1.2)))
            obj=bpy.context.object; obj.name="Garden leaf cluster"
            obj.data.materials.append(leaf_mats[random.randrange(len(leaf_mats))])

create_house()
create_fence()
create_tree(-11,-20,10.2,3.0,10)
create_tree(-6,-21,9.4,2.7,12)
create_tree(2,-21.8,10.7,3.3,14)
create_tree(8.5,-20.5,9.8,2.9,18)
create_sign()
create_pole()
create_garden()

# Blender's +Y converts to glTF/Godot -Z. The scene was sketched with forward
# objects at negative Y, which put it behind the matched camera. Reflect the
# authored placement across the road-frame Y axis before export; bake the
# reflection into vertices so normals/facing are correct in the runtime basis.
for obj in list(bpy.context.scene.objects):
    if obj.type != 'MESH':
        continue
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
