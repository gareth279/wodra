"""Print mesh and node bounds from a GLB using only the Python standard library.

Usage: python tools/modeling/inspect_wodra_glb.py [path/to/model.glb]
The report uses the GLB's exported coordinates and applies every node transform.
"""

from __future__ import annotations

import json
import math
import struct
import sys
from pathlib import Path

RUNTIME_SCALED_TARGETS = {
    "LeftCrank", "RightCrank", "LeftPedal", "RightPedal", "LeftThigh", "RightThigh",
    "LeftShin", "RightShin", "LeftCyclingShoe", "RightCyclingShoe", "LeftShoeSole",
    "RightShoeSole", "LeftAnkle", "RightAnkle", "LeftKnee", "RightKnee",
}


def identity():
    return [[1.0 if row == col else 0.0 for col in range(4)] for row in range(4)]


def multiply(a, b):
    return [[sum(a[row][k] * b[k][col] for k in range(4)) for col in range(4)] for row in range(4)]


def node_matrix(node):
    if "matrix" in node:
        values = node["matrix"]
        return [[values[col * 4 + row] for col in range(4)] for row in range(4)]
    t = node.get("translation", (0, 0, 0))
    qx, qy, qz, qw = node.get("rotation", (0, 0, 0, 1))
    sx, sy, sz = node.get("scale", (1, 1, 1))
    rotation = [
        [1 - 2 * (qy*qy + qz*qz), 2 * (qx*qy - qz*qw), 2 * (qx*qz + qy*qw), 0],
        [2 * (qx*qy + qz*qw), 1 - 2 * (qx*qx + qz*qz), 2 * (qy*qz - qx*qw), 0],
        [2 * (qx*qz - qy*qw), 2 * (qy*qz + qx*qw), 1 - 2 * (qx*qx + qy*qy), 0],
        [0, 0, 0, 1],
    ]
    scale = [[sx, 0, 0, 0], [0, sy, 0, 0], [0, 0, sz, 0], [0, 0, 0, 1]]
    result = multiply(rotation, scale)
    result[0][3], result[1][3], result[2][3] = t
    return result


def point(matrix, p):
    v = [*p, 1]
    out = [sum(matrix[row][col] * v[col] for col in range(4)) for row in range(4)]
    return tuple(out[i] / out[3] for i in range(3))


def bounds_corners(lo, hi):
    return [(x, y, z) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]


def bounds(points):
    lo = tuple(min(p[i] for p in points) for i in range(3))
    hi = tuple(max(p[i] for p in points) for i in range(3))
    return lo, hi, tuple(hi[i] - lo[i] for i in range(3))


def main():
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb"
    raw = path.read_bytes()
    magic, version, declared_length = struct.unpack_from("<4sII", raw)
    if magic != b"glTF" or version != 2 or declared_length != len(raw):
        raise ValueError(f"Invalid GLB header: magic={magic!r}, version={version}, declared={declared_length}, actual={len(raw)}")
    json_length, json_type = struct.unpack_from("<II", raw, 12)
    if json_type != 0x4E4F534A:
        raise ValueError("First GLB chunk is not JSON")
    document = json.loads(raw[20:20 + json_length].decode("utf-8"))
    print(f"File: {path}\nGLB {version}, {len(raw)} bytes, asset {document.get('asset', {}).get('version')}")
    nodes = document.get("nodes", [])
    meshes = document.get("meshes", [])
    accessors = document.get("accessors", [])
    parent_of = {}
    for parent, node in enumerate(nodes):
        for child in node.get("children", []):
            parent_of[child] = parent
    world = {}

    def visit(index, parent_matrix):
        matrix = multiply(parent_matrix, node_matrix(nodes[index]))
        world[index] = matrix
        for child in nodes[index].get("children", []):
            visit(child, matrix)

    scene = document.get("scenes", [{}])[document.get("scene", 0)]
    for root in scene.get("nodes", []):
        visit(root, identity())

    rows, all_points, static_points = [], [], []
    for node_index, node in enumerate(nodes):
        if "mesh" not in node:
            continue
        mesh = meshes[node["mesh"]]
        pos_accessors = [p["attributes"]["POSITION"] for p in mesh.get("primitives", []) if "POSITION" in p.get("attributes", {})]
        if not pos_accessors:
            continue
        lows, highs = [], []
        for ai in pos_accessors:
            accessor = accessors[ai]
            if "min" not in accessor or "max" not in accessor:
                raise ValueError(f"POSITION accessor {ai} on {node.get('name')} has no bounds")
            lows.append(accessor["min"]); highs.append(accessor["max"])
        lo = tuple(min(v[i] for v in lows) for i in range(3))
        hi = tuple(max(v[i] for v in highs) for i in range(3))
        local_size = tuple(hi[i] - lo[i] for i in range(3))
        matrix = world.get(node_index, node_matrix(node))
        transformed = [point(matrix, p) for p in bounds_corners(lo, hi)]
        world_lo, world_hi, world_size = bounds(transformed)
        all_points.extend(transformed)
        if node.get("name") not in RUNTIME_SCALED_TARGETS:
            static_points.extend(transformed)
        scale = node.get("scale", (1, 1, 1))
        rows.append((max(world_size), node.get("name", f"node#{node_index}"), local_size, world_size, scale, world_lo, world_hi, mesh.get("name", "")))
    for _, name, local_size, world_size, scale, lo, hi, mesh_name in sorted(rows, reverse=True):
        target_note = " [normalized target: runtime must scale]" if name in RUNTIME_SCALED_TARGETS else ""
        print(f"{name:30} mesh={mesh_name:30} local={tuple(round(x,4) for x in local_size)} world={tuple(round(x,4) for x in world_size)} scale={tuple(round(x,4) for x in scale)} bounds={tuple(round(x,4) for x in lo)}..{tuple(round(x,4) for x in hi)}{target_note}")
    if all_points:
        lo, hi, size = bounds(all_points)
        print(f"MODEL BOUNDS: {tuple(round(x,4) for x in size)} ({tuple(round(x,4) for x in lo)}..{tuple(round(x,4) for x in hi)})")
        static_lo, static_hi, static_size = bounds(static_points)
        print(f"STATIC MODEL BOUNDS (excluding runtime-scaled targets): {tuple(round(x,4) for x in static_size)}")
    print(f"Mesh nodes: {len(rows)}; nodes without meshes: {sum('mesh' not in n for n in nodes)}")
    names = {node.get("name") for node in nodes}
    missing_targets = sorted(RUNTIME_SCALED_TARGETS - names)
    oversized_targets = [r[1] for r in rows if r[1] in RUNTIME_SCALED_TARGETS and max(r[2]) > 1.01]
    if missing_targets:
        raise ValueError(f"Missing required runtime animation targets: {missing_targets}")
    if oversized_targets:
        raise ValueError(f"Runtime animation templates exceed normalized one-metre bounds: {oversized_targets}")
    print(f"Animation targets: {len(RUNTIME_SCALED_TARGETS)} present; normalized source geometry verified")
    large = [r for r in rows if max(r[3]) > 2.25]
    scaled = [r for r in rows if any(abs(s - 1.0) > 1e-4 for s in r[4])]
    print(f"Meshes over 2.25 m in any dimension: {len(large)}; non-unit node scales: {len(scaled)}")


if __name__ == "__main__":
    main()
