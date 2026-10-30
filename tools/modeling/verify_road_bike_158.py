"""Production GLB regression checks for the #158 road bicycle.

Usage: python tools/modeling/verify_road_bike_158.py [path/to/WodraCyclist.glb]
"""
from __future__ import annotations
import json
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
assert close(origin("Contact_LeftHood")[1],.930) and close(origin("Contact_RightHood")[1],.930),"hood targets should sit on the palm shelf height"
assert close(abs(origin("Contact_LeftHood")[2]),.416) and close(abs(origin("Contact_RightHood")[2]),.416),"hood targets should sit on the rider-side palm shelf"
for side in ("L","R"):
    hood_size=local_size("BrakeHood"+side)
    assert hood_size[0]>=.044 and hood_size[1]>=.050 and hood_size[2]>=.110,\
        f"BrakeHood{side} should have a broad, elongated palm body; size={hood_size}"
left_hood_top=transformed_bounds("BrakeHoodL")[1][1]
right_hood_top=transformed_bounds("BrakeHoodR")[1][1]
assert abs(left_hood_top-origin("Contact_LeftHood")[1])<.005 and abs(right_hood_top-origin("Contact_RightHood")[1])<.005,"hood contact targets must lie on the crowned palm shelf"

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
    assert lever_size[0]<=.010 and max(lever_size[1:])>=.018,\
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
