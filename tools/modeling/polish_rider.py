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
from mathutils import Matrix, Quaternion, Vector, geometry
from mathutils.bvhtree import BVHTree


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
    # Build around this source rider's evaluated head, rather than the old
    # alpha48 world-space estimate. The integrated CG bike uses a different
    # rider origin; hard-coded alpha48 coordinates left the shell visibly
    # floating above and in front of the skull in the production RideScreen.
    head_bone = rig.pose.bones.get("head")
    if head_bone is None:
        raise RuntimeError("rider head bone is missing")
    head_top = rig.matrix_world @ head_bone.tail
    helmet_offset = Vector((head_top.x, head_top.y - .0415,
                            head_top.z + .025 - 1.795))
    def helmet_point(point):
        return tuple(Vector(point) + helmet_offset)
    # Geometry in armature rest coordinates hugs the evaluated skull. It
    # widens over the temples and tapers rearward, with a real lower rim.
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
            x=helmet_offset.x + .101*radial*math.cos(a)
            # Match the weighted head centre (the source's head extends
            # farther forward than the neck/armature origin).
            y=.0415 + helmet_offset.y + .116*radial*math.sin(a)
            z=zcrown + helmet_offset.z - drop + .018*front*(t**2) - .006*rear*(t**2)
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
        ring.append((helmet_offset.x+.101*math.cos(a),.0415+helmet_offset.y+.116*math.sin(a),
                     1.795+helmet_offset.z-.105+.018*front-.006*rear))
    tube("HelmetRim", ring, .003, trim, rig, sides=8)
    # Six flat dark vent slots conform to the upper shell instead of floating
    # spheres. Their vertices follow the same ellipsoidal surface equation.
    idx=0
    for x in (-.035,.035):
        for y in (.020,.0415,.063):
            idx+=1; patch=[]; sides=20
            for k in range(sides):
                a=math.tau*k/sides
                px=helmet_offset.x+x+.0045*math.cos(a); py=helmet_offset.y+y+.0105*math.sin(a)
                t2=((px-helmet_offset.x)/.101)**2+((py-(.0415+helmet_offset.y))/.116)**2
                t=math.sqrt(min(.98,t2)); angle=math.atan2((py-(.0415+helmet_offset.y))/.116,
                                                              (px-helmet_offset.x)/.101)
                front=max(0.0,math.sin(angle)); rear=max(0.0,-math.sin(angle))
                pz=1.795+helmet_offset.z-.105*(1.0-math.sqrt(max(0.0,1.0-t*t)))+.018*front*t*t-.006*rear*t*t+.0006
                patch.append((px,py,pz))
            vent=mesh_obj(f"HelmetVent{idx}",patch,[tuple(range(sides))],dark,rig,smooth=True)
            vent["optional_accessory"] = True
    # Two continuous straps pass in front of each ear and meet under the chin.
    for side in (-1, 1):
        pts=[helmet_point((side*.084,.035,1.715)),helmet_point((side*.092,.004,1.675)),
             helmet_point((side*.078,.025,1.615)),helmet_point((side*.050,.065,1.565)),
             helmet_point((0,.090,1.550))]
        obj=tube("HelmetStrapL" if side<0 else "HelmetStrapR",pts,.004,dark,rig,sides=8)
        obj["optional_accessory"] = True


def refine_human_eyes(rig):
    """Remove exploratory eye overlays; keep the complete weighted MPFB face."""
    for obj in list(bpy.data.objects):
        if obj.name.startswith(("RiderIris", "RiderPupil", "RiderEyeHighlight")):
            bpy.data.objects.remove(obj, do_unlink=True)


