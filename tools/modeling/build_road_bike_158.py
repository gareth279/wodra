"""Build the #158 road-bike geometry pass in the preserved Wodra cyclist blend.

Run in Blender 5.2 against source/WodraCyclist.blend:
  blender --background source/WodraCyclist.blend --python build_road_bike_158.py -- <output.blend>
Then export the complete model with polish_rider.py --export-only so the accepted
MPFB export mask and outfit attributes remain unchanged.
"""
from __future__ import annotations
import math
import sys
import hashlib
from pathlib import Path
import bpy
from mathutils import Vector

# Size-54 modern road-bike reference dimensions (metres): 978 mm wheelbase,
# 410 mm chainstay, 74 mm BB drop, 420 mm bar, 100 mm stem, 170 mm crank.
REAR_Y, FRONT_Y, WHEEL_Z = -.489, .489, .339
BB_Y, BB_Z = -.079, .265
SEAT_Y, SEAT_Z = -.240, .845
HEAD_LOW = (.0, .343, .660)
HEAD_HIGH = (.0, .3065, .778)
BAR_CENTRE = (.0, .365, .854)
HOOD_PALM_X, HOOD_PALM_Y, HOOD_PALM_Z = .210, .421, .900
SADDLE_Y, SADDLE_Z = -.253, .951


def material(name, color, roughness, metallic=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (*color, 1)
    m.use_nodes = True
    p = m.node_tree.nodes.get("Principled BSDF")
    p.inputs["Base Color"].default_value = (*color, 1)
    p.inputs["Roughness"].default_value = roughness
    p.inputs["Metallic"].default_value = metallic
    return m

FRAME = material("Wodra Road Frame | deep teal satin", (.055, .285, .315), .42, .20)
CARBON = material("Wodra Road Carbon", (.035, .047, .052), .48, .10)
ALLOY = material("Wodra Machined Alloy", (.26, .29, .31), .62, .44)
DARK_METAL = material("Wodra Dark Anodised Alloy", (.075, .09, .10), .50, .62)
RUBBER = material("Wodra Road Tyre Rubber", (.014, .018, .021), .94, .0)
TAPE = material("Wodra Bar Tape", (.045, .052, .056), .88, .0)
HOOD = material("Wodra Brifter Rubber", (.028, .034, .037), .76, .0)
LEVER = material("Wodra Brifter Satin Graphite", (.028, .032, .035), .58, .08)
PIVOT = material("Wodra Brifter Pivot Alloy", (.22, .25, .27), .48, .60)
ROTOR = material("Wodra Stainless Rotor", (.32, .35, .36), .55, .58)


def obj_mesh(name, verts, faces, mat, parent, smooth=True):
    mesh = bpy.data.meshes.new(name + "Mesh")
    mesh.from_pydata(verts, [], faces)
    mesh.materials.append(mat)
    import bmesh
    bm = bmesh.new(); bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh); bm.free()
    for p in mesh.polygons: p.use_smooth = smooth
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.matrix_parent_inverse.identity()
    obj.location = (0, 0, 0)
    return obj


def tube_between(name, a, b, r0, mat, parent, r1=None, sides=12):
    a, b = Vector(a), Vector(b)
    axis = (b-a).normalized()
    # All frame tubes lie in a longitudinal plane; keep their lateral section
    # consistently aligned to the bicycle's x-axis.
    u = Vector((1, 0, 0))
    if abs(axis.dot(u)) > .96: u = Vector((0, 1, 0))
    u = (u-axis*axis.dot(u)).normalized()
    v = axis.cross(u).normalized()
    r1 = r0 if r1 is None else r1
    verts=[]; faces=[]
    for center, radius in ((a,r0),(b,r1)):
        for i in range(sides):
            t=math.tau*i/sides
            verts.append(tuple(center+radius*(u*math.cos(t)+v*math.sin(t))))
    for i in range(sides): faces.append((i,(i+1)%sides,(i+1)%sides+sides,i+sides))
    faces += [tuple(reversed(range(sides))),tuple(sides+i for i in range(sides))]
    return obj_mesh(name,verts,faces,mat,parent)


def frame_tube(name, a, b, width, depth, parent, mat=FRAME):
    a,b=Vector(a),Vector(b); axis=(b-a).normalized()
    # Width follows the lateral x axis. Depth follows the tube's normal in the
    # side plane, so an aero profile remains elliptical along each tube.
    normal=Vector((0,-axis.z,axis.y)).normalized()
    sections=((0,.78),(.08,.94),(.24,1.0),(.72,.94),(.94,.83),(1,.76))
    n=16; verts=[]; faces=[]
    for t,scale in sections:
        center=a.lerp(b,t)
        for i in range(n):
            angle=math.tau*i/n
            p=center+Vector((1,0,0))*width*scale*math.cos(angle)+normal*depth*scale*math.sin(angle)
            verts.append(tuple(p))
    for row in range(len(sections)-1):
        for i in range(n):
            a0=row*n+i; b0=row*n+(i+1)%n
            faces.append((a0,b0,b0+n,a0+n))
    faces += [tuple(reversed(range(n))),tuple((len(sections)-1)*n+i for i in range(n))]
    return obj_mesh(name,verts,faces,mat,parent)


