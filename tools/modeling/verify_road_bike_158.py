"""Production GLB regression checks for the #158 road bicycle.

Usage: python tools/modeling/verify_road_bike_158.py [path/to/WodraCyclist.glb]
"""
from __future__ import annotations
import json
import math
import re
import struct
import sys
from pathlib import Path
from inspect_wodra_glb import bounds_corners, identity, multiply, node_matrix, point

path=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).resolve().parents[2]/"src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb"
raw=path.read_bytes()
magic,version,length=struct.unpack_from("<4sII",raw)
assert magic==b"glTF" and version==2 and length==len(raw),"invalid GLB header"
json_length,json_type=struct.unpack_from("<II",raw,12)
assert json_type==0x4E4F534A,"invalid GLB JSON chunk"
doc=json.loads(raw[20:20+json_length].decode("utf-8"))
nodes=doc["nodes"]; meshes=doc["meshes"]; accessors=doc["accessors"]
parents={}
for parent,node in enumerate(nodes):
    for child in node.get("children",[]): parents[child]=parent
indices={}
for i,node in enumerate(nodes):
    name=node.get("name","")
    if name in indices: raise AssertionError(f"duplicate named node {name}")
    indices[name]=i

def require(name):
    assert name in indices,f"missing node {name}"
    return nodes[indices[name]]

required=("BicycleModel","BikeFrame","FrontWheelSpin","RearWheelSpin","CrankPivot",
          "LeftCrank","RightCrank","LeftPedal","RightPedal","FrontWheelTyre","RearWheelTyre",
          "FrontWheelRim","RearWheelRim","RoadDropHandlebars","BrakeHoodL","BrakeHoodR",
          "BrakeLeverL","BrakeLeverR","BarEndPlugL","BarEndPlugR","Saddle","ChainringOuter50T","ChainringInner34T",
          "DriveChain","Contact_LeftHood","Contact_RightHood",
          "Contact_Saddle","Contact_LeftPedal","Contact_RightPedal")
if "RoadBikeFrontWheelAssembly" not in indices:
    for name in required: require(name)
    for name in ("RoadBikeDetail_Wodra_Stainless_Rotor_FrontWheelSpin",
                 "RoadBikeDetail_Wodra_Stainless_Rotor_RearWheelSpin",
                 "RoadBikeDetail_Wodra_Machined_Alloy_BicycleModel"):
        require(name)
assert len(doc.get("skins",[]))>=1 and any(a.get("name")=="PedalCycle" for a in doc.get("animations",[])),"weighted rider or pedal animation missing"

world={}
def visit(index,parent_matrix):
    matrix=multiply(parent_matrix,node_matrix(nodes[index]));world[index]=matrix
    for child in nodes[index].get("children",[]):visit(child,matrix)
for root in doc.get("scenes",[{}])[doc.get("scene",0)].get("nodes",[]):visit(root,identity())

def origin(name):
    i=indices[name]
    return point(world[i],(0,0,0))
def transformed_bounds(name):
    n=require(name);mesh=meshes[n["mesh"]]
    positions=[p["attributes"]["POSITION"] for p in mesh.get("primitives",[]) if "POSITION" in p.get("attributes",{})]
    assert positions,f"{name} has no geometry"
    lo=tuple(min(accessors[a]["min"][d] for a in positions) for d in range(3))
    hi=tuple(max(accessors[a]["max"][d] for a in positions) for d in range(3))
    pts=[point(world[indices[name]],p) for p in bounds_corners(lo,hi)]
    return tuple(min(p[d] for p in pts) for d in range(3)),tuple(max(p[d] for p in pts) for d in range(3))
def local_size(name):
    n=require(name);mesh=meshes[n["mesh"]]
    positions=[p["attributes"]["POSITION"] for p in mesh.get("primitives",[]) if "POSITION" in p.get("attributes",{})]
    lo=tuple(min(accessors[a]["min"][d] for a in positions) for d in range(3))
    hi=tuple(max(accessors[a]["max"][d] for a in positions) for d in range(3))
    return tuple(hi[d]-lo[d] for d in range(3))
def close(a,b,tol=.002):return abs(a-b)<=tol