def add_road_kit_garment_details(rig):
    """Add small, skinned garment construction cues to the existing outfit.

    The jersey and bib remain the authored skinned body/outfit; these narrow
    trims make their edges and closure read as garments without simulation or
    a second wardrobe system.
    """
    for obj in list(bpy.data.objects):
        if obj.name.startswith(("RiderJersey", "RiderBib")):
            bpy.data.objects.remove(obj, do_unlink=True)
    human=bpy.data.objects.get("Human")
    if human is None:
        raise RuntimeError("weighted rider mesh is required for garment details")
    zipper=mat("Road kit | zipper and seam",(.075,.012,.012),.88)
    jersey_trim=mat("Road kit | collar and sleeve edge",(.12,.025,.02),.88)
    bib_trim=mat("Road kit | short leg gripper",(.018,.022,.024),.88)
    depsgraph=bpy.context.evaluated_depsgraph_get()
    evaluated=human.evaluated_get(depsgraph)
    evaluated_mesh=evaluated.to_mesh(preserve_all_data_layers=True,depsgraph=depsgraph)
    vertices=evaluated_mesh.vertices
    polygons=evaluated_mesh.polygons
    local_to_world=evaluated.matrix_world
    world_to_local=local_to_world.inverted()
    tree=BVHTree.FromObject(human,depsgraph)
    groups={group.index:group.name for group in human.vertex_groups}

    def project(point):
        local_point=world_to_local@Vector(point)
        nearest=tree.find_nearest(local_point)
        if nearest is None or nearest[2] < 0:
            raise RuntimeError(f"could not project garment path to rider surface: {point}")
        _,normal,polygon_index,distance=nearest
        polygon=polygons[polygon_index]
        indices=list(polygon.vertices)
        best=None
        for offset in range(1,len(indices)-1):
            tri=indices[0],indices[offset],indices[offset+1]
            a,b,c=(vertices[index].co for index in tri)
            closest=geometry.closest_point_on_tri(local_point,a,b,c)
            v0=b-a; v1=c-a; v2=closest-a
            d00=v0.dot(v0); d01=v0.dot(v1); d11=v1.dot(v1)
            d20=v2.dot(v0); d21=v2.dot(v1)
            denom=max(1e-12,d00*d11-d01*d01)
            wb=(d11*d20-d01*d21)/denom
            wc=(d00*d21-d01*d20)/denom
            bary=(max(0.,1-wb-wc),max(0.,wb),max(0.,wc))
            candidate=(local_point-closest).length
            if best is None or candidate<best[0]:
                best=(candidate,tri,bary,closest)
        if best is None:
            raise RuntimeError("rider surface polygon could not be triangulated")
        _,tri,bary,closest=best
        weights={}
        for vertex_index,factor in zip(tri,bary):
            for assignment in vertices[vertex_index].groups:
                name=groups.get(assignment.group)
                if name and rig.data.bones.get(name) and rig.data.bones[name].use_deform:
                    weights[name]=weights.get(name,0.)+factor*assignment.weight
        weights=dict(sorted(weights.items(),key=lambda item:item[1],reverse=True)[:4])
        total=sum(weights.values())
        if total<=1e-8:
            raise RuntimeError("garment detail projected to vertices without deform weights")
        weights={name:value/total for name,value in weights.items()}
        world_point=local_to_world@closest
        world_normal=(local_to_world.to_3x3()@normal).normalized()
        world_point+=world_normal*.0012
        return world_point,world_normal,weights

    def skin_detail(name,guide_points,material,radius,closed=False):
        path=[]
        for start,end in zip(guide_points,guide_points[1:]):
            for step in range(4):
                path.append(Vector(start).lerp(Vector(end),step/4))
        path.append(Vector(guide_points[-1]))
        samples=[project(point) for point in path]
        centers=[sample[0] for sample in samples]
        normals=[sample[1] for sample in samples]
        verts=[]; faces=[]; ring_weights=[]; sides=6
        for i,(center,normal,_,) in enumerate(samples):
            tangent=(centers[(i+1)%len(centers)]-centers[(i-1)%len(centers)]).normalized() if closed else (
                (centers[min(i+1,len(centers)-1)]-centers[max(i-1,0)]).normalized())
            across=tangent.cross(normal)
            if across.length<1e-5:
                across=tangent.cross(Vector((1,0,0)))
            across.normalize(); depth=normal.cross(across).normalized()
            ring_weights.append(samples[i][2])
            for side_index in range(sides):
                angle=math.tau*side_index/sides
                verts.append(center+radius*(across*math.cos(angle)+depth*math.sin(angle)))
        segment_count=len(centers) if closed else len(centers)-1
        for i in range(segment_count):
            next_i=(i+1)%len(centers)
            for side_index in range(sides):
                a=i*sides+side_index; b=i*sides+(side_index+1)%sides
                faces.append((a,b,next_i*sides+(side_index+1)%sides,next_i*sides+side_index))
        if not closed:
            faces.extend((tuple(reversed(range(sides))),
                          tuple((len(centers)-1)*sides+i for i in range(sides))))
        mesh=bpy.data.meshes.new(name+"Mesh")
        rig_world_to_local=rig.matrix_world.inverted()
        mesh.from_pydata([tuple(rig_world_to_local@vertex) for vertex in verts],[],faces)
        mesh.materials.append(material)
        for face in mesh.polygons: face.use_smooth=True
        obj=bpy.data.objects.new(name,mesh); bpy.context.scene.collection.objects.link(obj)
        obj.parent=rig; obj.matrix_parent_inverse=Matrix.Identity(4)
        deform={}
        for bone_name,weight in {name:value for ring in ring_weights for name,value in ring.items()}.items():
            pose_bone=rig.pose.bones[bone_name]; data_bone=rig.data.bones[bone_name]
            deform[bone_name]=pose_bone.matrix@data_bone.matrix_local.inverted()
            obj.vertex_groups.new(name=bone_name)
        for ring_index,weights in enumerate(ring_weights):
            blended=Matrix(((0.,0.,0.,0.),(0.,0.,0.,0.),(0.,0.,0.,0.),(0.,0.,0.,0.)))
            for bone_name,weight in weights.items(): blended+=deform[bone_name]*weight
            inverse=blended.inverted()
            for side_index in range(sides):
                vertex_index=ring_index*sides+side_index
                mesh.vertices[vertex_index].co=inverse@mesh.vertices[vertex_index].co
                for bone_name,weight in weights.items():
                    obj.vertex_groups[bone_name].add([vertex_index],weight,"REPLACE")
        modifier=obj.modifiers.new("Garment skin","ARMATURE"); modifier.object=rig
        obj["garment_detail"]=True

    # Chest seam, collar ring, and zipped closure all project to the current
    # skinned surface and inherit its interpolated bone weights.
    skin_detail("RiderJerseyCollarSeam",
                [(0.075*math.cos(math.tau*i/48),.13+.045*math.sin(math.tau*i/48),1.37)
                 for i in range(49)],jersey_trim,.0012,closed=True)
    skin_detail("RiderJerseyFrontZip",
                [(0,.31,1.42),(0,.27,1.38),(0,.23,1.33),(0,.18,1.28),
                 (0,.13,1.23),(0,.08,1.18)],zipper,.0009)
    for side in ("L","R"):
        sign=-1 if side=="L" else 1
        sleeve=[]
        for i in range(49):
            angle=math.tau*i/48
            sleeve.append((sign*(.19+.036*math.cos(angle)),.145+.036*math.sin(angle),1.25))
        skin_detail(f"RiderJerseySleeveCuff{side}",sleeve,jersey_trim,.0009,closed=True)
        upper=rig.pose.bones.get(f"upperleg02.{side}")
        if upper is None:
            raise RuntimeError(f"missing bib short attachment bone upperleg02.{side}")
        start=rig.matrix_world@upper.head; end=rig.matrix_world@upper.tail
        axis=(end-start).normalized(); across=axis.cross(Vector((0,0,1))).normalized()
        depth=axis.cross(across).normalized(); center=start.lerp(end,.84)
        band=[]
        for i in range(49):
            angle=math.tau*i/48
            band.append(tuple(center+.065*(across*math.cos(angle)+depth*math.sin(angle))))
        skin_detail(f"RiderBibLegGripper{side}",band,bib_trim,.001,closed=True)
    details=[obj for obj in bpy.context.scene.objects if obj.get("garment_detail")]
    if details:
        bpy.ops.object.select_all(action="DESELECT")
        for obj in details: obj.select_set(True)
        bpy.context.view_layer.objects.active=details[0]
        bpy.ops.object.join()
        details[0].name="RiderRoadKitDetails"
        details[0].data.name="RiderRoadKitDetailsMesh"
        details[0]["garment_detail"]=True
    evaluated.to_mesh_clear()


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
    # MPFB's helper-tights label overlaps real wrist transitions on a small
    # set of hand vertices. Those quads still carry deformation-bone weights;
    # preserve the weighted anatomy while removing the isolated proxy panels.
    hand_indices={group.index for group in human.vertex_groups
                  if group.name.startswith(("wrist.","metacarpal","finger"))}
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
            has_hand_skin=any(any(weight.get(index,0.0)>.01 for index in hand_indices)
                              for weight in weights)
            if has_hand_skin:
                continue
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


