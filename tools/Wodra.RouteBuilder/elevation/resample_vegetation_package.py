"""Refresh elevations in an existing vegetation sidecar from cached LINZ DEMs.

Usage: python resample_vegetation_package.py vegetation.json raster_directory
"""
import json
import math
import sys
from pathlib import Path

import rasterio
from pyproj import Transformer

from raster_priority import prefer_highest_resolution
from sample_vegetation import sample


def area_samples(polygon):
    points = [(float(point[1]), float(point[0])) for point in polygon]
    if len(points) < 4:
        return points
    origin_lat, origin_lon = points[0]
    scale_x = 6371000 * math.cos(math.radians(origin_lat)) * math.pi / 180
    scale_y = 6371000 * math.pi / 180
    local = [((lon - origin_lon) * scale_x, (lat - origin_lat) * scale_y) for lat, lon in points]
    min_x, max_x = min(p[0] for p in local), max(p[0] for p in local)
    min_y, max_y = min(p[1] for p in local), max(p[1] for p in local)

    def inside(x, y):
        result = False
        for index, (ax, ay) in enumerate(local):
            bx, by = local[index - 1]
            if (ay > y) != (by > y) and x < (bx - ax) * (y - ay) / (by - ay) + ax:
                result = not result
        return result

    candidates = []
    for fx, fy in ((.5, .5), (.25, .25), (.25, .75), (.75, .25), (.75, .75)):
        x, y = min_x + (max_x - min_x) * fx, min_y + (max_y - min_y) * fy
        if inside(x, y):
            candidates.append((origin_lat + y / scale_y, origin_lon + x / scale_x))
    return candidates or [points[0]]


def main():
    sidecar, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    report = Path(sys.argv[3]) if len(sys.argv) > 3 else sidecar.with_name(sidecar.name.replace(".vegetation.json", ".report.json"))
    data = json.loads(sidecar.read_text(encoding="utf-8"))
    paths = sorted(raster_dir.glob("*.tiff")) + sorted(raster_dir.glob("*.tif"))
    datasets = []
    try:
        for path in paths:
            ds = rasterio.open(path)
            datasets.append((ds, Transformer.from_crs("EPSG:4326", ds.crs, always_xy=True)))
        datasets = prefer_highest_resolution(datasets)
        used = set()

        def exact(lon, lat):
            for index, (ds, transformer) in enumerate(datasets):
                value = sample(ds, transformer, lon, lat)
                if value is not None:
                    used.add(index)
                    return value
            return None

        changed_trees = []
        for tree in data.get("trees", []):
            value = exact(tree["longitude"], tree["latitude"])
            if value is not None:
                old = tree.get("elevationMeters")
                tree["elevationMeters"] = value
                if old is not None and abs(value - old) > 2:
                    changed_trees.append((abs(value - old), tree["osmId"], tree.get("nearestRouteDistanceMeters"), old, value))

        changed_areas = 0
        for area in data.get("areas", []):
            values = [exact(lon, lat) for lat, lon in area_samples(area["polygon"])]
            if not values or any(value is None for value in values):
                continue
            values = [float(value) for value in values]
            area["baseElevationMeters"] = sum(values) / len(values)
            area["elevationVariationMeters"] = max(values) - min(values)
            area["elevationSampleCount"] = len(values)
            area["renderGround"] = area.get("distanceFromRouteMeters", 0) >= 5 and area["elevationVariationMeters"] <= 3
            changed_areas += 1

        stats = data.setdefault("statistics", {})
        stats["demSamples"] = len(data.get("trees", [])) + sum(area.get("elevationSampleCount", 0) for area in data.get("areas", []))
        stats["demCacheHits"] = len(used)
        stats["areaGroundOverlays"] = sum(bool(area.get("renderGround")) for area in data.get("areas", []))
        changed_trees.sort(reverse=True)
        sidecar.write_text(json.dumps(data, separators=(",", ":")) + "\n", encoding="utf-8")
        if report.is_file():
            report_data = json.loads(report.read_text(encoding="utf-8"))
            report_stats = report_data.setdefault("vegetation", {})
            for key in ("demSamples", "demCacheHits", "demSkippedAreas", "demSkippedTrees", "areaGroundOverlays"):
                if key in stats:
                    report_stats[key] = stats[key]
            report.write_text(json.dumps(report_data, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        print(f"{sidecar.name}: trees={len(data.get('trees', []))} areas={changed_areas} correctedTreeBases={len(changed_trees)} demTiles={len(used)}")
        for row in changed_trees[:12]:
            print(f"  OSM tree {row[1]} route={row[2]:.1f}m old={row[3]:.2f}m new={row[4]:.2f}m delta={row[0]:.2f}m")
    finally:
        for ds, _ in datasets:
            ds.close()


if __name__ == "__main__":
    main()
