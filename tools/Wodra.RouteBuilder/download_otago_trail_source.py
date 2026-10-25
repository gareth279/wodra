#!/usr/bin/env python3
"""Download the four ordered OSM rail-trail ways used by the bundled Clyde section."""
import argparse
import time
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

WAY_IDS = (133729829, 852195886, 852195887, 44362734)
TRAIL_NAME = "Otago Central Rail Trail"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, help="Temporary .osm XML source path (do not bundle this raw extract).")
    args = parser.parse_args()
    merged = ET.Element("osm", version="0.6", generator="Wodra reviewed route source fetch")
    seen_nodes = set()
    ways = []
    base = None
    for index, way_id in enumerate(WAY_IDS):
        request = urllib.request.Request(
            f"https://api.openstreetmap.org/api/0.6/way/{way_id}/full",
            headers={"User-Agent": "WodraRouteBuilder/1.0 (public route data authoring)"})
        with urllib.request.urlopen(request, timeout=60) as response:
            document = ET.fromstring(response.read())
        metadata = document.find("meta")
        if base is None and metadata is not None:
            base = ET.fromstring(ET.tostring(metadata))
        way = document.find("way")
        if way is None or int(way.get("id", "0")) != way_id:
            raise ValueError(f"OSM API response did not contain way {way_id}.")
        tags = {item.get("k"): item.get("v") for item in way.findall("tag")}
        if tags.get("name") != TRAIL_NAME or tags.get("highway") != "cycleway":
            raise ValueError(f"Way {way_id} no longer has the reviewed trail name and cycleway classification.")
        for node in document.findall("node"):
            node_id = int(node.get("id", "0"))
            if node_id not in seen_nodes:
                merged.append(ET.fromstring(ET.tostring(node)))
                seen_nodes.add(node_id)
        ways.append(ET.fromstring(ET.tostring(way)))
        print(f"way={way_id} surface={tags.get('surface', 'unknown')} nodes={len(way.findall('nd'))}")
        if index + 1 < len(WAY_IDS):
            time.sleep(1)
    if base is not None:
        merged.insert(0, base)
    for way in ways:
        merged.append(way)
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(merged, space="  ")
    ET.ElementTree(merged).write(output, encoding="utf-8", xml_declaration=True)
    print(f"Saved {len(ways)} verified ways and {len(seen_nodes)} nodes to {output}")


if __name__ == "__main__":
    main()
