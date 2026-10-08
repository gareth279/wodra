#!/usr/bin/env python3
"""LINZ STAC DEM sampler and distance-profile builder for Wodra.RouteBuilder."""
import argparse, csv, json, math, os, time, urllib.request
from pathlib import Path
from urllib.parse import urljoin

import numpy as np
import rasterio
from pyproj import Transformer


def fetch_json(url, cache):
    cache.mkdir(parents=True, exist_ok=True)
    if url.endswith('/collection.json') and 'dunedin-and-mosgiel_2021/dem_1m' in url:
        name = 'dunedin-and-mosgiel_2021-dem_1m-collection.json'
    elif url.endswith('/collection.json') and 'new-zealand-contour/dem_8m' in url:
        name = 'nz-contour-dem_8m-collection.json'
    elif url.endswith('.json'):
        name = url.rstrip('/').split('/')[-1]
    else:
        import hashlib
        name = hashlib.sha256(url.encode()).hexdigest() + '.json'
    path = cache / name
    if not path.exists():
        with urllib.request.urlopen(url, timeout=60) as response: path.write_bytes(response.read())
    return json.loads(path.read_text(encoding='utf-8'))


def intersects(bbox, bounds):
    return bbox[0] <= bounds[2] and bbox[2] >= bounds[0] and bbox[1] <= bounds[3] and bbox[3] >= bounds[1]


def selected_items(collection_url, bounds, cache):
    collection = fetch_json(collection_url, cache)
    title = collection.get('title', '')
    if 'DSM' in title.upper() or collection.get('linz:geospatial_category') not in ('dem', None):
        raise ValueError(f"Refusing non-DEM elevation collection: {title}")
    base = collection_url.rsplit('/', 1)[0] + '/'
    items = []
    for link in collection.get('links', []):
        if link.get('rel') != 'item': continue
        item_url = urljoin(base, link['href'])
        item = fetch_json(item_url, cache / 'stac')
        box = item.get('bbox')
        if box and intersects(box, bounds): items.append((item, item_url))
    return collection, items


class RasterSampler:
    def __init__(self, item, item_url, cache):
        self.item, self.item_url = item, item_url
        asset = item.get('assets', {}).get('visual') or next(iter(item['assets'].values()))
        self.url = urljoin(item_url.rsplit('/', 1)[0] + '/', asset['href'])
        self.path = cache / 'rasters' / Path(self.url).name
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if not self.path.exists():
            print(f"Downloading {self.url}")
            with urllib.request.urlopen(self.url, timeout=180) as response, self.path.open('wb') as output:
                while block := response.read(1024 * 1024): output.write(block)
        self.dataset = rasterio.open(self.path)
        self.transformer = Transformer.from_crs('EPSG:4326', self.dataset.crs, always_xy=True)
        self.tags = self.dataset.tags()

    def sample(self, lon, lat):
        x, y = self.transformer.transform(lon, lat)
        col, row = (~self.dataset.transform) * (x, y)
        col -= .5; row -= .5
        c0, r0 = math.floor(col), math.floor(row)
        arr = self.dataset.read(1, window=((r0, r0 + 2), (c0, c0 + 2)), boundless=True).astype(float)
        nodata = self.dataset.nodata
        return bilinear_value(arr, col, row, nodata)


def interp(distances, values, distance):
    return float(np.interp(distance, distances, values))


def circular_average(values, interval, window):
    half = window / 2
    offsets = np.arange(-half, half + interval * .5, interval)
    result = []
    for i in range(len(values)):
        result.append(float(np.mean([values[int(round((i + off / interval) % len(values))) % len(values)] for off in offsets])))
    return np.asarray(result)


def circular_gradient(values, interval, window):
    half = window / 2
    return np.asarray([(periodic_interp(values, interval, i*interval + half)
                        - periodic_interp(values, interval, i*interval - half)) / window * 100
                       for i in range(len(values))])


def periodic_interp(values, interval, distance):
    position = (distance / interval) % len(values)
    before = int(math.floor(position)); after = (before + 1) % len(values)
    fraction = position - before
    return float(values[before] + (values[after] - values[before]) * fraction)


def bilinear_value(arr, col, row, nodata=None):
    c0, r0 = math.floor(col), math.floor(row)
    tx, ty = col - c0, row - r0
    weights = np.asarray([[(1-ty)*(1-tx), (1-ty)*tx], [ty*(1-tx), ty*tx]])
    values = np.asarray(arr, dtype=float)
    valid = np.isfinite(values) & (weights > 1e-12)
    if nodata is not None: valid &= values != nodata
    if not np.any(valid): return None
    return float(np.sum(values[valid] * weights[valid]) / np.sum(weights[valid]))