def _copy_shape_keys_filtered(source, target, old_vertex_ids):
    """Copy MPFB expression keys onto the filtered export mesh."""
    source_keys=source.data.shape_keys
    if source_keys is None:return
    blocks={}
    for source_block in source_keys.key_blocks:
        block=target.shape_key_add(name=source_block.name,from_mix=False)
        for new_index,old_index in enumerate(old_vertex_ids):
            block.data[new_index].co=source_block.data[old_index].co
        block.slider_min=source_block.slider_min;block.slider_max=source_block.slider_max
        block.value=source_block.value;block.mute=source_block.mute
        block.vertex_group=source_block.vertex_group
        blocks[source_block.name]=block
    target.data.shape_keys.use_relative=source_keys.use_relative
    for source_block in source_keys.key_blocks:
        blocks[source_block.name].relative_key=blocks[source_block.relative_key.name]


def _build_mask_baked_mesh_copy(source, target, mask):
    """Bake Mask's body-group vertex filter while retaining skin and morphs."""
    mesh=source.data;group=source.vertex_groups.get(mask.vertex_group)
    if group is None:raise RuntimeError(f"Mask vertex group {mask.vertex_group!r} is missing")
    group_index=group.index;threshold=mask.threshold
    # MPFB's VERTEX_GROUP mask retains only vertices with weight above its
    # threshold. Validate that exact rule against Blender's evaluated result.
    mask_vertex_ids={v.index for v in mesh.vertices if any(
        assignment.group==group_index and assignment.weight>threshold
        for assignment in v.groups)}
    # MPFB's mask also removes the two deliberately unweighted eye surfaces.
    # Preserve those explicit eye-material faces while filtering helper proxies.
    eye_material_indices={i for i,material in enumerate(mesh.materials)
        if material and "Rider Eye" in material.name}
    group_by_index={g.index:g.name for g in source.vertex_groups}
    helper_tights=source.vertex_groups.get("helper-tights")
    joint_cubes=source.vertex_groups.get("JointCubes")
    helper_geometry=source.vertex_groups.get("HelperGeometry")
    hand_groups={g.index for g in source.vertex_groups
        if g.name.startswith(("wrist.","metacarpal","finger"))}
    if helper_tights is None or not hand_groups:
        raise RuntimeError("Expected MPFB helper-tights and weighted hand-deformation groups")
    wrist_group_ids={side:source.vertex_groups[f"wrist.{side}"].index for side in ("L","R")
        if source.vertex_groups.get(f"wrist.{side}") is not None}
    eye_group_ids={side:source.vertex_groups[f"eye.{side}"].index for side in ("L","R")
        if source.vertex_groups.get(f"eye.{side}") is not None}
    wrist_faces={"L":[],"R":[]}
    removed_rig_proxy_faces={"JointCubes":0,"HelperGeometry":0}
    retained=[]
    for polygon in mesh.polygons:
        body=all(i in mask_vertex_ids for i in polygon.vertices)
        eye=polygon.material_index in eye_material_indices
        vertices=[mesh.vertices[i] for i in polygon.vertices]
        # The restored MPFB wrist seam quads are fully tagged helper-tights,
        # like leg proxy faces, but also carry genuine wrist/finger skinning.
        # Keep exactly those weighted seam surfaces; JointCubes controls have
        # no helper-tights tag and remain excluded by the body mask.
        fully_helper_tights=all(any(a.group==helper_tights.index and a.weight>=.999
            for a in vertex.groups) for vertex in vertices)
        has_hand_deformation=sum(any(a.group in hand_groups and a.weight>.01
            for a in vertex.groups) for vertex in vertices)>=2
        retained_wrist_side=None
        if fully_helper_tights and has_hand_deformation:
            for side,wrist_id in wrist_group_ids.items():
                if sum(a.weight for vertex in vertices for a in vertex.groups
                       if a.group==wrist_id)>.01:
                    retained_wrist_side=side
                    break
        # MPFB includes disconnected control cubes in the Human mesh. They
        # carry deformation-bone weights as well as their full proxy-group
        # weights, so the body mask alone does not remove every fingertip
        # controller. Their geometry must not be skinned into the runtime
        # hand. HelperGeometry also marks a disconnected wrist control shell;
        # the 24 verified anatomical wrist seam quads per hand remain because
        # they do not have a full HelperGeometry weight on every corner.
        fully_joint_cube=joint_cubes is not None and all(any(
            a.group==joint_cubes.index and a.weight>=.999 for a in vertex.groups)
            for vertex in vertices)
        fully_helper_geometry=helper_geometry is not None and all(any(
            a.group==helper_geometry.index and a.weight>=.999 for a in vertex.groups)
            for vertex in vertices)
        # MPFB's visible eye shells share HelperGeometry weights, but the
        # adjacent JointCubes are rig controls and must still be removed.
        # Preserve only material-assigned eye faces with matching eye weights.
        anatomical_eye=eye and fully_helper_geometry and not fully_joint_cube and any(
            all(any(a.group==eye_index and a.weight>=.95 for a in vertex.groups)
                for vertex in vertices)
            for eye_index in eye_group_ids.values())
        if fully_joint_cube:
            removed_rig_proxy_faces["JointCubes"]+=1
            continue
        if fully_helper_geometry and retained_wrist_side is None and not anatomical_eye:
            removed_rig_proxy_faces["HelperGeometry"]+=1
            continue
        if retained_wrist_side is not None:
            wrist_faces[retained_wrist_side].append(polygon.index)
        if body or anatomical_eye or retained_wrist_side is not None:
            retained.append(polygon)
    if {side:len(indices) for side,indices in wrist_faces.items()}!={"L":24,"R":24}:
        raise RuntimeError(f"Expected 24 restored wrist faces per side; found "
            f"{ {side:len(indices) for side,indices in wrist_faces.items()} }")
    source_polygons=retained
    old_vertex_ids=sorted({i for p in source_polygons for i in p.vertices})
    remap={old:new for new,old in enumerate(old_vertex_ids)}
    new_mesh=bpy.data.meshes.new(mesh.name+"_GLTFMaskBaked")
    new_mesh.from_pydata([tuple(mesh.vertices[i].co) for i in old_vertex_ids],[],
                         [tuple(remap[i] for i in p.vertices) for p in source_polygons])
    for material in mesh.materials:new_mesh.materials.append(material)
    for source_poly,target_poly in zip(source_polygons,new_mesh.polygons):
        target_poly.material_index=source_poly.material_index
        target_poly.use_smooth=source_poly.use_smooth
    # Preserve UV coordinates and the outfit vertex colors used by Godot.
    for source_uv in mesh.uv_layers:
        target_uv=new_mesh.uv_layers.new(name=source_uv.name)
        for source_poly,target_poly in zip(source_polygons,new_mesh.polygons):
            for old_loop,new_loop in zip(source_poly.loop_indices,target_poly.loop_indices):
                target_uv.data[new_loop].uv=source_uv.data[old_loop].uv
    for source_attr in mesh.color_attributes:
        if source_attr.domain!="POINT":continue
        target_attr=new_mesh.color_attributes.new(name=source_attr.name,
            type=source_attr.data_type,domain=source_attr.domain)
        for new_index,old_index in enumerate(old_vertex_ids):
            target_attr.data[new_index].color=source_attr.data[old_index].color
    target.data=new_mesh
    # Vertex group weights are stored per object/mesh vertex. Transfer the
    # exact surviving influences; the glTF exporter can then choose its own
    # supported payload without us assuming that weight pruning fixes shape.
    if len(target.vertex_groups)==0:
        for source_group in source.vertex_groups:
            target.vertex_groups.new(name=source_group.name)
    if [g.name for g in target.vertex_groups]!=[g.name for g in source.vertex_groups]:
        raise RuntimeError("Mask-baked Human copy has different vertex groups")
    for new_index,old_index in enumerate(old_vertex_ids):
        for assignment in mesh.vertices[old_index].groups:
            target.vertex_groups[assignment.group].add([new_index],assignment.weight,"REPLACE")
    _copy_shape_keys_filtered(source,target,old_vertex_ids)
    if source.data.shape_keys and len(target.data.shape_keys.key_blocks)!=len(source.data.shape_keys.key_blocks):
        raise RuntimeError("Mask-baked Human copy lost MPFB expression keys")
    target.data.update()
    print("GLTF_MASK_COPY", "source_vertices",len(mesh.vertices),"export_vertices",len(old_vertex_ids),
          "source_faces",len(mesh.polygons),"retained_eye_faces",sum(1 for p in source_polygons if p.material_index in eye_material_indices),
          "retained_wrist_faces",{side:len(indices) for side,indices in wrist_faces.items()},
          "export_faces",len(source_polygons),
          "removed_rig_proxy_faces",removed_rig_proxy_faces,
          "shape_keys",len(target.data.shape_keys.key_blocks) if target.data.shape_keys else 0,
          "vertex_groups",len(target.vertex_groups))


