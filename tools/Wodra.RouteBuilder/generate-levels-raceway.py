#!/usr/bin/env python3
"""Resample the reviewed Levels Raceway OSM centreline GPX into a flat local workout route."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import xml.etree.ElementTree as ET
from pathlib import Path

EARTH_RADIUS_M = 6_371_008.8
SPACING_M = 5.0
OSM_WAY_ID = "101182095"
ATTRIBUTION = "© OpenStreetMap contributors, ODbL 1.0 (way 101182095); route centerline used for flat workout scenery only."


def haversine(a: tuple[float, float], b: tuple[float, float]) -> float:
    lat1, lon1 = map(math.radians, a)
    lat2, lon2 = map(math.radians, b)
    dlat, dlon = lat2 - lat1, lon2 - lon1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(h))


def signed_area(points: list[tuple[float, float]]) -> float:
    return sum(a[1] * b[0] - b[1] * a[0] for a, b in zip(points, points[1:])) / 2


def read_gpx(path: Path) -> list[tuple[float, float]]:
    root = ET.parse(path).getroot()
    points = [(float(node.attrib["lat"]), float(node.attrib["lon"]))
              for node in root.findall(".//{*}trkpt")]
    if len(points) < 4 or points[0] != points[-1]:
        raise ValueError("Levels Raceway source GPX must contain a closed track with at least four points")
    if len(set(points[:-1])) != len(points) - 1:
        raise ValueError("Levels Raceway source GPX contains duplicate non-seam coordinates")
    return points


def extract_osm_circuit(path: Path, start_chainage_m: float) -> list[tuple[float, float]]:
    root = ET.parse(path).getroot()
    way = next((item for item in root.findall("way") if item.attrib.get("id") == OSM_WAY_ID), None)
    if way is None:
        raise ValueError(f"OSM extract does not contain circuit way {OSM_WAY_ID}")
    tags = {item.attrib["k"]: item.attrib["v"] for item in way.findall("tag")}
    if tags.get("highway") != "raceway" or tags.get("name") != "Timaru International Motor Raceway":
        raise ValueError("OSM way 101182095 no longer identifies Timaru International Motor Raceway")
    refs = [item.attrib["ref"] for item in way.findall("nd")]
    if len(refs) < 4 or refs[0] != refs[-1]:
        raise ValueError("OSM circuit way is not a closed loop")
    nodes = {item.attrib["id"]: (float(item.attrib["lat"]), float(item.attrib["lon"]))
             for item in root.findall("node")}
    try:
        raw = [nodes[ref] for ref in refs]
    except KeyError as error:
        raise ValueError(f"OSM circuit extract is missing node {error.args[0]}") from error
    total = sum(haversine(a, b) for a, b in zip(raw, raw[1:]))
    if not 0 <= start_chainage_m < total:
        raise ValueError(f"Requested start chainage {start_chainage_m:.1f} m is outside the {total:.2f} m lap")

    traversed = 0.0
    for index, (a, b) in enumerate(zip(raw, raw[1:])):
        length = haversine(a, b)
        if traversed + length >= start_chainage_m:
            start = interpolate(a, b, (start_chainage_m - traversed) / length)
            points = [start, *raw[index + 1:-1], *raw[:index + 1], start]
            if signed_area(points) <= 0:
                raise ValueError("OSM way direction is not anticlockwise; refusing to silently reverse the official route")
            return points
        traversed += length
    raise ValueError("Could not position the start/finish reference on the OSM circuit")


def write_gpx(path: Path, points: list[tuple[float, float]]) -> None:
    namespace = "http://www.topografix.com/GPX/1/1"
    root = ET.Element(f"{{{namespace}}}gpx", {"version": "1.1", "creator": "Wodra route source preparation"})
    metadata = ET.SubElement(root, f"{{{namespace}}}metadata")
    ET.SubElement(metadata, f"{{{namespace}}}name").text = "Levels Raceway full circuit centreline"
    ET.SubElement(metadata, f"{{{namespace}}}desc").text = (
        "OpenStreetMap way 101182095 (source=LINZ), full anticlockwise 2.4 km layout. "
        "Chainage zero is aligned approximately with the official map start/finish position on the main straight."
    )
    track = ET.SubElement(root, f"{{{namespace}}}trk")
    ET.SubElement(track, f"{{{namespace}}}name").text = "Timaru International Motor Raceway · full circuit"
    segment = ET.SubElement(track, f"{{{namespace}}}trkseg")
    for latitude, longitude in points:
        ET.SubElement(segment, f"{{{namespace}}}trkpt", {
            "lat": f"{latitude:.10f}", "lon": f"{longitude:.10f}"
        })
    ET.indent(root, space="  ")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(ET.tostring(root, encoding="utf-8", xml_declaration=True) + b"\n")


def interpolate(a: tuple[float, float], b: tuple[float, float], t: float) -> tuple[float, float]:
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)


def resample(points: list[tuple[float, float]]) -> tuple[list[dict[str, float | None]], float]:
    lengths = [haversine(a, b) for a, b in zip(points, points[1:])]
    cumulative = [0.0]
    for length in lengths:
        cumulative.append(cumulative[-1] + length)
    total = cumulative[-1]
    if not 2300 <= total <= 2500:
        raise ValueError(f"Source circuit length {total:.2f} m is not credible for the published 2.4 km full layout")

    origin_lat, origin_lon = points[0]
    origin_lat_rad = math.radians(origin_lat)
    distances = [i * SPACING_M for i in range(math.floor(total / SPACING_M) + 1)]
    if not math.isclose(distances[-1], total, abs_tol=1e-8):
        distances.append(total)
    output = []
    segment = 0
    for distance in distances:
        while segment < len(lengths) - 1 and cumulative[segment + 1] < distance:
            segment += 1
        span = lengths[segment]
        ratio = 0 if span == 0 else (distance - cumulative[segment]) / span
        lat, lon = interpolate(points[segment], points[segment + 1], ratio)
        x = (lon - origin_lon) * math.pi / 180 * EARTH_RADIUS_M * math.cos(origin_lat_rad)
        z = -(lat - origin_lat) * math.pi / 180 * EARTH_RADIUS_M
        output.append({
            "distanceMeters": round(distance, 6), "latitude": None, "longitude": None,
            "localXMeters": round(x, 6), "localZMeters": round(z, 6),
            "elevationMeters": 0.0, "rawElevationMeters": 0.0, "gradientPercent": 0.0,
        })
    output[-1]["distanceMeters"] = round(total, 6)
    output[-1]["localXMeters"] = output[0]["localXMeters"]
    output[-1]["localZMeters"] = output[0]["localZMeters"]
    return output, total


def fingerprint(points: list[dict[str, float | None]]) -> str:
    canonical = "\n".join(
        f"{point['distanceMeters']:.3f},local:{point['localXMeters']:.3f},{point['localZMeters']:.3f},{point['elevationMeters']:.3f}"
        for point in points
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--gpx", type=Path)
    parser.add_argument("--osm", type=Path, help="Extracted OSM XML; writes the reviewed GPX source first")
    parser.add_argument("--gpx-output", type=Path)
    parser.add_argument("--start-chainage", type=float, default=1750.0,
                        help="Start/finish reference on the main straight in source-way chainage")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    if args.osm:
        if not args.gpx_output:
            parser.error("--osm requires --gpx-output")
        write_gpx(args.gpx_output, extract_osm_circuit(args.osm, args.start_chainage))
        gpx_path = args.gpx_output
    elif args.gpx:
        gpx_path = args.gpx
    else:
        parser.error("provide --gpx to resample the saved source or --osm to extract the route")

    coordinates = read_gpx(gpx_path)
    area = signed_area(coordinates)
    if abs(area) < 1e-9:
        raise ValueError("Circuit has no signed area and its direction cannot be established")
    if area < 0:
        raise ValueError("Source GPX direction is clockwise; review/reorient it before generating a route")

    route_points, length = resample(coordinates)
    data = {
        "id": "levels-raceway-workout",
        "name": "Levels Raceway · Timaru",
        "isValid": True,
        "actualOsmDistanceMeters": round(length, 6),
        "actualRouteDistanceMeters": round(length, 6),
        "points": route_points,
        "routeType": "loop",
        "difficulty": "Unrated",
        "routeDataVersion": 1,
        "geometryFingerprint": fingerprint(route_points),
        "sourceAttribution": ATTRIBUTION,
        "totalAscentMeters": 0.0,
        "environment": "Raceway",
        "discipline": "Track",
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    print(f"PASS Levels Raceway route: {length:.2f} m, {len(coordinates)} source points, "
          f"{len(route_points)} flat local samples, anticlockwise, closed, fingerprint {data['geometryFingerprint']}")


if __name__ == "__main__":
    main()
