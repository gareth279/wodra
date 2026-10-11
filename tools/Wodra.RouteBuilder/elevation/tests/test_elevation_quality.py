import unittest
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import generate_elevation as elevation
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import route_quality_report as quality


class ElevationQualityTests(unittest.TestCase):
    def test_route_point_source_labels_follow_distance_not_point_index(self):
        sources = ["1m"] * 3 + ["8m"] * 5 + ["1m"] * 4
        labels = elevation.source_labels_at_distances([0, 20, 40, 55, 60], sources, 5, 60)
        self.assertEqual(["1m", "8m", "1m", "1m", "1m"], labels)

    def test_fallback_bias_is_removed_without_flattening_sustained_slope(self):
        step = 5.0
        raw = np.arange(20, dtype=float) * 0.5
        sources = ["1m"] * 5 + ["8m"] * 10 + ["1m"] * 5
        raw[5:15] += 7.0
        calibrated, spans = elevation.calibrate_fallback_spans(raw, sources, step)
        self.assertEqual(1, len(spans))
        self.assertAlmostEqual(raw[4] + 0.5, calibrated[5], places=6)
        self.assertAlmostEqual(raw[14] - 7.0, calibrated[14], places=6)
        self.assertAlmostEqual(0.5, calibrated[8] - calibrated[7], places=6)
        self.assertAlmostEqual(0.5, calibrated[14] - calibrated[13], places=6)

    def test_fallback_transition_at_loop_seam_is_calibrated(self):
        raw = np.asarray([17.0, 17.2, 10.4, 10.6, 10.8, 11.0, 18.2, 18.4], dtype=float)
        sources = ["8m", "8m", "1m", "1m", "1m", "1m", "8m", "8m"]
        calibrated, spans = elevation.calibrate_fallback_spans(raw, sources, 5)
        self.assertEqual(1, len(spans))
        self.assertAlmostEqual(calibrated[6] - calibrated[5], 0.2, places=6)
        self.assertAlmostEqual(calibrated[2] - calibrated[1], 0.2, places=6)

    def test_short_primary_dem_outlier_is_filtered_without_changing_underlying_raw(self):
        raw = np.arange(20, dtype=float) * 0.5
        raw[8] -= 6
        raw[14:16] += 5
        filtered, runs = elevation.remove_isolated_primary_outliers(raw, ["1m"] * len(raw), 5)
        self.assertEqual(2, len(runs))
        self.assertAlmostEqual(4.0, filtered[8], places=6)
        self.assertAlmostEqual(7.0, filtered[14], places=6)
        self.assertAlmostEqual(0.5, filtered[15] - filtered[14], places=6)
        self.assertEqual(-2.0, raw[8])

    def test_sustained_grade_window_preserves_a_real_climb(self):
        distances = np.arange(0, 1001, 5, dtype=float)
        heights = np.where(distances <= 500, distances * 0.2, (1000 - distances) * 0.2)
        grades = quality.sustained_window_gradient(distances, heights, 100, 1000)
        self.assertAlmostEqual(20, grades[50], places=6)

    def test_isolated_raw_dem_spike_is_flagged_but_adjusted_source_edge_is_not(self):
        distances = np.arange(0, 25, 5, dtype=float)
        smooth = np.full(5, 10.0)
        raw = np.asarray([10, 10, 16, 10, 10], dtype=float)
        spikes = quality.suspicious_raw_spikes(distances, raw, raw, smooth, ["1m"] * 5, np.asarray([]))
        self.assertEqual([10.0], [item["distanceMeters"] for item in spikes])
        corrected = np.asarray([10, 10.2, 10.4, 10.6, 10.8], dtype=float)
        self.assertEqual([], quality.suspicious_raw_spikes(distances, raw, corrected, corrected,
            ["1m", "1m", "8m", "8m", "1m"], np.asarray([10, 20])))


if __name__ == "__main__":
    unittest.main()