def make_gltf_human_copy(human):
    """Copy the skinned mesh and bake MPFB's unsupported helper mask on it.

    Blender's GLB exporter omits the Mask modifier. Exporting the authoring
    Human directly therefore leaks MPFB rig-control proxy geometry at wrists
    and fingertips. This temporary duplicate keeps the editable full mesh and
    skin weights intact while applying only the helper mask to export geometry.
    """
    armature=next((m for m in human.modifiers if m.type=="ARMATURE"),None)
    mask=next((m for m in human.modifiers if m.type=="MASK"),None)
    if armature is None or mask is None:
        raise RuntimeError("Expected weighted Human with armature and MPFB helper-mask modifiers")
    original_name=human.name
    before=len(human.data.polygons)
    human.name="Human_Authoring"
    duplicate=human.copy()
    duplicate.data=human.data.copy()
    duplicate.name="Human"
    bpy.context.scene.collection.objects.link(duplicate)
    try:
        duplicate.parent=human.parent
        duplicate.matrix_parent_inverse=human.matrix_parent_inverse.copy()
        duplicate.matrix_basis=human.matrix_basis.copy()
        arm_copy=next(m for m in duplicate.modifiers if m.type=="ARMATURE")
        mask_copy=next(m for m in duplicate.modifiers if m.type=="MASK")
        original_arm_state=arm_copy.show_viewport,arm_copy.show_render
        arm_copy.show_viewport=False;arm_copy.show_render=False
        _build_mask_baked_mesh_copy(human,duplicate,mask_copy)
        duplicate.modifiers.remove(mask_copy)
        arm_copy=next(m for m in duplicate.modifiers if m.type=="ARMATURE")
        arm_copy.show_viewport,arm_copy.show_render=original_arm_state
        if len(duplicate.vertex_groups)!=len(human.vertex_groups):
            raise RuntimeError("Mask bake lost the Human skin vertex groups")
        expected=[sum(1 for g in v.groups if g.weight>.001)
                  for v in duplicate.data.vertices]
        if not expected or max(expected)==0:
            raise RuntimeError("Mask-baked Human copy has no surviving skin weights")
        print("GLTF_HELPER_MASK_BAKED", "source_faces",before,
              "export_faces",len(duplicate.data.polygons),
              "vertices",len(duplicate.data.vertices),
              "skin_groups",len(duplicate.vertex_groups))
        return duplicate,human,original_name
    except Exception:
        bpy.data.objects.remove(duplicate,do_unlink=True)
        human.name=original_name
        raise


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


