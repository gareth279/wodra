"""Replace Wodra's distorted MPFB hands with an attributed, sculpted hood grip.

Inputs are the canonical fitted rider blend and Andy Woodhead's CC BY 4.0
printable cyclist GLB supplied by the owner. The accepted bike is untouched.
The source is a posed sculpture, so its grip is the new hand rig's rest pose.
Existing PedalCycle wrist motion drives it without repeatedly solving finger IK.
"""
import argparse
import math
import sys
from pathlib import Path

import bpy
import bmesh
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.path.insert(0,str(Path(__file__).resolve().parent))
import polish_rider
import fit_rider_159

SOURCE_TITLE='Cyclist - racing position - free 3d printable'
SOURCE_AUTHOR='Andy Woodhead'
SOURCE_URL='https://sketchfab.com/3d-models/cyclist-racing-position-free-3d-printable-f97acacd76be484dbf0243d82f6a1c44'
SOURCE_LICENSE='CC BY 4.0 https://creativecommons.org/licenses/by/4.0/'
HAND_SCALE=.315
# The two source controls are not parallel to the Wodra brifter shelves. Apply
# a mirrored roll around each rider-forward forearm axis so both thumbs wrap
# down the inboard side of the hood instead of opening away from the control.
HAND_ROLL_DEGREES={'L':25.0,'R':-25.0}
# Mean source-surface points where each hand actually contacts the printable
# cyclist's own drop-bar controls (computed from hand/control mesh proximity).
SOURCE_GRIP_POINTS={
    'L':Vector((1.5420,.5922,1.2092)),
    'R':Vector((1.5474,-.6233,1.1716)),
}


def mesh_object(name,vertices,faces):
    mesh=bpy.data.meshes.new(name+'Mesh');mesh.from_pydata(vertices,[],faces);mesh.update()
    obj=bpy.data.objects.new(name,mesh);bpy.context.scene.collection.objects.link(obj)
    return obj


def source_hands(path):
    existing=set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    imported=set(bpy.data.objects)-existing
    sources=[o for o in imported if o.type=='MESH' and o.name.startswith('UMesh_Figure_1')]
    if len(sources)!=3:raise RuntimeError('Expected the three split sculpted-rider meshes')
    output={}
    for side,sign in [('L',1),('R',-1)]:
        vertices=[];faces=[]
        for obj in sources:
            points=[obj.matrix_world@v.co for v in obj.data.vertices]
            # Limit the extraction to the actual bar-end hand volume before
            # cutting at the wrist. Selecting an entire lateral half also
            # retained unrelated shoulder/torso surfaces which merged into a
            # broad mitten after fitting. These bounds include the complete
            # source fingers, thumb and wrist around its grip anchor.
            selected={i for i,v in enumerate(points)
                      if v.x>.78 and .28<sign*v.y<1.05 and .62<v.z<1.90}
            polys=[p for p in obj.data.polygons if all(i in selected for i in p.vertices)]
            ids=sorted({i for p in polys for i in p.vertices})
            remap={old:len(vertices)+new for new,old in enumerate(ids)}
            vertices.extend(points[i] for i in ids);faces.extend(tuple(remap[i] for i in p.vertices) for p in polys)
        hand=mesh_object('SculptedHoodHand.'+side,vertices,faces)
        bm=bmesh.new();bm.from_mesh(hand.data)
        bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0001)
        # The source rider is posed on its own road controls. Cut at the
        # anatomical wrist, where the forearm narrows into the palm, rather
        # than through the middle of the forearm. The previous 45-degree cut
        # removed nearly a tenth of a metre of forearm and left an oversized,
        # poorly oriented wrist opening.
        bmesh.ops.bisect_plane(bm,geom=list(bm.verts)+list(bm.edges)+list(bm.faces),
            plane_co=Vector((1.34,sign*.70,1.42)),plane_no=Vector((.86,0,-.51)).normalized(),
            clear_inner=True,clear_outer=False,dist=.00001)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(hand.data);bm.free()
        for p in hand.data.polygons:p.use_smooth=True
        hand['source_author']=SOURCE_AUTHOR;hand['source_license']=SOURCE_LICENSE;hand['source_url']=SOURCE_URL
        output[side]=hand
        print('EXTRACTED_HAND',side,len(hand.data.vertices),len(hand.data.polygons))
    for obj in imported:bpy.data.objects.remove(obj,do_unlink=True)
    return output


