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
            # Keep the sphere's face winding outward. Blender renders the
            # previous inward winding as two-sided, but Godot's back-face
            # culling turned the irises into dark angular patches.
            faces.append((a, a + segments, b + segments, b))
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
    old = [o for o in bpy.data.objects if o.name.startswith(("CyclingHelmet", "HelmetBrim", "HelmetRim", "HelmetStrap", "HelmetVent"))]
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


def refine_human_eyes(rig):
    """Remove exploratory eye overlays; keep the complete weighted MPFB face."""
    for obj in list(bpy.data.objects):
        if obj.name.startswith(("RiderIris", "RiderPupil", "RiderEyeHighlight")):
            bpy.data.objects.remove(obj, do_unlink=True)


def remove_mp_fb_leg_proxy_panels(human):
    """Remove the MPFB skirt/tights helper islands that bridge the actual legs.

    These are isolated, fully helper-weighted proxy components in MPFB's Human
    mesh. The real body is a separate continuous component and remains fully
    skinned. Match the dedicated helper groups at full weight so this cannot
    remove ordinary torso, pelvis, or leg vertices.
    """
    import bmesh
    mesh=human.data
    groups={name:human.vertex_groups.get(name) for name in ("helper-skirt","helper-tights")}
    if any(group is None for group in groups.values()):
        raise RuntimeError("Expected MPFB helper-skirt and helper-tights proxy groups")
    group_indices={name:group.index for name,group in groups.items()}
    bm=bmesh.new(); bm.from_mesh(mesh)
    remove=[]
    for face in bm.faces:
        # Blender's deform layer carries the actual full-weight helper label.
        weights=[face.verts[i][bm.verts.layers.deform.verify()] for i in range(len(face.verts))]
        matched=None
        for name,index in group_indices.items():
            if all(abs(weight.get(index,0.0)-1.0)<1e-5 for weight in weights):
                matched=name; break
        if matched:
            remove.append((face,matched))
    counts={name:0 for name in group_indices}
    for face,name in remove:
        if face.is_valid:
            bmesh.ops.delete(bm,geom=[face],context='FACES_ONLY')
            counts[name]+=1
    bm.to_mesh(mesh); bm.free(); mesh.update()
    # Allow a fully cleaned mesh, while rejecting partial helper islands.
    if any(0<count<100 for count in counts.values()):
        raise RuntimeError(f"Unexpected MPFB helper proxy removal counts: {counts}")
    print("REMOVED_MPFB_LEG_PROXY_FACES",counts)