def generate(args):
    started = time.perf_counter()
    route_path, report_path, cache = Path(args.route), Path(args.report), Path(args.cache)
    route = json.loads(route_path.read_text(encoding='utf-8'))
    points = route['points']
    if not points: raise ValueError('Route has no generated points.')
    bounds = [min(p['longitude'] for p in points), min(p['latitude'] for p in points),
              max(p['longitude'] for p in points), max(p['latitude'] for p in points)]
    primary, items = selected_items(args.primary, bounds, cache)
    if not items: raise ValueError(f"No primary DEM item intersects route: {primary.get('title')}")
    samplers = [(RasterSampler(item, url, cache), '1m') for item, url in items]
    fallback = None
    fallback_collection = None
    raw, sources = [], []
    dist = np.asarray([p['distanceMeters'] for p in points], dtype=float)
    length = float(dist[-1])
    count = max(1, math.ceil(length / args.interval))
    interval = length / count
    grid = np.arange(count, dtype=float) * interval
    route_lat = np.interp(grid, dist, [p['latitude'] for p in points])
    route_lon = np.interp(grid, dist, [p['longitude'] for p in points])
    for lon, lat, d in zip(route_lon, route_lat, grid):
        value = None
        for sampler, _ in samplers:
            value = sampler.sample(float(lon), float(lat))
            if value is not None: break
        source = '1m'
        if value is None and args.fallback:
            if fallback is None:
                fallback_collection, fi = selected_items(args.fallback, bounds, cache)
                fallback = [(RasterSampler(item, url, cache), '8m') for item, url in fi]
            for sampler, _ in fallback:
                value = sampler.sample(float(lon), float(lat))
                if value is not None: break
            source = '8m'
        if value is None: raise ValueError(f"No DEM elevation at route distance {d:.2f} m; no elevation was invented.")
        raw.append(value); sources.append(source)
    raw_grid = np.asarray(raw)
    profile_grid = raw_grid.copy()
    smooth_grid = circular_average(profile_grid, interval, args.smooth)
    gradient_grid = circular_gradient(smooth_grid, interval, args.gradient)
    point_raw = np.asarray([periodic_interp(raw_grid, interval, d) for d in dist])
    smooth = np.asarray([periodic_interp(smooth_grid, interval, d) for d in dist])
    gradient = np.asarray([periodic_interp(gradient_grid, interval, d) for d in dist])

    # Short tagged bridge/tunnel spans are replaced by linear interpolation between their road approaches.
    report = json.loads(report_path.read_text(encoding='utf-8'))
    structures = []
    for section in report.get('roadSegments', []):
        if not (section.get('bridge') or section.get('tunnel')): continue
        start, end = section['startDistanceMeters'], section['endDistanceMeters']
        span = end - start
        if span <= args.bridge_max:
            left = periodic_interp(profile_grid, interval, max(0, start - 10)); right = periodic_interp(profile_grid, interval, min(length, end + 10))
            for i, d in enumerate(grid):
                if start <= d <= end:
                    t = (d - start) / max(span, 1e-6); profile_grid[i] = left + (right-left)*t
            smooth_grid = circular_average(profile_grid, interval, args.smooth)
            gradient_grid = circular_gradient(smooth_grid, interval, args.gradient)
            smooth = np.asarray([periodic_interp(smooth_grid, interval, d) for d in dist])
            gradient = np.asarray([periodic_interp(gradient_grid, interval, d) for d in dist])
            action = 'interpolated from adjacent road elevations before smoothing'
        else: action = 'flagged for review; DEM retained'
        structures.append({**section, 'lengthMeters': span, 'handling': action})

    for i, p in enumerate(points):
        p.update(rawElevationMeters=float(point_raw[i]), smoothedElevationMeters=float(smooth[i]),
                 elevationMeters=float(smooth[i]), gradientPercent=float(gradient[i]), sourceDataset=sources[i])
    route['elevation'] = {'dataset': primary.get('title'), 'license': primary.get('license'), 'attribution': attribution(primary),
                          'smoothingWindowMeters': args.smooth, 'gradientWindowMeters': args.gradient,
                          'resampleIntervalMeters': interval}
    route_path.write_text(json.dumps(route, indent=2) + '\n', encoding='utf-8')
    csv_path = route_path.with_name(route_path.stem.replace('.route-points', '') + '.elevation.csv')
    with csv_path.open('w', newline='', encoding='utf-8') as f:
        writer = csv.writer(f); writer.writerow(['distanceMeters','latitude','longitude','rawElevationMeters','smoothedElevationMeters','gradientPercent','sourceDataset'])
        for p in points: writer.writerow([p['distanceMeters'],p['latitude'],p['longitude'],p['rawElevationMeters'],p['smoothedElevationMeters'],p['gradientPercent'],p['sourceDataset']])
    loop_differences = np.diff(np.r_[smooth_grid, smooth_grid[0]])
    ascent = float(np.maximum(0, loop_differences).sum()); descent = float(np.maximum(0, -loop_differences).sum())
    suspicious = [float(dist[i]) for i, g in enumerate(gradient) if abs(g) > args.review_gradient]
    all_samplers = samplers + (fallback or [])
    report['elevation'] = {'collection': primary.get('title'), 'collectionId': primary.get('id'), 'license': primary.get('license'),
        'providers': primary.get('providers', []), 'attribution': attribution(primary), 'acquisition': primary.get('extent',{}).get('temporal',{}).get('interval'),
        'fallbackCollection': None if fallback_collection is None else {'title':fallback_collection.get('title'),'id':fallback_collection.get('id'),
            'license':fallback_collection.get('license'),'providers':fallback_collection.get('providers',[])},
        'resolutionMeters': primary.get('gsd'), 'verticalDatum': 'not declared in STAC collection metadata',
        'selectedAssets': [{'itemId':s.item['id'],'url':s.url,'resolution':resolution,'cachedBytes':s.path.stat().st_size,
            'checksum':(s.item.get('assets',{}).get('visual') or next(iter(s.item.get('assets',{}).values()),{})).get('file:checksum'),
            'crs':s.dataset.crs.to_string(),'rasterTags':s.tags}
            for s,resolution in all_samplers],
        'oneMeterCoveragePercent':100.0*sum(x=='1m' for x in sources)/len(sources), 'fallbackCoveragePercent':100.0*sum(x=='8m' for x in sources)/len(sources),
        'minimumElevationMeters':float(np.min(smooth)), 'maximumElevationMeters':float(np.max(smooth)),
        'startElevationMeters':float(smooth[0]), 'totalAscentMeters':ascent, 'totalDescentMeters':descent,
        'minimumGradientPercent':float(np.min(gradient)), 'maximumGradientPercent':float(np.max(gradient)), 'averageGradientPercent':float(np.mean(gradient)),
        'gradientReviewThresholdPercent':args.review_gradient, 'suspiciousGradientDistancesMeters':suspicious,
        'bridgeTunnelSections':structures, 'profileCsv':csv_path.name, 'elapsedSeconds':time.perf_counter()-started,
        'method':'centered distance-based moving average and centered gradient on regular samples'}
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    make_chart(route_path.with_name(route_path.stem.replace('.route-points','') + '.elevation.html'), dist, smooth, gradient)
    print(f"Elevation: {primary.get('title')} ({primary.get('gsd')} m); min/max {np.min(smooth):.2f}/{np.max(smooth):.2f} m; ascent/descent {ascent:.1f}/{descent:.1f} m; gradient {np.min(gradient):.2f}%..{np.max(gradient):.2f}%")
    print(f"Profile CSV: {csv_path}; 1m {report['elevation']['oneMeterCoveragePercent']:.1f}%, fallback {report['elevation']['fallbackCoveragePercent']:.1f}%")


