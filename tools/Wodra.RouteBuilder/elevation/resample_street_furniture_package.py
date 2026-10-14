"""Refresh roadside object elevations from the finest valid cached LINZ DEM.

Usage: python resample_street_furniture_package.py street-furniture.json raster_directory
"""
import json
import sys
from pathlib import Path

import rasterio
from pyproj import Transformer

from raster_priority import prefer_highest_resolution
from sample_vegetation import sample

SAMPLER_VERSION = 2


def resample(data, datasets):
    datasets = prefer_highest_resolution(datasets)
    used = set()
    for item in data.get("items", []):
        value = None
        for index, (dataset, transformer) in enumerate(datasets):
            value = sample(dataset, transformer, item["longitude"], item["latitude"])
            if value is not None:
                used.add(index)
                break
        if value is None:
            raise RuntimeError(f"No cached LINZ DEM coverage for {item['osmType']} {item['osmId']}.")
        item["elevationMeters"] = value
    data["elevationSamplerVersion"] = SAMPLER_VERSION
    data["elevationSamplingMethod"] = "finest-valid LINZ DEM raster; bilinear pixel-centred samples"
    stats = data.setdefault("statistics", {})
    stats["elevationSamples"] = len(data.get("items", []))
    stats["elevationSkippedCount"] = 0
    stats["elevationRasterCount"] = len(used)
    return data


def main():
    sidecar, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    report = Path(sys.argv[3]) if len(sys.argv) > 3 else sidecar.with_name(
        sidecar.name.replace(".street-furniture.json", ".report.json"))
    data = json.loads(sidecar.read_text(encoding="utf-8"))
    paths = sorted(raster_dir.glob("*.tiff")) + sorted(raster_dir.glob("*.tif"))
    datasets = []
    try:
        for path in paths:
            dataset = rasterio.open(path)
            datasets.append((dataset, Transformer.from_crs("EPSG:4326", dataset.crs, always_xy=True)))
        resample(data, datasets)
        sidecar.write_text(json.dumps(data, separators=(",", ":")) + "\n", encoding="utf-8")
        if report.is_file():
            report_data = json.loads(report.read_text(encoding="utf-8"))
            stats = report_data.setdefault("streetFurniture", {})
            stats["elevationSamplerVersion"] = SAMPLER_VERSION
            stats["elevationRasterCount"] = data["statistics"]["elevationRasterCount"]
            report.write_text(json.dumps(report_data, ensure_ascii=False, separators=(",", ":")) + "\n",
                              encoding="utf-8")
        print(f"{sidecar.name}: refreshed {len(data.get('items', []))} furniture elevations "
              f"from {data['statistics']['elevationRasterCount']} finest-valid DEM tiles")
    finally:
        for dataset, _ in datasets:
            dataset.close()


if __name__ == "__main__":
    main()
