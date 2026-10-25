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


def moving_average(values, interval, window, wraps):
    half = window / 2
    offsets = np.arange(-half, half + interval * .5, interval)
    result = []
    for i in range(len(values)):
        indexes = []
        for off in offsets:
            index = int(round(i + off / interval))
            if wraps:
                index %= len(values)
            else:
                index = min(len(values) - 1, max(0, index))
            indexes.append(values[index])
        result.append(float(np.mean(indexes)))
    return np.asarray(result)


def distance_gradient(values, interval, window, wraps):
    half = window / 2
    return np.asarray([(distance_interp(values, interval, i*interval + half, wraps)
                        - distance_interp(values, interval, i*interval - half, wraps)) / window * 100
                       for i in range(len(values))])


def distance_interp(values, interval, distance, wraps):
    position = distance / interval
    if wraps:
        position %= len(values)
    else:
        position = min(len(values) - 1, max(0.0, position))
    before = int(math.floor(position)); after = (before + 1) % len(values) if wraps else min(len(values) - 1, before + 1)
    fraction = position - before
    return float(values[before] + (values[after] - values[before]) * fraction)


def source_labels_at_distances(distances, sources, interval, length, wraps=True):
    return [sources[int(round((distance % length) / interval)) % len(sources) if wraps
                    else min(len(sources) - 1, max(0, int(round(distance / interval))))] for distance in distances]