def attribution(c):
    providers = ', '.join(p['name'] for p in c.get('providers', []))
    return f"{providers}; {c.get('license','license not specified')}"


def make_chart(path, dist, elev, grade):
    width, height, pad = 1000, 360, 55
    lo, hi = float(min(elev)), float(max(elev)); span = max(hi-lo, 1)
    pts = ' '.join(f"{pad+(d/dist[-1])*(width-2*pad):.1f},{height-pad-(e-lo)/span*(height-2*pad):.1f}" for d,e in zip(dist,elev))
    path.write_text(f'''<!doctype html><meta charset="utf-8"><title>Elevation profile</title><h2>LINZ elevation profile</h2><svg viewBox="0 0 {width} {height}" width="100%" role="img"><polyline fill="none" stroke="#275d95" stroke-width="2" points="{pts}"/><text x="{pad}" y="{height-10}">0 m</text><text x="{width-pad-80}" y="{height-10}">{dist[-1]:.0f} m</text><text x="5" y="20">{lo:.1f}–{hi:.1f} m</text></svg><p>Gradient range {min(grade):.2f}% to {max(grade):.2f}%</p>''', encoding='utf-8')


def main():
    p=argparse.ArgumentParser(); p.add_argument('--route',required=True); p.add_argument('--report',required=True); p.add_argument('--cache',required=True)
    p.add_argument('--primary',required=True); p.add_argument('--fallback',default=''); p.add_argument('--smooth',type=float,default=30)
    p.add_argument('--gradient',type=float,default=25); p.add_argument('--interval',type=float,default=5); p.add_argument('--bridge-max',type=float,default=100)
    p.add_argument('--review-gradient',type=float,default=15); args=p.parse_args(); generate(args)

if __name__ == '__main__':
    try: main()
    except Exception as exc: raise SystemExit(f"Elevation generation error: {exc}")
