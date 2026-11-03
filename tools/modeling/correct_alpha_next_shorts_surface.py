"""Blend the shorts cleft into its bilateral rear fabric with a tapered mesh edit."""
import bpy,bmesh,os,sys,math
from mathutils import Vector
args=sys.argv[sys.argv.index('--')+1:]
if len(args)!=2: raise RuntimeError('Expected candidate blend and GLB paths')
blend_output,glb_output=map(os.path.abspath,args)
shorts=bpy.data.objects['RiderBibShortsShell']; rig=bpy.data.objects['Human.rig']
front=next((o for o in bpy.context.scene.objects if 'FrontWheelAssembly' in o.name),None)
rear=next((o for o in bpy.context.scene.objects if 'RearWheelAssembly' in o.name),None)
if front is None or rear is None: raise RuntimeError('Production bike assemblies are required')
rearward=(shorts.matrix_world.to_3x3().inverted() @ -(front.matrix_world.translation-rear.matrix_world.translation).normalized()).normalized()
bm=bmesh.new(); bm.from_mesh(shorts.data); bm.verts.ensure_lookup_table()
weights_before=[{g.group:g.weight for g in v.groups} for v in shorts.data.vertices]
faces_before=[tuple(p.vertices) for p in shorts.data.polygons]
# The connected rear shorts patch around the cleft; the front panel and inner-thigh
# separation are excluded by the rearward depth and height/lateral bounds.
patch={v for v in bm.verts if abs(v.co.x)<=.068 and .835<=v.co.z<=.958 and rearward.dot(v.co)>.035}
if len(patch)<40: raise RuntimeError(f'Expected connected rear fabric patch, got {len(patch)}')
remaining=set(patch); components=[]
while remaining:
 seed=remaining.pop(); stack=[seed]; comp={seed}
 while stack:
  v=stack.pop()
  for e in v.link_edges:
   n=e.other_vert(v)
   if n in remaining: remaining.remove(n); comp.add(n); stack.append(n)
 components.append(comp)
if len(components)!=1: raise RuntimeError(f'Patch is not one continuous region: {[len(c) for c in components]}')

rear_verts=[v for v in bm.verts if rearward.dot(v.co)>.035 and abs(v.co.x)<.075 and .835<v.co.z<.958]
def side_reference(z):
 # Find the bilateral rounded fabric just outside the cleft, symmetrically.
 left=[v for v in rear_verts if -.042<=v.co.x<=-.015 and abs(v.co.z-z)<=.020]
 right=[v for v in rear_verts if .015<=v.co.x<=.042 and abs(v.co.z-z)<=.020]
 if not left or not right: return None
 score=lambda v: abs(abs(v.co.x)-.026)+1.5*abs(v.co.z-z)
 a=min(left,key=score); b=min(right,key=score)
 return (rearward.dot(a.co)+rearward.dot(b.co))*.5

# Blend the central crease and its shoulders toward the paired fabric contour.
# A broad lateral/vertical Gaussian leaves the rounded lobes and inner-thigh edge intact.
changed=[]; max_delta=0.0
for v in patch:
 ref=side_reference(v.co.z)
 if ref is None: continue
 target=ref-.0015 # retain a subtle 1.5 mm central inset, not a ridge
 xfade=math.exp(-((v.co.x/.023)**2))
 zfade=math.exp(-(((v.co.z-.890)/.044)**2))
 influence=xfade*zfade
 current=rearward.dot(v.co)
 delta=max(0.0,(target-current)*influence)
 if delta>1e-6:
  v.co += rearward*delta
  changed.append(v); max_delta=max(max_delta,delta)
if len(changed)<8: bm.free(); raise RuntimeError(f'Expected broader bilateral smoothing, changed {len(changed)} vertices')
zero=sum(f.calc_area()<=1e-10 for f in bm.faces)
dup=len(bm.faces)-len({tuple(sorted(v.index for v in f.verts)) for f in bm.faces})
if zero or dup: bm.free(); raise RuntimeError(f'Invalid topology zero={zero} duplicate={dup}')
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces)); bm.normal_update(); bm.to_mesh(shorts.data); bm.free(); shorts.data.update()
if len(shorts.data.vertices)!=len(weights_before) or [tuple(p.vertices) for p in shorts.data.polygons]!=faces_before:
 raise RuntimeError('Shorts correction changed vertex count or face topology')
for index,vertex in enumerate(shorts.data.vertices):
 after={g.group:g.weight for g in vertex.groups}
 before=weights_before[index]
 if after.keys()!=before.keys() or any(abs(after[k]-before[k])>1e-6 for k in before):
  raise RuntimeError(f'Shorts correction changed skin weights at vertex {index}')
print(f'Bilateral cleft blend: connected patch={len(patch)} verts changed={len(changed)} max outward={max_delta*1000:.2f}mm target inset=1.5mm; weights/topology unchanged')
bpy.ops.wm.save_as_mainfile(filepath=blend_output)
bpy.context.view_layer.update()
jersey=bpy.data.objects.get('RiderJerseyShell')
if jersey is None: raise RuntimeError('Existing editable jersey source is missing')
rig_world=rig.matrix_world.copy(); rig.parent=None; rig.matrix_world=rig_world
for obj in (shorts,jersey):
 world=obj.matrix_world.copy(); obj.parent=rig; obj.matrix_world=world
bpy.ops.object.select_all(action='DESELECT')
for obj in (rig,shorts,jersey): obj.select_set(True)
bpy.context.view_layer.objects.active=rig
bpy.ops.export_scene.gltf(filepath=glb_output,export_format='GLB',use_selection=True,export_apply=True,export_skins=True,export_animations=False,export_yup=True,export_extras=True,export_materials='EXPORT',export_image_format='AUTO')
print(f'Saved bilateral shorts source={blend_output} GLB={glb_output}')
