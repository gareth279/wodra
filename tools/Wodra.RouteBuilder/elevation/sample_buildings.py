"""Sample OSM building representative points from the already cached LINZ DEM COGs."""
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
    building_path, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    data = json.loads(building_path.read_text(encoding='utf-8'))
    rasters = sorted(raster_dir.glob('*.tiff')) + sorted(raster_dir.glob('*.tif'))
    if not rasters:
        raise RuntimeError(f'No cached LINZ DEM rasters found in {raster_dir}; building bases are not fabricated.')
    datasets = []
    try:
        for path in rasters:
            ds = rasterio.open(path)
            datasets.append((ds, Transformer.from_crs('EPSG:4326', ds.crs, always_xy=True)))
        elevations = []
        for item in data['samples']:
            value = None
            for ds, transformer in datasets:
                value = sample(ds, transformer, item['longitude'], item['latitude'])
                if value is not None:
                    break
            if value is None:
                raise RuntimeError(f"No cached LINZ elevation at building {item['key']}; refusing invented base height.")
            elevations.append({'key': item['key'], 'elevationMeters': value})
        building_path.write_text(json.dumps({'elevations': elevations}, separators=(',', ':')) + '\n', encoding='utf-8')
    finally:
        for ds, _ in datasets:
            ds.close()


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        raise SystemExit(f'Building LINZ sampling error: {exc}')