# User-supplied artist mesh path. Keep the original prototype assertions below
# for historical review assets while validating the imported source by its
# runtime assemblies, measured bounds, animation pivots, materials and contacts.
if "RoadBikeFrontWheelAssembly" in indices:
    imported_required=("BicycleModel","BikeFrame","RoadBikeStatic","RoadBikeCockpit",
                       "RoadBikeSaddle","RoadBikeFrontWheelAssembly",
                       "RoadBikeRearWheelAssembly","FrontWheelSpin","RearWheelSpin",
                       "CrankPivot","LeftCrank","RightCrank","LeftPedal","RightPedal",
                       "Contact_LeftHood","Contact_RightHood","Contact_Saddle",
                       "Contact_LeftPedal","Contact_RightPedal")
    for name in imported_required: require(name)
    assert len(doc.get("skins",[]))>=1 and any(a.get("name")=="PedalCycle" for a in doc.get("animations",[])),\
        "weighted rider or pedal animation missing"
    front=origin("FrontWheelSpin"); rear=origin("RearWheelSpin"); bb=origin("CrankPivot")
    wheelbase=abs(front[2]-rear[2])
    assert close(wheelbase,.993,.004),f"source road-bike wheelbase changed: {wheelbase:.4f} m"
    assert close(front[1],.339,.003) and close(rear[1],.339,.003),\
        "wheel hubs no longer share the Wodra ground plane"
    for label,name,hub in (("front","RoadBikeFrontWheelAssembly",front),
                           ("rear","RoadBikeRearWheelAssembly",rear)):
        lo,hi=transformed_bounds(name)
        vertical=hi[1]-lo[1]; longitudinal=hi[2]-lo[2]
        assert .64<=vertical<=.69 and .64<=longitudinal<=.69,\
            f"{label} wheel is not a plausible 700C outer diameter: {vertical:.3f} x {longitudinal:.3f} m"
        assert abs((hi[1]+lo[1])/2-hub[1])<.006,\
            f"{label} wheel is not centered on its runtime spin pivot"
        assert lo[1]>=-.002 and lo[1]<.015,\
            f"{label} tyre is floating or below ground: bottom={lo[1]:.4f} m"
    for side,name in (("left","Contact_LeftPedal"),("right","Contact_RightPedal")):
        assert parents[indices[name]]==indices["CrankPivot"],f"{name} must rotate with CrankPivot"
        p=origin(name)
        radius=math.hypot(p[1]-bb[1],p[2]-bb[2])
        assert .15<=radius<=.18,f"{side} source crank radius is implausible: {radius:.4f} m"
        lo,hi=transformed_bounds("LeftPedal" if side=="left" else "RightPedal")
        assert all(lo[i]-.005<=p[i]<=hi[i]+.005 for i in range(3)),\
            f"{name} does not fall on the imported pedal surface"
    for side in ("Left","Right"):
        n=require("Contact_"+side+"Hood")
        assert "mesh" not in n and parents[indices[n["name"]]]==indices["BicycleModel"],\
            f"{n['name']} must be a stable bike-local contact target"
        p=origin(n["name"])
        lo,hi=transformed_bounds("RoadBikeCockpit")
        assert all(lo[i]-.01<=p[i]<=hi[i]+.01 for i in range(3)),\
            f"{n['name']} lies outside the imported cockpit geometry"
    assert close(origin("Contact_LeftHood")[0],-origin("Contact_RightHood")[0],.003),\
        "hood palm targets are not laterally mirrored"
    saddle=origin("Contact_Saddle")
    saddle_lo,saddle_hi=transformed_bounds("RoadBikeSaddle")
    assert saddle_lo[1]-.01<=saddle[1]<=saddle_hi[1]+.01,\
        "saddle/pelvis target is outside the imported saddle surface"
    materials={m.get("name","") for m in doc.get("materials",[])}
    assert "tape" in materials and "levers" in materials,\
        "artist-authored bar tape and control materials were not exported"
    asset_labels = " ".join(
        [n.get("name", "") for n in nodes] +
        [m.get("name", "") for m in doc.get("materials", [])] +
        [i.get("name", "") for i in doc.get("images", [])]
    )
    assert not re.search(r"gigant", asset_labels, re.IGNORECASE),\
        "branded GIGANT geometry/material/image remains in the runtime asset"
    triangles=sum(accessors[p["indices"]]["count"]//3
                  for mesh in meshes for p in mesh.get("primitives",[]) if "indices" in p)
    assert triangles<1_100_000,f"optimized artist bike regressed toward the original heavy import: {triangles} triangles"
    assert len(meshes)<40,f"imported production bicycle has too many render meshes: {len(meshes)}"
    print(f"ROAD_BIKE_158 IMPORTED ASSET PASS: {wheelbase*1000:.0f} mm wheelbase; "
          f"700C tire geometry grounded; source crank/pedal contacts; bilateral cockpit targets; "
          f"unbranded imported geometry; "
          f"{triangles:,} triangles / {len(meshes)} render meshes")
    raise SystemExit(0)