def clean_body(human):
    clean,original,name=polish_rider.make_gltf_human_copy(human)
    bpy.data.objects.remove(original,do_unlink=True);clean.name=name
    if clean.data.shape_keys:
        print('BAKED_APPEARANCE_KEYS',[(k.name,round(k.value,4)) for k in clean.data.shape_keys.key_blocks if abs(k.value)>.00001])
        mixed=clean.shape_key_add(name='BakedAppearance',from_mix=True)
        points=[p.co.copy() for p in mixed.data]
        clean.shape_key_clear()
        for vertex,point in zip(clean.data.vertices,points):vertex.co=point
    hand_groups={g.index for g in clean.vertex_groups if g.name.startswith(('wrist.','metacarpal','finger'))}
    bm=bmesh.new();bm.from_mesh(clean.data);weights=bm.verts.layers.deform.active
    removed=[v for v in bm.verts if sum(w for group,w in v[weights].items() if group in hand_groups)>.55]
    bmesh.ops.delete(bm,geom=removed,context='VERTS');bm.to_mesh(clean.data);bm.free()
    print('REMOVED_OLD_HAND_VERTICES',len(removed))
    return clean


def boundary_rings(obj):
    bm=bmesh.new();bm.from_mesh(obj.data);bm.verts.ensure_lookup_table()
    edges={e for e in bm.edges if e.is_boundary};loops=[]
    while edges:
        first=edges.pop();verts={*first.verts};queue=[*first.verts]
        while queue:
            v=queue.pop()
            for edge in v.link_edges:
                if edge in edges:
                    edges.remove(edge);other=edge.other_vert(v)
                    if other not in verts:verts.add(other);queue.append(other)
        loops.append([v.index for v in verts])
    bm.free();return loops


def source_wrist_target(hand,side):
    """Map the sculpt's true wrist cut to Wodra space using its source bars."""
    sign=-1 if side=='L' else 1
    turn=(Matrix.Rotation(math.pi/2,3,'Z') @
          Matrix.Rotation(math.radians(HAND_ROLL_DEGREES[side]),3,'X'))
    source_anchor=SOURCE_GRIP_POINTS[side]
    target=Vector((sign*.222,.502,.948))
    loops=boundary_rings(hand)
    ring=max(loops,key=len)
    center=sum((hand.data.vertices[i].co for i in ring),Vector())/len(ring)
    return target+turn@(center-source_anchor)*HAND_SCALE


