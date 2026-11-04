#!/usr/bin/env python3
"""Rebuild the Pine Hill loop from the reviewed Alpha.92 route and cached sidecars.

This deliberately uses only committed route geometry and sidecars. It retains the
recorded direction between the two verified matching points, removes the known
four-point corner spur, recomputes chainage/elevation/fingerprint metadata, and
reprojects route-relative scenery without moving its physical location.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
from typing import Any

EARTH_RADIUS_M = 6_371_000.0
RADIANS = math.pi / 180.0
START_INDEX = 22
END_INDEX = 334
SPUR_INDICES = {99, 100, 101, 102}
EXPECTED_START = (-45.8471292, 170.5146285)


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def geo_xy(latitude: float, longitude: float, origin_lat: float, origin_lon: float) -> tuple[float, float]:
    return ((longitude - origin_lon) * RADIANS * EARTH_RADIUS_M * math.cos(origin_lat * RADIANS),
            (latitude - origin_lat) * RADIANS * EARTH_RADIUS_M)


def haversine(a: dict[str, Any], b: dict[str, Any]) -> float:
    lat1, lat2 = a["latitude"] * RADIANS, b["latitude"] * RADIANS
    dlat = lat2 - lat1
    dlon = (b["longitude"] - a["longitude"]) * RADIANS
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(max(0.0, min(1.0, h))))


def point_xy(point: dict[str, Any], origin_lat: float, origin_lon: float) -> tuple[float, float]:
    return geo_xy(point["latitude"], point["longitude"], origin_lat, origin_lon)


def make_path(points: list[dict[str, Any]], origin_lat: float, origin_lon: float):
    xy = [point_xy(p, origin_lat, origin_lon) for p in points]
    chainage = [0.0]
    bearings: list[float] = []
    for i in range(1, len(points)):
        length = math.dist(xy[i - 1], xy[i])
        if length < 0.01:
            raise ValueError(f"zero or near-zero route edge at point {i}")
        chainage.append(chainage[-1] + length)
        dx, north = xy[i][0] - xy[i - 1][0], xy[i][1] - xy[i - 1][1]
        bearings.append(math.degrees(math.atan2(dx, north)) % 360)
    return xy, chainage, bearings


def project(position: tuple[float, float], xy: list[tuple[float, float]],
            chainage: list[float]) -> tuple[float, float, float, float, float]:
    """Return chainage, signed right offset, forward residual, distance, bearing."""
    best = None
    px, py = position
    for i, (a, b) in enumerate(zip(xy, xy[1:])):
        dx, dy = b[0] - a[0], b[1] - a[1]
        length2 = dx * dx + dy * dy
        t = max(0.0, min(1.0, ((px - a[0]) * dx + (py - a[1]) * dy) / length2))
        qx, qy = a[0] + t * dx, a[1] + t * dy
        fx, fy = dx / math.sqrt(length2), dy / math.sqrt(length2)
        rx, ry = fy, -fx  # rider's right, in east/north coordinates
        ex, ey = px - qx, py - qy
        right, forward = ex * rx + ey * ry, ex * fx + ey * fy
        distance = math.hypot(ex, ey)
        s = chainage[i] + t * (chainage[i + 1] - chainage[i])
        bearing = math.degrees(math.atan2(fx, fy)) % 360
        candidate = (distance, s, right, forward, bearing)
        if best is None or candidate[0] < best[0]:
            best = candidate
    assert best is not None
    return best[1], best[2], best[3], best[0], best[4]


def sample_path(distance: float, xy: list[tuple[float, float]], chainage: list[float]):
    d = max(0.0, min(chainage[-1], distance))
    for i in range(1, len(chainage)):
        if d <= chainage[i]:
            span = chainage[i] - chainage[i - 1]
            t = (d - chainage[i - 1]) / span
            a, b = xy[i - 1], xy[i]
            dx, dy = b[0] - a[0], b[1] - a[1]
            length = math.hypot(dx, dy)
            return ((a[0] + t * dx, a[1] + t * dy),
                    (dx / length, dy / length),
                    math.degrees(math.atan2(dx, dy)) % 360)
    return xy[-1], (0.0, 1.0), 0.0


def interpolate(values: list[float], chainage: list[float], distance: float, loop: bool) -> float:
    if loop:
        distance %= chainage[-1]
    else:
        distance = max(0.0, min(chainage[-1], distance))
    for i in range(1, len(chainage)):
        if distance <= chainage[i]:
            t = (distance - chainage[i - 1]) / (chainage[i] - chainage[i - 1])
            return values[i - 1] + t * (values[i] - values[i - 1])
    return values[-1]


def fingerprint(points: list[dict[str, Any]]) -> str:
    rows = [f'{p["distanceMeters"]:.3f},{p["latitude"]:.7f},{p["longitude"]:.7f},{p["elevationMeters"]:.3f}'
            for p in points]
    return hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest()


def feature_world_position(feature: dict[str, Any], old_points: list[dict[str, Any]],
                           old_xy: list[tuple[float, float]], old_s: list[float]):
    position, forward, bearing = sample_path(float(feature.get("routeChainageMeters", 0)), old_xy, old_s)
    right = (forward[1], -forward[0])
    lateral = float(feature.get("lateralPositionMeters", 0))
    ahead = float(feature.get("forwardOffsetMeters", 0))
    return (position[0] + right[0] * lateral + forward[0] * ahead,
            position[1] + right[1] * lateral + forward[1] * ahead), bearing


def update_geo_chainage(item: dict[str, Any], latitude: float, longitude: float,
                        xy: list[tuple[float, float]], chainage: list[float],
                        origin_lat: float, origin_lon: float) -> None:
    s, _, _, offset, bearing = project(geo_xy(latitude, longitude, origin_lat, origin_lon), xy, chainage)
    item["nearestRouteDistanceMeters"] = round(s, 6)
    if "distanceFromRouteMeters" in item:
        item["distanceFromRouteMeters"] = round(offset, 6)
    if "routeTangentDegrees" in item:
        item["routeTangentDegrees"] = round(bearing, 6)


def average_geo(points: list[dict[str, Any]]) -> tuple[float, float]:
    return (sum(p["latitude"] for p in points) / len(points),
            sum(p["longitude"] for p in points) / len(points))


def build(repository: Path, apply: bool) -> dict[str, Any]:
    package = repository / "routes/user-packages/pine-hill-test"
    route_path = package / "pine-hill-test.route-points.json"
    original = read_json(route_path)
    if original.get("pineHillLoopTransform", {}).get("sourceFingerprint"):
        points = original["points"]
        if (original.get("routeType") != "loop" or not original.get("loop")
                or points[0]["latitude"] != points[-1]["latitude"]
                or points[0]["longitude"] != points[-1]["longitude"]):
            raise ValueError("previous Pine Hill loop transform marker exists but the output geometry is invalid")
        return {"route": original, "length": points[-1]["distanceMeters"],
                "sourceLength": original["pineHillLoopTransform"]["sharedSourceLengthMeters"],
                "spurRemovedMeters": original["pineHillLoopTransform"]["removedSpurMeters"],
                "pointCount": len(points), "alreadyApplied": True}
    source_points = original["points"]
    if len(source_points) != 396:
        raise ValueError(f"expected the reviewed 396-point Alpha.92 route, got {len(source_points)}")
    first, repeated = source_points[START_INDEX], source_points[END_INDEX]
    if (abs(first["latitude"] - EXPECTED_START[0]) > 1e-9
            or abs(first["longitude"] - EXPECTED_START[1]) > 1e-9
            or first["latitude"] != repeated["latitude"]
            or first["longitude"] != repeated["longitude"]):
        raise ValueError("the published loop boundary no longer matches the reviewed coordinates")
    old_origin_lat, old_origin_lon = source_points[0]["latitude"], source_points[0]["longitude"]
    old_xy, old_s, _ = make_path(source_points, old_origin_lat, old_origin_lon)

    kept = [(i, dict(source_points[i])) for i in range(START_INDEX, END_INDEX + 1) if i not in SPUR_INDICES]
    points = [point for _, point in kept]
    origin_lat, origin_lon = points[0]["latitude"], points[0]["longitude"]
    xy, chainage, bearings = make_path(points, origin_lat, origin_lon)
    length = round(chainage[-1], 9)
    origin_shift_east, origin_shift_north = geo_xy(origin_lat, origin_lon, old_origin_lat, old_origin_lon)
    if not 1310 <= length <= 1340:
        raise ValueError(f"corrected loop length outside the expected 1.31–1.34 km range: {length:.3f} m")
    if points[-1]["latitude"] != points[0]["latitude"] or points[-1]["longitude"] != points[0]["longitude"]:
        raise ValueError("the corrected route does not close geographically")

    elevation_fields = ("rawElevationMeters", "calibratedSourceElevationMeters",
                        "filteredSourceElevationMeters", "smoothedElevationMeters", "elevationMeters")
    elevation_seam_delta = points[0]["elevationMeters"] - points[-1]["elevationMeters"]
    for i, point in enumerate(points):
        point["distanceMeters"] = round(chainage[i], 9)
        correction = elevation_seam_delta * chainage[i] / length
        for field in elevation_fields:
            if field in point:
                point[field] = point[field] + correction
    for field in elevation_fields:
        if field in points[0] and field in points[-1]:
            points[-1][field] = points[0][field]
    elevations = [float(p["elevationMeters"]) for p in points]
    for i, point in enumerate(points):
        d = chainage[i]
        if i in (0, len(points) - 1):
            left = interpolate(elevations, chainage, d - 10, True)
            right = interpolate(elevations, chainage, d + 10, True)
        else:
            left = interpolate(elevations, chainage, d - 10, False)
            right = interpolate(elevations, chainage, d + 10, False)
        point["gradientPercent"] = (right - left) / 20 * 100

    old_to_new_index = {source_index: i for i, (source_index, _) in enumerate(kept)}
    del old_to_new_index  # retained explicitly while the removed source indexes stay auditable below
    route = dict(original)
    route.update({"routeType": "loop", "loop": True, "routeDataVersion": 2,
                  "geometryFingerprint": fingerprint(points), "referenceDistanceMeters": length,
                  "actualOsmDistanceMeters": length, "sourceNetworkDistanceMeters": length,
                  "actualToReferenceRatio": 1.0, "candidatePointCount": len(points), "isValid": True,
                  "totalAscentMeters": sum(max(0.0, elevations[i] - elevations[i - 1])
                                            for i in range(1, len(elevations))), "points": points,
                  "pineHillLoopTransform": {
                      "sourceFingerprint": original["geometryFingerprint"],
                      "sourceStartIndex": START_INDEX, "sourceEndIndex": END_INDEX,
                      "removedSpurPointIndices": sorted(SPUR_INDICES),
                      "sharedSourceLengthMeters": source_points[END_INDEX]["distanceMeters"]
                          - source_points[START_INDEX]["distanceMeters"],
                      "removedSpurMeters": source_points[102]["distanceMeters"]
                          - source_points[98]["distanceMeters"],
                      "elevationSeamCorrectionMeters": elevation_seam_delta,
                  }})

    # Every geographic scenery object remains in place. Only its nearest route chainage
    # and tangent metadata are recomputed against the closed route.
    for suffix in ("buildings", "landmarks", "street-furniture", "vegetation", "streets"):
        path = package / f"pine-hill-test.{suffix}.json"
        sidecar = read_json(path)
        collections = {"buildings": "buildings", "landmarks": "landmarks",
                       "street-furniture": "items", "vegetation": "areas", "streets": "streets"}
        for item in sidecar.get(collections[suffix], []):
            if suffix == "buildings":
                lat, lon = average_geo(item["footprint"])
            elif suffix == "vegetation":
                lat = sum(p[1] for p in item["polygon"]) / len(item["polygon"])
                lon = sum(p[0] for p in item["polygon"]) / len(item["polygon"])
            elif suffix == "streets":
                lat, lon = average_geo(item["points"])
            else:
                lat, lon = item["latitude"], item["longitude"]
            update_geo_chainage(item, lat, lon, xy, chainage, origin_lat, origin_lon)
        if suffix == "vegetation":
            for tree in sidecar.get("trees", []):
                update_geo_chainage(tree, tree["latitude"], tree["longitude"], xy, chainage, origin_lat, origin_lon)
        if apply:
            write_json(path, sidecar)

    # Terrain grids use local east/south coordinates based on the old first point.
    terrain_path = package / "pine-hill-test.terrain.json"
    terrain = read_json(terrain_path)
    origin_shift_x = origin_shift_east
    terrain["routeStartElevationMeters"] = points[0]["elevationMeters"]
    for collection_name in ("chunks", "farChunks"):
        for chunk in terrain.get(collection_name, []):
            chunk["originX"] -= origin_shift_x
            chunk["originZ"] += origin_shift_north
            spacing = float(chunk.get("spacingMeters", terrain.get("spacingMeters", 10)))
            if collection_name == "farChunks":
                spacing = float(terrain.get("farSpacingMeters", 20))
            center = (chunk["originX"] + (chunk["columns"] - 1) * spacing / 2,
                      -(chunk["originZ"] + (chunk["rows"] - 1) * spacing / 2))
            chunk["nearestRouteDistanceMeters"] = round(project(center, xy, chainage)[0], 6)
    if apply:
        write_json(terrain_path, terrain)

    # Transform authored scene features through world space. This preserves exact
    # placement/orientation even for observations outside the new lap's road corridor.
    observations_path = package / "pine-hill-test.observations.json"
    observations = read_json(observations_path)
    for observation in observations["observations"]:
        old_chainage = float(observation["routeChainageMeters"])
        old_position, _, _ = sample_path(old_chainage, old_xy, old_s)
        old_position = (old_position[0] - origin_shift_east, old_position[1] - origin_shift_north)
        old_position, _, _, _, _ = project(old_position, xy, chainage)
        observation["sourceRouteChainageMeters"] = old_chainage
        observation["routeChainageMeters"] = round(old_position, 6)
        for feature in observation.get("features", []):
            world, old_bearing = feature_world_position(feature, source_points, old_xy, old_s)
            world = (world[0] - origin_shift_east, world[1] - origin_shift_north)
            new_s, new_lateral, new_forward, _, new_bearing = project(world, xy, chainage)
            feature["sourceRouteChainageMeters"] = feature["routeChainageMeters"]
            feature["routeChainageMeters"] = round(new_s, 6)
            feature["lateralPositionMeters"] = round(new_lateral, 6)
            feature["forwardOffsetMeters"] = round(new_forward, 6)
            feature["orientationDegrees"] = (float(feature.get("orientationDegrees", 0))
                                                + old_bearing - new_bearing + 180) % 360 - 180
    observations["geometryFingerprint"] = route["geometryFingerprint"]
    for observation in observations["observations"]:
        observation["geometryFingerprint"] = route["geometryFingerprint"]

    # Derive a route-builder authoring config from the retained mapped road points.
    config_path = repository / "routes/authoring/pine-hill-test.json"
    config = read_json(config_path)
    old_anchors = config.get("anchors", [])
    config["loop"] = True
    config["routeType"] = "loop"
    config["routeDataVersion"] = 2
    config["referenceDistanceMeters"] = length
    config["anchors"] = []
    targets = [0.0]
    targets.extend(d for d in chainage[1:-1] if d - targets[-1] >= 25.0)
    if length - targets[-1] < 12:
        targets.pop()
    targets.append(length)
    for number, d in enumerate(targets):
        if number == len(targets) - 1:
            latitude, longitude = points[0]["latitude"], points[0]["longitude"]
        else:
            position, _, _ = sample_path(d, xy, chainage)
            latitude = origin_lat + position[1] / EARTH_RADIUS_M / RADIANS
            longitude = origin_lon + position[0] / (EARTH_RADIUS_M * math.cos(origin_lat * RADIANS)) / RADIANS
        config["anchors"].append({"name": f"Loop road anchor {number:02d}", "sourceDistanceMeters": d,
                                  "latitude": latitude, "longitude": longitude})
    config["legacyReferencePoints"] = old_anchors

    result = {"route": route, "terrain": terrain, "observations": observations, "config": config,
              "length": length, "sourceLength": source_points[END_INDEX]["distanceMeters"] - source_points[START_INDEX]["distanceMeters"],
              "spurRemovedMeters": (source_points[102]["distanceMeters"] - source_points[98]["distanceMeters"]),
              "pointCount": len(points), "oldOriginShift": (origin_shift_x, origin_shift_north)}
    if apply:
        write_json(route_path, route)
        write_json(observations_path, observations)
        write_json(config_path, config)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--apply", action="store_true", help="rewrite the route, authoring config, and sidecars")
    args = parser.parse_args()
    repository = args.repository.resolve()
    result = build(repository, args.apply)
    points = result["route"]["points"]
    _, _, headings = make_path(points, points[0]["latitude"], points[0]["longitude"])
    turns = [abs((headings[i] - headings[i - 1] + 540.0) % 360.0 - 180.0)
             for i in range(1, len(headings))]
    transform = result["route"].get("pineHillLoopTransform", {})
    report = {
        "id": "pine-hill-test-loop-transform",
        "routeId": "pine-hill-test",
        "method": "deterministic trim of published route geometry; no connector or new map data",
        "source": {
            "geometryFingerprint": transform.get("sourceFingerprint"),
            "pointCount": 396,
            "startIndex": transform.get("sourceStartIndex", START_INDEX),
            "repeatIndex": transform.get("sourceEndIndex", END_INDEX),
            "sharedLengthMeters": result["sourceLength"],
            "removedSpurPointIndices": transform.get("removedSpurPointIndices", sorted(SPUR_INDICES)),
            "removedSpurMeters": result["spurRemovedMeters"],
        },
        "result": {
            "routeType": "loop",
            "pointCount": result["pointCount"],
            "distanceMeters": result["length"],
            "geometryFingerprint": result["route"]["geometryFingerprint"],
            "seamCoordinatesExact": points[0]["latitude"] == points[-1]["latitude"]
                and points[0]["longitude"] == points[-1]["longitude"],
            "seamElevationDeltaMeters": points[-1]["elevationMeters"] - points[0]["elevationMeters"],
            "seamIncomingBearingDegrees": headings[-1],
            "seamOutgoingBearingDegrees": headings[0],
            "maximumSuccessiveHeadingChangeDegrees": max(turns, default=0.0),
            "elevationSeamCorrectionMeters": transform.get("elevationSeamCorrectionMeters"),
        },
        "sidecars": {
            "authoringConfigLoopAnchors": len(read_json(repository / "routes/authoring/pine-hill-test.json").get("anchors", [])),
            "routeRelativeObservations": len(read_json(repository / "routes/user-packages/pine-hill-test/pine-hill-test.observations.json").get("observations", [])),
            "terrainChunks": len(read_json(repository / "routes/user-packages/pine-hill-test/pine-hill-test.terrain.json").get("chunks", [])),
            "farTerrainChunks": len(read_json(repository / "routes/user-packages/pine-hill-test/pine-hill-test.terrain.json").get("farChunks", [])),
        },
    }
    if args.apply:
        write_json(repository / "routes/review/pine-hill-test-loop-transform.json", report)
    print(f"routeType=loop points={result['pointCount']} length={result['length']:.3f} m "
          f"(published shared segment {result['sourceLength']:.3f} m; spur removed "
          f"{result['spurRemovedMeters']:.3f} m)")
    print(f"fingerprint={result['route']['geometryFingerprint']} apply={args.apply} "
          f"alreadyApplied={result.get('alreadyApplied', False)}")


if __name__ == "__main__":
    main()