def _catmull_rom_path(points, steps_per_span=6):
    """Sample a smooth open curve through the authored cockpit control points."""
    points=[Vector(point) for point in points]
    samples=[]
    for span in range(len(points)-1):
        p0=points[max(0,span-1)]; p1=points[span]
        p2=points[span+1]; p3=points[min(len(points)-1,span+2)]
        for step in range(steps_per_span):
            t=step/steps_per_span; t2=t*t; t3=t2*t
            samples.append(.5*((2*p1)+(-p0+p2)*t+(2*p0-5*p1+4*p2-p3)*t2+
                               (-p0+3*p1-3*p2+p3)*t3))
    samples.append(points[-1])
    return samples


def _swept_round_tube(points, radius, sides=12):
    """Create one continuous capped mesh along a Catmull-Rom path."""
    path=_catmull_rom_path(points)
    vertices=[]; faces=[]
    for index,center in enumerate(path):
        before=path[max(0,index-1)]; after=path[min(len(path)-1,index+1)]
        tangent=(after-before).normalized()
        reference=Vector((0,1,0))
        if abs(tangent.dot(reference))>.92: reference=Vector((0,0,1))
        u=tangent.cross(reference).normalized(); v=tangent.cross(u).normalized()
        for section in range(sides):
            angle=math.tau*section/sides
            vertices.append(tuple(center+radius*(u*math.cos(angle)+v*math.sin(angle))))
    for row in range(len(path)-1):
        for section in range(sides):
            a=row*sides+section; b=row*sides+(section+1)%sides
            faces.append((a,b,b+sides,a+sides))
    faces.extend((tuple(reversed(range(sides))),
                  tuple((len(path)-1)*sides+i for i in range(sides))))
    return vertices,faces


