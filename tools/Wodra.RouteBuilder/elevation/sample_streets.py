"""Sample densified OSM street points from cached LINZ DEM COGs only."""
import json, math, sys
from pathlib import Path
import rasterio
from pyproj import Transformer

def main():
    path, raster_dir = Path(sys.argv[1]), Path(sys.argv[2])
    data=json.loads(path.read_text(encoding='utf-8'))
    rasters=sorted(raster_dir.glob('*.tiff'))+sorted(raster_dir.glob('*.tif'))
    if not rasters: raise RuntimeError('No cached LINZ DEM tiles available.')
    datasets=[]
    try:
        for p in rasters:
            ds=rasterio.open(p); datasets.append((ds,Transformer.from_crs('EPSG:4326',ds.crs,always_xy=True)))
        def value(lon,lat):
            for ds,tr in datasets:
                x,y=tr.transform(lon,lat); col,row=(~ds.transform)*(x,y); col-=.5;row-=.5
                c,r=round(col),round(row)
                a=ds.read(1,window=((r,r+1),(c,c+1)),boundless=True)[0,0]
                if math.isfinite(float(a)) and (ds.nodata is None or a!=ds.nodata): return float(a)
            # Keep this sample unavailable. The C# caller drops only the affected
            # street fragment, preserving measured streets without inventing height.
            return None
        data['elevations']=[[value(p['longitude'],p['latitude']) for p in street['points']] for street in data['streets']]
        path.write_text(json.dumps(data),encoding='utf-8')
    finally:
        for ds,_ in datasets: ds.close()
if __name__=='__main__':
    try: main()
    except Exception as exc: print(f'LINZ street sampling error: {exc}',file=sys.stderr);sys.exit(2)
