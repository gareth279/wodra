"""Sample vegetation tree points and park representative points from cached LINZ DEM COGs."""
import json
import math
import sys
from pathlib import Path

import numpy as np
import rasterio
from pyproj import Transformer


def sample(dataset, transformer, lon, lat):
    x, y = transformer.transform(lon, lat)
    col, row = (~dataset.transform) * (x, y)
    col -= .5
    row -= .5
    c0, r0 = math.floor(col), math.floor(row)
    values = dataset.read(1, window=((r0, r0 + 2), (c0, c0 + 2)), boundless=True).astype(float)
    tx, ty = col - c0, row - r0
    weights = np.array([[(1-ty)*(1-tx), (1-ty)*tx], [ty*(1-tx), ty*tx]])
    valid = np.isfinite(values) & (weights > 1e-12)
    if dataset.nodata is not None:
        valid &= values != dataset.nodata
    if not valid.any():
        return None
    return float((values[valid] * weights[valid]).sum() / weights[valid].sum())


def main():
    path, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    data = json.loads(path.read_text(encoding='utf-8'))
    raster_paths = sorted(raster_dir.glob('*.tiff')) + sorted(raster_dir.glob('*.tif'))
    if not raster_paths:
        raise RuntimeError(f'No cached LINZ DEM rasters found in {raster_dir}; elevations are not fabricated.')
    datasets = []
    try:
        for raster_path in raster_paths:
            dataset = rasterio.open(raster_path)
            datasets.append((dataset, Transformer.from_crs('EPSG:4326', dataset.crs, always_xy=True)))

        def exact_elevation(lon, lat, label):
            for index, (dataset, transformer) in enumerate(datasets):
                value = sample(dataset, transformer, lon, lat)
                if value is not None:
                    used_tiles.add(index)
                    return value
            raise RuntimeError(f'No cached LINZ elevation at {label}; refusing to invent terrain height.')

        used_tiles = set()
        for area in data['areas']:
            area['elevations'] = [exact_elevation(point['longitude'], point['latitude'], f"area {area['index']}")
                                  for point in area['samples']]
            del area['samples']
        for tree in data['trees']:
            tree['elevationMeters'] = exact_elevation(tree['longitude'], tree['latitude'], f"tree {tree['index']}")
        data['cacheHits'] = len(used_tiles)
        data['areaElevations'] = data.pop('areas')
        data['treeElevations'] = data.pop('trees')
        path.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    finally:
        for dataset, _ in datasets:
            dataset.close()


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        raise SystemExit(f'Vegetation LINZ sampling error: {exc}')
