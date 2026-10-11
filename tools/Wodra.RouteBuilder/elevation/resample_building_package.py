"""Refresh building base and per-footprint elevations from cached LINZ DEMs.

Usage: python resample_building_package.py buildings.json raster_directory
"""
import json
import sys
from pathlib import Path

import rasterio
from pyproj import Transformer

from raster_priority import prefer_highest_resolution
from sample_vegetation import sample


def main():
    sidecar, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    report = Path(sys.argv[3]) if len(sys.argv) > 3 else sidecar.with_name(sidecar.name.replace(".buildings.json", ".report.json"))
    data = json.loads(sidecar.read_text(encoding="utf-8"))
    datasets = []
    try:
        for path in sorted(raster_dir.glob("*.tiff")) + sorted(raster_dir.glob("*.tif")):
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

        sloped = 0
        elevation_samples = 0
        for building in data.get("buildings", []):
            elevations = [exact(float(point["longitude"]), float(point["latitude"]))
                          for point in building["footprint"]]
            elevation_samples += sum(value is not None for value in elevations)
            # Footprint rings repeat the first vertex at the end; do not weight
            # that sample twice when storing the representative base elevation.
            valid = [float(value) for value in elevations[:-1] if value is not None]
            if not valid:
                continue
            building["footprintElevationMeters"] = elevations
            building["baseElevationMeters"] = sum(valid) / len(valid)
            if max(valid) - min(valid) > 1:
                sloped += 1

        stats = data.setdefault("statistics", {})
        stats["footprintElevationSamples"] = elevation_samples
        stats["slopedFootprintCount"] = sloped
        stats["elevationRasterCount"] = len(used)
        sidecar.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        if report.is_file():
            report_data = json.loads(report.read_text(encoding="utf-8"))
            report_stats = report_data.setdefault("buildings", {})
            report_stats.update({"footprintElevationSamples": elevation_samples,
                                 "slopedFootprintCount": sloped, "elevationRasterCount": len(used)})
            report.write_text(json.dumps(report_data, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        print(f"{sidecar.name}: buildings={len(data.get('buildings', []))} footprintSamples={elevation_samples} "
              f"slopedFootprints={sloped} demTiles={len(used)}")
    finally:
        for ds, _ in datasets:
            ds.close()


if __name__ == "__main__":
    main()
