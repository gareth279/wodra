#!/usr/bin/env python3
"""Audit the four Dunedin route packages and produce profiles plus QA summaries."""
import argparse
import csv
import json
import math
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from pyproj import Transformer
from scipy.spatial import cKDTree


ROUTES = [
    ("dunedin-one-way-oval-gardens", "routes/generated", "Dunedin Oval to Gardens"),
    ("dunedin-south-coastal-loop", "routes/generated/additional", "Dunedin South Dunedin Coastal Loop"),
    ("dunedin-rolling-loop", "routes/generated/additional", "Dunedin Rolling City and Valley Loop"),
    ("dunedin-signal-hill-climbing-loop", "routes/generated/additional", "Dunedin Signal Hill Climbing Loop"),
]


def read_csv(path):
    with path.open(newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    fields = ["distanceMeters", "latitude", "longitude", "rawElevationMeters", "smoothedElevationMeters", "gradientPercent"]
    for optional in ("calibratedSourceElevationMeters", "filteredSourceElevationMeters"):
        if rows and optional in rows[0]: fields.append(optional)
    return {key: np.asarray([float(row[key]) for row in rows]) for key in fields} | {
                "sourceDataset": [row.get("sourceDataset", "unknown") for row in rows]}


def interp_loop(distances, values, distance, length):
    d = np.asarray(distance) % length
    return np.interp(d, distances, values)


def nearest_source(graph_files, ll, transformer):
    if not graph_files:
        return None
    x, y = transformer.transform(ll[1], ll[0])
    # Flattened trees retain parallel edge endpoint arrays and metadata.
    candidates = []
    for tree, segments, metadata_by_edge in graph_files:
        distances, indexes = tree.query([x, y], k=min(8, tree.n))
        for dist, idx in zip(np.atleast_1d(distances), np.atleast_1d(indexes)):
            ax, ay, bx, by = segments[idx]
            dx, dy = bx - ax, by - ay
            t = np.clip(((x - ax) * dx + (y - ay) * dy) / max(dx * dx + dy * dy, 1e-9), 0, 1)
            exact = math.hypot(x - (ax + t * dx), y - (ay + t * dy))
            candidates.append((exact, metadata_by_edge[idx]))
    if not candidates:
        return None
    distance, metadata = min(candidates, key=lambda row: row[0])
    return {"distanceMeters": float(distance), **metadata}


def load_graph_trees(cache_dir, transformer):
    trees = []
    for path in sorted(Path(cache_dir).glob("road-graph-*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        coordinates = {node["Id"]: node["Coordinate"] for node in data["Nodes"]}
        starts, ends, metadata = [], [], []
        for edge in data["Edges"]:
            a, b = coordinates.get(edge["FromNodeId"]), coordinates.get(edge["ToNodeId"])
            if not a or not b:
                continue
            ax, ay = transformer.transform(a["Longitude"], a["Latitude"])
            bx, by = transformer.transform(b["Longitude"], b["Latitude"])
            starts.append((ax, ay)); ends.append((bx, by))
            metadata.append({"wayId": edge["WayId"], "name": edge.get("Name"), "highway": edge.get("Highway"),
                "bridge": edge.get("Bridge"), "tunnel": edge.get("Tunnel"), "layer": edge.get("Layer")})
        if starts:
            starts, ends = np.asarray(starts), np.asarray(ends)
            tree = cKDTree((starts + ends) * 0.5)
            trees.append((tree, np.column_stack((starts, ends)), metadata))
    return trees


def contiguous_runs(mask, distances):
    indexes = np.flatnonzero(mask)
    if not len(indexes):
        return []
    groups = np.split(indexes, np.where(np.diff(indexes) > 1)[0] + 1)
    return [(int(group[0]), int(group[-1])) for group in groups]


def suspicious_raw_spikes(distances, raw, calibrated, processed, sources, transition_distances):
    """Flag local DEM outliers left after source correction, not corrected source offsets."""
    spikes = []
    for i in range(1, len(distances) - 1):
        raw_step = max(abs(calibrated[i] - calibrated[i - 1]), abs(calibrated[i + 1] - calibrated[i]))
        processed_step = max(abs(processed[i] - processed[i - 1]), abs(processed[i + 1] - processed[i]))
        residual = abs(calibrated[i] - processed[i])
        reverses = (calibrated[i] - calibrated[i - 1]) * (calibrated[i + 1] - calibrated[i]) < 0
        near_transition = bool(len(transition_distances) and np.min(np.abs(transition_distances - distances[i])) <= 15)
        if residual >= 1.0 and raw_step >= 1.0 and processed_step < 0.5 and (reverses or near_transition):
            spikes.append({"distanceMeters": float(distances[i]), "rawElevationMeters": float(raw[i]),
                "calibratedSourceElevationMeters": float(calibrated[i]),
                "processedElevationMeters": float(processed[i]), "rawResidualMeters": float(residual),
                "largestAdjacentRawStepMeters": float(raw_step), "largestAdjacentProcessedStepMeters": float(processed_step),
                "sourceDataset": sources[i], "nearSourceTransition": near_transition})
    return spikes


def sustained_window_gradient(distances, elevations, window_meters, route_length):
    return (interp_loop(distances, elevations, distances + window_meters / 2, route_length) -
            interp_loop(distances, elevations, distances - window_meters / 2, route_length)) / window_meters * 100


def bearing_degrees(a_lat, a_lon, b_lat, b_lon):
    lat = math.radians((a_lat + b_lat) / 2)
    east = (b_lon - a_lon) * math.cos(lat)
    north = b_lat - a_lat
    return math.degrees(math.atan2(east, north)) % 360


def sharp_turn_candidates(distances, latitudes, longitudes, route_length, report, graph_trees, transformer):
    candidates = []
    stride = max(1, int(round(20 / np.median(np.diff(distances)))))
    for i in range(stride, len(distances) - stride):
        first = bearing_degrees(latitudes[i - stride], longitudes[i - stride], latitudes[i], longitudes[i])
        second = bearing_degrees(latitudes[i], longitudes[i], latitudes[i + stride], longitudes[i + stride])
        angle = abs((second - first + 180) % 360 - 180)
        if angle >= 55:
            candidates.append((angle, i))
    selected = []
    for angle, index in sorted(candidates, reverse=True):
        if all(abs(distances[index] - distances[other]) >= 100 for _, other, _ in selected):
            latitude, longitude = float(latitudes[index]), float(longitudes[index])
            selected.append((angle, index, {"distanceMeters": float(distances[index]), "turnAngleDegrees": float(angle),
                "latitude": latitude, "longitude": longitude,
                "nearestOsmRoad": nearest_source(graph_trees, (latitude, longitude), transformer),
                **route_leg_names(report, float(distances[index]))}))
        if len(selected) >= 12:
            break
    return [item for _, _, item in selected]


def route_leg_names(report, distance):
    chainage = 0.0
    for leg in report.get("legs", []):
        end = chainage + float(leg.get("distanceMeters", 0))
        if chainage <= distance <= end:
            names = sorted({way.get("name") for way in leg.get("ways", []) if way.get("name")})
            return {"leg": f'{leg.get("fromAnchor")} -> {leg.get("toAnchor")}', "mappedRoadNamesInLeg": names}
        chainage = end
    return {"leg": None, "mappedRoadNamesInLeg": []}


def audit_route(route_id, directory, title, output, graph_trees, transformer):
    root = Path(directory)
    route = json.loads((root / f"{route_id}.route-points.json").read_text(encoding="utf-8"))
    report = json.loads((root / f"{route_id}.report.json").read_text(encoding="utf-8"))
    csv_path = root / f"{route_id}.elevation-samples.csv"
    if not csv_path.exists():
        csv_path = root / f"{route_id}.elevation.csv"
    samples = read_csv(csv_path)
    d = samples["distanceMeters"]
    length = float(d[-1] + (d[1] - d[0] if len(d) > 1 and d[-1] < route["actualOsmDistanceMeters"] else 0))
    if abs(length - float(route["actualOsmDistanceMeters"])) > 1.0:
        length = float(route["actualOsmDistanceMeters"])
    sample_step = float(np.median(np.diff(d)))
    smooth = samples["smoothedElevationMeters"]
    grade = samples["gradientPercent"]
    raw = samples["rawElevationMeters"]
    calibrated = samples.get("calibratedSourceElevationMeters", raw)
    filtered = samples.get("filteredSourceElevationMeters", calibrated)

    grade_100 = sustained_window_gradient(d, smooth, 100, length)
    grade_250 = sustained_window_gradient(d, smooth, 250, length)
    max_up = int(np.argmax(grade_100)); max_down = int(np.argmin(grade_100))
    extrema = []
    for kind, index in (("maximum100mClimb", max_up), ("maximum100mDescent", max_down)):
        latitude = float(np.interp(d[index], d, samples["latitude"]))
        longitude = float(np.interp(d[index], d, samples["longitude"]))
        extrema.append({"kind": kind, "distanceMeters": float(d[index]), "gradientPercent": float(grade_100[index]),
            "latitude": latitude, "longitude": longitude,
            "nearestOsmRoad": nearest_source(graph_trees, (latitude, longitude), transformer),
            **route_leg_names(report, float(d[index]))})
    sharp_turns = sharp_turn_candidates(d, samples["latitude"], samples["longitude"], length,
        report, graph_trees, transformer)
    sharp_runs = contiguous_runs(np.abs(grade) > 15, d)
    steep_sections = []
    for first, last in sharp_runs:
        start, end = float(d[first]), float(d[last])
        mid = (start + end) / 2
        lat = float(np.interp(mid, d, samples["latitude"]))
        lon = float(np.interp(mid, d, samples["longitude"]))
        road = nearest_source(graph_trees, (lat, lon), transformer)
        sample_rows = [{"distanceMeters": float(d[j]), "rawElevationMeters": float(raw[j]),
            "calibratedSourceElevationMeters": float(calibrated[j]),
            "filteredSourceElevationMeters": float(filtered[j]), "processedElevationMeters": float(smooth[j]),
            "gradientPercent": float(grade[j]), "sourceDataset": samples["sourceDataset"][j],
            "sourceResolutionMeters": 1 if samples["sourceDataset"][j] == "1m" else 8}
            for j in range(max(0, first - 2), min(len(raw), last + 3))]
        nearby_transitions = [transition for transition in report.get("elevation", {}).get("sourceTransitions", [])
            if start - 30 <= transition["distanceMeters"] <= end + 30]
        steep_sections.append({"startMeters": start, "endMeters": end, "lengthMeters": end - start,
            "peakPositive25mGradientPercent": float(np.max(grade[first:last + 1])),
            "peakNegative25mGradientPercent": float(np.min(grade[first:last + 1])),
            "mean100mGradientPercent": float(np.mean(grade_100[first:last + 1])),
            "rawSamples": sample_rows, "sourceTransitionNearby": nearby_transitions,
            "nearSourceTransition": bool(nearby_transitions),
            "nearestOsmRoad": road, **route_leg_names(report, mid)})

    transitions = report.get("elevation", {}).get("sourceTransitions", [])
    transition_distances = np.asarray([x["distanceMeters"] for x in transitions], dtype=float)
    spikes = suspicious_raw_spikes(d, raw, filtered, smooth, samples["sourceDataset"], transition_distances)
    for spike in spikes:
        i = int(np.argmin(np.abs(d - spike["distanceMeters"])))
        spike["latitude"] = float(samples["latitude"][i]); spike["longitude"] = float(samples["longitude"][i])
        spike["sourceResolutionMeters"] = 1 if spike["sourceDataset"] == "1m" else 8
        lo, hi = max(0, i - 4), min(len(d), i + 5)
        spike["rawSamples"] = [{"distanceMeters": float(d[j]), "elevationMeters": float(raw[j]),
            "sourceDataset": samples["sourceDataset"][j]} for j in range(lo, hi)]
        spike["nearestOsmRoad"] = nearest_source(graph_trees, (spike["latitude"], spike["longitude"]), transformer)
        spike.update(route_leg_names(report, spike["distanceMeters"]))
    for transition in transitions:
        transition["fromResolutionMeters"] = 1 if transition["from"] == "1m" else 8
        transition["toResolutionMeters"] = 1 if transition["to"] == "1m" else 8
        if "latitude" in transition and "longitude" in transition:
            transition["nearestOsmRoad"] = nearest_source(graph_trees,
                (transition["latitude"], transition["longitude"]), transformer)
            transition.update(route_leg_names(report, transition["distanceMeters"]))
    corrected_outliers = report.get("elevation", {}).get("isolatedRawDemOutliers", [])
    for outlier in corrected_outliers:
        mid = (outlier["startDistanceMeters"] + outlier["endDistanceMeters"]) / 2
        i = int(np.argmin(np.abs(d - mid)))
        outlier["nearestOsmRoad"] = nearest_source(graph_trees,
            (float(samples["latitude"][i]), float(samples["longitude"][i])), transformer)
        outlier.update(route_leg_names(report, mid))
        road = outlier["nearestOsmRoad"] or {}
        outlier["interpretation"] = ("OSM-tagged bridge; the DEM sample is below the road deck and the short run is interpolated from adjacent road samples."
            if road.get("bridge") else "Short 1m DEM outlier run interpolated from adjacent route samples.")
        lo = max(0, int(np.searchsorted(d, outlier["startDistanceMeters"])) - 2)
        hi = min(len(d), int(np.searchsorted(d, outlier["endDistanceMeters"])) + 3)
        outlier["rawSamples"] = [{"distanceMeters": float(d[j]), "rawElevationMeters": float(raw[j]),
            "filteredSourceElevationMeters": float(filtered[j]), "sourceDataset": samples["sourceDataset"][j]}
            for j in range(lo, hi)]
    for section in steep_sections:
        section["sourceTransitionNearby"] = [transition for transition in transitions
            if section["startMeters"] - 30 <= transition["distanceMeters"] <= section["endMeters"] + 30]
        section["nearSourceTransition"] = bool(section["sourceTransitionNearby"])

    points = route["points"]
    seam_delta = abs(float(points[0].get("elevationMeters", 0)) - float(points[-1].get("elevationMeters", 0)))
    def bearing(a, b):
        lat = math.radians((a["latitude"] + b["latitude"]) / 2)
        east = (b["longitude"] - a["longitude"]) * math.cos(lat)
        north = b["latitude"] - a["latitude"]
        return math.degrees(math.atan2(east, north)) % 360
    seam_angle = abs((bearing(points[-2], points[-1]) - bearing(points[0], points[1]) + 180) % 360 - 180)
    warnings = report.get("warnings", [])
    leg_statuses = [leg.get("status") for leg in report.get("legs", [])]
    leg_ok = all(status in ("passed", "warning") for status in leg_statuses) if leg_statuses else route_id == "dunedin-one-way-oval-gardens"
    has_routing_warning = any(status == "warning" for status in leg_statuses)
    route_status = "pass_with_routing_warning" if has_routing_warning else "pass"
    if seam_angle > 45:
        route_status += "_requires_seam_turn_review"
    coverage = report.get("elevation", {})
    summary = {"id": route_id, "name": title, "distanceMeters": float(route["actualOsmDistanceMeters"],),
        "distanceKm": float(route["actualOsmDistanceMeters"]) / 1000,
        "ascentMeters": float(report.get("totalAscentMeters", coverage.get("totalAscentMeters", 0))),
        "difficulty": route.get("difficulty", "Easy"), "pointCount": len(points),
        "maximumAdjacentPointDistanceMeters": report.get("maximumAdjacentPointDistanceMeters"),
        "loopClosed": abs(points[0]["latitude"] - points[-1]["latitude"]) < 1e-8 and abs(points[0]["longitude"] - points[-1]["longitude"]) < 1e-8,
        "loopSeamElevationDifferenceMeters": seam_delta, "loopSeamHeadingDifferenceDegrees": seam_angle,
        "legsConnectedAndPassed": leg_ok,
        "routeWarnings": warnings, "routeStatus": route_status,
        "sourcePbfFilename": report.get("sourcePbfFilename"),
        "sourceAttribution": route.get("sourceAttribution", report.get("sourceAttribution")),
        "elevationSource": coverage.get("collection"), "elevationFallbackSource": coverage.get("fallbackCollection"),
        "oneMeterCoveragePercent": coverage.get("oneMeterCoveragePercent", 100),
        "fallbackCoveragePercent": coverage.get("fallbackCoveragePercent", 0),
        "sourceTransitions": transitions, "sourceTransitionCount": len(transitions),
        "maxSustained100mClimbPercent": float(grade_100[max_up]), "maxSustained100mClimbAtMeters": float(d[max_up]),
        "maxSustained100mDescentPercent": float(grade_100[max_down]), "maxSustained100mDescentAtMeters": float(d[max_down]),
        "maxSustainedGradientLocations": extrema,
        "sharpTurnCandidates": sharp_turns,
        "maxSustained250mClimbPercent": float(np.max(grade_250)), "maxSustained250mDescentPercent": float(np.min(grade_250)),
        "reportedPointsOver15Percent": len(coverage.get("suspiciousGradientDistancesMeters", [])),
        "fiveMeterSamplesOver15Percent": int(np.count_nonzero(np.abs(grade) > 15)),
        "steepSectionsOver15Percent": steep_sections, "suspiciousRawElevationSpikes": spikes,
        "suspiciousGradientSpikeCount": len(spikes), "correctedRawDemOutliers": corrected_outliers,
        "correctedRawDemOutlierCount": len(corrected_outliers), "elevationSamplesCsv": csv_path.name}

    manual_checks = [{"distanceMeters": 0.0, "reason": "route start and lap seam"},
        {"distanceMeters": float(length), "reason": "route lap seam"}]
    manual_checks += [{"distanceMeters": item["distanceMeters"], "reason": item["kind"] + " at " + (item.get("nearestOsmRoad") or {}).get("name", "mapped road")}
        for item in extrema]
    manual_checks += [{"distanceMeters": item["distanceMeters"], "reason": f'DEM transition {item["from"]} to {item["to"]}'}
        for item in transitions]
    manual_checks += [{"distanceMeters": (item["startDistanceMeters"] + item["endDistanceMeters"]) / 2, "reason": "corrected DEM sample on " + ((item.get("nearestOsmRoad") or {}).get("name") or "mapped road")}
        for item in corrected_outliers]
    manual_checks += [{"distanceMeters": item["distanceMeters"], "reason": "isolated raw DEM candidate"}
        for item in spikes]
    for section in steep_sections:
        manual_checks.append({"distanceMeters": section["startMeters"], "reason": "steep section start on " + ((section.get("nearestOsmRoad") or {}).get("name") or "mapped road")})
    manual_checks += [{"distanceMeters": item["distanceMeters"], "reason": f'sharp turn {item["turnAngleDegrees"]:.0f} degrees on ' + ((item.get("nearestOsmRoad") or {}).get("name") or "mapped road")}
        for item in sharp_turns]
    for leg in report.get("legs", []):
        end_chainage = sum(float(x.get("distanceMeters", 0)) for x in report["legs"][:report["legs"].index(leg) + 1])
        manual_checks.append({"distanceMeters": end_chainage, "reason": f'{leg.get("toAnchor", "mapped junction")} route leg boundary'})
    deduped = {}
    for item in manual_checks:
        key = round(float(item["distanceMeters"]))
        deduped.setdefault(key, set()).add(item["reason"])
    summary["manualVisualChecks"] = [{"distanceMeters": float(key), "reasons": sorted(reasons)} for key, reasons in sorted(deduped.items())]

    output.mkdir(parents=True, exist_ok=True)
    fig, (ax, gx) = plt.subplots(2, 1, figsize=(16, 7), sharex=True, gridspec_kw={"height_ratios": [2, 1]})
    ax.plot(d / 1000, smooth, color="#28614f", lw=1.6, label="Processed ride elevation")
    if "calibratedSourceElevationMeters" in samples:
        ax.plot(d / 1000, calibrated, color="#c28d36", lw=0.8, alpha=0.75, label="Source-aligned elevation")
    if "filteredSourceElevationMeters" in samples:
        ax.plot(d / 1000, filtered, color="#8765a6", lw=0.65, alpha=0.65, label="Outlier-filtered elevation")
    ax.plot(d / 1000, raw, color="#a8a392", lw=0.55, alpha=0.65, label="Raw DEM sample")
    ax.set_ylabel("Elevation (m)"); ax.grid(alpha=0.2); ax.legend(loc="upper right")
    gx.plot(d / 1000, grade, color="#344b70", lw=0.8, label="Processed grade (25 m window)")
    gx.fill_between(d / 1000, 15, grade, where=grade >= 15, color="#c65e45", alpha=0.55, interpolate=True)
    gx.fill_between(d / 1000, -15, grade, where=grade <= -15, color="#c65e45", alpha=0.55, interpolate=True)
    gx.axhline(15, color="#ad5138", lw=0.7, ls="--"); gx.axhline(-15, color="#ad5138", lw=0.7, ls="--")
    for j, transition in enumerate(transitions):
        x = float(transition["distanceMeters"]) / 1000
        for axis in (ax, gx): axis.axvline(x, color="#df9e32", lw=0.9, alpha=0.8)
        gx.text(x, 0.95 if j % 2 == 0 else 0.82, f'{transition["from"]}→{transition["to"]}',
                transform=gx.get_xaxis_transform(), rotation=90, va="top", ha="right", fontsize=7, color="#8b5a12")
    gx.set_ylabel("Gradient (%)"); gx.set_xlabel("Distance (km)"); gx.set_ylim(min(-40, float(np.min(grade)) - 2), max(40, float(np.max(grade)) + 2)); gx.grid(alpha=0.2)
    fig.suptitle(f"{title} — elevation and gradient; orange lines show DEM-source transitions")
    fig.tight_layout()
    fig.savefig(output / f"{route_id}.profile.png", dpi=150)
    plt.close(fig)
    (output / f"{route_id}.quality.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return summary


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, default=Path("routes/review/dunedin-route-quality"))
    parser.add_argument("--cache", type=Path, default=Path("routes/cache"))
    args = parser.parse_args()
    transformer = Transformer.from_crs("EPSG:4326", "EPSG:2193", always_xy=True)
    trees = load_graph_trees(args.repo / args.cache, transformer)
    results = [audit_route(route_id, args.repo / folder, name, args.repo / args.output, trees, transformer)
               for route_id, folder, name in ROUTES]
    out = args.repo / args.output
    (out / "route-quality-summary.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    with (out / "suspicious-elevation-sections.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f); writer.writerow(["routeId", "distanceMeters", "latitude", "longitude", "roadName", "wayId", "rawElevationMeters", "calibratedSourceElevationMeters", "processedElevationMeters", "rawResidualMeters", "largestAdjacentRawStepMeters", "sourceDataset", "sourceResolutionMeters", "nearSourceTransition"])
        for result in results:
            for spike in result["suspiciousRawElevationSpikes"]:
                road = spike.get("nearestOsmRoad") or {}
                writer.writerow([result["id"], spike["distanceMeters"], spike.get("latitude"), spike.get("longitude"), road.get("name"), road.get("wayId"), spike["rawElevationMeters"], spike.get("calibratedSourceElevationMeters"), spike["processedElevationMeters"], spike["rawResidualMeters"], spike["largestAdjacentRawStepMeters"], spike["sourceDataset"], spike["sourceResolutionMeters"], spike["nearSourceTransition"]])
    with (out / "corrected-raw-dem-outliers.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f); writer.writerow(["routeId", "startMeters", "endMeters", "roadName", "wayId", "bridge", "tunnel", "resolutionMeters", "rawElevationsMeters", "filteredElevationsMeters", "maxResidualMeters", "interpretation"])
        for result in results:
            for item in result["correctedRawDemOutliers"]:
                road = item.get("nearestOsmRoad") or {}
                writer.writerow([result["id"], item["startDistanceMeters"], item["endDistanceMeters"], road.get("name"), road.get("wayId"), road.get("bridge"), road.get("tunnel"), item["resolutionMeters"], item["rawElevationsMeters"], item["filteredElevationsMeters"], item["maximumResidualMeters"], item["interpretation"]])
    lines = ["# Dunedin route quality report", "", "Maximum sustained gradient is measured over a 100 m moving window; the separate 250 m values show longer-term climbing/descent. Route points >15% are the generator’s original route-point samples (roughly 20 m apart), computed from the smoothed 25 m gradient profile; the separate 5 m count is from the dense elevation sample CSV. ‘Corrected DEM outliers’ are short 1 m source runs whose samples diverge from and return to the local trend. Underlying raw elevations remain in the sample CSVs. ‘Remaining spike candidates’ are unresolved post-filter local deviations.", "", "| Route | Distance | Ascent | 100 m climb max | 100 m descent min | 1 m DEM | 8 m fallback | transitions | route points >15% | 5 m samples >15% | corrected DEM outliers | remaining spike candidates | route status |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|"]
    for row in results:
        status = row["routeStatus"] if row["loopClosed"] and row["legsConnectedAndPassed"] and row["loopSeamElevationDifferenceMeters"] < 0.01 else "review"
        lines.append(f'| {row["name"]} | {row["distanceKm"]:.3f} km | {row["ascentMeters"]:.1f} m | {row["maxSustained100mClimbPercent"]:.1f}% | {row["maxSustained100mDescentPercent"]:.1f}% | {row["oneMeterCoveragePercent"]:.1f}% | {row["fallbackCoveragePercent"]:.1f}% | {row["sourceTransitionCount"]} | {row["reportedPointsOver15Percent"]} | {row["fiveMeterSamplesOver15Percent"]} | {row["correctedRawDemOutlierCount"]} | {row["suspiciousGradientSpikeCount"]} | {status} |')
    lines += ["", "## Manual checks", "", "Godot interactive rendering was unavailable during this audit. Use the Debug Jump to m control at these reported distances to check the lap start/seam, anchor junctions, steep stretches, DEM-source edges and isolated raw samples:"]
    for row in results:
        checks = "; ".join(f'{item["distanceMeters"]:.0f} m ({", ".join(item["reasons"])})' for item in row["manualVisualChecks"])
        lines.append(f'- **{row["name"]}:** {checks}')
    lines += ["", "Profile PNGs show processed elevation, source-aligned elevation where applicable, raw DEM samples, grade and DEM-source transitions. `corrected-raw-dem-outliers.csv` lists the isolated samples filtered from ride elevation while preserving raw DEM values; `suspicious-elevation-sections.csv` lists any unresolved candidates. Full 5 m samples, transitions, and flagged section details are in each route’s `.quality.json` file. Nearest road names are matched against the local OSM-derived graph cache; entries include OSM way IDs for traceability."]
    (out / "README.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote route-quality report and profiles to {out}")


if __name__ == "__main__":
    main()