def canonicalize_repeated_elevation_samples(distances, latitudes, longitudes, raw_elevations,
                                            elevations, maximum_lateral_match_meters=0.75,
                                            maximum_along_track_match_meters=4.5,
                                            maximum_raw_difference_meters=0.75,
                                            minimum_repeated_span_meters=25.0,
                                            boundary_blend_meters=300.0):
    """Share elevations across long, closely retraced route segments.

    Route-order DEM calibration can drift between two visits to the same physical
    road. Only reciprocal near-coincident samples with matching source heights,
    opposing travel tangents, and a sustained monotonic reverse match are merged.
    A single crossing, bridge overlap, or nearby parallel carriageway is not enough.
    """
    count = len(distances)
    if not (count == len(latitudes) == len(longitudes) == len(raw_elevations) == len(elevations)):
        raise ValueError('Repeated-elevation inputs must have matching lengths.')
    if count < 8:
        return np.asarray(elevations, dtype=float).copy(), []

    latitude_scale = 111_195.0
    mean_latitude = float(np.mean(latitudes)) * math.pi / 180
    x = np.asarray(longitudes, dtype=float) * latitude_scale * math.cos(mean_latitude)
    y = np.asarray(latitudes, dtype=float) * latitude_scale
    raw = np.asarray(raw_elevations, dtype=float)
    chainage = np.asarray(distances, dtype=float)
    dx = np.zeros(count, dtype=float)
    dy = np.zeros(count, dtype=float)
    dx[1:-1] = x[2:] - x[:-2]
    dy[1:-1] = y[2:] - y[:-2]
    dx[0], dy[0] = x[1] - x[0], y[1] - y[0]
    dx[-1], dy[-1] = x[-1] - x[-2], y[-1] - y[-2]
    tangent_length = np.hypot(dx, dy)
    valid_tangent = tangent_length > 1e-6
    dx[valid_tangent] /= tangent_length[valid_tangent]
    dy[valid_tangent] /= tangent_length[valid_tangent]

    cell_size = maximum_along_track_match_meters
    buckets = {}
    for index, (px, py) in enumerate(zip(x, y)):
        buckets.setdefault((math.floor(px / cell_size), math.floor(py / cell_size)), []).append(index)

    nearest_forward = {}
    nearest_reverse = {}
    minimum_chainage_separation = max(40.0, minimum_repeated_span_meters * 1.5)
    for index in range(count):
        if not valid_tangent[index]:
            continue
        cell_x, cell_y = math.floor(x[index] / cell_size), math.floor(y[index] / cell_size)
        candidates = []
        for offset_x in (-1, 0, 1):
            for offset_y in (-1, 0, 1):
                for other in buckets.get((cell_x + offset_x, cell_y + offset_y), ()):
                    if other == index or abs(chainage[other] - chainage[index]) < minimum_chainage_separation:
                        continue
                    delta_x, delta_y = x[other] - x[index], y[other] - y[index]
                    along_distance = abs(delta_x * dx[index] + delta_y * dy[index])
                    lateral_distance = abs(delta_x * dy[index] - delta_y * dx[index])
                    if lateral_distance > maximum_lateral_match_meters or along_distance > maximum_along_track_match_meters \
                            or abs(raw[index] - raw[other]) > maximum_raw_difference_meters:
                        continue
                    if abs(dx[index] * dx[other] + dy[index] * dy[other]) < 0.80:
                        continue
                    candidates.append((lateral_distance, along_distance, abs(raw[index] - raw[other]), other))
        if candidates:
            _, _, _, other = min(candidates)
            nearest_forward[index] = other
            reverse_choice = nearest_reverse.get(other)
            if reverse_choice is None or (math.hypot(x[index] - x[other], y[index] - y[other]), index) < reverse_choice[0]:
                nearest_reverse[other] = ((math.hypot(x[index] - x[other], y[index] - y[other]), index), index)

    pairs = sorted((index, other, 1 if dx[index] * dx[other] + dy[index] * dy[other] >= 0 else -1)
                   for index, other in nearest_forward.items()
                   if index < other and other in nearest_reverse and nearest_reverse[other][1] == index)
    if not pairs:
        return np.asarray(elevations, dtype=float).copy(), []

    steps = np.diff(chainage)
    typical_step = float(np.median(steps[steps > 0])) if np.any(steps > 0) else 1.0
    allowed_gap = max(typical_step * 2.5, minimum_repeated_span_meters / 8)
    runs = []
    current = []
    for pair in pairs:
        if current:
            previous = current[-1]
            forward_gap = chainage[pair[0]] - chainage[previous[0]]
            paired_gap = chainage[pair[1]] - chainage[previous[1]]
            oriented_gap = paired_gap if pair[2] > 0 else -paired_gap
            if pair[2] != previous[2] or not (0 < forward_gap <= allowed_gap and 0 < oriented_gap <= allowed_gap):
                if current: runs.append(current)
                current = []
        current.append(pair)
    if current:
        runs.append(current)

    result = np.asarray(elevations, dtype=float).copy()
    original = result.copy()
    summaries = []
    valid_runs = []
    matched_indexes = set()
    boundary_adjustments = np.zeros(count, dtype=float)
    boundary_weights = np.zeros(count, dtype=float)
    for run in runs:
        first, last = run[0], run[-1]
        forward_span = chainage[last[0]] - chainage[first[0]]
        reverse_span = chainage[last[1]] - chainage[first[1]] if first[2] > 0 else chainage[first[1]] - chainage[last[1]]
        if len(run) < 5 or min(forward_span, reverse_span) < minimum_repeated_span_meters:
            continue
        valid_runs.append(run)
        matched_indexes.update(index for left, right, _ in run for index in (left, right))

    for run in valid_runs:
        first, last = run[0], run[-1]
        forward_span = chainage[last[0]] - chainage[first[0]]
        reverse_span = chainage[last[1]] - chainage[first[1]] if first[2] > 0 else chainage[first[1]] - chainage[last[1]]
        before = [abs(result[left] - result[right]) for left, right, _ in run]
        for left, right, _ in run:
            shared = (result[left] + result[right]) / 2
            result[left] = result[right] = shared
        orientation = first[2]
        endpoints = (
            (first[0], -1, result[first[0]] - original[first[0]]),
            (first[1], 1 if orientation < 0 else -1, result[first[1]] - original[first[1]]),
            (last[0], 1, result[last[0]] - original[last[0]]),
            (last[1], -1 if orientation < 0 else 1, result[last[1]] - original[last[1]])
        )
        for anchor, step_direction, delta in endpoints:
            if abs(delta) < 1e-9:
                continue
            index = anchor + step_direction
            while 0 <= index < count:
                distance = abs(chainage[index] - chainage[anchor])
                if distance > boundary_blend_meters:
                    break
                if index not in matched_indexes:
                    weight = 0.5 * (1 + math.cos(math.pi * distance / boundary_blend_meters))
                    boundary_adjustments[index] += delta * weight
                    boundary_weights[index] += weight
                index += step_direction
        summaries.append({
            'samplePairs': len(run),
            'firstDistanceMeters': float(chainage[first[0]]),
            'lastDistanceMeters': float(chainage[last[0]]),
            'secondTraversalFirstDistanceMeters': float(chainage[first[1]]),
            'secondTraversalLastDistanceMeters': float(chainage[last[1]]),
            'matchedSpanMeters': float(min(forward_span, reverse_span)),
            'maximumElevationDifferenceBeforeMeters': float(max(before)),
            'maximumElevationDifferenceAfterMeters': 0.0,
            'boundaryBlendMeters': boundary_blend_meters,
            'method': 'reciprocal spatial match, consistent source elevation, aligned or reversed tangents, and sustained traversal'
        })
    for index in range(count):
        if boundary_weights[index] > 0:
            result[index] = original[index] + boundary_adjustments[index] / boundary_weights[index]
    return result, summaries


