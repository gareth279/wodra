#!/usr/bin/env python3
"""Fetch the reviewed OSM ways for the one-way Signal Hill Big Easy route."""
import argparse
import time
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

PATH_WAYS = (1552538872, 317151858, 688281912, 317151866)
CONNECTOR_WAY = 226859348
TRAIL_NAME = "The Emersons Big Easy"


def fetch_way(way_id):
    request = urllib.request.Request(
        f"https://api.openstreetmap.org/api/0.6/way/{way_id}/full",
        headers={"User-Agent": "WodraRouteBuilder/1.0 (public route authoring)"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return ET.fromstring(response.read())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, help="Temporary OSM XML path; do not bundle raw source data.")
    args = parser.parse_args()
    root = ET.Element("osm", version="0.6", generator="Wodra reviewed MTB route source fetch")
    metadata = None
    nodes, ways = {}, {}
    for index, way_id in enumerate((*PATH_WAYS, CONNECTOR_WAY)):
        document = fetch_way(way_id)
        if metadata is None and document.find("meta") is not None:
            metadata = ET.fromstring(ET.tostring(document.find("meta")))
        way = document.find("way")
        if way is None or int(way.get("id", "0")) != way_id:
            raise ValueError(f"OSM response did not contain requested way {way_id}.")
        tags = {item.get("k"): item.get("v") for item in way.findall("tag")}
        if way_id in PATH_WAYS and (tags.get("name") != TRAIL_NAME or tags.get("highway") != "path"):
            raise ValueError(f"Reviewed Big Easy way {way_id} changed name/classification; route needs review.")
        if way_id == CONNECTOR_WAY and (tags.get("highway") != "track"
                or "linz.govt.nz" not in tags.get("source_ref", "")):
            raise ValueError(f"Reviewed LINZ-mapped connector {way_id} changed classification/source.")
        for node in document.findall("node"):
            nodes[int(node.get("id", "0"))] = ET.fromstring(ET.tostring(node))
        ways[way_id] = ET.fromstring(ET.tostring(way))
        print(f"way={way_id} highway={tags.get('highway')} name={tags.get('name', 'unnamed')} "
              f"surface={tags.get('surface', 'unknown')} width={tags.get('width', 'unknown')} nodes={len(way.findall('nd'))}")
        if index + 1 < len(PATH_WAYS) + 1:
            time.sleep(1)
    if metadata is not None:
        root.append(metadata)
    for node_id in sorted(nodes):
        root.append(nodes[node_id])
    for way_id in (*PATH_WAYS, CONNECTOR_WAY):
        root.append(ways[way_id])
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(root, space="  ")
    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
    print(f"Saved reviewed source extract: {len(ways)} ways, {len(nodes)} nodes to {output}")


if __name__ == "__main__":
    main()
