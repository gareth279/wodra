#!/usr/bin/env python3
"""Fetch small OSM map tiles around an authored route for mapped vegetation extraction."""
import argparse
import json
import math
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

EARTH_RADIUS_METERS = 6371000.0


def samples(points, spacing):
    cumulative = [0.0]
    for a, b in zip(points, points[1:]):
        lat1, lon1 = math.radians(a["latitude"]), math.radians(a["longitude"])
        lat2, lon2 = math.radians(b["latitude"]), math.radians(b["longitude"])
        dlat, dlon = lat2 - lat1, lon2 - lon1
        hav = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
        cumulative.append(cumulative[-1] + EARTH_RADIUS_METERS * 2 * math.asin(math.sqrt(hav)))
    for distance in [*range(0, int(cumulative[-1]), spacing), cumulative[-1]]:
        i = next((i for i in range(1, len(cumulative)) if cumulative[i] >= distance), len(cumulative) - 1)
        t = (distance - cumulative[i - 1]) / max(0.001, cumulative[i] - cumulative[i - 1])
        yield (points[i - 1]["latitude"] + t * (points[i]["latitude"] - points[i - 1]["latitude"]),
               points[i - 1]["longitude"] + t * (points[i]["longitude"] - points[i - 1]["longitude"]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--route", required=True, help="Generated route-points JSON.")
    parser.add_argument("--output", required=True, help="Temporary .osm XML context path (do not bundle raw source data).")
    parser.add_argument("--radius-meters", type=float, default=225)
    parser.add_argument("--sample-spacing-meters", type=int, default=400)
    args = parser.parse_args()
    route = json.loads(Path(args.route).read_text(encoding="utf-8"))
    if not route.get("isValid") or len(route.get("points", [])) < 2:
        raise ValueError("The authored route-points input must be valid and contain geometry.")
    origin = route["points"][0]
    radians = math.pi / 180
    dlat = args.radius_meters / EARTH_RADIUS_METERS / radians
    dlon = dlat / max(.01, math.cos(origin["latitude"] * radians))
    root = ET.Element("osm", version="0.6", generator="Wodra mapped route-context fetch")
    metadata = None
    nodes, ways, relations = {}, {}, {}
    locations = list(samples(route["points"], args.sample_spacing_meters))
    for index, (latitude, longitude) in enumerate(locations):
        bounds = (longitude - dlon, latitude - dlat, longitude + dlon, latitude + dlat)
        query = urllib.parse.urlencode({"bbox": ",".join(f"{v:.7f}" for v in bounds)})
        request = urllib.request.Request("https://api.openstreetmap.org/api/0.6/map?" + query,
            headers={"User-Agent": "WodraRouteBuilder/1.0 (mapped vegetation authoring)"})
        with urllib.request.urlopen(request, timeout=60) as response:
            tile = ET.fromstring(response.read())
        if metadata is None and tile.find("meta") is not None:
            metadata = ET.fromstring(ET.tostring(tile.find("meta")))
        for element, store in (("node", nodes), ("way", ways), ("relation", relations)):
            for item in tile.findall(element):
                key = int(item.get("id", "0"))
                store[key] = ET.fromstring(ET.tostring(item))
        print(f"tile {index + 1}/{len(locations)}; "
              f"nodes={len(nodes)} ways={len(ways)} relations={len(relations)}")
        if index + 1 < len(locations):
            time.sleep(1)
    if metadata is not None:
        root.append(metadata)
    for collection in (nodes, ways, relations):
        for key in sorted(collection):
            root.append(collection[key])
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(root, space="  ")
    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
    print(f"Saved context: {len(nodes)} nodes, {len(ways)} ways, {len(relations)} relations to {output}")


if __name__ == "__main__":
    main()