def polyline_tube(name, points, radius, mat, parent, sides=10):
    pts=[Vector(p) for p in points]; verts=[]; faces=[]
    for i,c in enumerate(pts):
        tangent=(pts[min(i+1,len(pts)-1)]-pts[max(i-1,0)]).normalized()
        ref=Vector((1,0,0))
        if abs(tangent.dot(ref))>.92: ref=Vector((0,0,1))
        u=tangent.cross(ref).normalized(); v=tangent.cross(u).normalized()
        for j in range(sides):
            t=math.tau*j/sides
            verts.append(tuple(c+radius*(u*math.cos(t)+v*math.sin(t))))
    for row in range(len(pts)-1):
        for j in range(sides):
            a=row*sides+j; b=row*sides+(j+1)%sides
            faces.append((a,b,b+sides,a+sides))
    faces += [tuple(reversed(range(sides))),tuple((len(pts)-1)*sides+i for i in range(sides))]
    return obj_mesh(name,verts,faces,mat,parent)


def ring_x(name, center_y, center_z, radii, x0, x1, mat, parent, segments=96):
    """Solid annular component in a plane normal to the bicycle axle."""
    # radius profile runs inner->outer->inner and makes a shallow, finished rim.
    profile=[(x0,radii[0]),(x0,radii[1]),(x1,radii[1]),(x1,radii[0])]
    verts=[]; faces=[]
    for x,r in profile:
        for i in range(segments):
            t=math.tau*i/segments
            verts.append((x,center_y+r*math.cos(t),center_z+r*math.sin(t)))
    for row in range(len(profile)):
        nextrow=(row+1)%len(profile)
        for i in range(segments):
            j=(i+1)%segments
            faces.append((row*segments+i,row*segments+j,nextrow*segments+j,nextrow*segments+i))
    return obj_mesh(name,verts,faces,mat,parent)


def torus_x(name, radius, tube_r, xcenter, mat, parent, major=96, minor=12):
    verts=[]; faces=[]
    for i in range(major):
        t=math.tau*i/major
        cy=radius*math.cos(t); cz=radius*math.sin(t)
        for j in range(minor):
            q=math.tau*j/minor
            verts.append((xcenter+tube_r*math.sin(q),(radius+tube_r*math.cos(q))*math.cos(t),
                          (radius+tube_r*math.cos(q))*math.sin(t)))
    for i in range(major):
        for j in range(minor):
            a=i*minor+j; b=i*minor+(j+1)%minor
            c=((i+1)%major)*minor+(j+1)%minor; d=((i+1)%major)*minor+j
            faces.append((a,b,c,d))
    return obj_mesh(name,verts,faces,mat,parent)