def refine_road_cockpit(bike):
    """Replace the segmented placeholder cockpit with a continuous road bar and fitted controls."""
    bar=bpy.data.objects.get("RoadDropHandlebars")
    if bar is None: raise RuntimeError("RoadDropHandlebars is missing")
    if bar.get("WodraRoadCockpitV3",False): return
    # Coordinates are bike-local. This single path joins the drops, bends and
    # tops on both sides; it removes the capped, disconnected cylinder pieces
    # that read as broken angular bars in the runtime screenshots.
    left=[(0,.22,.920),(-.09,.22,.920),(-.17,.225,.924),
          (-.218,.229,.938),(-.246,.244,.946),(-.270,.266,.932),
          (-.279,.276,.898),(-.274,.264,.858),(-.254,.240,.812),
          (-.226,.212,.765),(-.198,.180,.733)]
    # Keep 15 mm swept tube around a 470 mm hood-centre width, scaled for the
    # existing adult rider while moving the hoods into the original hand reach.
    left=[(x*.85,y,z) for x,y,z in left]
    path=list(reversed(left))+[(-point[0],point[1],point[2]) for point in left[1:]]
    vertices,faces=_swept_round_tube(path,.0115,14)
    new_mesh=bpy.data.meshes.new("RoadDropHandlebarsContinuousMesh")
    new_mesh.from_pydata(vertices,[],faces)
    if bar.data.materials: new_mesh.materials.append(bar.data.materials[0])
    import bmesh
    bm=bmesh.new(); bm.from_mesh(new_mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces)); bm.to_mesh(new_mesh); bm.free()
    old=bar.data; bar.data=new_mesh
    if old.users==0: bpy.data.meshes.remove(old)
    bar.location=(0,0,0); bar.rotation_euler=(0,0,0); bar.scale=(1,1,1)
    bar["WodraRoadCockpitV3"]=True

    rubber=bpy.data.materials.get("Brake Hood Rubber") or mat("Brake Hood Rubber",(.085,.092,.098),.9)
    rubber.diffuse_color=(.085,.092,.098,1)
    if rubber.use_nodes:
        bsdf=next((node for node in rubber.node_tree.nodes if node.type=="BSDF_PRINCIPLED"),None)
        if bsdf: bsdf.inputs["Base Color"].default_value=(.085,.092,.098,1)
    lever_material=bpy.data.materials.get("Brake Lever Alloy") or mat("Brake Lever Alloy",(.12,.14,.15),.82)
    for side in (-1,1):
        suffix="L" if side<0 else "R"
        hood=bpy.data.objects.get("BrakeHood"+suffix)
        if hood is None: raise RuntimeError(f"BrakeHood{suffix} is missing")
        # Match modern long, slim road hoods: 102 mm fore-aft, 33 mm across,
        # and 36 mm high. The top rises about seven degrees and the nose
        # narrows; the rear flare meets the wrapped bar without a bulb.
        center_x=side*.235; center_y=.215
        sections=((-0.051,.005,.008,.944),(-.038,.011,.013,.946),
                  (-.018,.015,.017,.949),(.006,.0165,.018,.952),
                  (.030,.014,.016,.955),(.051,.006,.009,.958))
        hood_vertices=[]; hood_faces=[]; ring=16
        for y_offset,rx,rz,center_z in sections:
            inboard=-side*min(max(y_offset,0)*.12, .006)
            for section in range(ring):
                angle=math.tau*section/ring
                # A flattened superellipse leaves a broad palm shelf and
                # tapered sides instead of the old circular knob profile.
                cx=math.cos(angle); sz=math.sin(angle)
                hood_vertices.append((center_x+inboard+rx*math.copysign(abs(cx)**.62,cx),
                                      center_y+y_offset,
                                      center_z+rz*math.copysign(abs(sz)**.62,sz)))
        for row in range(len(sections)-1):
            for section in range(ring):
                a=row*ring+section; b=row*ring+(section+1)%ring
                hood_faces.append((a,b,b+ring,a+ring))
        hood_faces.extend((tuple(reversed(range(ring))),
                           tuple((len(sections)-1)*ring+i for i in range(ring))))
        hood_mesh=bpy.data.meshes.new("BrakeHood"+suffix+"ErgoShellV2")
        hood_mesh.from_pydata(hood_vertices,[],hood_faces); hood_mesh.materials.append(rubber)
        bm=bmesh.new(); bm.from_mesh(hood_mesh)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces)); bm.to_mesh(hood_mesh); bm.free()
        for polygon in hood_mesh.polygons: polygon.use_smooth=True
        old=hood.data; hood.data=hood_mesh
        if old.users==0: bpy.data.meshes.remove(old)
        hood.location=(0,0,0); hood.rotation_euler=(0,0,0); hood.scale=(1,1,1)
        hood["WodraErgoHoodV3"]=True

        # A narrow, flattened brake blade emerges below the front of the hood.
        lever_name="BrakeLever"+suffix
        lever=bpy.data.objects.get(lever_name)
        if lever is None:
            lever_mesh=bpy.data.meshes.new(lever_name+"V2Mesh")
            lever=bpy.data.objects.new(lever_name,lever_mesh); bpy.context.collection.objects.link(lever)
            lever.parent=bike
        points=[(side*.235,.239,.938),(side*.235,.247,.914),
                (side*.235,.243,.889),(side*.235,.228,.867),
                (side*.235,.208,.853)]
        lever_vertices=[]; lever_faces=[]; sides=10
        for row,point in enumerate(points):
            previous=Vector(points[max(0,row-1)]); following=Vector(points[min(len(points)-1,row+1)])
            tangent=(following-previous).normalized()
            across=Vector((1,0,0))
            across=(across-tangent*across.dot(tangent)).normalized()
            depth=tangent.cross(across).normalized()
            taper=1.0-.34*row/(len(points)-1)
            center=Vector(point)
            for section in range(sides):
                angle=math.tau*section/sides
                vertex=center+across*(.0065*taper*math.cos(angle))+depth*(.0026*taper*math.sin(angle))
                lever_vertices.append(tuple(vertex))
        for row in range(len(points)-1):
            for section in range(sides):
                a=row*sides+section; b=row*sides+(section+1)%sides
                lever_faces.append((a,b,b+sides,a+sides))
        lever_faces.extend((tuple(reversed(range(sides))),
                            tuple((len(points)-1)*sides+i for i in range(sides))))
        lever_mesh=bpy.data.meshes.new(lever_name+"CurvedBladeV2Mesh")
        lever_mesh.from_pydata(lever_vertices,[],lever_faces); lever_mesh.materials.append(lever_material)
        bm=bmesh.new(); bm.from_mesh(lever_mesh)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces)); bm.to_mesh(lever_mesh); bm.free()
        for polygon in lever_mesh.polygons: polygon.use_smooth=True
        old=lever.data; lever.data=lever_mesh
        if old.users==0: bpy.data.meshes.remove(old)
        lever.location=(0,0,0); lever.rotation_euler=(0,0,0); lever.scale=(1,1,1)
        lever.parent=bike
        lever.matrix_parent_inverse=bike.matrix_world.inverted()
    bpy.context.view_layer.update()
    print("ROAD_COCKPIT_V3", "continuous-bar vertices",len(bar.data.vertices),
          "bar dimensions",tuple(round(x,4) for x in bar.dimensions))


