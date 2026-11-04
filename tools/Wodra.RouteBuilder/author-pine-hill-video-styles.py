#!/usr/bin/env python3
"""Apply conservative, timestamp-linked suburban styles to Pine Hill OSM footprints.

The route extract has footprints but only the generic `building=yes` tag. The
source footage supports a neighbourhood-level residential treatment, not an
exact facade survey. Existing hand-reviewed overrides are preserved verbatim.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
from typing import Any

EARTH_RADIUS_M = 6_371_000.0
RADIANS = 3.141592653589793 / 180.0
FACADES = ["#D8D2C6", "#B9C5C3", "#AEBAB0", "#C7BBA5", "#B67A66", "#C7C9C4", "#82948B", "#D3C3B4"]
ROOFS = ["#4C5050", "#5C5B56", "#69574D", "#515E64", "#736B60"]
def read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def stable(value: int, slot: str) -> int:
    digest = hashlib.sha256(f"pine-hill:{value}:{slot}".encode("ascii")).digest()
    return int.from_bytes(digest[:4], "big")


def footprint_area(building: dict[str, Any]) -> float:
    ring = building.get("footprint", [])
    if len(ring) < 3:
        return 80.0
    lat0 = float(ring[0]["latitude"]) * RADIANS
    points = [((float(p["longitude"]) - float(ring[0]["longitude"])) * RADIANS
               * EARTH_RADIUS_M * math.cos(lat0),
               (float(p["latitude"]) - float(ring[0]["latitude"])) * RADIANS * EARTH_RADIUS_M)
              for p in ring]
    return abs(sum(points[i][0] * points[(i + 1) % len(points)][1]
                   - points[(i + 1) % len(points)][0] * points[i][1]
                   for i in range(len(points))) / 2)


def nearest_frame(chainage: float, length: float,
                  checkpoints: list[tuple[str, int, float]]) -> tuple[str, int, float]:
    def wrapped_distance(frame: tuple[str, int, float]) -> float:
        delta = abs(chainage - frame[2])
        return min(delta, length - delta)
    return min(checkpoints, key=wrapped_distance)


def estimated_height(area: float, key: int) -> float:
    lower, upper = (3.5, 5.0) if area < 100 else (4.0, 5.5) if area < 240 else (4.8, 6.3)
    unit = stable(key, "height") / 0xFFFFFFFF
    return round(lower + (upper - lower) * unit, 2)


def author(repository: Path, apply: bool) -> dict[str, Any]:
    package = repository / "routes/user-packages/pine-hill-test"
    building_path = package / "pine-hill-test.buildings.json"
    style_path = package / "pine-hill-test.observations.json"
    route = read(package / "pine-hill-test.route-points.json")
    buildings = read(building_path)
    sidecar = read(style_path)
    length = float(route["points"][-1]["distanceMeters"])
    checkpoints = [(str(item["sourceVideo"]), int(float(item["videoSeconds"])),
                    float(item["routeChainageMeters"])) for item in sidecar.get("observations", [])]
    if not checkpoints:
        raise ValueError("Pine Hill source-video observations are required to style buildings")
    styles = sidecar.setdefault("buildingStyles", [])
    existing = {int(item["osmId"]) for item in styles}
    previous_coverage = sidecar.get("videoStyleCoverage", {})
    preserved_count = int(previous_coverage.get("priorHumanReviewedStyleCount", len(styles)))
    previous_estimated_count = int(previous_coverage.get("addedNeighbourhoodEstimateCount", 0))
    additions = []
    for building in buildings["buildings"]:
        osm_id = int(building["osmId"])
        if osm_id in existing:
            continue
        chainage = float(building["nearestRouteDistanceMeters"]) % length
        video, seconds, _ = nearest_frame(chainage, length, checkpoints)
        area = footprint_area(building)
        key = stable(osm_id, "style")
        roof_slot = key % 10
        additions.append({
            "osmId": osm_id,
            "buildingType": "residential" if area < 350 else "commercial/retail",
            "facadeColour": FACADES[stable(osm_id, "facade") % len(FACADES)],
            "roofColour": ROOFS[stable(osm_id, "roof-colour") % len(ROOFS)],
            "roofShape": "gabled" if roof_slot < 5 else "hipped" if roof_slot < 9 else "flat",
            "sourceVideo": video,
            "videoSeconds": float(seconds),
            "confidence": 0.32,
            "provenance": "Neighbourhood-level style inferred from timestamp-aligned Pine Hill footage and OSM footprint only. Exact facade material, colour, roof form and house identity are not visible or surveyed for this individual footprint.",
            "visualHeightMeters": estimated_height(area, osm_id),
        })
    styles.extend(additions)
    estimated_styles = [item for item in styles
                        if item.get("confidence") == 0.32
                        and str(item.get("provenance", "")).startswith("Neighbourhood-level style inferred")]
    sidecar["videoStyleCoverage"] = {
        "source": "timestamp-aligned Pine Hill route footage",
        "footprintCount": len(buildings["buildings"]),
        "priorHumanReviewedStyleCount": preserved_count,
        "addedNeighbourhoodEstimateCount": previous_estimated_count + len(additions),
        "warning": "Added styles are neighbourhood-level estimates and do not identify individual surveyed houses.",
    }
    report = {
        "routeId": "pine-hill-test",
        "geometryFingerprint": route["geometryFingerprint"],
        "sourceFootprints": len(buildings["buildings"]),
        "preservedHumanReviewedStyles": preserved_count,
        "addedTimestampLinkedEstimates": previous_estimated_count + len(additions),
        "resultingStyles": len(styles),
        "sourceCheckpoints": [{"sourceVideo": video, "videoSeconds": seconds,
                               "routeChainageMeters": chainage} for video, seconds, chainage in checkpoints],
        "roofShapeCounts": {shape: sum(item.get("roofShape") == shape for item in styles)
                            for shape in ("gabled", "hipped", "flat")},
        "distinctFacadeColours": sorted({item["facadeColour"] for item in estimated_styles}),
        "distinctRoofColours": sorted({item["roofColour"] for item in estimated_styles}),
        "heightEstimateRangeMeters": ([min(item["visualHeightMeters"] for item in estimated_styles),
                                        max(item["visualHeightMeters"] for item in estimated_styles)]
                                       if estimated_styles else None),
        "limitations": "Source footage shows a low-rise suburban streetscape but cannot reliably identify each OSM footprint. New properties use varied, conservative styles as approximation only.",
    }
    if apply:
        sidecar["geometryFingerprint"] = route["geometryFingerprint"]
        write(style_path, sidecar)
        write(repository / "routes/review/pine-hill-video-style-report.json", report)
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--apply", action="store_true", help="write inferred styles and review report")
    args = parser.parse_args()
    report = author(args.repository.resolve(), args.apply)
    print(f"preserved={report['preservedHumanReviewedStyles']} added={report['addedTimestampLinkedEstimates']} "
          f"total={report['resultingStyles']} apply={args.apply}")


if __name__ == "__main__":
    main()
