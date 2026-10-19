"""Focused, repeatable helmet and finish pass on an existing cyclist blend.

Usage:
  blender --background source/WodraCyclist.blend --python tools/modeling/polish_rider.py -- <output.glb> <output.blend> <preview-dir>

This intentionally does not regenerate the MPFB human, pose, or pedal action.
"""
from __future__ import annotations
import math
import sys
from pathlib import Path
import bpy
from mathutils import Matrix, Vector


def mat(name, color, roughness=.72, metallic=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (*color, 1)
    m.use_nodes = True
    p = m.node_tree.nodes.get("Principled BSDF")
    p.inputs["Base Color"].default_value = (*color, 1)
    p.inputs["Roughness"].default_value = roughness
    p.inputs["Metallic"].default_value = metallic
    return m


def mesh_obj(name, verts, faces, material, rig, bone="head", smooth=True):
    # Input vertices are evaluated world positions. Convert them through the
    # current pose into the selected bone's rest space, exactly as the shoe
    # shells do, so the skinned accessory starts fitted and follows animation.
    pose_bone = rig.pose.bones[bone]
    world_to_rig = rig.matrix_world.inverted()
    pose_to_rest = rig.data.bones[bone].matrix_local @ pose_bone.matrix.inverted()
    verts = [tuple(pose_to_rest @ (world_to_rig @ Vector(v))) for v in verts]
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(verts, [], faces)
    mesh.materials.append(material)
    for f in mesh.polygons:
        f.use_smooth = smooth
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = rig
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.vertex_groups.new(name=bone).add(list(range(len(verts))), 1, "REPLACE")
    mod = obj.modifiers.new("Head skin", "ARMATURE")
    mod.object = rig
    return obj


def sphere_mesh(name, center, scale, material, rig, segments=16, rings=8, bone="head"):
    verts, faces = [], []
    for j in range(rings + 1):
        phi = math.pi * j / rings
        for i in range(segments):
            theta = math.tau * i / segments
            verts.append((center[0] + scale[0] * math.sin(phi) * math.cos(theta),
                          center[1] + scale[1] * math.sin(phi) * math.sin(theta),
                          center[2] + scale[2] * math.cos(phi)))
    for j in range(rings):
        for i in range(segments):
            a = j * segments + i
            b = j * segments + (i + 1) % segments
            faces.append((a, b, b + segments, a + segments))
    return mesh_obj(name, verts, faces, material, rig, bone)


def tube(name, points, radius, material, rig, bone="head", sides=8):
    verts, faces = [], []
    points = [Vector(p) for p in points]
    for k, p in enumerate(points):
        tangent = (points[min(k + 1, len(points)-1)] - points[max(0, k-1)]).normalized()
        axis = tangent.cross(Vector((1, 0, 0))).normalized()
        if axis.length < .1:
            axis = tangent.cross(Vector((0, 0, 1))).normalized()
        other = tangent.cross(axis).normalized()
        for i in range(sides):
            q = p + radius * (axis * math.cos(math.tau*i/sides) + other * math.sin(math.tau*i/sides))
            verts.append(tuple(q))
    for k in range(len(points)-1):
        for i in range(sides):
            a, b = k*sides+i, k*sides+(i+1)%sides
            faces.append((a, b, b+sides, a+sides))
    return mesh_obj(name, verts, faces, material, rig, bone)


def make_helmet(rig):
    # Head-local basis is derived from the actual evaluated head bone; source
    # model faces +Y and the original rider object is rotated in the scene.
    old = [o for o in bpy.data.objects if o.name.startswith(("CyclingHelmet", "HelmetBrim", "HelmetStrap", "HelmetVent"))]
    for obj in old:
        bpy.data.objects.remove(obj, do_unlink=True)
    # Geometry in armature rest coordinates matches the existing fitted skull:
    # shell sits close to the crown, widens to cover temples, and tapers rearward.
    shell_mat = mat("Road Helmet | neutral warm white", (.72, .75, .74), .33)
    dark = mat("Road Helmet | vents and straps", (.035, .045, .05), .64)
    trim = mat("Road Helmet | edge trim", (.38, .41, .40), .5)
    # A continuous low-profile shell follows the skull. The open underside is
    # elliptical; the rear extends farther down over the occiput, while the
    # front edge rises above the brow. Dense latitude rings avoid the old
    # stacked-disc/oversized-hat silhouette.
    n=64; rings=24; verts=[]; faces=[]
    for j in range(rings):
        t=j/(rings-1)  # 0 crown to 1 lower rim
        radial=max(.018, t)
        zcrown=1.795
        # Rounded dome: an ellipsoidal section gives a low, curved crown rather
        # than a flat-topped stack or a straight conical slope.
        drop=.105*(1.0-math.sqrt(max(0.0,1.0-t*t)))
        for i in range(n):
            a=math.tau*i/n
            front=max(0.0,math.sin(a))
            rear=max(0.0,-math.sin(a))
            x=.101*radial*math.cos(a)
            # Match the weighted head centre (the source's head extends
            # farther forward than the neck/armature origin).
            y=.0415 + .116*radial*math.sin(a)
            z=zcrown-drop + .018*front*(t**2) - .006*rear*(t**2)
            verts.append((x,y,z))
    for j in range(rings-1):
        for i in range(n):
            a=j*n+i; b=j*n+(i+1)%n
            faces.append((a,b,b+n,a+n))
    # Close crown only; underside remains open for natural skull clearance.
    faces.append(tuple((rings-1)*n+i for i in range(n)))
    shell=mesh_obj("CyclingHelmet", verts, faces, shell_mat, rig)
    shell["optional_accessory"] = True
    for poly in shell.data.polygons:
        poly.use_smooth=True
    # Distinct narrow rim follows the shell lower edge.
    ring=[]
    for i in range(65):
        a=math.tau*i/64
        front=max(0.0,math.sin(a)); rear=max(0.0,-math.sin(a))
        ring.append((.101*math.cos(a),.0415+.116*math.sin(a),1.795-.105+.018*front-.006*rear))
    tube("HelmetRim", ring, .003, trim, rig, sides=8)
    # Six flat dark vent slots conform to the upper shell instead of floating
    # spheres. Their vertices follow the same ellipsoidal surface equation.
    idx=0
    for x in (-.035,.035):
        for y in (.020,.0415,.063):
            idx+=1; patch=[]; sides=20
            for k in range(sides):
                a=math.tau*k/sides
                px=x+.0045*math.cos(a); py=y+.0105*math.sin(a)
                t2=(px/.101)**2+((py-.0415)/.116)**2
                t=math.sqrt(min(.98,t2)); angle=math.atan2((py-.0415)/.116,px/.101)
                front=max(0.0,math.sin(angle)); rear=max(0.0,-math.sin(angle))
                pz=1.795-.105*(1.0-math.sqrt(max(0.0,1.0-t*t)))+.018*front*t*t-.006*rear*t*t+.0006
                patch.append((px,py,pz))
            vent=mesh_obj(f"HelmetVent{idx}",patch,[tuple(range(sides))],dark,rig,smooth=True)
            vent["optional_accessory"] = True
    # Two continuous straps pass in front of each ear and meet under the chin.
    for side in (-1, 1):
        pts=[(side*.084,.035,1.715),(side*.092,.004,1.675),
             (side*.078,.025,1.615),(side*.050,.065,1.565),(0,.090,1.550)]
        obj=tube("HelmetStrapL" if side<0 else "HelmetStrapR",pts,.004,dark,rig,sides=8)
        obj["optional_accessory"] = True


def round_clothing_edges():
    human=bpy.data.objects.get("Human")
    if not human or not human.type == "MESH":
        return
    if len(human.data.materials) < 4:
        return
    # Blend the existing skin, jersey, shorts, and shoe colors in a shader over
    # a narrow smoothstep band. This preserves the accepted body and rig while
    # removing the jagged face-by-face hems from the prototype.
    old=list(human.data.materials)
    colors=[]
    for material in old[:4]:
        bsdf=material.node_tree.nodes.get("Principled BSDF") if material and material.use_nodes else None
        colors.append(tuple(bsdf.inputs["Base Color"].default_value) if bsdf else tuple(material.diffuse_color))
    head_group=human.vertex_groups.get("head")
    if head_group:
        attr=human.data.color_attributes.get("RiderHeadWeight") or human.data.color_attributes.new(
            name="RiderHeadWeight",type="FLOAT_COLOR",domain="POINT")
        for vertex in human.data.vertices:
            weight=next((g.weight for g in vertex.groups if g.group==head_group.index),0.0)
            attr.data[vertex.index].color=(weight,0.0,0.0,1.0)
        human.data.color_attributes.active_color=attr
    m=bpy.data.materials.get("Rider Outfit | smooth fitted transitions") or bpy.data.materials.new("Rider Outfit | smooth fitted transitions")
    m.use_nodes=True; nodes=m.node_tree.nodes; links=m.node_tree.links; nodes.clear()
    out=nodes.new("ShaderNodeOutputMaterial"); bsdf=nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value=.78; links.new(bsdf.outputs["BSDF"],out.inputs["Surface"])
    tex=nodes.new("ShaderNodeTexCoord"); sep=nodes.new("ShaderNodeSeparateXYZ"); links.new(tex.outputs["Object"],sep.inputs["Vector"])
    absx=nodes.new("ShaderNodeMath"); absx.operation="ABSOLUTE"; links.new(sep.outputs["X"],absx.inputs[0])
    head_color=nodes.new("ShaderNodeVertexColor"); head_color.layer_name="RiderHeadWeight"
    head_sep=nodes.new("ShaderNodeSeparateColor"); head_sep.mode="RGB"
    links.new(head_color.outputs["Color"],head_sep.inputs["Color"])
    def smooth(source,lo,hi):
        n=nodes.new("ShaderNodeMapRange"); n.clamp=True; n.interpolation_type="SMOOTHSTEP"
        n.inputs["From Min"].default_value=lo; n.inputs["From Max"].default_value=hi
        n.inputs["To Min"].default_value=0; n.inputs["To Max"].default_value=1
        links.new(source,n.inputs["Value"]); return n.outputs["Result"]
    def inv(socket):
        n=nodes.new("ShaderNodeMath"); n.operation="SUBTRACT"; n.inputs[0].default_value=1; links.new(socket,n.inputs[1]); return n.outputs[0]
    def mul(a,b):
        n=nodes.new("ShaderNodeMath"); n.operation="MULTIPLY"; links.new(a,n.inputs[0]); links.new(b,n.inputs[1]); return n.outputs[0]
    jersey=mul(mul(mul(smooth(sep.outputs["Z"],.955,.99),inv(smooth(sep.outputs["Z"],1.39,1.42))),
                   inv(smooth(absx.outputs[0],.26,.36))),inv(smooth(head_sep.outputs["Red"],.08,.50)))
    # The original coarse classifier extended jersey color into the lower face.
    # Preserve the crew-neck edge by tapering jersey away from the face center.
    face_center=inv(smooth(absx.outputs[0],.075,.15))
    face_upper=smooth(sep.outputs["Z"],1.29,1.34)
    jersey=mul(jersey,inv(mul(face_center,face_upper)))
    shorts=mul(mul(smooth(sep.outputs["Z"],.70,.74),inv(smooth(sep.outputs["Z"],1.00,1.04))),inv(smooth(absx.outputs[0],.20,.23)))
    shoe=mul(inv(smooth(sep.outputs["Z"],.10,.13)),inv(smooth(sep.outputs["Y"],-.075,-.045)))
    def mix(base,color,factor):
        n=nodes.new("ShaderNodeMixRGB"); n.blend_type="MIX"
        if isinstance(base,bpy.types.NodeSocket): links.new(base,n.inputs[1])
        else: n.inputs[1].default_value=base
        n.inputs[2].default_value=color; links.new(factor,n.inputs[0]); return n.outputs[0]
    color=mix(colors[0],colors[1],jersey); color=mix(color,colors[2],shorts)
    color=mix(color,colors[3],shoe)
    links.new(color,bsdf.inputs["Base Color"])
    human.data.materials.clear(); human.data.materials.append(m)
    # Assign the retained toe/foot weighted skin to the exact dark upper color.
    # The editable body and skeleton stay intact; this removes exposed skin
    # slivers at the modeled shell edge and the face material survives glTF.
    toe_groups={g.index for g in human.vertex_groups
                if g.name in ("foot.L","foot.R") or g.name.startswith(tuple(
                    f"toe{i}-{j}.{side}" for side in ("L","R")
                    for i in range(1,6) for j in range(1,5)))}
    toe_material=mat("Road Shoe Upper",(.045,.065,.075),.58)
    human.data.materials.append(toe_material)
    enclosed=0
    for poly in human.data.polygons:
        indices=list(poly.vertices)
        foot_weight=sum(max((g.weight for g in human.data.vertices[i].groups
                             if g.group in toe_groups),default=0.0) for i in indices)/len(indices)
        mean_z=sum(human.data.vertices[i].co.z for i in indices)/len(indices)
        poly.material_index=1 if foot_weight>.25 and mean_z<.36 else 0
        enclosed+=poly.material_index
    print("SHOE_COVERAGE",enclosed,"toe/foot material faces")


def curl_fingers(rig):
    """Bake a modest, symmetric finger curl into the existing cadence action."""
    if not rig.animation_data or not rig.animation_data.action:
        return
    scene=bpy.context.scene; action=rig.animation_data.action
    start,end=scene.frame_start,scene.frame_end
    digit_bones=[b for b in rig.pose.bones if any(f"finger{i}-" in b.name for i in range(1,6))]
    if not digit_bones: return
    for frame in range(start,end+1):
        scene.frame_set(frame)
        for bone in digit_bones:
            parts=bone.name.split("-")
            finger=int(parts[0].replace("finger","")); joint=int(parts[1].split(".")[0])
            angles={1:(.10,.18,.10),2:(.16,.24,.14),3:(.16,.24,.14),4:(.15,.22,.12),5:(.12,.19,.10)}
            angle=angles[finger][joint-1]
            bone.rotation_mode="XYZ"
            bone.rotation_euler=(angle,0,0)
            bone.keyframe_insert(data_path="rotation_euler",frame=frame,group=bone.name)
    scene.frame_set(start)


def refine_brake_hoods():
    """Replace the oversized contact blobs with compact, smooth road hoods."""
    for side in (-1,1):
        name="BrakeHoodL" if side<0 else "BrakeHoodR"
        obj=bpy.data.objects.get(name)
        if not obj: continue
        rubber=obj.data.materials[0] if obj.data.materials else mat("Brake Hood Rubber",(.045,.052,.055))
        old_vertices=list(obj.data.vertices)
        center=Vector(tuple((min(v.co[i] for v in old_vertices)+max(v.co[i] for v in old_vertices))*.5
                            for i in range(3)))
        rx,ry,rz=.034,.046,.050
        rings=((-.95,.52),(-.60,.88),(0,1.0),(.52,.78),(.92,.46))
        seg=16; verts=[]; faces=[]
        for zfactor,rfactor in rings:
            for i in range(seg):
                a=math.tau*i/seg
                verts.append((center.x+rx*rfactor*math.cos(a),
                              center.y+ry*rfactor*math.sin(a),
                              center.z+rz*zfactor))
        for j in range(len(rings)-1):
            for i in range(seg):
                a=j*seg+i; b=j*seg+(i+1)%seg
                faces.append((a,b,b+seg,a+seg))
        faces.extend((tuple(range(seg-1,-1,-1)),
                      tuple((len(rings)-1)*seg+i for i in range(seg))))
        mesh=bpy.data.meshes.new(name+"RoadHoodMesh")
        mesh.from_pydata(verts,[],faces); mesh.materials.append(rubber)
        for polygon in mesh.polygons: polygon.use_smooth=True
        old=obj.data; obj.data=mesh
        if old.users==0: bpy.data.meshes.remove(old)


def extend_shoe_uppers(rig):
    """Extend the existing skinned shoe mesh into one tapered toe box."""
    for side in ("L","R"):
        shoe_obj=bpy.data.objects.get("CyclingShoe"+side)
        if not shoe_obj: continue
        foot=rig.pose.bones["foot."+side]
        ankle=rig.matrix_world @ foot.head
        if len(shoe_obj.data.vertices)!=60:
            raise RuntimeError(f"{shoe_obj.name}: expected the five-ring source shoe mesh, found {len(shoe_obj.data.vertices)} vertices")
        rest=rig.data.bones["foot."+side].matrix_local
        pose=foot.matrix
        world_to_rest=rest @ pose.inverted() @ rig.matrix_world.inverted()
        # Replace the last two cross-sections in the existing 12-sided shell;
        # the new toe covers the measured MPFB toe joints without a second mesh.
        stations=((.118,.052,-.086,.022),(.190,.048,-.092,.002))
        ring_count=12
        for ring_offset,(dy,width,bottom,top) in enumerate(stations,start=3):
            center_z=(bottom+top)*.5; radius_z=(top-bottom)*.5
            for i in range(ring_count):
                angle=math.tau*i/ring_count
                world=Vector((ankle.x+width*math.cos(angle),ankle.y+dy,
                              ankle.z+center_z+radius_z*math.sin(angle)))
                shoe_obj.data.vertices[ring_offset*ring_count+i].co=world_to_rest @ world
        shoe_obj.data.update()


def render_cycle(preview_root: Path, rig):
    """Render two complete action revolutions from the existing pedal clip."""
    if not rig.animation_data or not rig.animation_data.action: return
    scene=bpy.context.scene; preview_root.mkdir(parents=True,exist_ok=True)
    old_cam=scene.camera; old_engine=scene.render.engine
    old_res=(scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage)
    old_range=(scene.frame_start,scene.frame_end)
    created=[]
    try:
        engines={item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine="BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x=800; scene.render.resolution_y=600; scene.render.resolution_percentage=100
        scene.render.image_settings.file_format="PNG"
        target=Vector((0,0,.93))
        for name,loc,energy,size in (("CycleKey",(3,-2,3),600,2.5),("CycleFill",(-3,.7,2.5),400,2.2)):
            data=bpy.data.lights.new(name,"AREA"); data.energy=energy; data.shape="DISK"; data.size=size
            lamp=bpy.data.objects.new(name,data); scene.collection.objects.link(lamp); lamp.location=loc
            lamp.rotation_euler=(target-lamp.location).to_track_quat("-Z","Y").to_euler(); created.append(lamp)
        data=bpy.data.cameras.new("AnimatedHelmetSide"); data.type="ORTHO"; data.ortho_scale=2.55
        cam=bpy.data.objects.new("AnimatedHelmetSide",data); scene.collection.objects.link(cam)
        cam.location=(3.5,.02,1.0); cam.rotation_euler=(target-cam.location).to_track_quat("-Z","Y").to_euler()
        created.append(cam); scene.camera=cam
        frame_dir=preview_root/"frames"; frame_dir.mkdir(parents=True,exist_ok=True)
        # Include the repeated 0-degree pose at frame 73 so the last frame
        # explicitly verifies the loop seam against frame 1.
        for index,frame in enumerate(range(1,74,3)):
            scene.frame_set(frame); scene.render.filepath=str(frame_dir/f"frame_{index:03d}.png")
            bpy.ops.render.render(write_still=True)
    finally:
        scene.camera=old_cam
        for obj in created: bpy.data.objects.remove(obj,do_unlink=True)
        scene.render.engine=old_engine
        scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage=old_res
        scene.frame_start,scene.frame_end=old_range; scene.frame_set(1)


def inspect_and_render(preview_root: Path, rig):
    scene=bpy.context.scene
    preview_root.mkdir(parents=True,exist_ok=True)
    # Preserve prior render settings and scene state.
    old_cam=scene.camera; old_engine=scene.render.engine
    old_res=(scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage)
    old_world=scene.world
    created=[]
    try:
        engines={item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine="BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x=1000; scene.render.resolution_y=900; scene.render.resolution_percentage=100
        scene.render.image_settings.file_format="PNG"
        world=bpy.data.worlds.new("HelmetReviewWorld"); world.color=(.035,.045,.06); scene.world=world
        target=Vector((0,-.02,1.615))
        for name,location,energy,size in (("ReviewKey",(2,-2,2.5),420,1.4),("ReviewFill",(-2,.7,2),320,1.5),("ReviewRim",(0,1.4,2.2),350,1.2)):
            data=bpy.data.lights.new(name,"AREA"); data.energy=energy; data.shape="DISK"; data.size=size
            obj=bpy.data.objects.new(name,data); scene.collection.objects.link(obj); obj.location=location
            obj.rotation_euler=(target-obj.location).to_track_quat("-Z","Y").to_euler(); created.append(obj)
        views={"front":((0,2.8,1.62),.56),"side":((2.8,0,1.62),.56),"rear":((0,-2.8,1.62),.56)}
        accessories=[o for o in bpy.data.objects if o.get("optional_accessory") or o.name.startswith(("HelmetRim","HelmetStrap","HelmetYAnchor"))]
        for visible in (False,True):
            for obj in accessories: obj.hide_render=not visible
            for view,(location,scale) in views.items():
                data=bpy.data.cameras.new(f"HeadReview_{view}"); data.type="ORTHO"; data.ortho_scale=scale
                cam=bpy.data.objects.new(f"HeadReview_{view}",data); scene.collection.objects.link(cam)
                cam.location=location; cam.rotation_euler=(target-cam.location).to_track_quat("-Z","Y").to_euler()
                created.append(cam); scene.camera=cam
                out=preview_root/("helmet_on" if visible else "helmet_off")/f"head_{view}.png"
                out.parent.mkdir(parents=True,exist_ok=True); scene.render.filepath=str(out)
                bpy.ops.render.render(write_still=True)
        for obj in accessories: obj.hide_render=False
    finally:
        scene.camera=old_cam
        for obj in created: bpy.data.objects.remove(obj,do_unlink=True)
        scene.world=old_world; scene.render.engine=old_engine
        scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage=old_res
    # Print evaluated head/headwear bounds so review has a numerical companion.
    bpy.context.view_layer.update()
    for name in ("Human","CyclingHelmet","HelmetRim","HelmetStrapL","HelmetStrapR"):
        obj=bpy.data.objects.get(name)
        if obj:
            corners=[obj.matrix_world @ Vector(c) for c in obj.bound_box]
            print("REVIEW_BOUNDS",name,tuple(round(min(v[a] for v in corners),4) for a in range(3)),
                  tuple(round(max(v[a] for v in corners),4) for a in range(3)))
    human=bpy.data.objects.get("Human")
    if human and human.type=="MESH" and "head" in human.vertex_groups:
        head_index=human.vertex_groups["head"].index
        head_vertices=[]
        evaluated=human.evaluated_get(bpy.context.evaluated_depsgraph_get())
        evaluated_mesh=evaluated.to_mesh()
        for vertex in evaluated_mesh.vertices:
            if any(g.group==head_index and g.weight>.5 for g in vertex.groups):
                head_vertices.append(evaluated.matrix_world @ vertex.co)
        evaluated.to_mesh_clear()
        if head_vertices:
            print("WEIGHTED_HEAD_BOUNDS",
                  tuple(round(min(v[a] for v in head_vertices),4) for a in range(3)),
                  tuple(round(max(v[a] for v in head_vertices),4) for a in range(3)))
    print("HEAD_POSE_WORLD",tuple(round(v,4) for v in (rig.matrix_world @ rig.pose.bones["head"].matrix).translation))


def render_full_views(preview_root: Path):
    """Render complete fitted rider and bicycle for clothing, shoe, and grip review."""
    scene=bpy.context.scene; preview_root.mkdir(parents=True,exist_ok=True)
    old_cam=scene.camera; old_engine=scene.render.engine
    old_res=(scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage)
    created=[]
    try:
        engines={item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine="BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x=1100; scene.render.resolution_y=900; scene.render.resolution_percentage=100
        scene.render.image_settings.file_format="PNG"
        target=Vector((0,0,.93))
        for name,loc,energy,size in (("FullKey",(3,-2,3),650,2.4),("FullFill",(-3,.7,2.5),420,2.1)):
            data=bpy.data.lights.new(name,"AREA"); data.energy=energy; data.shape="DISK"; data.size=size
            lamp=bpy.data.objects.new(name,data); scene.collection.objects.link(lamp); lamp.location=loc
            lamp.rotation_euler=(target-lamp.location).to_track_quat("-Z","Y").to_euler(); created.append(lamp)
        for name,location,scale in (("side",(3.5,.02,1.0),2.55),("front",(0,3,1.0),2.35),
                                    ("rear",(0,-3,1.0),2.35)):
            data=bpy.data.cameras.new("FullReview_"+name); data.type="ORTHO"; data.ortho_scale=scale
            cam=bpy.data.objects.new("FullReview_"+name,data); scene.collection.objects.link(cam)
            cam.location=location; cam.rotation_euler=(target-cam.location).to_track_quat("-Z","Y").to_euler()
            created.append(cam); scene.camera=cam; scene.frame_set(1)
            scene.render.filepath=str(preview_root/f"rider_{name}.png")
            bpy.ops.render.render(write_still=True)
    finally:
        scene.camera=old_cam
        for obj in created: bpy.data.objects.remove(obj,do_unlink=True)
        scene.render.engine=old_engine
        scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage=old_res


def main():
    args=sys.argv[sys.argv.index("--")+1:]
    head_only="--head-only" in args
    args=[arg for arg in args if arg!="--head-only"]
    if len(args)!=3: raise SystemExit("expected output GLB, output blend, preview directory [--head-only]")
    glb,blend,preview=map(Path,args)
    glb.parent.mkdir(parents=True,exist_ok=True); blend.parent.mkdir(parents=True,exist_ok=True)
    rig=bpy.data.objects.get("metarig") or next((o for o in bpy.data.objects if o.type=="ARMATURE"),None)
    if rig is None: raise RuntimeError("preserved cyclist blend has no armature")
    print("PRESERVED", "bones",len(rig.data.bones),"actions",[a.name for a in bpy.data.actions])
    print("HEAD_BONE",rig.data.bones.get("head"))
    make_helmet(rig)
    round_clothing_edges()
    refine_brake_hoods()
    extend_shoe_uppers(rig)
    curl_fingers(rig)
    inspect_and_render(preview,rig)
    if not head_only:
        render_full_views(preview/"full_rider_review")
        render_cycle(preview/"animated_side",rig)
    # Save a non-destructive review checkpoint and export full weighted model/action.
    bpy.ops.wm.save_as_mainfile(filepath=str(blend))
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects: obj.select_set(True)
    bpy.context.view_layer.objects.active=bpy.data.objects.get("BicycleModel")
    bpy.ops.export_scene.gltf(filepath=str(glb),export_format="GLB",use_selection=True,
        export_animations=True,export_frame_range=True,export_force_sampling=True,export_animation_mode="ACTIONS")
    print("EXPORTED",glb)


if __name__=="__main__": main()
