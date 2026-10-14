"""Sample densified OSM street points from the finest valid cached LINZ DEM COG."""
import json, math, sys
from pathlib import Path
import numpy as np
import rasterio
from pyproj import Transformer
from raster_priority import prefer_highest_resolution

SAMPLER_VERSION = 2


def sample(dataset, transformer, lon, lat):
    """Bilinearly sample pixel-centred DEM data, matching the other scenery samplers."""
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

def resample(data, raster_dir):
    rasters=sorted(raster_dir.glob('*.tiff'))+sorted(raster_dir.glob('*.tif'))
    if not rasters: raise RuntimeError('No cached LINZ DEM tiles available.')
    datasets=[]
    try:
        for p in rasters:
            ds=rasterio.open(p); datasets.append((ds,Transformer.from_crs('EPSG:4326',ds.crs,always_xy=True)))
        datasets = prefer_highest_resolution(datasets)

        def value(lon,lat):
            for ds,tr in datasets:
                a=sample(ds,tr,lon,lat)
                if a is not None: return a
            # Keep this sample unavailable. The C# caller drops only the affected
            # street fragment, preserving measured streets without inventing height.
            return None
        elevations=[[value(p['longitude'],p['latitude']) for p in street['points']] for street in data['streets']]
        if data.get('id') and data['streets'] and 'index' not in data['streets'][0]:
            for street, values in zip(data['streets'], elevations):
                for point, elevation in zip(street['points'], values):
                    point['elevationMeters'] = elevation
            statistics = data.setdefault('statistics', {})
            statistics['elevationSamplerVersion'] = SAMPLER_VERSION
            statistics['elevationSamplingMethod'] = 'finest-valid LINZ DEM raster; bilinear pixel-centred samples'
        else:
            data['elevations'] = elevations
        data['elevationSamplerVersion']=SAMPLER_VERSION
        data['elevationSamplingMethod']='finest-valid LINZ DEM raster; bilinear pixel-centred samples'
        return data
    finally:
        for ds,_ in datasets: ds.close()


def main():
    if len(sys.argv) == 4 and sys.argv[1] == '--bundle':
        path, raster_dir = Path(sys.argv[2]), Path(sys.argv[3])
        data=json.loads(path.read_text(encoding='utf-8'))
        resample(data, raster_dir)
        path.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
        return
    path, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    data=json.loads(path.read_text(encoding='utf-8'))
    resample(data, raster_dir)
    path.write_text(json.dumps(data),encoding='utf-8')
if __name__=='__main__':
    try: main()
    except Exception as exc: print(f'LINZ street sampling error: {exc}',file=sys.stderr);sys.exit(2)