front=origin("FrontWheelSpin");rear=origin("RearWheelSpin");bb=origin("CrankPivot")
assert close(abs(front[2]-rear[2]),.978),f"wheelbase is {abs(front[2]-rear[2]):.4f} m"
assert close(front[1],.339) and close(rear[1],.339),"front/rear wheel hubs must share the road plane"
assert close(front[1]-bb[1],.074),f"bottom-bracket drop is {front[1]-bb[1]:.4f} m"
assert close(local_size("FrontWheelTyre")[1],.678),"front 700x28C tire outer diameter is not 678 mm"
assert close(local_size("RearWheelTyre")[1],.678),"rear 700x28C tire outer diameter is not 678 mm"
assert close(local_size("FrontWheelTyre")[0],.028),"front tire width is not 28 mm"
assert close(local_size("FrontWheelRim")[1],.622),"front rim bead-seat diameter should be 622 mm"
assert close(local_size("RoadDropHandlebars")[0],.444,.010),"420 mm centreline bar width should produce a 444 mm wrapped outside width"

# Contacts must remain named, parented to the bike (or moving crank), and agree
# with their physical surfaces in the exported production model.
for name in ("Contact_LeftHood","Contact_RightHood","Contact_Saddle"):
    n=require(name);assert "mesh" not in n,f"{name} must remain a stable non-rendering target"
for name in ("Contact_LeftPedal","Contact_RightPedal"):
    n=require(name);assert "mesh" not in n and parents[indices[name]]==indices["CrankPivot"],f"{name} must rotate with CrankPivot"
left=origin("Contact_LeftPedal");right=origin("Contact_RightPedal")
for label,p in (("left",left),("right",right)):
    radial=((p[1]-bb[1])**2+(p[2]-bb[2])**2)**.5
    assert close(radial,.170),f"{label} pedal crank radius is {radial:.4f} m"
assert close(origin("Contact_Saddle")[1],.951),"saddle contact target should be at documented seat height"
assert close(origin("Contact_LeftHood")[0],-.210) and close(origin("Contact_RightHood")[0],.210),"hood targets should be symmetric and follow the bar ramp"
assert close(origin("Contact_LeftHood")[1],.900) and close(origin("Contact_RightHood")[1],.900),"hood targets should sit on the palm shelf height"
assert close(abs(origin("Contact_LeftHood")[2]),.421) and close(abs(origin("Contact_RightHood")[2]),.421),"hood targets should sit at the rider-side palm shelf depth"
for name in ("Contact_LeftHood","Contact_RightHood"):
    rotation=require(name).get("rotation",[0,0,0,1])
    assert abs(rotation[0])>.02,"hood target orientation must follow the shallow palm shelf"
for side in ("L","R"):
    hood_size=local_size("BrakeHood"+side)
    # glTF's Y/Z to Godot Y/Z mapping presents Blender longitudinal Y as
    # exported Z; the palm body should remain wide, tall enough, and elongated.
    assert hood_size[0]>=.040 and hood_size[1]>=.035 and hood_size[2]>=.110,\
        f"BrakeHood{side} should have a broad, elongated palm body; size={hood_size}"
left_hood_top=transformed_bounds("BrakeHoodL")[1][1]
right_hood_top=transformed_bounds("BrakeHoodR")[1][1]
assert left_hood_top>origin("Contact_LeftHood")[1] and right_hood_top>origin("Contact_RightHood")[1],"palm shelf target must be below the compact raised nose"

# Check both lever blades sit close to their corresponding brifter shell rather
# than exporting at the old detached location.
def center(name):
    lo,hi=transformed_bounds(name)
    return tuple((lo[d]+hi[d])/2 for d in range(3))
for side in ("L","R"):
    hood=center("BrakeHood"+side);lever=center("BrakeLever"+side)
    gap=sum((hood[d]-lever[d])**2 for d in range(3))**.5
    assert gap<.120,f"BrakeLever{side} is detached from its hood ({gap:.3f} m)"
    lever_size=local_size("BrakeLever"+side)
    assert lever_size[0]<=.012 and max(lever_size[1:])>=.018,\
        f"BrakeLever{side} should be thin across the bike and broad in the side plane; size={lever_size}"

# Geometry and mesh budget guard against accidental runaway exports.
triangles=0
for mesh in meshes:
    for primitive in mesh.get("primitives",[]):
        if "indices" in primitive:
            count=accessors[primitive["indices"]]["count"]
            triangles+=count//3
assert triangles<100_000,f"production cyclist mesh budget exceeded: {triangles} triangles"
assert len(meshes)<75,f"production bike contains too many draw meshes: {len(meshes)}"
print(f"ROAD_BIKE_158 PASS: 978 mm wheelbase; 74 mm BB drop; 700x28C wheels; 170 mm cranks;" \
      f" bilateral hood/saddle/pedal targets; connected brake levers; {triangles} triangles / {len(meshes)} meshes")