def calibrate_fallback_spans(raw, sources, interval, wraps=True):
    """Remove local vertical offsets in fallback spans without flattening their grade."""
    values = np.asarray(raw, dtype=float).copy()
    n = len(values)
    transitions = [i for i in range(n) if sources[i] != sources[(i - 1) % n]] if wraps else [
        i for i in range(1, n) if sources[i] != sources[i - 1]]
    entries = [i for i in transitions if (sources[(i - 1) % n] if wraps or i > 0 else None) == '1m'
               and sources[i] == '8m']
    calibrations = []
    for start in entries:
        exits = [i for i in transitions if sources[(i - 1) % n] == '8m' and sources[i] == '1m'
                 and 0 < ((i - start) % n if wraps else i - start) < n]
        if not exits:
            continue
        end = min(exits, key=lambda i: (i - start) % n if wraps else i - start)
        span_count = (end - start) % n if wraps else end - start
        if span_count <= 0:
            continue

        def local_primary_step(indices):
            steps = [values[b] - values[a] for a, b in indices
                     if sources[a] == '1m' and sources[b] == '1m']
            return float(np.median(steps)) if steps else 0.0

        before_pairs = [((start - offset - 1) % n, (start - offset) % n) for offset in range(1, 9)
                        if wraps or start - offset > 0]
        after_pairs = [((end + offset - 1) % n, (end + offset) % n) for offset in range(1, 9)
                       if wraps or end + offset < n]
        expected_start = local_primary_step(before_pairs)
        expected_end = local_primary_step(after_pairs)
        observed_start = values[start] - values[(start - 1) % n]
        observed_end = values[end] - values[(end - 1) % n]
        correction_start = expected_start - observed_start
        correction_end = observed_end - expected_end
        indices = (start + np.arange(span_count)) % n if wraps else np.arange(start, end)
        corrections = np.linspace(correction_start, correction_end, span_count)
        values[indices] += corrections
        calibrations.append({
            'startDistanceMeters': float(start * interval),
            'endDistanceMeters': float(end * interval),
            'lengthMeters': float(span_count * interval),
            'correctionAtStartMeters': float(correction_start),
            'correctionAtEndMeters': float(correction_end),
            'rawEntryStepMeters': float(observed_start),
            'expectedEntryStepMeters': expected_start,
            'rawExitStepMeters': float(observed_end),
            'expectedExitStepMeters': expected_end,
            'method': 'linear source-bias correction anchored to median adjacent 1m DEM slopes over 40m on both sides'
        })
    return values, calibrations


