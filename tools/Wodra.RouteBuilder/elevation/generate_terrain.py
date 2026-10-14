#!/usr/bin/env python3
"""Build a compact, offline route-corridor mesh bundle from cached LINZ DEM COGs."""
import argparse
import json
import math
import time
from pathlib import Path
from urllib.parse import urlparse

import numpy as np
import rasterio
from pyproj import Transformer
from rasterio.windows import Window, from_bounds
from raster_priority import prefer_highest_resolution

EARTH_RADIUS_METERS = 6371000.0
SOURCE_NAME = "Otago – Dunedin and Mosgiel LiDAR 1 m DEM (2021)"
ELEVATION_SAMPLER_VERSION = 2


def order_samplers(samplers):
    """Use the shared finest-valid raster order, with coarser tiles only as fallback."""
    return [sampler for _, sampler in prefer_highest_resolution(
        [(sampler.dataset, sampler) for sampler in samplers])]


class Sampler:
    def __init__(self, path):
        self.dataset = rasterio.open(path)
        self.transformer = Transformer.from_crs("EPSG:4326", self.dataset.crs, always_xy=True)
        self.nodata = self.dataset.nodata

    def sample_many(self, longitude, latitude):
        x, y = self.transformer.transform(longitude, latitude)
        transform = self.dataset.transform
        col = (x - transform.c) / transform.a - 0.5
        row = (y - transform.f) / transform.e - 0.5
        c0, r0 = np.floor(col).astype(int), np.floor(row).astype(int)
        window = from_bounds(float(np.min(x))-3, float(np.min(y))-3,
                             float(np.max(x))+3, float(np.max(y))+3, transform)
        window = window.round_offsets().round_lengths().intersection(Window(0, 0, self.dataset.width, self.dataset.height))
        values = self.dataset.read(1, window=window).astype(float)
        lc, lr = c0-int(window.col_off), r0-int(window.row_off)
        inside = (lc >= 0) & (lr >= 0) & (lc+1 < values.shape[1]) & (lr+1 < values.shape[0])
        result = np.full(np.shape(col), np.nan, dtype=float)
        if not np.any(inside):
            return result
        indices = np.flatnonzero(inside)
        tx, ty = col.flat[indices]-c0.flat[indices], row.flat[indices]-r0.flat[indices]
        weights = np.stack(((1-ty)*(1-tx), (1-ty)*tx, ty*(1-tx), ty*tx), axis=1)
        samples = np.stack((values[lr.flat[indices],lc.flat[indices]],
                            values[lr.flat[indices],lc.flat[indices]+1],
                            values[lr.flat[indices]+1,lc.flat[indices]],
                            values[lr.flat[indices]+1,lc.flat[indices]+1]), axis=1)
        valid = np.isfinite(samples) & (weights > 1e-12)
        if self.nodata is not None:
            valid &= samples != self.nodata
        weights = np.where(valid, weights, 0)
        denominator = weights.sum(axis=1)
        sampled = np.divide((np.where(valid, samples, 0)*weights).sum(axis=1), denominator,
                            out=np.full(len(indices), np.nan), where=denominator > 1e-12)
        result.flat[indices] = sampled
        return result


def local_xz(latitude, longitude, origin_latitude, origin_longitude):
    radians = math.pi / 180
    return ((longitude-origin_longitude)*radians*EARTH_RADIUS_METERS*math.cos(origin_latitude*radians),
            -(latitude-origin_latitude)*radians*EARTH_RADIUS_METERS)


def closest_route(x, z, route):
    best_distance2, best_chainage = float("inf"), 0.0
    for a, b in zip(route, route[1:]):
        dx, dz = b[0]-a[0], b[2]-a[2]
        length2 = dx*dx + dz*dz
        t = 0 if length2 <= 1e-12 else max(0, min(1, ((x-a[0])*dx+(z-a[2])*dz)/length2))
        px, pz = a[0]+dx*t, a[2]+dz*t
        distance2 = (x-px)**2 + (z-pz)**2
        if distance2 < best_distance2:
            best_distance2 = distance2
            best_chainage = a[3] + (b[3]-a[3])*t
            road_y = a[1] + (b[1]-a[1])*t
    return math.sqrt(best_distance2), best_chainage, road_y


