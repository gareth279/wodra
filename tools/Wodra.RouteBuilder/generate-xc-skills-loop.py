#!/usr/bin/env python3
"""Generate the fictional, route-local Wodra XC skills-loop package deterministically."""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "src" / "IndoorCycling.Game" / "Data" / "Routes"
ROUTE_ID = "wodra-xc-skills-loop"
LENGTH = 1600.0
STEP = 2.0


def smooth(t: float) -> float:
    t = max(0.0, min(1.0, t))
    return t * t * (3.0 - 2.0 * t)


def raw_point(theta: float) -> tuple[float, float]:
    rx = 228.0 * (1.0 + 0.065 * math.cos(3 * theta) + 0.022 * math.sin(5 * theta))
    rz = 174.0 * (1.0 + 0.085 * math.sin(2 * theta) + 0.025 * math.cos(3 * theta))
    return rx * math.cos(theta), rz * math.sin(theta)


def terrain_height(x: float, z: float) -> float:
    theta = math.atan2(z / 174.0, x / 228.0)
    radius = math.sqrt((x / 228.0) ** 2 + (z / 174.0) ** 2)
    return 118.0 + 10.5 * math.sin(theta + 0.28) + 3.3 * math.sin(2 * theta - 0.55) \
        + 2.0 * math.sin(3 * theta + 0.6) + 5.0 * (radius - 1.0)


def build_route_points() -> list[dict[str, float | None]]:
    raw = [raw_point(2.0 * math.pi * index / 50000.0) for index in range(50001)]
    cumulative = [0.0]
    for a, b in zip(raw, raw[1:]):
        cumulative.append(cumulative[-1] + math.dist(a, b))
    total = cumulative[-1]
    # Resample the authored local curve at a fixed 2 m reference chainage. Its closing
    # duplicate point is required by the shared route loader and lap-seam policy.
    count = int(LENGTH / STEP)
    result = []
    source = 0
    for index in range(count + 1):
        distance = index * STEP
        target = distance / LENGTH * total
        while source + 1 < len(cumulative) and cumulative[source + 1] < target:
            source += 1
        span = cumulative[source + 1] - cumulative[source]
        t = (target - cumulative[source]) / span if span else 0.0
        x = raw[source][0] + (raw[source + 1][0] - raw[source][0]) * t
        z = raw[source][1] + (raw[source + 1][1] - raw[source][1]) * t
        result.append({"distanceMeters": distance, "latitude": None, "longitude": None,
                       "localXMeters": x, "localZMeters": z,
                       "elevationMeters": terrain_height(x, z), "rawElevationMeters": terrain_height(x, z),
                       "gradientPercent": None})
    result[0]["localXMeters"] = result[-1]["localXMeters"]
    result[0]["localZMeters"] = result[-1]["localZMeters"]
    elevations = [float(point["elevationMeters"]) for point in result]
    for index, point in enumerate(result):
        previous = elevations[max(0, index - 1)]
        following = elevations[min(len(elevations) - 1, index + 1)]
        span = min(LENGTH, (index + 1) * STEP) - max(0.0, (index - 1) * STEP)
        point["gradientPercent"] = (following - previous) / span * 100.0 if span else 0.0
    canonical = "\n".join(
        f"{point['distanceMeters']:.3f},local:{point['localXMeters']:.3f},{point['localZMeters']:.3f},{point['elevationMeters']:.3f}"
        for point in result
    )
    fingerprint = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    ascent = sum(max(0.0, elevations[index + 1] - elevations[index]) for index in range(len(elevations) - 1))
    attribution = ("Fictional Wodra-authored XC course. Geometry, heightfield, trail lines, berms, rollers and vegetation "
                   "are original procedural training content; no real-world trail, survey, or geographic source is claimed.")
    route = {
        "id": ROUTE_ID, "name": "Wodra XC Skills Loop (fictional)", "isValid": True,
        "actualOsmDistanceMeters": None, "actualRouteDistanceMeters": LENGTH, "points": result,
        "routeType": "loop", "difficulty": "Unrated", "routeDataVersion": 1,
        "geometryFingerprint": fingerprint, "sourceAttribution": attribution,
        "totalAscentMeters": ascent, "environment": "MountainBike", "discipline": "MountainBike",
        "surfaceSegments": [{"startMeters": 0, "endMeters": LENGTH, "surface": "authored-hardpack-singletrack",
                              "sourceOsmWayId": 0, "sourceName": "Wodra authored fictional XC course"}]
    }
    return route