def round_clothing_edges():
    human=bpy.data.objects.get("Human")
    if not human or not human.type == "MESH":
        return
    remove_mp_fb_leg_proxy_panels(human)
    # Bake smooth category masks into glTF's standard COLOR_0 vertex attribute.
    # The earlier procedural material graph used unsupported glTF nodes, so the
    # exported body fell back to its white diffuse default in Godot. Keeping the
    # masks in RGBA lets the runtime shader apply rider variants to jersey only.
    mesh=human.data
    groups={g.name:g.index for g in human.vertex_groups}
    head_idx=groups.get("head")
    foot_indices=[idx for name,idx in groups.items() if name in ("foot.L","foot.R") or name.startswith("toe")]
    def smooth(value,lo,hi):
        t=max(0.0,min(1.0,(value-lo)/(hi-lo)))
        return t*t*(3-2*t)
    def group_weight(vertex,index):
        if index is None: return 0.0
        return max((g.weight for g in vertex.groups if g.group==index),default=0.0)
    attr=mesh.color_attributes.get("RiderOutfitWeights") or mesh.color_attributes.new(
        name="RiderOutfitWeights",type="FLOAT_COLOR",domain="POINT")
    for vertex in mesh.vertices:
        x,y,z=vertex.co
        ax=abs(x)
        head=group_weight(vertex,head_idx)
        jersey=(smooth(z,.955,.99)*(1-smooth(z,1.39,1.42))*(1-smooth(ax,.26,.36))
                *(1-smooth(head,.08,.50)))
        face_center=1-smooth(ax,.075,.15)
        face_upper=smooth(z,1.29,1.34)
        jersey*=1-face_center*face_upper
        shorts=smooth(z,.70,.74)*(1-smooth(z,1.00,1.04))*(1-smooth(ax,.20,.23))
        shoe=(1-smooth(z,.10,.13))*(1-smooth(y,-.075,-.045))
        foot=max((group_weight(vertex,idx) for idx in foot_indices),default=0.0)
        shoe=max(shoe,foot*(1-smooth(z,.27,.38)))
        # Pack three outfit masks in glTF's RGB vertex attribute. Skin is the
        # residual weight in the Godot shader, avoiding dependence on optional
        # COLOR_0 alpha (which Blender's exporter omits for RGB materials).
        w_jersey=jersey*(1-shorts)*(1-shoe)
        w_shorts=shorts*(1-shoe)
        w_shoe=shoe
        attr.data[vertex.index].color=(w_jersey,w_shorts,w_shoe,1.0)
    # Blender glTF exports the render color layer as COLOR_0. Merely selecting
    # the active layer leaves the prior MPFB head-weight layer in that slot.
    for other in list(mesh.color_attributes):
        if other != attr:
            mesh.color_attributes.remove(other)
    color_index=next(i for i,layer in enumerate(mesh.color_attributes)
                     if layer.name=="RiderOutfitWeights")
    mesh.color_attributes.active_color=attr
    mesh.color_attributes.active_color_index=color_index
    mesh.color_attributes.render_color_index=color_index
    m=bpy.data.materials.get("Rider Outfit | glTF vertex masks") or bpy.data.materials.new("Rider Outfit | glTF vertex masks")
    m.diffuse_color=(.58,.27,.2,1); m.use_nodes=True
    nodes=m.node_tree.nodes; links=m.node_tree.links; nodes.clear()
    out=nodes.new("ShaderNodeOutputMaterial"); bsdf=nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value=.82
    color=nodes.new("ShaderNodeVertexColor"); color.layer_name="RiderOutfitWeights"
    separate=nodes.new("ShaderNodeSeparateColor"); separate.mode="RGB"
    links.new(color.outputs["Color"],separate.inputs["Color"])
    def scaled(channel,rgba):
        n=nodes.new("ShaderNodeVectorMath"); n.operation="SCALE"
        n.inputs[0].default_value=rgba; links.new(channel,n.inputs[3]); return n.outputs[0]
    # Blender preview uses the same masks as Godot; the GLB runtime shader maps
    # these channels to the selected variant and fixed shorts/skin/shoe colors.
    jersey=scaled(separate.outputs["Red"],(.58,.13,.09))
    shorts=scaled(separate.outputs["Green"],(.035,.047,.058))
    shoe=scaled(separate.outputs["Blue"],(.68,.66,.58))
    used=nodes.new("ShaderNodeMath"); used.operation="ADD"
    links.new(separate.outputs["Red"],used.inputs[0]); links.new(separate.outputs["Green"],used.inputs[1])
    used2=nodes.new("ShaderNodeMath"); used2.operation="ADD"
    links.new(used.outputs[0],used2.inputs[0]); links.new(separate.outputs["Blue"],used2.inputs[1])
    skin_weight=nodes.new("ShaderNodeMath"); skin_weight.operation="SUBTRACT"
    skin_weight.inputs[0].default_value=1.0; links.new(used2.outputs[0],skin_weight.inputs[1])
    skin=scaled(skin_weight.outputs[0],(.58,.36,.25))
    add=jersey
    for channel in (shorts,skin,shoe):
        node=nodes.new("ShaderNodeVectorMath"); node.operation="ADD"
        links.new(add,node.inputs[0]); links.new(channel,node.inputs[1]); add=node.outputs[0]
    links.new(add,bsdf.inputs["Base Color"]); links.new(bsdf.outputs["BSDF"],out.inputs["Surface"])
    # The GLB's exposed eye islands read as bright rectangular patches under
    # route key lighting. A quiet warm brown keeps the low-poly eye surfaces
    # legible without adding floating iris geometry or changing world light.
    eye_mat=mat("Rider Eye | muted warm brown",(.065,.045,.035),.92)
    eye_indices=[groups.get("eye.L"),groups.get("eye.R")]
    eye_indices=[idx for idx in eye_indices if idx is not None]
    mesh.materials.clear(); mesh.materials.append(m); mesh.materials.append(eye_mat)
    for polygon in mesh.polygons:
        polygon.material_index=0
        if eye_indices and any(all(group_weight(mesh.vertices[index],group_idx)>.95
                                   for index in polygon.vertices)
                               for group_idx in eye_indices):
            polygon.material_index=1
    print("BAKED_RIDER_VERTEX_MASKS",len(mesh.vertices),"channels=jersey,shorts,shoe")


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
            # MPFB local-axis sweep against the real hood surface showed
            # positive X flexes down toward the grip. A graduated curl retains
            # finger spread while bringing the distal pads around the hood.
            angles={1:(.25,.40,.30),2:(.45,.65,.58),3:(.45,.65,.60),
                    4:(.48,.68,.62),5:(.43,.63,.58)}
            angle=angles[finger][joint-1]
            bone.rotation_mode="XYZ"
            bone.rotation_euler=(angle,0,0)
            bone.keyframe_insert(data_path="rotation_euler",frame=frame,group=bone.name)
    scene.frame_set(start)