def refine_hood_grip_pose():
    """Make a small symmetric relaxed grip correction using the photographed hood posture."""
    rig=bpy.data.objects.get("Human.rig")
    if rig is None or rig.animation_data is None or rig.animation_data.action is None:
        raise RuntimeError("preserved animated rider is missing")
    action=rig.animation_data.action
    if action.get("WodraPhotoGuidedGripV1",False): return
    slot=next((slot for slot in action.slots if slot.identifier=="OB"+rig.name),action.slots[0])
    bag=next((strip.channelbag(slot) for layer in action.layers for strip in layer.strips
              if strip.channelbag(slot)),None)
    if bag is None: raise RuntimeError("PedalCycle channel bag is missing")
    # The source action already contains the measured MPFB hand pose. This
    # correction only reduces the visible MCP fan a few degrees and closes the
    # fingers another 3–5 degrees at each joint. It does not scale bones,
    # change topology or move the wrist/arm anchors.
    adduction={"L":{1:-3,2:-2,3:3,4:3},"R":{1:3,2:-2,3:3,4:-3}}
    for side in ("L","R"):
        for digit,degrees in adduction[side].items():
            path=f'pose.bones["metacarpal{digit}.{side}"].rotation_euler'
            curve=next((fc for fc in bag.fcurves if fc.data_path==path and fc.array_index==2),None)
            if curve is None: raise RuntimeError(f"missing metacarpal bend channel: {path}")
            delta=math.radians(degrees)
            for key in curve.keyframe_points:
                key.co.y+=delta; key.handle_left.y+=delta; key.handle_right.y+=delta
            curve.update()
        for finger in range(1,6):
            for joint,delta in ((1,.045),(2,.07),(3,.05)):
                path=f'pose.bones["finger{finger}-{joint}.{side}"].rotation_euler'
                curve=next((fc for fc in bag.fcurves if fc.data_path==path and fc.array_index==0),None)
                if curve is None: raise RuntimeError(f"missing finger flex channel: {path}")
                for key in curve.keyframe_points:
                    key.co.y+=delta; key.handle_left.y+=delta; key.handle_right.y+=delta
                curve.update()
    action["WodraPhotoGuidedGripV1"]=True
    print("PHOTO_GUIDED_GRIP_V1", "metacarpal adduction",adduction,"joint curl deltas radians",(.045,.07,.05))


def refine_finger_bends_in_bike_space():
    """Pose the existing finger bones with a modest, mirrored hood grip."""
    rig=bpy.data.objects.get("Human.rig")
    bike=bpy.data.objects.get("BicycleModel")
    if rig is None or bike is None or rig.animation_data is None or rig.animation_data.action is None:
        raise RuntimeError("preserved animated rider and bicycle are required")
    action=rig.animation_data.action
    if action.get("WodraBikeSpaceGripFlexV1",False): return
    slot=next((slot for slot in action.slots if slot.identifier=="OB"+rig.name),action.slots[0])
    bag=next((strip.channelbag(slot) for layer in action.layers for strip in layer.strips
              if strip.channelbag(slot)),None)
    if bag is None: raise RuntimeError("PedalCycle channel bag is missing")
    # The authored MPFB fingers have different local axes on mirrored digits.
    # Transform the bike-lateral curl into each evaluated bone frame. Keep the
    # thumb relaxed, the index and middle on the lever, and the other fingers
    # lightly wrapped over the hood. Apply the same measured values each run.
    bend_signs={
        "L":{1:(0,1,1),2:(1,1,1),3:(1,1,1),4:(-1,-1,-1),5:(1,-1,-1)},
        "R":{1:(0,1,1),2:(-1,-1,-1),3:(1,1,1),4:(-1,-1,-1),5:(-1,-1,-1)},
    }
    scene=bpy.context.scene
    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.update()
    bike_lateral=(bike.matrix_world.to_3x3()@Vector((1,0,0))).normalized()
    curl_by_finger={1:(.08,.05,.03),2:(.32,.26,.19),3:(.33,.27,.19),
                    4:(.32,.26,.18),5:(.31,.25,.18)}
    changes=[]
    for side in ("L","R"):
        for finger,joint_signs in bend_signs[side].items():
            for joint,sign in enumerate(joint_signs,1):
                if sign==0: continue
                name=f"finger{finger}-{joint}.{side}"
                bone=rig.pose.bones.get(name)
                if bone is None or bone.rotation_mode!="XYZ":
                    raise RuntimeError(f"expected XYZ deformation bone {name}")
                path=f'pose.bones["{name}"].rotation_euler'
                curves=[next((fc for fc in bag.fcurves
                              if fc.data_path==path and fc.array_index==axis),None)
                        for axis in range(3)]
                if any(curve is None or not curve.keyframe_points for curve in curves):
                    raise RuntimeError(f"incomplete finger rotation channels: {path}")
                current=bone.rotation_euler.copy()
                current_world=(rig.matrix_world@bone.matrix).to_3x3().to_quaternion()
                axis_local=current_world.inverted()@bike_lateral
                curl=curl_by_finger[finger][joint-1]
                corrected=current.to_quaternion()@Quaternion(axis_local,sign*curl)
                corrected_euler=corrected.to_euler("XYZ",current)
                deltas=[corrected_euler[axis]-current[axis] for axis in range(3)]
                for axis,curve in enumerate(curves):
                    for key in curve.keyframe_points:
                        key.co.y+=deltas[axis]
                        key.handle_left.y+=deltas[axis]
                        key.handle_right.y+=deltas[axis]
                    curve.update()
                changes.append((name,sign,curl,tuple(round(value,4) for value in deltas)))
                bpy.context.view_layer.update()
    action["WodraBikeSpaceGripFlexV1"]=True
    print("BIKE_SPACE_GRIP_FLEX",changes)


