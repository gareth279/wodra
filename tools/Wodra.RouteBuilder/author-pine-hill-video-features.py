#!/usr/bin/env python3
"""Add timestamp-linked roadside details at Pine Hill's sparse video checkpoints.

Offsets are visual estimates from the aligned dash-camera frames, not surveyed
parcel locations. Existing hand-authored features are retained unchanged.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


FEATURES: dict[str, list[dict[str, Any]]] = {
    "video-018s": [
        {"id": "118-left-grass-verge", "objectType": "grass-verge", "lateral": -3.9, "forward": -1.0, "length": 15, "width": 4, "height": .08, "variant": "mown-roadside-grass", "colour": "#78945C"},
        {"id": "118-left-garden-tree", "objectType": "broadleaf-tree", "lateral": -8.0, "forward": 1.5, "length": 6.0, "width": 6.0, "height": 8.2, "variant": "broadleaf", "colour": "#527448"},
        {"id": "118-right-utility-pole", "objectType": "utility-pole", "lateral": 5.9, "forward": 2.0, "length": .3, "width": .3, "height": 8.0, "variant": "concrete-crossarm", "colour": "#918B7C"},
    ],
    "video-138s": [
        {"id": "254-right-utility-pole", "objectType": "utility-pole", "lateral": 6.0, "forward": 1.5, "length": .3, "width": .3, "height": 8.1, "variant": "concrete-crossarm", "colour": "#918B7C"},
        {"id": "254-right-mature-tree", "objectType": "broadleaf-tree", "lateral": 9.0, "forward": 3.0, "length": 6.5, "width": 6.5, "height": 9.0, "variant": "broadleaf", "colour": "#456D45"},
        {"id": "254-left-grass-verge", "objectType": "grass-verge", "lateral": -4.0, "forward": -2.0, "length": 14, "width": 4.5, "height": .08, "variant": "mown-roadside-grass", "colour": "#78945C"},
    ],
    "video-036s": [
        {"id": "307-right-trimmed-hedge", "objectType": "hedge", "lateral": 5.6, "forward": 1.0, "length": 18, "width": 1.4, "height": 1.7, "variant": "trimmed-dark-green", "colour": "#4C7043"},
        {"id": "307-left-front-garden-tree", "objectType": "broadleaf-tree", "lateral": -8.5, "forward": 2.0, "length": 5.5, "width": 5.5, "height": 8.4, "variant": "broadleaf", "colour": "#527448"},
    ],
    "video-045s": [
        {"id": "391-left-grass-verge", "objectType": "grass-verge", "lateral": -4.0, "forward": -1.0, "length": 20, "width": 5, "height": .08, "variant": "mown-roadside-grass", "colour": "#7E9A61"},
        {"id": "391-right-utility-pole", "objectType": "utility-pole", "lateral": 5.8, "forward": 2.0, "length": .3, "width": .3, "height": 8.4, "variant": "concrete-crossarm", "colour": "#918B7C"},
        {"id": "391-right-mature-tree", "objectType": "broadleaf-tree", "lateral": 8.8, "forward": 4.0, "length": 6.0, "width": 6.0, "height": 8.7, "variant": "broadleaf", "colour": "#527448"},
    ],
    "video-063s": [
        {"id": "645-left-open-verge", "objectType": "grass-verge", "lateral": -4.0, "forward": -1.5, "length": 16, "width": 5.0, "height": .08, "variant": "mown-roadside-grass", "colour": "#78945C"},
        {"id": "645-right-utility-pole", "objectType": "utility-pole", "lateral": 5.9, "forward": 1.0, "length": .3, "width": .3, "height": 8.2, "variant": "concrete-crossarm", "colour": "#918B7C"},
        {"id": "645-right-garden-tree", "objectType": "broadleaf-tree", "lateral": 9.0, "forward": 3.0, "length": 6.0, "width": 6.0, "height": 8.0, "variant": "broadleaf", "colour": "#527448"},
    ],
    "video-072s": [
        {"id": "740-left-bank-grass", "objectType": "grass-verge", "lateral": -4.2, "forward": -1.0, "length": 19, "width": 5.5, "height": .08, "variant": "mown-roadside-grass", "colour": "#78945C"},
        {"id": "740-left-garden-tree", "objectType": "broadleaf-tree", "lateral": -8.5, "forward": 2.0, "length": 6.0, "width": 6.0, "height": 8.3, "variant": "broadleaf", "colour": "#527448"},
        {"id": "740-right-utility-pole", "objectType": "utility-pole", "lateral": 6.0, "forward": 2.0, "length": .3, "width": .3, "height": 8.0, "variant": "concrete-crossarm", "colour": "#918B7C"},
    ],
    "video-075s": [
        {"id": "772-right-trimmed-hedge", "objectType": "hedge", "lateral": 5.5, "forward": 1.0, "length": 22, "width": 1.4, "height": 1.8, "variant": "trimmed-dark-green", "colour": "#4C7043"},
        {"id": "772-right-garden-tree", "objectType": "broadleaf-tree", "lateral": 9.0, "forward": 4.0, "length": 5.8, "width": 5.8, "height": 8.0, "variant": "broadleaf", "colour": "#527448"},
    ],
    "video-084s": [
        {"id": "886-left-utility-pole", "objectType": "utility-pole", "lateral": -5.8, "forward": 1.0, "length": .3, "width": .3, "height": 8.3, "variant": "concrete-crossarm", "colour": "#918B7C"},
        {"id": "886-right-conifer", "objectType": "conifer-tree", "lateral": 8.5, "forward": 3.0, "length": 4.5, "width": 4.5, "height": 9.0, "variant": "garden-conifer", "colour": "#386348"},
        {"id": "886-right-grass-verge", "objectType": "grass-verge", "lateral": 4.0, "forward": -2.0, "length": 17, "width": 4.0, "height": .08, "variant": "mown-roadside-grass", "colour": "#78945C"},
    ],
    "video-087s": [
        {"id": "907-right-weathered-fence", "objectType": "timber-fence", "lateral": 5.0, "forward": 1.5, "length": 24, "width": .06, "height": 1.5, "variant": "weathered-white-paling", "colour": "#BEBEB5"},
        {"id": "907-right-conifer", "objectType": "conifer-tree", "lateral": 8.5, "forward": 4.0, "length": 4.2, "width": 4.2, "height": 9.0, "variant": "garden-conifer", "colour": "#386348"},
        {"id": "907-left-grass-verge", "objectType": "grass-verge", "lateral": -4.0, "forward": -1.0, "length": 17, "width": 4.5, "height": .08, "variant": "mown-roadside-grass", "colour": "#78945C"},
    ],
}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--apply", action="store_true", help="write estimated route scenery features")
    args = parser.parse_args()
    path = args.repository.resolve() / "routes/user-packages/pine-hill-test/pine-hill-test.observations.json"
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    by_id = {item["id"]: item for item in data["observations"]}
    added = 0
    added_types: dict[str, int] = {}
    for observation_id, specifications in FEATURES.items():
        observation = by_id[observation_id]
        observation.setdefault("positionPrecision", "route-relative; object side and lateral offset are estimated from the video and are not surveyed")
        known = {item["id"] for item in observation.get("features", [])}
        for spec in specifications:
            if spec["id"] in known:
                continue
            feature = {
                "id": spec["id"],
                "objectType": spec["objectType"],
                "routeChainageMeters": float(observation["routeChainageMeters"]),
                "lateralPositionMeters": spec["lateral"],
                "forwardOffsetMeters": spec["forward"],
                "orientationDegrees": 0,
                "lengthMeters": spec["length"],
                "widthMeters": spec["width"],
                "heightMeters": spec["height"],
                "variant": spec["variant"],
                "colour": spec["colour"],
                "sourceVideo": observation["sourceVideo"],
                "videoSeconds": observation["videoSeconds"],
                "confidence": 0.42,
                "provenance": "Timestamp-aligned footage shows this roadside feature type. Route side and lateral offset are visual estimates from the frame; the individual object position is not surveyed.",
                "geometryFingerprint": data["geometryFingerprint"],
            }
            observation.setdefault("features", []).append(feature)
            added += 1
            added_types[feature["objectType"]] = added_types.get(feature["objectType"], 0) + 1
    data["videoCheckpointFeatureCoverage"] = {
        "sourceCheckpointCount": len(data["observations"]),
        "checkpointsWithEstimatedRoadsideFeatures": len(FEATURES),
        "addedFeatureCount": sum(len(specs) for specs in FEATURES.values()),
        "addedFeatureTypes": {kind: sum(spec["objectType"] == kind for specs in FEATURES.values() for spec in specs)
                              for kind in sorted({spec["objectType"] for specs in FEATURES.values() for spec in specs})},
        "limitations": "Positions, road side and lateral offsets are visual estimates; the video does not survey parcel geometry.",
    }
    if args.apply:
        path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        report_path = args.repository.resolve() / "routes/review/pine-hill-video-style-report.json"
        if report_path.exists():
            report = json.loads(report_path.read_text(encoding="utf-8-sig"))
        else:
            report = {"routeId": "pine-hill-test", "geometryFingerprint": data["geometryFingerprint"]}
        report["addedRoadsideFeatureCount"] = data["videoCheckpointFeatureCoverage"]["addedFeatureCount"]
        report["roadsideFeatureTypes"] = data["videoCheckpointFeatureCoverage"]["addedFeatureTypes"]
        report["roadsideFeatureCheckpoints"] = len(FEATURES)
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"added={added} types={json.dumps(added_types, sort_keys=True)} apply={args.apply}")


if __name__ == "__main__":
    main()