def fit_hands_to_hoods(rig):
    """Lower the complete hand chains onto the existing brake-hood grip tops."""
    wrists = [rig.pose.bones.get(f"wrist.{side}") for side in ("L", "R")]
    if not all(wrists):
        raise RuntimeError("MPFB wrist bones are required for hood contact fitting")
    if all(bone.get("WodraAlpha48HoodContactFix1", False) for bone in wrists):
        return
    scene = bpy.context.scene
    for frame in range(scene.frame_start, scene.frame_end + 1):
        scene.frame_set(frame)
        for side, wrist in zip(("L", "R"), wrists):
            # Measured from the saved pose and BrakeHoodL/R world bounds:
            # Measured against the runtime hood: align the palm on the shelf,
            # keeping the adjustment inside the existing elbow reach.
            # Move the wrist root and its weighted hand/finger descendants
            # together, preserving the upper-arm, elbow, and leg animation.
            # The prior authored pose places the palm near the grip shelf.
            # Evaluated finger pads were 30–50 mm forward of the hood; this
            # measured 30 mm move places the complete wrist-rooted hand back
            # over the grip while preserving elbow, leg, and torso transforms.
            world_delta = Vector((0, 0, .030))
            pose = wrist.matrix.copy()
            current_world = rig.matrix_world @ pose.translation
            pose.translation = rig.matrix_world.inverted_safe() @ (current_world + world_delta)
            wrist.matrix = pose
            bpy.context.view_layer.update()
            evaluated_world = rig.matrix_world @ wrist.matrix.translation
            if (evaluated_world - current_world - world_delta).length > 1e-4:
                raise RuntimeError(f"{wrist.name} hood fit did not apply in world space")
            wrist.keyframe_insert(data_path="location", frame=frame, group=wrist.name)
        bpy.context.view_layer.update()
    for wrist in wrists:
            wrist["WodraAlpha48HoodContactFix1"] = True
    scene.frame_set(scene.frame_start)