def retarget_arms_to_sculpt(rig,desired_wrists,desired_palms):
    """Bake arm IK so each sculpted palm support stays on its hood."""
    fit_rider_159.enable_arm_ik(rig)
    scene=bpy.context.scene
    start,end=scene.frame_start,scene.frame_end
    if rig.animation_data is None or rig.animation_data.action is None:
        raise RuntimeError('PedalCycle action is required to refit the rider arms')
    offsets={'left':Vector((-.005,.101,-.085)),
             'right':Vector((.005,.101,-.085))}
    rig_inverse=rig.matrix_world.inverted()
    palm_targets={side:rig_inverse@target for side,target in desired_palms.items()}
    palm_local={}
    marker_rest={}
    for frame in range(start,end+1):
        scene.frame_set(frame);bpy.context.view_layer.update()
        for side in ('left','right'):
            suffix='L' if side=='left' else 'R'
            wrist=rig.pose.bones['wrist.'+suffix]
            wrist_world=rig.matrix_world@wrist.matrix
            hand_axis=rig.matrix_world.to_3x3()@(wrist.tail-wrist.head)
            hand_rotation=(hand_axis.normalized().rotation_difference(Vector((0,1,0)))@
                           wrist_world.to_quaternion())
            if frame==start:
                desired_wrist_world=desired_wrists[suffix]
            else:
                # The sculpt is rigid to the wrist bone. Compensate the wrist
                # translation for its changing orientation so the actual palm
                # support point, rather than the discarded finger bones, stays
                # on the fixed hood contact through the cadence cycle.
                desired_wrist_rig=palm_targets[side]-wrist.matrix.to_3x3()@palm_local[side]
                desired_wrist_world=rig.matrix_world@desired_wrist_rig
            control_position=desired_wrist_world+offsets[side]
            control=fit_rider_159.set_control_world(
                rig,side+'_hand_ik',control_position,hand_rotation.to_matrix())
            elbow=fit_rider_159.set_control_world(rig,side+'_elbow_ik',fit_rider_159.ELBOW_TARGETS[side])
            for _ in range(5):
                bpy.context.view_layer.update()
                wrist=rig.pose.bones['wrist.'+suffix]
                if frame==start:
                    actual=rig.matrix_world@wrist.head
                    correction=desired_wrists[suffix]-actual
                else:
                    actual=rig.matrix_world@(wrist.matrix@palm_local[side])
                    correction=desired_palms[side]-actual
                if correction.length<.00075:break
                control_position+=correction
                control=fit_rider_159.set_control_world(
                    rig,side+'_hand_ik',control_position,hand_rotation.to_matrix())
            bpy.context.view_layer.update()
            wrist=rig.pose.bones['wrist.'+suffix]
            if frame==start:
                # Record the real hood support point in the wrist's local pose
                # frame. This is also the geometric assertion exported for
                # Godot's full-cycle production-scene regression.
                palm_local[side]=wrist.matrix.inverted()@palm_targets[side]
                marker_rest[side]=wrist.bone.matrix_local@palm_local[side]
            control.keyframe_insert(data_path='location',frame=frame)
            control.keyframe_insert(data_path='rotation_quaternion',frame=frame)
            elbow.keyframe_insert(data_path='location',frame=frame)
            elbow.keyframe_insert(data_path='rotation_quaternion',frame=frame)
            bpy.context.view_layer.update()

    if bpy.context.object and bpy.context.object.mode!='OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    bpy.ops.object.mode_set(mode='EDIT')
    for side in ('L','R'):
        name='palm_contact.'+side
        old=rig.data.edit_bones.get(name)
        if old:rig.data.edit_bones.remove(old)
        marker=rig.data.edit_bones.new(name)
        marker.head=marker_rest['left' if side=='L' else 'right']
        marker.tail=marker.head+Vector((0,0,.025))
        marker.parent=rig.data.edit_bones['wrist.'+side]
        marker.use_connect=False
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.objects.active=rig;rig.select_set(True)
    bpy.ops.object.mode_set(mode='POSE')
    bpy.ops.nla.bake(frame_start=start,frame_end=end,step=1,only_selected=False,
        visual_keying=True,clear_constraints=True,use_current_action=False,bake_types={'POSE'})
    bpy.ops.object.mode_set(mode='OBJECT')
    action=rig.animation_data.action if rig.animation_data else None
    if action is None:raise RuntimeError('Could not bake arms to the source wrist seams')
    action.name='PedalCycle'
    scene.frame_set(start);bpy.context.view_layer.update()
    errors={}
    for side in ('L','R'):
        actual=rig.matrix_world@rig.pose.bones['wrist.'+side].head
        errors[side]=(actual-desired_wrists[side]).length
        print('SCULPT_WRIST_IK',side,'desired',list(desired_wrists[side]),'actual',list(actual),'error',round(errors[side],4))
    if max(errors.values())>.04:
        raise RuntimeError(f'Arm IK could not reach the hand mesh wrist seams: {errors}')
    palm_errors={}
    for frame in range(start,end+1):
        scene.frame_set(frame);bpy.context.view_layer.update()
        for side,hand_side in [('L','left'),('R','right')]:
            marker=rig.pose.bones['palm_contact.'+side]
            actual=rig.matrix_world@marker.matrix.translation
            error=(actual-desired_palms[hand_side]).length
            palm_errors[side]=max(palm_errors.get(side,0.0),error)
    print('SCULPT_PALM_CONTACT_MAX', {side:round(value,4) for side,value in palm_errors.items()})
    if max(palm_errors.values())>.02:
        raise RuntimeError(f'Sculpted palm support left a hood during PedalCycle: {palm_errors}')


def fit_hand(hand,side,rig,body):
    sign=-1 if side=='L' else 1
    # The sculpture uses +X forward, +Z up and is 2.86 times life size.
    # Its supplied drop bar is the placement reference: the handlebar's grip
    # center (1.50, +/-0.634, 1.20) maps to Wodra's measured palm contact.
    # This keeps the posed fingers registered to the bar they actually hold.
    scale=HAND_SCALE
    turn=(Matrix.Rotation(math.pi/2,3,'Z') @
          Matrix.Rotation(math.radians(HAND_ROLL_DEGREES[side]),3,'X'))
    source_anchor=SOURCE_GRIP_POINTS[side]
    target=Vector((sign*.222,.502,.948))
    source_points=[v.co.copy() for v in hand.data.vertices]
    fitted=[target+turn@(p-source_anchor)*scale for p in source_points]
    wrist=rig.pose.bones['wrist.'+side]
    depsgraph=bpy.context.evaluated_depsgraph_get();eval_body=body.evaluated_get(depsgraph)
    body_points=[body.matrix_world@v.co for v in eval_body.data.vertices]
    rings=boundary_rings(body)
    wrist_world=rig.matrix_world@wrist.head
    body_ring=min(rings,key=lambda loop:((sum((body_points[i] for i in loop),Vector())/len(loop))-wrist_world).length)
    body_center=sum((body_points[i] for i in body_ring),Vector())/len(body_ring)
    hand_ring=max(boundary_rings(hand),key=len)
    hand_center=sum((fitted[i] for i in hand_ring),Vector())/len(hand_ring)
    lowerarm=rig.pose.bones['lowerarm02.'+side]
    axis=(rig.matrix_world.to_3x3()@(lowerarm.tail-lowerarm.head)).normalized()
    destination=body_center+axis*.006
    delta=destination-hand_center
    source_normal=Vector((.86,0,-.51)).normalized()
    plane=Vector((1.34,sign*.70,1.42))
    for i,p in enumerate(source_points):
        d=max(0,(p-plane).dot(source_normal))
        # Keep the supplied hand's complete curled silhouette. Only blend the
        # wrist cut ring into the existing forearm seam; finger proportions
        # and the opposed thumb are part of the source sculpture.
        t=max(0,1-d/.18);t=t*t*(3-2*t)
        fitted[i]+=delta*t
    skin_inverse=wrist.bone.matrix_local@wrist.matrix.inverted()@rig.matrix_world.inverted()
    hand.matrix_world=rig.matrix_world.copy()
    for vertex,p in zip(hand.data.vertices,fitted):vertex.co=skin_inverse@p
    hand.data.update()
    group=hand.vertex_groups.new(name='wrist.'+side);group.add(list(range(len(hand.data.vertices))),1,'REPLACE')
    hand.data.materials.clear();hand.data.materials.append(body.data.materials[0])
    colors=hand.data.color_attributes.new(name='RiderOutfitWeights',type='FLOAT_COLOR',domain='POINT')
    for color in colors.data:color.color=(0,0,0,1)
    hand.data.color_attributes.active_color=colors
    modifier=hand.modifiers.new('Armature','ARMATURE');modifier.object=rig
    hand.parent=rig;hand.matrix_parent_inverse=rig.matrix_world.inverted();hand.matrix_world=rig.matrix_world.copy()
    print('HAND_FIT',side,'body_center',list(body_center),'sculpt_wrist',list(hand_center),'collar_delta',list(delta),'body_loop',len(body_ring),'hand_loop',len(hand_ring))
    return body_ring,hand_ring