def closest_many(xs, zs, route):
    xs, zs = np.asarray(xs, dtype=float).ravel(), np.asarray(zs, dtype=float).ravel()
    result_distance = np.full(len(xs), np.inf)
    result_chainage = np.zeros(len(xs))
    result_y = np.zeros(len(xs))
    ax = np.asarray([p[0] for p in route[:-1]])
    az = np.asarray([p[2] for p in route[:-1]])
    ay = np.asarray([p[1] for p in route[:-1]])
    ad = np.asarray([p[3] for p in route[:-1]])
    dx = np.diff([p[0] for p in route])
    dz = np.diff([p[2] for p in route])
    dy = np.diff([p[1] for p in route])
    dd = np.diff([p[3] for p in route])
    length2 = dx*dx + dz*dz
    for start in range(0, len(xs), 512):
        end = min(len(xs), start+512)
        px, pz = xs[start:end, None], zs[start:end, None]
        t = np.divide((px-ax)*dx+(pz-az)*dz, length2, out=np.zeros((end-start,len(dx))), where=length2>1e-12)
        np.clip(t, 0, 1, out=t)
        qx, qz = ax+t*dx, az+t*dz
        dist2 = (px-qx)**2+(pz-qz)**2
        nearest = np.argmin(dist2, axis=1)
        rows = np.arange(end-start)
        result_distance[start:end] = np.sqrt(dist2[rows, nearest])
        result_chainage[start:end] = ad[nearest]+t[rows,nearest]*dd[nearest]
        result_y[start:end] = ay[nearest]+t[rows,nearest]*dy[nearest]
    return result_distance, result_chainage, result_y