def refine_brake_hoods():
    """Replace the old oval blobs with compact, tapered road hoods."""
    for side in (-1,1):
        name="BrakeHoodL" if side<0 else "BrakeHoodR"
        obj=bpy.data.objects.get(name)
        if not obj: continue
        if obj.get("WodraRoadHoodPolished",False):
            # Existing authoring blends already contain the shaped hood, but
            # the imported GLB still showed a swollen dark blob because its
            # normals were inward. Preserve its geometry and fix its normals.
            import bmesh
            bm=bmesh.new(); bm.from_mesh(obj.data)
            bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
            bm.to_mesh(obj.data); bm.free(); obj.data.update()
            continue
        rubber=obj.data.materials[0] if obj.data.materials else mat("Brake Hood Rubber",(.045,.052,.055))
        old_vertices=list(obj.data.vertices)
        center=Vector(tuple((min(v.co[i] for v in old_vertices)+max(v.co[i] for v in old_vertices))*.5
                            for i in range(3)))
        center.y-=.020
        center.z+=.015
        # Cross-sections run along the bar; the rear palm shelf tapers into a
        # compact forward nose instead of reading as a rounded knob.
        sections=((-0.038,.019,.024),(-.022,.027,.032),(.002,.029,.032),
                  (.023,.024,.027),(.039,.016,.020))
        seg=12; verts=[]; faces=[]
        for y_offset,rx,rz in sections:
            for i in range(seg):
                a=math.tau*i/seg
                verts.append((center.x+rx*math.cos(a),center.y+y_offset,
                              center.z+rz*math.sin(a)))
        for j in range(len(sections)-1):
            for i in range(seg):
                a=j*seg+i; b=j*seg+(i+1)%seg
                faces.append((a,b,b+seg,a+seg))
        faces.extend((tuple(range(seg-1,-1,-1)),
                      tuple((len(sections)-1)*seg+i for i in range(seg))))
        mesh=bpy.data.meshes.new(name+"RoadHoodMesh")
        mesh.from_pydata(verts,[],faces); mesh.materials.append(rubber)
        # Recompute the closed hood's normals outward. The old mesh used the
        # opposite winding on its side strips, which Godot's cull_back mode
        # rendered as a dark, swollen-looking blob around the fingers.
        import bmesh
        bm=bmesh.new(); bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(mesh); bm.free()
        for polygon in mesh.polygons: polygon.use_smooth=True
        old=obj.data; obj.data=mesh
        if old.users==0: bpy.data.meshes.remove(old)
        obj["WodraRoadHoodPolished"]=True


def refine_road_bike():
    """Refine the preserved frame's wheel profile, spokes, and road controls."""
    bike=bpy.data.objects.get("BicycleModel")
    if bike is None: raise RuntimeError("Preserved bicycle model is missing")
    rim_material=bpy.data.materials.get("Deep Carbon Rim") or mat("Deep Carbon Rim",(.055,.065,.07))
    spoke_material=bpy.data.materials.get("Dark Stainless Spokes") or mat("Dark Stainless Spokes",(.035,.042,.045),.9)
    frame=bpy.data.objects.get("BikeFrame")
    if frame is None or len(frame.data.vertices)!=480:
        raise RuntimeError("Preserved BikeFrame does not match the documented five aero-tube mesh")
    head_low=Vector((0,.32,.72)); head_high=Vector((0,.37,.84))
    sections=(0.0,.12,.34,.72,.9,1.0)
    first_head_vertex=164
    if not frame.get("WodraHeadTubePolished",False):
        for row,along in enumerate(sections):
            center=head_low.lerp(head_high,along)
            for vertex in frame.data.vertices[first_head_vertex+row*12:first_head_vertex+(row+1)*12]:
                offset=vertex.co-center
                vertex.co=center+Vector((offset.x*.72,offset.y*.76,offset.z*.76))
        frame["WodraHeadTubePolished"]=True
        frame.data.update()
    stem=bpy.data.objects.get("Stem")
    if stem is not None and not stem.get("WodraStemPolished",False):
        center=sum((v.co for v in stem.data.vertices),Vector())/len(stem.data.vertices)
        for vertex in stem.data.vertices: vertex.co=center+(vertex.co-center)*.76
        stem["WodraStemPolished"]=True
        stem.data.update()
    for side in ("Front","Rear"):
        rim=bpy.data.objects.get(side+"WheelRim")
        if rim is None: raise RuntimeError(f"Missing {side} wheel rim")
        # The bead seat stays on the existing tyre radius; grow the visible
        # annular carbon section inward to a 40 mm radial profile.
        if not rim.get("WodraDeepCarbonRim",False):
            for vertex in rim.data.vertices:
                radius=math.hypot(vertex.co.y,vertex.co.z)
                new_radius=.283+(radius-.295)*(.020/.011)
                factor=new_radius/radius if radius else 1.0
                vertex.co.x*=1.45
                vertex.co.y*=factor; vertex.co.z*=factor
            rim["WodraDeepCarbonRim"]=True
        rim.data.materials.clear(); rim.data.materials.append(rim_material)
        spokes=bpy.data.objects.get(side+"WheelSpokes")
        if spokes is None: raise RuntimeError(f"Missing {side} wheel spokes")
        vertices=[]; faces=[]
        for flange_x in (-.012,.012):
            for spoke in range(9):
                angle=math.tau*(spoke+(.5 if flange_x>0 else 0.0))/9
                start=(flange_x, .045*math.sin(angle), .045*math.cos(angle))
                end=(flange_x, .267*math.sin(angle), .267*math.cos(angle))
                part_vertices,part_faces=thin_tube_mesh(start,end,.0015,.0012,5)
                offset=len(vertices); vertices.extend(part_vertices)
                faces.extend(tuple(offset+i for i in face) for face in part_faces)
        spokes.data.clear_geometry(); spokes.data.from_pydata(vertices,[],faces)
        spokes.data.materials.clear(); spokes.data.materials.append(spoke_material)
        for polygon in spokes.data.polygons: polygon.use_smooth=True
        spokes.data.update()
    # Discrete lever blades join each compact hood to the drop bar.
    lever_material=bpy.data.materials.get("Brake Lever Alloy") or mat("Brake Lever Alloy",(.12,.14,.15),.82)
    for side in (-1,1):
        name="BrakeLeverL" if side<0 else "BrakeLeverR"
        old=bpy.data.objects.get(name)
        if old: bpy.data.objects.remove(old,do_unlink=True)
        start=Vector((side*.25,.195,.940)); end=Vector((side*.25,.250,.877))
        direction=end-start
        bpy.ops.mesh.primitive_cylinder_add(vertices=8,radius=.006,depth=direction.length,
            location=(start+end)*.5)
        lever=bpy.context.object; lever.name=name
        lever.rotation_mode='QUATERNION'; lever.rotation_quaternion=direction.to_track_quat('Z','Y')
        lever.parent=bike
        lever.data.materials.append(lever_material)
        lever.matrix_parent_inverse=bike.matrix_world.inverted()
    pedal_material=bpy.data.materials.get("Wodra Contact Carbon") or mat("Wodra Contact Carbon",(.045,.052,.055),.82)
    for name in ("LeftPedal","RightPedal"):
        pedal=bpy.data.objects.get(name)
        if pedal is None: raise RuntimeError(f"Missing contact anchor geometry {name}")
        pedal.scale=(.05,.095,.015)
        pedal.data.materials.clear(); pedal.data.materials.append(pedal_material)