def build_course_features(route: dict) -> dict:
    features = []
    for feature_id, start, side, name in [
        ("north-tabletop", 210.0, 1.0, "North tabletop"),
        ("ridge-tabletop", 730.0, -1.0, "Ridge tabletop"),
        ("lower-tabletop", 1250.0, 1.0, "Lower tabletop"),
    ]:
        takeoff = start + 70.0
        landing = takeoff + 25.0
        end = start + 150.0
        target = side * 2.65
        points = []
        for distance in range(int(start), int(end) + 1, 2):
            out = smooth((distance - start) / 38.0)
            back = 1.0 - smooth((distance - (end - 38.0)) / 38.0)
            lateral = target * min(out, back)
            # This is a small visual trail surface profile, intentionally independent
            # of the route's authoritative elevation/gradient samples.
            lift = 0.0
            if takeoff - 9.0 < distance <= takeoff:
                lift = 0.42 * smooth((distance - (takeoff - 9.0)) / 9.0)
            elif takeoff < distance < landing:
                lift = 0.42 - 0.12 * smooth((distance - takeoff) / (landing - takeoff))
            elif landing <= distance < landing + 12.0:
                lift = 0.30 * (1.0 - smooth((distance - landing) / 12.0))
            points.append({"routeDistanceMeters": float(distance), "lateralMeters": lateral,
                           "surfaceLiftMeters": lift})
        features.append({"id": feature_id, "name": name, "startMeters": start,
                         "takeoffMeters": takeoff, "landingMeters": landing, "endMeters": end,
                         "requiredLateralMeters": target, "halfWidthMeters": 0.86,
                         "minimumSpeedKph": 20.0, "maximumAirHeightMeters": 0.58,
                         "scorePoints": 75, "path": points})
    berms = [
        {"id": "upper-left-berm", "startMeters": 430.0, "endMeters": 478.0, "side": -1.0, "widthMeters": 2.4, "heightMeters": 0.62},
        {"id": "ridge-right-berm", "startMeters": 970.0, "endMeters": 1024.0, "side": 1.0, "widthMeters": 2.6, "heightMeters": 0.7},
        {"id": "lower-left-berm", "startMeters": 1082.0, "endMeters": 1128.0, "side": -1.0, "widthMeters": 2.2, "heightMeters": 0.55},
    ]
    rollers = [
        {"id": "flow-roller-one", "startMeters": 520.0, "peakMeters": 530.0, "endMeters": 540.0, "heightMeters": 0.2},
        {"id": "flow-roller-two", "startMeters": 1150.0, "peakMeters": 1161.0, "endMeters": 1172.0, "heightMeters": 0.24},
    ]
    return {"id": ROUTE_ID, "version": 1, "features": features, "berms": berms, "rollers": rollers}


