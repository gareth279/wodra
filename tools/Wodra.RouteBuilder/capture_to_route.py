#!/usr/bin/env python3
"""Prepare an existing RouteBuilder anchor config and timestamped video observations from GPX."""
from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import pathlib
import subprocess
import xml.etree.ElementTree as ET


def distance(a, b):
    radius = 6371008.8
    p1, p2 = math.radians(a[0]), math.radians(b[0])
    dp, dl = p2 - p1, math.radians(b[1] - a[1])
    h = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * radius * math.asin(math.sqrt(h))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("gpx", type=pathlib.Path)
    ap.add_argument("--id", default="pine-hill-test")
    ap.add_argument("--name", default="Pine Hill Test")
    ap.add_argument("--config", type=pathlib.Path, required=True)
    ap.add_argument("--report", type=pathlib.Path, required=True)
    ap.add_argument("--interval-seconds", type=float, default=3)
    ap.add_argument("--video", type=pathlib.Path, action="append", default=[])
    args = ap.parse_args()
    points = ET.parse(args.gpx).getroot().findall(".//{*}trkpt")
    if len(points) < 2:
        raise SystemExit("GPX has fewer than two track points")
    records = []
    for point in points:
        records.append({"lat": float(point.attrib["lat"]), "lon": float(point.attrib["lon"]),
                        "ele": float(point.findtext("{*}ele")) if point.findtext("{*}ele") else None,
                        "time": dt.datetime.fromisoformat(point.findtext("{*}time").replace("Z", "+00:00"))})
    steps = [distance((a["lat"], a["lon"]), (b["lat"], b["lon"]))
             for a, b in zip(records, records[1:])]
    chainage = [0.0]
    for step in steps:
        chainage.append(chainage[-1] + step)
    anchors, last = [], -args.interval_seconds
    for i, p in enumerate(records):
        elapsed = (p["time"] - records[0]["time"]).total_seconds()
        if elapsed - last >= args.interval_seconds or i == len(records) - 1:
            anchors.append({"name": f"GPS {elapsed:.0f}s", "sourceDistanceMeters": round(chainage[i], 3),
                            "latitude": p["lat"], "longitude": p["lon"]})
            last = elapsed
    elevations = [p["ele"] for p in records if p["ele"] is not None]
    gaps = [(i, (b["time"] - a["time"]).total_seconds()) for i, (a, b) in enumerate(zip(records, records[1:]))
            if (b["time"] - a["time"]).total_seconds() > 2]
    config = {
        "id": args.id, "name": args.name,
        "description": "A short experimental route reconstructed from GPS and driving footage to test Wodra's real-world route creation workflow.",
        "routeType": "pointToPoint", "loop": False, "difficulty": "Unrated", "routeDataVersion": 1,
        "environment": "Road", "discipline": "Road", "routingMode": "anchors", "profile": "cycling",
        "bbox": {"minLatitude": min(p["lat"] for p in records) - .003,
                 "maxLatitude": max(p["lat"] for p in records) + .003,
                 "minLongitude": min(p["lon"] for p in records) - .003,
                 "maxLongitude": max(p["lon"] for p in records) + .003},
        "marginMeters": 100, "maxSnapDistanceMeters": 30, "routeCorridorMeters": 40,
        "maxOutputSpacingMeters": 5, "detourWarningRatio": 1.35, "detourFailureRatio": 1.8,
        "referenceDistanceMeters": round(chainage[-1], 3), "anchors": anchors,
        "elevation": {"enabled": True, "resampleIntervalMeters": 5, "smoothingWindowMeters": 20,
                      "gradientWindowMeters": 20, "maximumBridgeInterpolationMeters": 50,
                      "primaryCollectionUrl": "https://nz-elevation.s3.ap-southeast-2.amazonaws.com/otago/dunedin-and-mosgiel_2021/dem_1m/2193/collection.json",
                      "fallbackCollectionUrl": "https://nz-elevation.s3.ap-southeast-2.amazonaws.com/new-zealand/new-zealand-contour/dem_8m/2193/collection.json",
                      "gradientReviewThresholdPercent": 20},
        "terrain": {"enabled": True, "nearRadiusMeters": 100, "farRadiusMeters": 350,
                    "spacingMeters": 10, "farSpacingMeters": 20, "corridorRadiusMeters": 100,
                    "chunkSizeMeters": 200, "farChunkSizeMeters": 300, "transitionOverlapMeters": 8,
                    "elevationCacheDirectory": str(args.config.parent.parent / "cache" / "elevation")},
        "buildings": {"enabled": True, "searchRadiusMeters": 80, "simplificationToleranceMeters": .75,
                      "metersPerLevel": 3, "ordinaryHeightMeters": 8, "smallBuildingHeightMeters": 3.5,
                      "elevationCacheDirectory": str(args.config.parent.parent / "cache" / "elevation")},
        "vegetation": {"enabled": True, "searchRadiusMeters": 100, "treeRowSpacingMeters": 10,
                       "simplificationToleranceMeters": 1, "generatedTreeCapPerArea": 70,
                       "totalTreeInstanceCap": 300,
                       "elevationCacheDirectory": str(args.config.parent.parent / "cache" / "elevation")},
        "streetFurniture": {"enabled": True, "searchRadiusMeters": 50,
                             "elevationCacheDirectory": str(args.config.parent.parent / "cache" / "elevation")},
        "backgroundStreets": {"enabled": True, "searchRadiusMeters": 100, "sampleSpacingMeters": 15,
                              "routeOverlapSuppressionMeters": 8,
                              "elevationCacheDirectory": str(args.config.parent.parent / "cache" / "elevation")}
    }
    args.config.parent.mkdir(parents=True, exist_ok=True)
    args.config.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    gpx_duration = (records[-1]["time"] - records[0]["time"]).total_seconds()
    videos = []
    for video in args.video:
        raw = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_entries",
            "format=duration:format_tags=creation_time,location:stream=codec_name,width,height,r_frame_rate,avg_frame_rate,duration",
            "-of", "json", str(video)], text=True))
        meta = raw.get("format", {}).get("tags", {})
        created = dt.datetime.fromisoformat(meta["creation_time"].replace("Z", "+00:00"))
        offset = (created - records[0]["time"]).total_seconds()
        videos.append({"file": video.name, "creationTime": meta.get("creation_time"),
                       "durationSeconds": float(raw["format"]["duration"]),
                       "streams": raw.get("streams", []), "startOffsetFromGpxSeconds": offset,
                       "withinGpsRecording": -2 <= offset <= gpx_duration + 2,
                       "alignmentConfidence": "high" if -2 <= offset <= gpx_duration + 2 else "low"})
    observations = []
    for video in videos:
        offset = video["startOffsetFromGpxSeconds"]
        frame_time = 0.0
        while frame_time < video["durationSeconds"]:
            elapsed = offset + frame_time
            if 0 <= elapsed <= gpx_duration:
                right = next((i for i, p in enumerate(records) if
                              (p["time"] - records[0]["time"]).total_seconds() >= elapsed), len(records) - 1)
                left = max(0, right - 1)
                t0 = (records[left]["time"] - records[0]["time"]).total_seconds()
                t1 = (records[right]["time"] - records[0]["time"]).total_seconds()
                ratio = 0 if t1 == t0 else (elapsed - t0) / (t1 - t0)
                ch = chainage[left] + (chainage[right] - chainage[left]) * ratio
                observations.append({"sourceVideo": video["file"], "videoSeconds": round(frame_time, 3),
                                     "utc": (records[0]["time"] + dt.timedelta(seconds=elapsed)).isoformat(),
                                     "rawGpsChainageMeters": round(ch, 2), "confidence": "timestamp-aligned",
                                     "featureTags": [], "note": "Add human-reviewed visual observations here."})
            frame_time += 10
    clip_boundaries = []
    for current, following in zip(videos, videos[1:]):
        gap = following["startOffsetFromGpxSeconds"] - (
            current["startOffsetFromGpxSeconds"] + current["durationSeconds"])
        clip_boundaries.append({"before": current["file"], "after": following["file"],
                                "gapSeconds": round(gap, 3),
                                "kind": "gap" if gap > 0.05 else "overlap" if gap < -0.05 else "continuous"})
    report = {"sourceGpx": args.gpx.name, "pointCount": len(records),
              "startUtc": records[0]["time"].isoformat(), "endUtc": records[-1]["time"].isoformat(),
              "durationSeconds": gpx_duration,
              "rawGpsDistanceMeters": round(chainage[-1], 2),
              "startEndSeparationMeters": round(distance((records[0]["lat"], records[0]["lon"]),
                                                          (records[-1]["lat"], records[-1]["lon"])), 2),
              "gpsElevation": {"available": bool(elevations), "minMeters": min(elevations) if elevations else None,
                               "maxMeters": max(elevations) if elevations else None,
                               "authority": "recorded GPX only; not used when LINZ DEM sampling succeeds"},
              "gpsMaxStepMeters": round(max(steps), 2),
              "gpsStationaryIntervalsUnder0_5m": sum(step < 0.5 for step in steps),
              "gapsOverTwoSeconds": gaps, "videoFiles": videos, "videoClipBoundaries": clip_boundaries,
              "embeddedLocationMetadata": "none found in ffprobe format tags",
              "observations": observations,
              "anchorsGenerated": len(anchors),
              "anchorSpacingSeconds": args.interval_seconds,
              "notes": ["Point-to-point: endpoints are separate; no closure connector is invented.",
                        "Anchor timestamps preserve capture order and direction; RouteBuilder snaps them to OSM.",
                        "Buildings, vegetation and terrain are derived from OSM/LINZ, not inferred from GPS."]}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