def thin_tube_mesh(start,end,r0,r1,sides):
    a,b=Vector(start),Vector(end); axis=(b-a).normalized()
    ref=Vector((0,0,1)) if abs(axis.z)<.9 else Vector((1,0,0))
    u=axis.cross(ref).normalized(); v=axis.cross(u).normalized()
    verts=[]; faces=[]
    for center,radius in ((a,r0),(b,r1)):
        for i in range(sides):
            angle=math.tau*i/sides
            verts.append(tuple(center+radius*(u*math.cos(angle)+v*math.sin(angle))))
    for i in range(sides):
        faces.append((i,(i+1)%sides,(i+1)%sides+sides,i+sides))
    return verts,faces


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


def render_hand_contact_views(preview_root: Path):
    """Render each hand against its real road hood at close review distance."""
    scene=bpy.context.scene; preview_root.mkdir(parents=True,exist_ok=True)
    old_cam=scene.camera; old_engine=scene.render.engine
    old_res=(scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage)
    created=[]
    try:
        engines={item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine="BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
        scene.render.resolution_x=900; scene.render.resolution_y=700; scene.render.resolution_percentage=100
        scene.render.image_settings.file_format="PNG"
        target_lights=Vector((0,.18,.98))
        for name,loc,energy,size in (("GripKey",(.8,-.25,1.8),260,.8),
                                     ("GripFill",(-.8,.5,1.5),180,.9)):
            data=bpy.data.lights.new(name,"AREA"); data.energy=energy; data.shape="DISK"; data.size=size
            lamp=bpy.data.objects.new(name,data); scene.collection.objects.link(lamp); lamp.location=loc
            lamp.rotation_euler=(target_lights-lamp.location).to_track_quat("-Z","Y").to_euler(); created.append(lamp)
        for side,label in ((-1,"left"),(1,"right")):
            target=Vector((side*.25,.195,.985))
            data=bpy.data.cameras.new("HoodReview_"+label); data.type="ORTHO"; data.ortho_scale=.235
            cam=bpy.data.objects.new("HoodReview_"+label,data); scene.collection.objects.link(cam)
            cam.location=target+Vector((side*.47,.045,.105))
            cam.rotation_euler=(target-cam.location).to_track_quat("-Z","Y").to_euler()
            created.append(cam); scene.camera=cam; scene.frame_set(scene.frame_start)
            scene.render.filepath=str(preview_root/f"hand_{label}_hood.png")
            bpy.ops.render.render(write_still=True)
    finally:
        scene.camera=old_cam
        for obj in created: bpy.data.objects.remove(obj,do_unlink=True)
        scene.render.engine=old_engine
        scene.render.resolution_x,scene.render.resolution_y,scene.render.resolution_percentage=old_res


def configure_gltf_vertex_color_material():
    """Export COLOR_0 directly; Godot applies the weighted outfit shader."""
    human=bpy.data.objects["Human"]
    material=human.data.materials[0]
    nodes=material.node_tree.nodes; links=material.node_tree.links
    bsdf=next(node for node in nodes if node.type=="BSDF_PRINCIPLED")
    for link in list(bsdf.inputs["Base Color"].links): links.remove(link)
    color=next((node for node in nodes if node.type=="VERTEX_COLOR" and
                node.layer_name=="RiderOutfitWeights"),None)
    if color is None:
        color=nodes.new("ShaderNodeVertexColor"); color.layer_name="RiderOutfitWeights"
    bsdf.inputs["Base Color"].default_value=(1,1,1,1)
    links.new(color.outputs["Color"],bsdf.inputs["Base Color"])
    print("GLTF_EXPORT_COLOR_ATTRIBUTE",color.layer_name,"directly connected to base color")


def main():
    args=sys.argv[sys.argv.index("--")+1:]
    head_only="--head-only" in args
    skip_previews="--skip-previews" in args
    args=[arg for arg in args if arg not in ("--head-only","--skip-previews")]
    if len(args)!=3: raise SystemExit("expected output GLB, output blend, preview directory [--head-only]")
    glb,blend,preview=map(Path,args)
    glb.parent.mkdir(parents=True,exist_ok=True); blend.parent.mkdir(parents=True,exist_ok=True)
    rig=bpy.data.objects.get("metarig") or next((o for o in bpy.data.objects if o.type=="ARMATURE"),None)
    if rig is None: raise RuntimeError("preserved cyclist blend has no armature")
    print("PRESERVED", "bones",len(rig.data.bones),"actions",[a.name for a in bpy.data.actions])
    print("HEAD_BONE",rig.data.bones.get("head"))
    make_helmet(rig)
    refine_human_eyes(rig)
    round_clothing_edges()
    refine_brake_hoods()
    refine_road_bike()
    extend_shoe_uppers(rig)
    curl_fingers(rig)
    fit_hands_to_hoods(rig)
    if not skip_previews:
        inspect_and_render(preview,rig)
        if not head_only:
            render_full_views(preview/"full_rider_review")
            render_hand_contact_views(preview/"hand_contact")
            render_cycle(preview/"animated_side",rig)
    # Save a non-destructive review checkpoint and export full weighted model/action.
    bpy.ops.wm.save_as_mainfile(filepath=str(blend))
    # Keep the editable checkpoint on the Blender authoring shader network;
    # only the exported GLB uses the direct COLOR_0 connection Godot imports.
    configure_gltf_vertex_color_material()
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects: obj.select_set(True)
    bpy.context.view_layer.objects.active=bpy.data.objects.get("BicycleModel")
    bpy.ops.export_scene.gltf(filepath=str(glb),export_format="GLB",use_selection=True,
        export_animations=True,export_frame_range=True,export_force_sampling=True,export_animation_mode="ACTIONS")
    print("EXPORTED",glb)


if __name__=="__main__": main()