def validate_palm_surface(hand,side,target):
    """Check the evaluated, skinned hand surface reaches the real hood shelf."""
    depsgraph=bpy.context.evaluated_depsgraph_get()
    evaluated=hand.evaluated_get(depsgraph)
    mesh=evaluated.to_mesh()
    try:
        tree=BVHTree.FromPolygons([vertex.co for vertex in mesh.vertices],
                                  [polygon.vertices for polygon in mesh.polygons])
        local_target=evaluated.matrix_world.inverted()@target
        nearest=tree.find_nearest(local_target)
        if nearest is None:raise RuntimeError(f'{side} sculpted hand has no evaluated surface')
        distance=float(nearest[3])
        print('SCULPT_PALM_SURFACE',side,'distance_m',round(distance,4),
              'point',list(evaluated.matrix_world@nearest[0]))
        if distance>.04:
            raise RuntimeError(f'{side} actual hand surface misses hood shelf by {distance:.3f}m')
    finally:
        evaluated.to_mesh_clear()


def join_hands(body,hands):
    bpy.ops.object.select_all(action='DESELECT');body.select_set(True)
    for hand in hands.values():hand.select_set(True)
    bpy.context.view_layer.objects.active=body;bpy.ops.object.join()
    body['WodraSculptedHoodHandsV1']=True
    body['WodraSculptedHoodHandsAuthor']=SOURCE_AUTHOR
    body['WodraSculptedHoodHandsLicense']=SOURCE_LICENSE
    body['WodraSculptedHoodHandsSource']=SOURCE_URL
    # The geometry is already the runtime body, with the helper mask baked.
    body['WodraRuntimeMeshReady']=True


def export(blend,glb):
    bpy.ops.wm.save_as_mainfile(filepath=str(blend),compress=True)
    polish_rider.configure_gltf_vertex_color_material()
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.export_scene.gltf(filepath=str(glb),export_format='GLB',use_selection=True,
        export_animations=True,export_frame_range=True,export_force_sampling=True,export_animation_mode='ACTIONS')
    print('SCULPTED_HANDS_EXPORTED',glb)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--base',type=Path,required=True);parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--blend',type=Path,required=True);parser.add_argument('--glb',type=Path,required=True)
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    bpy.ops.wm.open_mainfile(filepath=str(args.base))
    rig=bpy.data.objects['Human.rig'];bpy.context.scene.frame_set(1);bpy.context.view_layer.update()
    hands=source_hands(args.source)
    desired_wrists={side:source_wrist_target(hand,side) for side,hand in hands.items()}
    desired_palms={'left':Vector((-.222,.502,.948)),'right':Vector((.222,.502,.948))}
    retarget_arms_to_sculpt(rig,desired_wrists,desired_palms)
    body=clean_body(bpy.data.objects['Human']);bpy.context.view_layer.update()
    for side,hand in hands.items():
        fit_hand(hand,side,rig,body)
        validate_palm_surface(hand,side,desired_palms['left' if side=='L' else 'right'])
    join_hands(body,hands)
    args.blend.parent.mkdir(parents=True,exist_ok=True);args.glb.parent.mkdir(parents=True,exist_ok=True)
    export(args.blend,args.glb)

if __name__=='__main__':main()




\r\n