def spoke_mesh(name, hub_y, hub_z, rim_r, parent, count=24):
    verts=[]; faces=[]
    # Two crossed flanges. The paired lace reads like a conventional 2-cross
    # wheel and resolves to a light spoke structure at normal ride distance.
    for flange,phase in ((-.012,0.0),(.012,.5)):
        for i in range(count//2):
            a=math.tau*(i+phase)/(count//2)
            b=a + (0.48 if i%2 else -0.48)
            start=(flange,hub_y+.027*math.cos(a),hub_z+.027*math.sin(a))
            end=(flange*1.4,rim_r*math.cos(b),rim_r*math.sin(b))
            part=tube_between_data(start,end,.00135,.00105,5)
            offset=len(verts); verts.extend(part[0]); faces.extend(tuple(offset+k for k in f) for f in part[1])
    return obj_mesh(name,verts,faces,material("Wodra Stainless Spokes",(.055,.065,.068),.52,.72),parent)


def tube_between_data(a,b,r0,r1,sides):
    a,b=Vector(a),Vector(b); axis=(b-a).normalized(); ref=Vector((0,0,1))
    if abs(axis.dot(ref))>.9: ref=Vector((0,1,0))
    u=axis.cross(ref).normalized(); v=axis.cross(u).normalized(); verts=[];faces=[]
    for c,r in ((a,r0),(b,r1)):
        for i in range(sides):
            q=math.tau*i/sides; verts.append(tuple(c+r*(u*math.cos(q)+v*math.sin(q))))
    for i in range(sides): faces.append((i,(i+1)%sides,(i+1)%sides+sides,i+sides))
    faces += [tuple(reversed(range(sides))),tuple(sides+i for i in range(sides))]
    return verts,faces


def toothed_ring(name, center_y, center_z, inner_r, root_r, tip_r, teeth, x0, x1, mat, parent):
    n=teeth*4; verts=[]; faces=[]
    # Four-point tooth cycle: root, rising flank, tip, falling flank.
    tooth_r=[]
    for i in range(n):
        phase=i%4
        tooth_r.append(root_r if phase in (0,3) else tip_r)
    # Four concentric side surfaces close the machined chainring plate.
    profiles=((x0,lambda i:inner_r),(x0,lambda i:tooth_r[i]),
              (x1,lambda i:tooth_r[i]),(x1,lambda i:inner_r))
    for x,radius_at in profiles:
        for i in range(n):
            t=math.tau*i/n;r=radius_at(i)
            verts.append((x,center_y+r*math.cos(t),center_z+r*math.sin(t)))
    for row in range(4):
        nextrow=(row+1)%4
        for i in range(n):
            j=(i+1)%n
            faces.append((row*n+i,row*n+j,nextrow*n+j,nextrow*n+i))
    return obj_mesh(name,verts,faces,mat,parent,False)


def saddle_mesh(parent):
    # Anatomical profile: broad sit-bone wings taper to a narrow relieved nose.
    sections=[(-.107,.064,.010),(-.082,.078,.014),(-.049,.076,.017),
              (-.017,.062,.018),(.023,.043,.016),(.073,.030,.013),(.113,.021,.009)]
    verts=[];faces=[];n=20
    for y_offset,width,thick in sections:
        y=SADDLE_Y+y_offset
        for i in range(n):
            t=math.tau*i/n; x=width*math.cos(t)
            z=SADDLE_Z + thick*math.sin(t) - .003*(1-abs(x)/.08)
            verts.append((x,y,z))
    for row in range(len(sections)-1):
        for i in range(n):
            a=row*n+i;b=row*n+(i+1)%n
            faces.append((a,b,b+n,a+n))
    faces.extend((tuple(reversed(range(n))),tuple((len(sections)-1)*n+i for i in range(n))))
    return obj_mesh("Saddle",verts,faces,material("Wodra Saddle | matte cover",(.075,.083,.087),.86),parent)


def merge_frame_tubes(parent):
    names=("BikeFrame","SeatTube","TopTube","HeadTube","ChainStayL","ChainStayR",
           "SeatStayL","SeatStayR","ForkBladeL","ForkBladeR")
    verts=[];faces=[]
    for name in names:
        obj=bpy.data.objects.get(name)
        if obj is None: raise RuntimeError(f"Frame tube {name} is missing")
        offset=len(verts)
        verts.extend(tuple(v.co) for v in obj.data.vertices)
        faces.extend(tuple(offset+i for i in p.vertices) for p in obj.data.polygons)
    for name in names:
        obj=bpy.data.objects[name]
        bpy.data.objects.remove(obj,do_unlink=True)
    result=obj_mesh("BikeFrame",verts,faces,FRAME,parent)
    result["WodraRoadFrameV1"]=True


def extruded_side_profile(name, side, parent, profile, x_center, half_width, mat):
    """Build a reference-traced Y/Z silhouette, bevelled across bicycle width."""
    cy=sum(p[0] for p in profile)/len(profile)
    cz=sum(p[1] for p in profile)/len(profile)
    stations=((-half_width,.05),(-half_width*.86,.55),(-half_width*.60,.88),
              (-half_width*.30,1.0),(0,1.04),(half_width*.30,1.0),
              (half_width*.60,.88),(half_width*.86,.55),(half_width,.05))
    verts=[];faces=[];n=len(profile)
    for dx,scale in stations:
        for y,z in profile:
            verts.append((side*x_center+dx,cy+(y-cy)*scale,cz+(z-cz)*scale))
    faces.append(tuple(reversed(range(n))))
    for layer in range(len(stations)-1):
        a=layer*n;b=(layer+1)*n
        for i in range(n):
            j=(i+1)%n
            faces.append((a+i,a+j,b+j,b+i))
    faces.append(tuple((len(stations)-1)*n+i for i in range(n)))
    obj=obj_mesh(name,verts,faces,mat,parent,True)
    bevel=obj.modifiers.new("Control silhouette edge radius","BEVEL")
    bevel.width=.0025 if "BrakeHood" in name else .001
    bevel.segments=3
    bevel.limit_method='NONE'
    obj.modifiers.new("Control weighted normals","WEIGHTED_NORMAL")
    return obj


def rounded_outline(points, steps=3):
    """Smooth only the hand-authored two-dimensional silhouette corners."""
    output=[];count=len(points)
    for i,b in enumerate(points):
        a=points[(i-1)%count];c=points[(i+1)%count];d=points[(i+2)%count]
        for k in range(steps):
            t=k/steps;t2=t*t;t3=t2*t
            output.append(tuple(.5*((2*b[j])+(-a[j]+c[j])*t+
                                    (2*a[j]-5*b[j]+4*c[j]-d[j])*t2+
                                    (-a[j]+3*b[j]-3*c[j]+d[j])*t3)
                                for j in range(2)))
    return output


def hood_shell(side, parent):
    suffix="L" if side<0 else "R"
    # The previous silhouette extrusion rendered as a pointed fin.  Model the
    # rubber hood as a compact, rounded volume around the bar instead: a hand-
    # shaped block with a broad palm shelf, softened heel and raised nose.
    bpy.ops.mesh.primitive_cube_add(size=1,location=(side*.207,.410,.881))
    hood=bpy.context.object
    hood.name="BrakeHood"+suffix
    hood.data.name=hood.name+"Mesh"
    hood.parent=parent;hood.matrix_parent_inverse.identity()
    hood.dimensions=(.044,.116,.034)
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    bevel=hood.modifiers.new("Moulded rubber edge radius","BEVEL")
    bevel.width=.015;bevel.segments=8;bevel.limit_method='ANGLE'
    bpy.context.view_layer.objects.active=hood
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    for polygon in hood.data.polygons: polygon.use_smooth=True
    # Keep the middle of the top broad and nearly level for palm support, then
    # lift the front upper edge into the short brifter nose. The heel remains
    # low around the wrapped bar instead of tapering into a long fin.
    for vertex in hood.data.vertices:
        forward=max(0.0,min(1.0,(vertex.co.y+.025)/.080))
        forward=forward*forward*(3.0-2.0*forward)
        if vertex.co.z > .004:
            vertex.co.z += .010*forward
        elif vertex.co.y < -.020:
            vertex.co.z -= .003
    hood.data.materials.append(HOOD)
    hood.modifiers.new("Control weighted normals","WEIGHTED_NORMAL")
    hood["construction"]="rounded sculpted control volume with palm shelf and raised nose"
    hood["contact_surface"]="rising palm shelf and raised nose; Contact_"+suffix+"Hood is tangent to shelf"

    # The pivot sits at the front underside of the integrated shifter body; the short axle
    # and screw remain visible on the outside of each integrated control.
    pivot_y=.458;pivot_z=.898
    tube_between("BrakeLeverPivot"+suffix,(side*.223,pivot_y,pivot_z),
                 (side*.235,pivot_y,pivot_z),.0035,PIVOT,parent,.0035,24)
    tube_between("BrakeLeverPivotScrew"+suffix,(side*.234,pivot_y,pivot_z),
                 (side*.237,pivot_y,pivot_z),.0015,ALLOY,parent,.0015,20)

    # The lever grows from beneath the front of the rubber body and sweeps
    # forward before dropping.  Its upper root overlaps the moulded control.
    brake_profile=[(.454,.892),(.462,.894),(.468,.884),(.473,.864),
                   (.477,.844),(.477,.827),(.472,.814),(.466,.812),
                   (.468,.829),(.468,.846),(.464,.866),(.458,.881)]
    blade=extruded_side_profile("BrakeLever"+suffix,side,parent,rounded_outline(brake_profile),.213,.0055,LEVER)
    blade["component"]="integrated curved brake blade"
    blade["section_width_mm"]=11.0

    # The shorter inner shift paddle remains distinct but sits behind the
    # brake blade and follows the same forward/down control plane.
    shift_profile=[(.438,.875),(.443,.880),(.449,.873),(.451,.850),
                   (.448,.833),(.444,.831),(.443,.850)]
    extruded_side_profile("ShiftPaddle"+suffix,side,parent,rounded_outline(shift_profile),.190,.004,DARK_METAL)


def make_chain(parent):
    # Shared-tangent closed chain path around the 50T ring and 11T outer cassette.
    cy,cz=REAR_Y,WHEEL_Z; rsmall=.047; rbig=.103
    d=Vector((BB_Y-cy,BB_Z-cz)); base=math.atan2(d.y,d.x)
    alpha=math.asin((rbig-rsmall)/d.length)
    upper=base+math.pi/2+alpha
    lower=base-math.pi/2-alpha
    # Start at the front ring's upper tangent and follow a continuous loop:
    # straight to cassette, wrap its rear side, return on lower tangent, and
    # wrap the front ring's forward side.
    pts=[(.057,BB_Y+rbig*math.cos(upper),BB_Z+rbig*math.sin(upper)),
         (.057,cy+rsmall*math.cos(upper),cz+rsmall*math.sin(upper))]
    for k in range(1,33):
        a=upper+(lower+2*math.pi-upper)*k/32
        pts.append((.057,cy+rsmall*math.cos(a),cz+rsmall*math.sin(a)))
    pts.append((.057,BB_Y+rbig*math.cos(lower),BB_Z+rbig*math.sin(lower)))
    for k in range(1,33):
        a=lower+2*math.pi+(upper+2*math.pi-(lower+2*math.pi))*k/32
        pts.append((.057,BB_Y+rbig*math.cos(a),BB_Z+rbig*math.sin(a)))
    polyline_tube("DriveChain",pts,.0022,ALLOY,parent,6)
    # Cassette eleven distinct, stepped sprockets and a compact rear derailleur.
    for i in range(11):
        radius=.047-.00175*i
        ring_x(f"CassetteSprocket{i+1:02d}",REAR_Y,WHEEL_Z,(radius-.0024,radius),.046+i*.0022,.048+i*.0022,ALLOY,parent,64)
    ring_x("CassetteCarrier",REAR_Y,WHEEL_Z,(.026,.044),.039,.043,DARK_METAL,parent,64)
    tube_between("RearDerailleurBody",(.076,REAR_Y-.035,WHEEL_Z-.005),(.075,REAR_Y-.048,WHEEL_Z-.046),.012,DARK_METAL,parent,.009,10)
    for index,zoff in enumerate((-.025,-.075)):
        y=REAR_Y-.045
        ring_x(f"DerailleurPulley{index+1}",y,WHEEL_Z+zoff,(.009,.012),.071,.076,ALLOY,parent,36)
    # Front derailleur cage and seat-tube clamp, aligned with the chainring.
    tube_between("FrontDerailleurClamp",(.040,-.185,.535),(.040,-.173,.555),.011,DARK_METAL,parent,.011,12)
    tube_between("FrontDerailleurCage",(.062,-.180,.534),(.062,-.115,.534),.0038,ALLOY,parent,.0038,8)


def make_brakes(prefix, spin, parent, is_front):
    # 140 mm road rotors, six-bolt carrier and a mechanical-looking compact caliper.
    cy=0;cz=0
    for side in (-1,1):
        x=side*.031
        ring_x(prefix+"DiscRotor"+("L" if side<0 else "R"),cy,cz,(.031,.071),x,x+side*.0018,ROTOR,spin,72)
        # Six spider arms join hub carrier to braking track.
        for i in range(6):
            a=math.tau*i/6
            tube_between("RotorArm"+prefix+str(side)+str(i),
                         (x, .027*math.cos(a),.027*math.sin(a)),
                         (x, .057*math.cos(a+.05),.057*math.sin(a+.05)),.0020,ROTOR,spin,.0017,6)
    if is_front:
        caliper=(FRONT_Y-.066,WHEEL_Z+.028)
    else:
        caliper=(REAR_Y+.066,WHEEL_Z+.028)
    # The caliper straddles the disc from the outer flange to its fork or stay
    # mount, instead of floating above the braking track.
    tube_between(prefix+"DiscCaliper",(.025,caliper[0],caliper[1]),
                 (.074,caliper[0],caliper[1]),.010,DARK_METAL,parent,.010,10)
    tube_between(prefix+"CaliperBridge",(.052,caliper[0],caliper[1]),
                 (.052,caliper[0]-.018,caliper[1]+.020),.005,ALLOY,parent,.004,8)


def make_wheel(prefix, spin, front):
    spin.location=(0,FRONT_Y if front else REAR_Y,WHEEL_Z)
    # 28 mm 700C tyre: 622 mm bead-seat plus casing gives 678 mm outer diameter.
    torus_x(prefix+"WheelTyre",.325,.014,0,RUBBER,spin,128,16)
    # 35 mm deep asymmetric carbon rim with a bead hook and machined channel.
    rim=ring_x(prefix+"WheelRim",0,0,(.274,.311),-.012,.012,CARBON,spin,128)
    rim["radial_depth_mm"]=37
    spoke_mesh(prefix+"WheelSpokes",0,0,.300,spin,24)
    # Hub shell and end caps.
    ring_x(prefix+"HubShell",0,0,(.016,.025),-.035,.035,ALLOY,spin,48)
    for side in (-1,1):
        tube_between(prefix+"HubEndCap"+str(side),(side*.035,0,0),(side*.043,0,0),.014,DARK_METAL,spin,.014,16)
    make_brakes(prefix,spin,bpy.data.objects['BicycleModel'],front)


def create_contact_target(name, parent, location):
    o=bpy.data.objects.new(name,None);o.empty_display_type='SPHERE';o.empty_display_size=.008
    bpy.context.scene.collection.objects.link(o);o.parent=parent;o.matrix_parent_inverse.identity();o.location=location
    o["WodraContactTargetV1"]=True
    return o


def merge_static_details(bike):
    """Batch small same-material details by parent, keeping visible/runtime nodes stable."""
    protected={"BikeFrame","RoadDropHandlebars","BarTapeL","BarTapeR","Saddle","Stem",
               "BrakeHoodL","BrakeHoodR","BrakeLeverL","BrakeLeverR","ChainringOuter50T",
               "ChainringInner34T","DriveChain","BarEndPlugL","BarEndPlugR","FrontWheelTyre","FrontWheelRim","FrontWheelSpokes",
               "RearWheelTyre","RearWheelRim","RearWheelSpokes","LeftCrank","RightCrank",
               "LeftPedal","RightPedal"}
    bpy.context.view_layer.update()
    candidates=[o for o in bike.children_recursive if o.type=="MESH" and o.name not in protected]
    groups={}
    for o in candidates:
        material_name=o.data.materials[0].name if o.data.materials and o.data.materials[0] else "Unmaterialed"
        groups.setdefault((o.parent,material_name),[]).append(o)
    for (parent,material_name),objects in groups.items():
        if len(objects)<2: continue
        vertices=[];faces=[]
        inv=parent.matrix_world.inverted()
        for o in objects:
            offset=len(vertices);to_parent=inv @ o.matrix_world
            vertices.extend(tuple(to_parent @ v.co) for v in o.data.vertices)
            faces.extend(tuple(offset+i for i in p.vertices) for p in o.data.polygons)
        for o in objects:bpy.data.objects.remove(o,do_unlink=True)
        safe="".join(ch if ch.isalnum() else "_" for ch in material_name)
        obj_mesh(f"RoadBikeDetail_{safe}_{parent.name}",vertices,faces,
                 bpy.data.materials[material_name],parent)


def preserve_runtime_wrist_seams(human):
    """Restore the accepted runtime human export surfaces from its source.

    The matching alpha48 mask-export checkpoint has the same human vertices,
    weights, bones and topology; the raw canonical file predates its 48 wrist
    seam faces and eye-material assignments. Restore only those exact faces and
    assignments after strict vertex validation. No rider vertices, weights,
    bones, or animation keys are changed.
    """
    checkpoint=Path(__file__).resolve().parent/"source"/"WodraCyclist.alpha48-mask-export-fixed.blend"
    with bpy.data.libraries.load(str(checkpoint),link=False) as (data_from,data_to):
        if "base" not in data_from.meshes: raise RuntimeError("Validated wrist checkpoint mesh 'base' is missing")
        data_to.meshes=["base"]
    reference=data_to.meshes[0]
    source=human.data
    if len(reference.vertices)!=len(source.vertices): raise RuntimeError("Wrist checkpoint vertex count differs from canonical source")
    for a,b in zip(source.vertices,reference.vertices):
        if (a.co-b.co).length>1e-7: raise RuntimeError("Wrist checkpoint vertex coordinates differ; refusing rider changes")
    source_faces={tuple(sorted(p.vertices)) for p in source.polygons}
    missing=[p for p in reference.polygons if tuple(sorted(p.vertices)) not in source_faces]
    counts={"L":0,"R":0}
    if missing:
        if len(missing)!=48: raise RuntimeError(f"Expected 48 documented wrist seam quads; found {len(missing)}")
        helper=human.vertex_groups.get("helper-tights")
        hand_groups={g.index for g in human.vertex_groups if g.name.startswith(("wrist.","metacarpal","finger"))}
        wrist={side:human.vertex_groups[f"wrist.{side}"].index for side in ("L","R")}
        for poly in missing:
            verts=[source.vertices[i] for i in poly.vertices]
            full_helper=all(any(a.group==helper.index and a.weight>=.999 for a in v.groups) for v in verts)
            hand_vertices=sum(any(a.group in hand_groups and a.weight>.01 for a in v.groups) for v in verts)
            side=next((s for s,i in wrist.items() if sum(a.weight for v in verts for a in v.groups if a.group==i)>.01),None)
            if not full_helper or hand_vertices<2 or side is None or len(poly.vertices)!=4:
                raise RuntimeError("Checkpoint contains an unexpected non-wrist face; refusing rider changes")
            counts[side]+=1
        if counts!={"L":24,"R":24}: raise RuntimeError(f"Unexpected seam distribution: {counts}")
        ref_uv=reference.uv_layers.active
        uv_by_face={}
        if ref_uv:
            for poly in missing:
                uv_by_face[tuple(sorted(poly.vertices))]={reference.loops[i].vertex_index:tuple(ref_uv.data[i].uv) for i in poly.loop_indices}
        import bmesh
        bm=bmesh.new();bm.from_mesh(source);bm.verts.ensure_lookup_table()
        uv_layer=bm.loops.layers.uv.verify() if ref_uv and source.uv_layers else None
        for poly in missing:
            face=bm.faces.new([bm.verts[i] for i in poly.vertices])
            face.material_index=poly.material_index;face.smooth=poly.use_smooth
            if uv_layer:
                uv=uv_by_face[tuple(sorted(poly.vertices))]
                for loop in face.loops: loop[uv_layer].uv=uv[loop.vert.index]
        bm.to_mesh(source);bm.free();source.update()
    else:
        print("RUNTIME_WRIST_SEAMS", "already present")
    # The production GLB keeps 152 anatomical eye faces on a separate warm
    # material. The old canonical source lost that material slot even though
    # the reviewed reference mesh still has the same face topology. Restore
    # those face-material assignments by exact vertex-index signature so the
    # road-bike export does not silently change the rider's eyes.
    source_face_by_key={tuple(sorted(p.vertices)):p for p in source.polygons}
    reference_face_by_key={tuple(sorted(p.vertices)):p for p in reference.polygons}
    if source_face_by_key.keys()!=reference_face_by_key.keys():
        raise RuntimeError("Restored human face topology differs from the validated export reference")
    while len(source.materials): source.materials.pop(index=len(source.materials)-1)
    for mat in reference.materials: source.materials.append(mat)
    changed_material_faces=0
    for key,polygon in source_face_by_key.items():
        material_index=reference_face_by_key[key].material_index
        if polygon.material_index!=material_index: changed_material_faces+=1
        polygon.material_index=material_index
    eye_faces=sum(1 for p in source.polygons if p.material_index<len(source.materials) and
                  source.materials[p.material_index] and "Rider Eye" in source.materials[p.material_index].name)
    if eye_faces!=152: raise RuntimeError(f"Expected 152 validated anatomical eye faces, found {eye_faces}")
    bpy.data.meshes.remove(reference)
    print("RUNTIME_WRIST_SEAMS",counts,"RUNTIME_EYE_MATERIAL_FACES",eye_faces,
          "material assignments restored",changed_material_faces,
          "human vertices unchanged",len(source.vertices),"polygons",len(source.polygons))


def build():
    bike=bpy.data.objects.get("BicycleModel")
    if bike is None: raise RuntimeError("BicycleModel missing")
    rider=bpy.data.objects.get("Human")
    rig=bpy.data.objects.get("Human.rig")
    preserve_runtime_wrist_seams(rider)
    rider_signature=(len(rider.data.vertices),len(rider.data.polygons),len(rider.vertex_groups),len(rig.data.bones),
                     tuple(a.name for a in bpy.data.actions))
    # Keep the GLB animation-contract nodes; replace the old bicycle geometry,
    # not the human mesh, rig, pose, action, root or scene/world transforms.
    keep={"FrontWheelSpin","RearWheelSpin","CrankPivot"}
    for o in list(bike.children_recursive):
        if o.name not in keep: bpy.data.objects.remove(o,do_unlink=True)
    for name in keep:
        node=bpy.data.objects.get(name)
        if node is None:
            node=bpy.data.objects.new(name,None);bpy.context.scene.collection.objects.link(node);node.parent=bike
        node.rotation_euler=(0,0,0);node.scale=(1,1,1)
        if node.parent!=bike: node.parent=bike
        node.matrix_parent_inverse.identity();node.location=(0,0,0)
    front=bpy.data.objects['FrontWheelSpin'];rear=bpy.data.objects['RearWheelSpin'];crank=bpy.data.objects['CrankPivot']
    make_wheel("Front",front,True);make_wheel("Rear",rear,False)

    # Contemporary compact carbon frame: wheelbase 978, chainstay 410, BB drop 74.
    seat=(0,SEAT_Y,SEAT_Z); bb=(0,BB_Y,BB_Z); rear_hub=(0,REAR_Y,WHEEL_Z); front_hub=(0,FRONT_Y,WHEEL_Z)
    head_low=HEAD_LOW;head_high=HEAD_HIGH
    frame_tube("BikeFrame",bb,head_low,.034,.020,bike)
    frame_tube("SeatTube",bb,seat,.019,.015,bike)
    frame_tube("TopTube",seat,head_high,.019,.014,bike)
    frame_tube("HeadTube",head_low,head_high,.022,.019,bike)
    for side in (-1,1):
        frame_tube("ChainStay"+("L" if side<0 else "R"),(side*.045,REAR_Y,WHEEL_Z),(side*.045,BB_Y,BB_Z),.010,.009,bike)
        frame_tube("SeatStay"+("L" if side<0 else "R"),(side*.045,REAR_Y,WHEEL_Z),(side*.045,SEAT_Y,SEAT_Z-.025),.008,.008,bike)
        # Fork blades meet the front axle with 47 mm rake from the 73 degree steering axis.
        frame_tube("ForkBlade"+("L" if side<0 else "R"),(side*.043,FRONT_Y,WHEEL_Z),(side*.038,head_low[1],head_low[2]),.011,.010,bike)
    merge_frame_tubes(bike)
    # Seatpost, clamp and rail hardware.
    tube_between("Seatpost",(0,SEAT_Y-.010,SEAT_Z-.098),(0,SADDLE_Y,SADDLE_Z-.027),.015,CARBON,bike,.014,14)
    tube_between("SeatpostClamp",(-.018,SEAT_Y-.006,SEAT_Z-.100),(.018,SEAT_Y-.006,SEAT_Z-.100),.010,ALLOY,bike,.010,12)
    tube_between("SaddleRailL",(-.024,SADDLE_Y-.10,SADDLE_Z-.030),(-.024,SADDLE_Y+.08,SADDLE_Z-.030),.0024,ALLOY,bike,.0024,8)
    tube_between("SaddleRailR",(.024,SADDLE_Y-.10,SADDLE_Z-.030),(.024,SADDLE_Y+.08,SADDLE_Z-.030),.0024,ALLOY,bike,.0024,8)
    saddle_mesh(bike)

    # 73-degree integrated headset, 100 mm stem, 420 mm drop bar.
    tube_between("LowerHeadset",(-.027,.343,.654),(.027,.343,.654),.013,DARK_METAL,bike,.013,16)
    tube_between("UpperHeadset",(-.024,.3045,.781),(.024,.3045,.781),.012,DARK_METAL,bike,.012,16)
    tube_between("Stem",(0,.313,.785),(0,.365,.854),.015,CARBON,bike,.014,14)
    tube_between("StemFaceplate",(-.024,.365,.854),(.024,.365,.854),.011,ALLOY,bike,.011,14)
    # mirrored, continuous drops path: 420 mm bar width, real reach and drop
    # with a compact forward ramp into the hoods.
    left=[(0,.365,.854),(-.055,.365,.854),(-.115,.365,.854),(-.165,.366,.856),
          (-.195,.378,.865),(-.210,.402,.874),(-.209,.427,.880),(-.196,.444,.877),
          (-.183,.446,.858),(-.181,.434,.829),(-.180,.408,.798),(-.172,.378,.774),
          (-.168,.354,.760),(-.155,.337,.756),(-.145,.329,.760)]
    left=[Vector(p) for p in left]
    # Interpolate each segment to keep bends smooth and the drop in one tube.
    def smooth_path(points,steps=4):
        output=[]
        for i in range(len(points)-1):
            p0=points[max(i-1,0)];p1=points[i];p2=points[i+1];p3=points[min(i+2,len(points)-1)]
            for k in range(steps):
                t=k/steps;t2=t*t;t3=t2*t
                output.append(tuple(.5*((2*p1)+(-p0+p2)*t+(2*p0-5*p1+4*p2-p3)*t2+(-p0+3*p1-3*p2+p3)*t3)))
        output.append(tuple(points[-1]));return output
    lpath=smooth_path(left)
    path=list(reversed(lpath))+[(-p[0],p[1],p[2]) for p in lpath[1:]]
    polyline_tube("RoadDropHandlebars",path,.0115,ALLOY,bike,12)
    # Wrap the exact same sampled bar path to avoid visible gaps or z-fighting.
    # Leave only the short central clamp section exposed as alloy.
    for side in (-1,1):
        tape_path=lpath[2:] if side<0 else [(-p[0],p[1],p[2]) for p in lpath[2:]]
        polyline_tube("BarTape"+("L" if side<0 else "R"),tape_path,.0124,TAPE,bike,10)
        hood_shell(side,bike)
        # Close the bar ends with compact dark plugs; the open tube cut used
        # to read as an unfinished model at front three-quarter angles.
        end=Vector(lpath[-1] if side<0 else (-lpath[-1][0],lpath[-1][1],lpath[-1][2]))
        previous=Vector(lpath[-2] if side<0 else (-lpath[-2][0],lpath[-2][1],lpath[-2][2]))
        tangent=(end-previous).normalized()
        tube_between("BarEndPlug"+("L" if side<0 else "R"),end-tangent*.005,end+tangent*.003,
                     .0085,CARBON,bike,.0075,12)
        target=create_contact_target("Contact_"+("Left" if side<0 else "Right")+"Hood",bike,
                                     (side*HOOD_PALM_X,HOOD_PALM_Y,HOOD_PALM_Z))
        # Local Y follows the actual shelf tangent; local Z is its upward normal.
        target.rotation_euler.x=math.atan2(.05,1.0)
    # Modern internal routing keeps unsupported cable loops out of the cockpit.

    # Steering axis and fork offset correspond to a measured 73-degree road fit.
    # Crank pivot remains the stable game-controlled node. 170 mm arms preserve
    # right/left pedal phase and expose dynamic contact targets under that pivot.
    crank.location=(0,BB_Y,BB_Z)
    for side,label,phase in ((-1,"Left",-1),(1,"Right",1)):
        x=side*.13; z=phase*.170
        tube_between(label+"Crank",(side*.048,0,0),(side*.055,0,z),.009,DARK_METAL,crank,.008,12)
        # Spindle and broad but slim clipless pedal body.
        tube_between(label+"PedalSpindle",(side*.055,0,z),(x,0,z),.005,ALLOY,crank,.005,10)
        # chamfered inset pedal platform via bevelled cube
        bpy.ops.mesh.primitive_cube_add(size=1,location=(0,0,0))
        p=bpy.context.object;p.name=label+"Pedal";p.data.name=label+"PedalMesh";p.parent=crank;p.matrix_parent_inverse.identity()
        p.location=(x,0,z);p.dimensions=(.045,.096,.018);bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
        p.data.materials.append(DARK_METAL)
        bevel=p.modifiers.new("Pedal edge radii","BEVEL");bevel.width=.004;bevel.segments=2
        p.modifiers.new("Pedal normals","WEIGHTED_NORMAL")
        create_contact_target("Contact_"+label+"Pedal",crank,(x,0,z))
    # 50/34 double chainring: the small ring is visible behind the large ring.
    toothed_ring("ChainringOuter50T",BB_Y,BB_Z,.084,.103,.106,50,.046,.053,ALLOY,bike)
    toothed_ring("ChainringInner34T",BB_Y,BB_Z,.062,.069,.073,34,.039,.045,DARK_METAL,bike)
    make_chain(bike)
    create_contact_target("Contact_Saddle",bike,(0,SADDLE_Y,SADDLE_Z))

    # Add small, named frame-level dimensions to the editable file for review.
    bike["road_bike_geometry_version"]="issue158-v2"
    bike["wheelbase_mm"]=978
    bike["chainstay_mm"]=410
    bike["bottom_bracket_drop_mm"]=74
    bike["tire_size"]="700x28c (622 bead seat, 678 outer diameter)"
    bike["handlebar_width_mm"]=420
    bike["crank_length_mm"]=170
    # Validate component targets and that human/rig/action were not modified.
    current=(len(rider.data.vertices),len(rider.data.polygons),len(rider.vertex_groups),len(rig.data.bones),tuple(a.name for a in bpy.data.actions))
    if current!=rider_signature: raise RuntimeError(f"Rider data changed unexpectedly: {rider_signature} -> {current}")
    required=("FrontWheelSpin","RearWheelSpin","CrankPivot","LeftCrank","RightCrank","LeftPedal","RightPedal",
              "BrakeHoodL","BrakeHoodR","BrakeLeverL","BrakeLeverR","BikeFrame","Contact_LeftHood",
              "Contact_RightHood","Contact_Saddle","Contact_LeftPedal","Contact_RightPedal","DriveChain",
              "BarEndPlugL","BarEndPlugR","FrontDiscRotorL","RearDiscRotorR","CassetteSprocket01",
              "FrontDerailleurCage","RearDerailleurBody")
    missing=[n for n in required if bpy.data.objects.get(n) is None]
    if missing: raise RuntimeError("Missing expected bicycle parts: "+", ".join(missing))
    merge_static_details(bike)
    print("ROAD_BIKE_158", "rider-unchanged",current,"objects",len(bike.children_recursive),"targets",[n for n in required if n.startswith("Contact_")])


def main():
    args=sys.argv[sys.argv.index("--")+1:]
    if len(args)!=1: raise SystemExit("expected output editable .blend path")
    output=Path(args[0]);output.parent.mkdir(parents=True,exist_ok=True)
    build();bpy.ops.wm.save_as_mainfile(filepath=str(output));print("SAVED",output)

if __name__=="__main__": main()