def generate(args):
    started = time.perf_counter()
    route_path, report_path, cache = Path(args.route), Path(args.report), Path(args.cache)
    output = Path(args.output)
    route_file = json.loads(route_path.read_text(encoding="utf-8"))
    report = json.loads(report_path.read_text(encoding="utf-8"))
    points = route_file["points"]
    if len(points) < 3:
        raise ValueError("Terrain requires at least three route points.")
    start_elevation = float(points[0]["smoothedElevationMeters"])
    origin_lat, origin_lon = float(points[0]["latitude"]), float(points[0]["longitude"])
    length = float(route_file.get("actualOsmDistanceMeters", points[-1]["distanceMeters"]))

    raw_route = []
    for point in points:
        x, z = local_xz(float(point["latitude"]), float(point["longitude"]), origin_lat, origin_lon)
        raw_route.append([x, float(point["smoothedElevationMeters"])-start_elevation, z,
                          float(point["distanceMeters"])])
    correction = [raw_route[-1][i]-raw_route[0][i] for i in range(3)]
    route = []
    for x, y, z, distance in raw_route:
        t = max(0.0, min(1.0, (distance/length-.75)/.25))
        blend = t*t*(3-2*t)
        route.append([x-correction[0]*blend, y-correction[1]*blend,
                      z-correction[2]*blend, distance])
    route[-1][:3] = route[0][:3]

    selected = report.get("elevation", {}).get("selectedAssets", [])
    files = []
    for asset in selected:
        name = Path(urlparse(asset.get("url", "")).path).name
        path = cache / name
        if path.exists():
            files.append(path)
    if not files:
        files = sorted(cache.glob("*.tiff"))
    if not files:
        raise FileNotFoundError(f"No cached source LINZ DEM tiles in {cache}; terrain authoring is offline and will not download data.")
    samplers = order_samplers([Sampler(path) for path in files])

    spacing, chunk_size = float(args.spacing), float(args.chunk_size)
    radius = float(args.near_radius)
    cells_per_chunk = max(1, round(chunk_size / spacing))
    chunk_size = cells_per_chunk * spacing
    route_x = [p[0] for p in route]
    route_z = [p[2] for p in route]
    x0 = math.floor((min(route_x)-radius)/spacing)*spacing
    x1 = math.ceil((max(route_x)+radius)/spacing)*spacing
    z0 = math.floor((min(route_z)-radius)/spacing)*spacing
    z1 = math.ceil((max(route_z)+radius)/spacing)*spacing
    columns, rows = round((x1-x0)/spacing), round((z1-z0)/spacing)
    grid_x, grid_z = np.meshgrid(x0+np.arange(columns+1)*spacing,
                                 z0+np.arange(rows+1)*spacing)
    grid_lat = origin_lat-grid_z/EARTH_RADIUS_METERS*180/math.pi
    grid_lon = origin_lon+grid_x/(EARTH_RADIUS_METERS*math.cos(origin_lat*math.pi/180))*180/math.pi
    sampled = np.full(grid_x.shape, np.nan)
    for sampler in samplers:
        missing = ~np.isfinite(sampled)
        if not np.any(missing):
            break
        candidate = sampler.sample_many(grid_lon[missing], grid_lat[missing])
        sampled[missing] = candidate

    interpolated = set()
    missing_rows, missing_columns = np.where(~np.isfinite(sampled))
    original_samples = sampled.copy()
    for row, column in zip(missing_rows, missing_columns):
        r0, r1 = max(0,row-1), min(rows+1,row+2)
        c0, c1 = max(0,column-1), min(columns+1,column+2)
        neighbours = original_samples[r0:r1,c0:c1]
        neighbours = neighbours[np.isfinite(neighbours)]
        if len(neighbours) >= 7:
            sampled[row,column] = float(np.mean(neighbours))
            interpolated.add((column,row))

    cell_x, cell_z = np.meshgrid(x0+(np.arange(columns)+.5)*spacing,
                                 z0+(np.arange(rows)+.5)*spacing)
    cell_distances, _, _ = closest_many(cell_x, cell_z, route)
    cell_distances = cell_distances.reshape(cell_x.shape)
    vertex_distances, _, vertex_road_y = closest_many(grid_x, grid_z, route)
    vertex_distances = vertex_distances.reshape(grid_x.shape)
    vertex_road_y = vertex_road_y.reshape(grid_x.shape)

    chunks = []
    referenced = set()
    no_data_cells = 0
    road_adjusted = set()
    terrain_values = []
    chunk_columns = math.ceil(columns/cells_per_chunk)
    chunk_rows = math.ceil(rows/cells_per_chunk)
    for cr in range(chunk_rows):
        for cc in range(chunk_columns):
            col_start, row_start = cc*cells_per_chunk, cr*cells_per_chunk
            cell_cols, cell_rows = min(cells_per_chunk, columns-col_start), min(cells_per_chunk, rows-row_start)
            distance_slice = cell_distances[row_start:row_start+cell_rows,
                                            col_start:col_start+cell_cols]
            mask_array = distance_slice <= radius
            mask = mask_array.ravel().tolist()
            active = bool(mask_array.any())
            if not active:
                continue
            elevations = []
            for r in range(cell_rows+1):
                for c in range(cell_cols+1):
                    elevations.append(float(sampled[row_start+r,col_start+c])
                                      if np.isfinite(sampled[row_start+r,col_start+c]) else None)
            valid_cells = 0
            for r in range(cell_rows):
                for c in range(cell_cols):
                    cell_index = r*cell_cols+c
                    if not mask[cell_index]:
                        continue
                    global_col, global_row = col_start+c, row_start+r
                    corners = [(global_col,global_row),(global_col+1,global_row),
                               (global_col,global_row+1),(global_col+1,global_row+1)]
                    values = [sampled[key[1],key[0]] for key in corners]
                    if any(not np.isfinite(value) for value in values):
                        no_data_cells += 1
                        mask[cell_index] = False
                        continue
                    valid_cells += 1
                    for key, value in zip(corners, values):
                        referenced.add(key)
                        terrain_values.append(float(value))
                        distance = vertex_distances[key[1],key[0]]
                        road_y = vertex_road_y[key[1],key[0]]
                        dem_y = value-start_elevation-float(args.vertical_bias)
                        if distance <= args.road_corridor_radius and dem_y > road_y-float(args.road_clearance):
                            road_adjusted.add(key)
            if valid_cells == 0:
                continue
            center_x = x0+(col_start+cell_cols/2)*spacing
            center_z = z0+(row_start+cell_rows/2)*spacing
            _, nearest_chainage, _ = closest_route(center_x, center_z, route)
            chunks.append({"id": f"terrain-{cr:02d}-{cc:02d}",
                           "originX": x0+col_start*spacing,
                           "originZ": z0+row_start*spacing,
                           "columns": cell_cols, "rows": cell_rows,
                           "nearestRouteDistanceMeters": nearest_chainage,
                           "elevationsMeters": elevations, "cellMask": mask})

    # Independently sample the far zone from the same cached LINZ tiles at a
    # coarser grid. A small radial overlap is biased down at runtime.
    far_spacing, far_radius = float(args.far_spacing), float(args.far_radius)
    far_chunk_size = max(far_spacing, round(float(args.far_chunk_size)/far_spacing)*far_spacing)
    far_cells_per_chunk = max(1, round(far_chunk_size/far_spacing))
    far_x0 = math.floor((min(route_x)-far_radius)/far_spacing)*far_spacing
    far_x1 = math.ceil((max(route_x)+far_radius)/far_spacing)*far_spacing
    far_z0 = math.floor((min(route_z)-far_radius)/far_spacing)*far_spacing
    far_z1 = math.ceil((max(route_z)+far_radius)/far_spacing)*far_spacing
    fc, fr = round((far_x1-far_x0)/far_spacing), round((far_z1-far_z0)/far_spacing)
    fgx, fgz = np.meshgrid(far_x0+np.arange(fc+1)*far_spacing, far_z0+np.arange(fr+1)*far_spacing)
    flat_lat = origin_lat-fgz.ravel()/EARTH_RADIUS_METERS*180/math.pi
    flat_lon = origin_lon+fgx.ravel()/(EARTH_RADIUS_METERS*math.cos(origin_lat*math.pi/180))*180/math.pi
    fsampled = np.full(fgx.size, np.nan)
    for sampler in samplers:
        missing = ~np.isfinite(fsampled)
        if not np.any(missing): break
        fsampled[missing] = sampler.sample_many(flat_lon[missing], flat_lat[missing])
    fsampled = fsampled.reshape(fgx.shape)
    # In the overlap, cap coarse DEM vertices to the bilinearly sampled near
    # DEM. Runtime's slightly larger far bias then guarantees underlap.
    near_col = (fgx-x0)/spacing
    near_row = (fgz-z0)/spacing
    c0, r0 = np.floor(near_col).astype(int), np.floor(near_row).astype(int)
    inside_support = (c0 >= 0) & (r0 >= 0) & (c0 < columns) & (r0 < rows)
    supported = np.zeros(fgx.shape, dtype=float)
    rr, cc = np.where(inside_support)
    tx, ty = near_col[rr,cc]-c0[rr,cc], near_row[rr,cc]-r0[rr,cc]
    values = np.stack((sampled[r0[rr,cc],c0[rr,cc]], sampled[r0[rr,cc],c0[rr,cc]+1],
                       sampled[r0[rr,cc]+1,c0[rr,cc]], sampled[r0[rr,cc]+1,c0[rr,cc]+1]), axis=1)
    weights = np.stack(((1-ty)*(1-tx),(1-ty)*tx,ty*(1-tx),ty*tx),axis=1)
    valid = np.isfinite(values)
    weights = np.where(valid,weights,0)
    denom = weights.sum(axis=1)
    interp = np.divide((np.where(valid,values,0)*weights).sum(axis=1),denom,
                       out=np.full(len(rr),np.nan),where=denom>1e-12)
    supported[rr,cc]=interp
    near_vertex_distance,_,_=closest_many(fgx,fgz,route)
    clamp_mask=inside_support & (near_vertex_distance.reshape(fgx.shape) <= radius) & np.isfinite(supported) & np.isfinite(fsampled)
    transition_clamped=int(np.count_nonzero(clamp_mask & (fsampled > supported)))
    fsampled[clamp_mask]=np.minimum(fsampled[clamp_mask],supported[clamp_mask])
    fcx, fcz = np.meshgrid(far_x0+(np.arange(fc)+.5)*far_spacing, far_z0+(np.arange(fr)+.5)*far_spacing)
    fdist, _, _ = closest_many(fcx, fcz, route)
    fdist = fdist.reshape(fcx.shape)
    far_chunks, far_referenced = [], set()
    far_start = max(0.0, radius-float(args.transition_overlap))
    for cr in range(math.ceil(fr/far_cells_per_chunk)):
        for cc in range(math.ceil(fc/far_cells_per_chunk)):
            rs, cs = cr*far_cells_per_chunk, cc*far_cells_per_chunk
            nr, nc = min(far_cells_per_chunk,fr-rs), min(far_cells_per_chunk,fc-cs)
            mask_array = (fdist[rs:rs+nr,cs:cs+nc] >= far_start) & (fdist[rs:rs+nr,cs:cs+nc] <= far_radius)
            mask = mask_array.ravel().tolist()
            if not any(mask): continue
            elev = [float(fsampled[rs+r,cs+c]) if np.isfinite(fsampled[rs+r,cs+c]) else None
                    for r in range(nr+1) for c in range(nc+1)]
            for r in range(nr):
                for c in range(nc):
                    if not mask[r*nc+c]: continue
                    keys=((cs+c,rs+r),(cs+c+1,rs+r),(cs+c,rs+r+1),(cs+c+1,rs+r+1))
                    if any(not np.isfinite(fsampled[y,x]) for x,y in keys):
                        mask[r*nc+c]=False
                    else: far_referenced.update(keys)
            if not any(mask): continue
            center_x,center_z=far_x0+(cs+nc/2)*far_spacing,far_z0+(rs+nr/2)*far_spacing
            _,chainage,_=closest_route(center_x,center_z,route)
            far_chunks.append({"id":f"far-{cr:02d}-{cc:02d}","originX":far_x0+cs*far_spacing,
                "originZ":far_z0+rs*far_spacing,"columns":nc,"rows":nr,
                "nearestRouteDistanceMeters":chainage,"elevationsMeters":elev,"cellMask":mask})
    far_triangles=sum(2*sum(c["cellMask"]) for c in far_chunks)
    triangles = sum(2*sum(chunk["cellMask"]) for chunk in chunks)
    valid_elevations = [float(sampled[key[1],key[0]]) for key in referenced]
    far_valid_elevations = [float(fsampled[y,x]) for x,y in far_referenced]
    all_valid_elevations = valid_elevations + far_valid_elevations
    if not chunks or not valid_elevations:
        raise ValueError("No usable terrain cells were covered by cached LINZ DEM data.")
    terrain = {"id": route_file["id"], "sourceDataset": SOURCE_NAME,
               "elevationSamplerVersion": ELEVATION_SAMPLER_VERSION,
               "elevationSamplingMethod": "finest-valid LINZ DEM raster; bilinear pixel-centred samples",
               "sourceCrs": samplers[0].dataset.crs.to_string(),
               "spacingMeters": spacing, "corridorRadiusMeters": radius,
               "chunkSizeMeters": chunk_size, "routeStartElevationMeters": start_elevation,
               "verticalBiasMeters": float(args.vertical_bias),
               "roadClearanceMeters": float(args.road_clearance),
               "farSpacingMeters":far_spacing,"nearRadiusMeters":radius,"farRadiusMeters":far_radius,
               "transitionOverlapMeters":float(args.transition_overlap),"farVerticalBiasMeters":float(args.far_vertical_bias),"farChunks":far_chunks,
               "roadClearanceRadiusMeters": float(args.road_corridor_radius),
               "minimumSourceElevationMeters": float(min(all_valid_elevations)),
               "maximumSourceElevationMeters": float(max(all_valid_elevations)),
               "vertexCount": len(referenced)+len(far_referenced), "triangleCount": triangles+far_triangles,
               "noDataCells": no_data_cells, "interpolatedVertices": len(interpolated & referenced),
               "chunks": chunks}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(terrain, separators=(",", ":"), allow_nan=False)+"\n", encoding="utf-8")
    elapsed = time.perf_counter()-started
    x_coords = [x0+key[0]*spacing for key in referenced]
    z_coords = [z0+key[1]*spacing for key in referenced]
    x_coords.extend(far_x0+x*far_spacing for x,y in far_referenced)
    z_coords.extend(far_z0+y*far_spacing for x,y in far_referenced)
    report["terrain"] = {"sourceDataset": SOURCE_NAME,
        "sourceAttribution": report.get("elevation", {}).get("attribution"),
        "sourceLicense": report.get("elevation", {}).get("license"),
        "sourceAssets": [p.name for p in files],
        "sourceCrs": terrain["sourceCrs"], "spacingMeters": spacing,
        "corridorRadiusMeters": radius, "chunkSizeMeters": chunk_size,
        "near": {"radiusMeters":radius,"spacingMeters":spacing,"vertices":len(referenced),
                 "triangles":triangles,"chunks":len(chunks),"areaSquareMeters":(triangles//2)*spacing*spacing},
        "far": {"radiusMeters":far_radius,"spacingMeters":far_spacing,"vertices":len(far_referenced),
                "triangles":far_triangles,"chunks":len(far_chunks),"areaSquareMeters":(far_triangles//2)*far_spacing*far_spacing,
                "transitionOverlapMeters":float(args.transition_overlap),"verticalBiasMeters":float(args.far_vertical_bias),
                "transitionVerticesClampedBelowNear":transition_clamped},
        "combinedTriangleCount":triangles+far_triangles,
        "combinedChunkCount":len(chunks)+len(far_chunks),
        "extentMeters": {"minX": min(x_coords), "maxX": max(x_coords), "minZ": min(z_coords), "maxZ": max(z_coords)},
        "minimumSourceElevationMeters": terrain["minimumSourceElevationMeters"],
        "maximumSourceElevationMeters": terrain["maximumSourceElevationMeters"],
        "vertexCount": len(referenced)+len(far_referenced), "triangleCount": triangles+far_triangles, "chunkCount": len(chunks)+len(far_chunks),
        "runtimeMeshInstanceCount": len(chunks)+len(far_chunks),
        "coveredAreaSquareMeters": (triangles//2)*spacing*spacing + (far_triangles//2)*far_spacing*far_spacing,
        "noDataCells": no_data_cells, "interpolatedVertices": terrain["interpolatedVertices"],
        "roadClearanceRadiusMeters": float(args.road_corridor_radius),
        "roadClearanceMeters": float(args.road_clearance),
        "roadClearanceAdjustedVertices": len(road_adjusted),
        "visualDownwardBiasMeters": float(args.vertical_bias),
        "bundleFile": output.name, "bundleBytes": output.stat().st_size,
        "generationSeconds": elapsed,
        "method": "bilinear LINZ COG samples on 10 m near and 25 m far grids; radial masks with a biased-down overlap; isolated near NoData filled from 8-neighbour mean"}
    report_path.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    for sampler in samplers:
        sampler.dataset.close()
    print(f"Terrain near: {len(referenced)} vertices, {triangles} triangles, {len(chunks)} chunks; "
          f"far: {len(far_referenced)} vertices, {far_triangles} triangles, {len(far_chunks)} chunks; "
          f"combined {triangles+far_triangles} triangles; elevation {min(all_valid_elevations):.2f}..{max(all_valid_elevations):.2f} m; "
          f"NoData cells {no_data_cells}, interpolated vertices {terrain['interpolatedVertices']}; "
          f"{output.stat().st_size} bytes in {elapsed:.2f}s")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--route", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--cache", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--spacing", type=float, default=10)
    parser.add_argument("--near-radius", type=float, default=250)
    parser.add_argument("--far-spacing", type=float, default=25)
    parser.add_argument("--far-radius", type=float, default=750)
    parser.add_argument("--far-chunk-size", type=float, default=500)
    parser.add_argument("--transition-overlap", type=float, default=10)
    parser.add_argument("--far-vertical-bias", type=float, default=.15)
    parser.add_argument("--corridor-radius", type=float, default=250)
    parser.add_argument("--chunk-size", type=float, default=250)
    parser.add_argument("--vertical-bias", type=float, default=.08)
    parser.add_argument("--road-clearance", type=float, default=.15)
    parser.add_argument("--road-corridor-radius", type=float, default=5)
    generate(parser.parse_args())


if __name__ == "__main__":
    main()