def remove_isolated_primary_outliers(values, sources, interval, threshold_meters=2.0, maximum_run_meters=15.0):
    """Repair only short 1m DEM pits/peaks against the local trend; preserve raw samples."""
    original = np.asarray(values, dtype=float)
    filtered = original.copy()
    n = len(original)
    residuals = np.zeros(n, dtype=float)
    candidates = np.zeros(n, dtype=bool)
    for i in range(n):
        if sources[i] != '1m':
            continue
        left = [j for j in range(max(0, i - 4), i) if sources[j] == '1m']
        right = [j for j in range(i + 1, min(n, i + 5)) if sources[j] == '1m']
        if len(left) < 2 or len(right) < 2:
            continue
        left_d, right_d = float(np.mean(left)), float(np.mean(right))
        left_h, right_h = float(np.median(original[left])), float(np.median(original[right]))
        predicted = left_h + (right_h - left_h) * ((i - left_d) / (right_d - left_d))
        residuals[i] = original[i] - predicted
        candidates[i] = abs(residuals[i]) >= threshold_meters

    runs = []
    indexes = np.flatnonzero(candidates)
    if len(indexes):
        groups = np.split(indexes, np.where(np.diff(indexes) > 1)[0] + 1)
        for group in groups:
            start, end = int(group[0]), int(group[-1])
            if (end - start + 1) * interval > maximum_run_meters or start == 0 or end >= n - 1:
                continue
            if sources[start - 1] != '1m' or sources[end + 1] != '1m':
                continue
            corrected = np.linspace(original[start - 1], original[end + 1], end - start + 3)[1:-1]
            correction = corrected - original[start:end + 1]
            if np.max(np.abs(correction)) < 2.0:
                continue
            filtered[start:end + 1] = corrected
            runs.append({'startSampleIndex': start, 'endSampleIndex': end,
                'rawElevationsMeters': [float(x) for x in original[start:end + 1]],
                'filteredElevationsMeters': [float(x) for x in filtered[start:end + 1]],
                'maximumResidualMeters': float(np.max(np.abs(residuals[start:end + 1]))),
                'maximumCorrectionMeters': float(np.max(np.abs(correction))),
                'resolutionMeters': 1, 'method': 'linear interpolation across <=15m primary DEM outlier run, anchored by adjacent 1m samples'})
    return filtered, runs


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
    wraps = route.get('routeType', 'loop') != 'pointToPoint'
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
    if not wraps:
        count += 1
    interval = length / count if wraps else length / max(1, count - 1)
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
    calibrated_grid, source_calibrations = calibrate_fallback_spans(raw_grid, sources, interval, wraps)
    calibrated_grid, repeated_segments = canonicalize_repeated_elevation_samples(
        grid, route_lat, route_lon, raw_grid, calibrated_grid)
    filtered_grid, raw_outlier_runs = remove_isolated_primary_outliers(calibrated_grid, sources, interval)
    profile_grid = filtered_grid.copy()
    smooth_grid = moving_average(profile_grid, interval, args.smooth, wraps)
    smooth_grid, smoothed_repeated_segments = canonicalize_repeated_elevation_samples(
        grid, route_lat, route_lon, raw_grid, smooth_grid)
    gradient_grid = distance_gradient(smooth_grid, interval, args.gradient, wraps)
    point_raw = np.asarray([distance_interp(raw_grid, interval, d, wraps) for d in dist])
    point_calibrated = np.asarray([distance_interp(calibrated_grid, interval, d, wraps) for d in dist])
    point_filtered = np.asarray([distance_interp(filtered_grid, interval, d, wraps) for d in dist])
    smooth = np.asarray([distance_interp(smooth_grid, interval, d, wraps) for d in dist])
    gradient = np.asarray([distance_interp(gradient_grid, interval, d, wraps) for d in dist])
    # Source labels describe the nearest actually sampled DEM cell at each
    # route point. Indexing sources by route-point index mislabels every route
    # whose output points are spaced differently from the DEM resample grid.
    point_sources = source_labels_at_distances(dist, sources, interval, length, wraps)

    # Short tagged bridge/tunnel spans are replaced by linear interpolation between their road approaches.
    report = json.loads(report_path.read_text(encoding='utf-8'))
    structures = []
    for section in report.get('roadSegments', []):
        if not (section.get('bridge') or section.get('tunnel')): continue
        start, end = section['startDistanceMeters'], section['endDistanceMeters']
        span = end - start
        if span <= args.bridge_max:
            left = distance_interp(profile_grid, interval, max(0, start - 10), wraps); right = distance_interp(profile_grid, interval, min(length, end + 10), wraps)
            for i, d in enumerate(grid):
                if start <= d <= end:
                    t = (d - start) / max(span, 1e-6); profile_grid[i] = left + (right-left)*t
            smooth_grid = moving_average(profile_grid, interval, args.smooth, wraps)
            smooth_grid, smoothed_repeated_segments = canonicalize_repeated_elevation_samples(
                grid, route_lat, route_lon, raw_grid, smooth_grid)
            gradient_grid = distance_gradient(smooth_grid, interval, args.gradient, wraps)
            smooth = np.asarray([distance_interp(smooth_grid, interval, d, wraps) for d in dist])
            gradient = np.asarray([distance_interp(gradient_grid, interval, d, wraps) for d in dist])
            action = 'interpolated from adjacent road elevations before smoothing'
        else: action = 'flagged for review; DEM retained'
        structures.append({**section, 'lengthMeters': span, 'handling': action})

    for i, p in enumerate(points):
        p.update(rawElevationMeters=float(point_raw[i]), calibratedSourceElevationMeters=float(point_calibrated[i]),
                 filteredSourceElevationMeters=float(point_filtered[i]),
                 smoothedElevationMeters=float(smooth[i]),
                 elevationMeters=float(smooth[i]), gradientPercent=float(gradient[i]), sourceDataset=point_sources[i])
    route['elevation'] = {'dataset': primary.get('title'), 'license': primary.get('license'), 'attribution': attribution(primary),
                          'smoothingWindowMeters': args.smooth, 'gradientWindowMeters': args.gradient,
                          'resampleIntervalMeters': interval}
    route_path.write_text(json.dumps(route, indent=2) + '\n', encoding='utf-8')
    sample_csv_path = route_path.with_name(route_path.stem.replace('.route-points', '') + '.elevation-samples.csv')
    with sample_csv_path.open('w', newline='', encoding='utf-8') as f:
        writer = csv.writer(f); writer.writerow(['distanceMeters','latitude','longitude','rawElevationMeters','calibratedSourceElevationMeters','filteredSourceElevationMeters','smoothedElevationMeters','gradientPercent','sourceDataset'])
        for d, lat, lon, raw_value, calibrated, filtered, smooth_value, grade_value, source in zip(grid, route_lat, route_lon, raw_grid, calibrated_grid, filtered_grid, smooth_grid, gradient_grid, sources):
            writer.writerow([float(d), float(lat), float(lon), float(raw_value), float(calibrated), float(filtered), float(smooth_value), float(grade_value), source])
    csv_path = route_path.with_name(route_path.stem.replace('.route-points', '') + '.elevation.csv')
    with csv_path.open('w', newline='', encoding='utf-8') as f:
        writer = csv.writer(f); writer.writerow(['distanceMeters','latitude','longitude','rawElevationMeters','calibratedSourceElevationMeters','filteredSourceElevationMeters','smoothedElevationMeters','gradientPercent','sourceDataset'])
        for p in points: writer.writerow([p['distanceMeters'],p['latitude'],p['longitude'],p['rawElevationMeters'],p['calibratedSourceElevationMeters'],p['filteredSourceElevationMeters'],p['smoothedElevationMeters'],p['gradientPercent'],p['sourceDataset']])
    profile_differences = np.diff(np.r_[smooth_grid, smooth_grid[0]]) if wraps else np.diff(smooth_grid)
    ascent = float(np.maximum(0, profile_differences).sum()); descent = float(np.maximum(0, -profile_differences).sum())
    suspicious = [float(dist[i]) for i, g in enumerate(gradient) if abs(g) > args.review_gradient]
    all_samplers = samplers + (fallback or [])
    source_transitions = []
    for i in range(1, len(sources)):
        if sources[i] != sources[i-1]:
            source_transitions.append({'distanceMeters': float(grid[i]), 'latitude': float(route_lat[i]), 'longitude': float(route_lon[i]),
                'from': sources[i-1], 'to': sources[i],
                'rawElevationBeforeMeters': float(raw_grid[i-1]), 'rawElevationAfterMeters': float(raw_grid[i]),
                'rawStepMeters': float(raw_grid[i] - raw_grid[i-1]),
                'calibratedElevationBeforeMeters': float(calibrated_grid[i-1]), 'calibratedElevationAfterMeters': float(calibrated_grid[i]),
                'calibratedStepMeters': float(calibrated_grid[i] - calibrated_grid[i-1]),
                'smoothedElevationBeforeMeters': float(smooth_grid[i-1]), 'smoothedElevationAfterMeters': float(smooth_grid[i]),
                'smoothedStepMeters': float(smooth_grid[i] - smooth_grid[i-1])})
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
        'sourceTransitions': source_transitions, 'sourceCalibrations': source_calibrations,
        'sharedRoadElevation': {
            'segments': repeated_segments,
            'smoothedSegments': smoothed_repeated_segments,
            'description': 'Repeated route traversals with sustained reciprocal spatial matches share canonical DEM-derived elevation before gradient calculation.'
        },
        'isolatedRawDemOutliers': [{**run, 'startDistanceMeters': float(grid[run['startSampleIndex']]),
            'endDistanceMeters': float(grid[run['endSampleIndex']]),
            'latitude': float(route_lat[run['startSampleIndex']:run['endSampleIndex']+1].mean()),
            'longitude': float(route_lon[run['startSampleIndex']:run['endSampleIndex']+1].mean())}
            for run in raw_outlier_runs], 'sampleCsv': sample_csv_path.name,
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