def build_terrain(route: dict) -> dict:
    points = route["points"]
    origin_x = float(points[0]["localXMeters"])
    origin_z = float(points[0]["localZMeters"])
    xs = [float(p["localXMeters"]) - origin_x for p in points]
    zs = [float(p["localZMeters"]) - origin_z for p in points]
    spacing = 8.0
    min_x, max_x = math.floor(min(xs) / spacing) * spacing - 72, math.ceil(max(xs) / spacing) * spacing + 72
    min_z, max_z = math.floor(min(zs) / spacing) * spacing - 72, math.ceil(max(zs) / spacing) * spacing + 72
    columns = int(round((max_x - min_x) / spacing))
    rows = int(round((max_z - min_z) / spacing))
    elevations = []
    for row in range(rows + 1):
        z = origin_z + min_z + row * spacing
        for column in range(columns + 1):
            x = origin_x + min_x + column * spacing
            elevations.append(terrain_height(x, z))
    cell_mask = [True] * (columns * rows)
    return {"id": ROUTE_ID, "sourceDataset": "Wodra-authored-fictional-heightfield-v1",
            "sourceCrs": "route-local metres; no geographic CRS", "spacingMeters": spacing,
            "corridorRadiusMeters": 420.0, "chunkSizeMeters": max(columns, rows) * spacing,
            "routeStartElevationMeters": float(points[0]["elevationMeters"]), "verticalBiasMeters": 0.0,
            "roadClearanceMeters": 0.45, "roadClearanceRadiusMeters": 4.0,
            "minimumSourceElevationMeters": min(elevations), "maximumSourceElevationMeters": max(elevations),
            "vertexCount": len(elevations), "triangleCount": columns * rows * 2,
            "noDataCells": 0, "interpolatedVertices": 0,
            "chunks": [{"id": "FictionalXCHeightfield", "originX": min_x, "originZ": min_z,
                        "columns": columns, "rows": rows, "nearestRouteDistanceMeters": 0.0,
                        "elevationsMeters": elevations, "cellMask": cell_mask}],
            "farSpacingMeters": 0, "farRadiusMeters": 0, "nearRadiusMeters": 420,
            "transitionOverlapMeters": 0, "farVerticalBiasMeters": 0,
            "elevationSamplerVersion": 1, "elevationSamplingMethod": "original analytic local heightfield"}


def build_vegetation(route: dict) -> dict:
    points = route["points"]
    origin_x = float(points[0]["localXMeters"])
    origin_z = float(points[0]["localZMeters"])
    trees = []
    count = len(points) - 1
    # Hand-authored scenery stays well outside the trail and jump corridors.
    for index in range(0, count, 10):
        point = points[index]
        previous = points[max(0, index - 1)]
        following = points[min(count, index + 1)]
        dx = float(following["localXMeters"]) - float(previous["localXMeters"])
        dz = float(following["localZMeters"]) - float(previous["localZMeters"])
        length = math.hypot(dx, dz) or 1.0
        left_x, left_z = -dz / length, dx / length
        for sign in (-1.0, 1.0):
            lateral = 12.0 + ((index * 17) % 19)
            x = float(point["localXMeters"]) + left_x * lateral * sign
            z = float(point["localZMeters"]) + left_z * lateral * sign
            trees.append({"source": "wodra-authored", "osmId": 0, "latitude": None, "longitude": None,
                          "localXMeters": x, "localZMeters": z, "elevationMeters": terrain_height(x, z),
                          "treeType": "conifer" if (index // 10) % 3 else "broadleaf",
                          "heightMeters": 7.0 + ((index * 7) % 8), "canopyRadiusMeters": 2.0,
                          "nearestRouteDistanceMeters": float(point["distanceMeters"])})
    return {"id": ROUTE_ID, "areas": [], "trees": trees}


def write_json(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    route = build_route_points()
    write_json(OUT / f"{ROUTE_ID}.route-points.json", route)
    write_json(OUT / f"{ROUTE_ID}.trail-features.json", build_course_features(route))
    write_json(OUT / f"{ROUTE_ID}.terrain.json", build_terrain(route))
    write_json(OUT / f"{ROUTE_ID}.vegetation.json", build_vegetation(route))
    ascent = route["totalAscentMeters"]
    print(f"Generated fictional XC loop: {LENGTH:.0f} m, ascent={ascent:.1f} m, "
          f"points={len(route['points'])}, branches=3; attribution: {route['sourceAttribution']}")


if __name__ == "__main__":
    main()