def seat_hands_on_hood_shelves():
    """Translate only the existing hand IK targets using a measured rig-space Jacobian."""
    rig=bpy.data.objects.get("Human.rig")
    if rig is None or rig.animation_data is None or rig.animation_data.action is None:
        raise RuntimeError("preserved animated rider is missing")
    action=rig.animation_data.action
    if action.get("WodraHoodShelfFitV1",False): return
    slot=next((slot for slot in action.slots if slot.identifier=="OB"+rig.name),action.slots[0])
    bag=next((strip.channelbag(slot) for layer in action.layers for strip in layer.strips
              if strip.channelbag(slot)),None)
    if bag is None: raise RuntimeError("PedalCycle channel bag is missing")
    scene=bpy.context.scene; scene.frame_set(scene.frame_start); bpy.context.view_layer.update()
    # The hands sit slightly inboard, 35 mm forward, and 15 mm lower so their
    # palm heels meet the new shelves. The local channel correction comes from
    # the evaluated MPFB control Jacobian; mirrored bone axes are measured.
    corrections=[]
    for prefix,world_delta in (("left",Vector((.015,.035,-.015))),
                               ("right",Vector((-.015,.035,-.015)))):
        bone=rig.pose.bones[prefix+"_hand_ik"]
        origin=(rig.matrix_world @ bone.matrix).translation.copy()
        original=bone.location.copy(); columns=[]
        for axis in range(3):
            bone.location[axis]+=0.01; bpy.context.view_layer.update()
            moved=(rig.matrix_world @ bone.matrix).translation.copy()
            columns.append((moved-origin)/0.01)
            bone.location=original; bpy.context.view_layer.update()
        jacobian=Matrix(((columns[0].x,columns[1].x,columns[2].x),
                         (columns[0].y,columns[1].y,columns[2].y),
                         (columns[0].z,columns[1].z,columns[2].z)))
        local_delta=jacobian.inverted() @ world_delta
        corrections.append((prefix,world_delta,local_delta))
    for prefix,world_delta,local_delta in corrections:
        path=f'pose.bones["{prefix}_hand_ik"].location'
        curves=[fc for fc in bag.fcurves if fc.data_path==path]
        if {fc.array_index for fc in curves}!={0,1,2}:
            raise RuntimeError(f"incomplete hand target channels: {path}")
        for curve in curves:
            delta=local_delta[curve.array_index]
            for key in curve.keyframe_points:
                key.co.y+=delta; key.handle_left.y+=delta; key.handle_right.y+=delta
            curve.update()
        print("HOOD_SHELF_HAND_FIT",prefix,"worldDelta",tuple(round(v,4) for v in world_delta),
              "measuredLocalDelta",tuple(round(v,5) for v in local_delta))
    action["WodraHoodShelfFitV1"]=True


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
    # Preserve a polished, already tagged cockpit on repeat exports. The older
    # initialization path below creates temporary cylindrical levers before the
    # first cockpit refinement; rebuilding those on every run would overwrite
    # the finished blade profile.
    bar=bpy.data.objects.get("RoadDropHandlebars")
    if not (bar and bar.get("WodraRoadCockpitV3",False)):
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
        refine_road_cockpit(bike)
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
    export_only="--export-only" in args
    rider_only="--rider-only" in args
    args=[arg for arg in args if arg not in ("--head-only","--skip-previews","--export-only","--rider-only")]
    if len(args)!=3: raise SystemExit("expected output GLB, output blend, preview directory [--head-only] [--skip-previews] [--export-only] [--rider-only]")
    glb,blend,preview=map(Path,args)
    glb.parent.mkdir(parents=True,exist_ok=True); blend.parent.mkdir(parents=True,exist_ok=True)
    rig=bpy.data.objects.get("metarig") or next((o for o in bpy.data.objects if o.type=="ARMATURE"),None)
    if rig is None: raise RuntimeError("preserved cyclist blend has no armature")
    print("PRESERVED", "bones",len(rig.data.bones),"actions",[a.name for a in bpy.data.actions])
    print("HEAD_BONE",rig.data.bones.get("head"))
    if not export_only:
        make_helmet(rig)
        add_road_kit_garment_details(rig)
        refine_human_eyes(rig)
        round_clothing_edges()
        if not rider_only:
            refine_brake_hoods()
            refine_road_bike()
        refine_hood_grip_pose()
        refine_finger_bends_in_bike_space()
        if not rider_only:
            seat_hands_on_hood_shelves()
        extend_shoe_uppers(rig)
        # Keep the accepted authored MPFB hand pose and its existing animation
        # curves intact. The prior alpha48 pass overwrote every finger's XYZ Euler
        # rotation with an assumed common local X curl and translated the wrist
        # root an extra 30 mm. Inspection showed that this authored stage, before
        # export, pinched the webbing and created sharp knuckle/wrist deformation.
        if not skip_previews:
            inspect_and_render(preview,rig)
            if not head_only:
                render_full_views(preview/"full_rider_review")
                render_hand_contact_views(preview/"hand_contact")
                render_cycle(preview/"animated_side",rig)
        # Save a non-destructive review checkpoint and export full weighted model/action.
        bpy.ops.wm.save_as_mainfile(filepath=str(blend))
    # Keep the editable checkpoint on the Blender authoring shader network;
    # only the temporary export copy gets glTF's direct COLOR_0 connection.
    export_human,authoring_human,authoring_name=make_gltf_human_copy(bpy.data.objects["Human"])
    configure_gltf_vertex_color_material()
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects:
        if obj is not authoring_human: obj.select_set(True)
    bpy.context.view_layer.objects.active=bpy.data.objects.get("BicycleModel")
    try:
        bpy.ops.export_scene.gltf(filepath=str(glb),export_format="GLB",use_selection=True,
            export_animations=True,export_frame_range=True,export_force_sampling=True,export_animation_mode="ACTIONS")
        print("EXPORTED",glb)
    finally:
        bpy.data.objects.remove(export_human,do_unlink=True)
        authoring_human.name=authoring_name


if __name__=="__main__": main()
