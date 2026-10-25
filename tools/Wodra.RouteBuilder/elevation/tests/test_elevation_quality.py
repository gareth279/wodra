import unittest
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import generate_elevation as elevation
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import route_quality_report as quality


class ElevationQualityTests(unittest.TestCase):
    @staticmethod
    def projected_path(points, lat=-45.0):
        metres_per_longitude = 111_195 * np.cos(np.deg2rad(lat))
        return (np.full(len(points), lat), np.asarray(points, dtype=float) / metres_per_longitude)

    def test_shared_out_and_back_road_uses_one_canonical_elevation_before_and_after_smoothing(self):
        forward_x = np.arange(0.0, 101.0, 5.0)
        second_x = np.arange(100.0, -1.0, -5.0) + 0.2
        lat_a, lon_a = self.projected_path(forward_x)
        lat_b, lon_b = self.projected_path(second_x)
        distances = np.r_[forward_x, 200 + np.arange(len(second_x)) * 5.0]
        latitudes, longitudes = np.r_[lat_a, lat_b], np.r_[lon_a, lon_b]
        physical = np.r_[forward_x, 100 - np.arange(len(second_x)) * 5.0]
        raw = 80 + physical * .02
        drift = np.r_[np.linspace(3.5, 0, len(forward_x)), np.linspace(0, -3.5, len(second_x))]
        calibrated = raw + drift

        result, segments = elevation.canonicalize_repeated_elevation_samples(
            distances, latitudes, longitudes, raw, calibrated)

        self.assertEqual(1, len(segments))
        self.assertGreater(segments[0]['maximumElevationDifferenceBeforeMeters'], 5)
        self.assertEqual(0, segments[0]['maximumElevationDifferenceAfterMeters'])
        self.assertGreaterEqual(segments[0]['matchedSpanMeters'], 25)
        for left, right in zip(range(len(forward_x)), reversed(range(len(forward_x), len(distances)))):
            self.assertAlmostEqual(result[left], result[right], places=9)

        smoothed, smooth_segments = elevation.canonicalize_repeated_elevation_samples(
            distances, latitudes, longitudes, raw, result + drift)
        self.assertEqual(1, len(smooth_segments))
        for left, right in zip(range(len(forward_x)), reversed(range(len(forward_x), len(distances)))):
            self.assertAlmostEqual(smoothed[left], smoothed[right], places=9)
        for index in range(1, len(forward_x) - 1):
            outbound_grade = (result[index + 1] - result[index - 1]) / 10
            return_index = len(forward_x) + (len(forward_x) - 1 - index)
            return_grade = (result[return_index + 1] - result[return_index - 1]) / 10
            self.assertAlmostEqual(outbound_grade, -return_grade, places=9)

    def test_same_direction_repeated_loop_segment_is_canonicalized_once(self):
        segment = np.arange(0.0, 101.0, 5.0)
        first_lat, first_lon = self.projected_path(segment)
        second_lat, second_lon = self.projected_path(segment + .1)
        distances = np.arange(2 * len(segment), dtype=float) * 5
        latitudes, longitudes = np.r_[first_lat, second_lat], np.r_[first_lon, second_lon]
        raw = 70 + np.r_[segment, segment] * .01
        heights = raw + np.r_[np.full(len(segment), 2.0), np.full(len(segment), -2.0)]
        result, segments = elevation.canonicalize_repeated_elevation_samples(
            distances, latitudes, longitudes, raw, heights)
        self.assertEqual(1, len(segments))
        for left, right in zip(range(1, len(segment) - 1), range(len(segment) + 1, len(distances) - 1)):
            self.assertAlmostEqual(result[left], result[right], places=9)

    def test_shared_segment_with_different_sampling_density_is_matched(self):
        first_x = np.arange(0.0, 101.0, 5.0)
        second_x = np.arange(100.0, -1.0, -7.0) + .15
        lat_a, lon_a = self.projected_path(first_x)
        lat_b, lon_b = self.projected_path(second_x)
        distances = np.r_[np.cumsum(np.r_[0, np.diff(first_x)]),
                          200 + np.cumsum(np.r_[0, np.abs(np.diff(second_x))])]
        latitudes, longitudes = np.r_[lat_a, lat_b], np.r_[lon_a, lon_b]
        raw = 60 + np.r_[first_x, second_x] * .01
        shifted = raw + np.r_[np.linspace(2, 0, len(first_x)), np.linspace(0, -2, len(second_x))]
        result, segments = elevation.canonicalize_repeated_elevation_samples(
            distances, latitudes, longitudes, raw, shifted)
        self.assertEqual(1, len(segments))
        self.assertGreaterEqual(segments[0]['matchedSpanMeters'], 25)
        self.assertEqual(0, segments[0]['maximumElevationDifferenceAfterMeters'])

    def test_parallel_roads_and_crossings_are_not_deduplicated(self):
        forward = np.arange(0.0, 101.0, 5.0)
        parallel = np.arange(100.0, -1.0, -5.0)
        lat_a, lon_a = self.projected_path(forward)
        lat_b, lon_b = self.projected_path(parallel, lat=-45 + 5 / 111_195)
        distances = np.r_[forward, 200 + np.arange(len(parallel)) * 5.0]
        raw = np.r_[80 + forward * .01, 80 + (100 - np.arange(len(parallel)) * 5) * .01]
        shifted = raw + np.r_[np.full(len(forward), 4.0), np.full(len(parallel), -4.0)]
        _, segments = elevation.canonicalize_repeated_elevation_samples(
            distances, np.r_[lat_a, lat_b], np.r_[lon_a, lon_b], raw, shifted)
        self.assertEqual([], segments)

        close_lat, close_lon = self.projected_path(parallel, lat=-45 + 2 / 111_195)
        _, close_parallel_segments = elevation.canonicalize_repeated_elevation_samples(
            distances, np.r_[lat_a, close_lat], np.r_[lon_a, close_lon], raw, shifted)
        self.assertEqual([], close_parallel_segments)

        crossing_lat, crossing_lon = self.projected_path(np.zeros(21))
        crossing_lat = np.linspace(-45.0005, -44.9995, 21)
        _, crossing_segments = elevation.canonicalize_repeated_elevation_samples(
            np.r_[np.arange(0, 101, 5), 200 + np.arange(0, 101, 5)],
            np.r_[lat_a[:21], crossing_lat], np.r_[lon_a[:21], crossing_lon],
            np.r_[np.full(42, 80.0)], np.r_[np.full(21, 82.0), np.full(21, 82.0)])
        self.assertEqual([], crossing_segments)

        bridge_heights = np.r_[np.full(21, 80.0), np.full(21, 88.0)]
        _, bridge_segments = elevation.canonicalize_repeated_elevation_samples(
            np.r_[np.arange(0, 101, 5), 200 + np.arange(0, 101, 5)],
            np.r_[lat_a[:21], crossing_lat], np.r_[lon_a[:21], crossing_lon],
            bridge_heights, bridge_heights)
        self.assertEqual([], bridge_segments)

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

    def test_point_to_point_profile_clamps_at_end_and_does_not_wrap_smoothing_or_grade(self):
        values = np.asarray([0.0, 10.0, 20.0, 40.0])
        self.assertEqual(0.0, elevation.distance_interp(values, 1, -10, False))
        self.assertEqual(40.0, elevation.distance_interp(values, 1, 99, False))
        self.assertAlmostEqual(40.0, elevation.distance_interp(values, 1, 3, False))
        # A large elevation change at the far endpoint must not bleed into the start.
        smoothed = elevation.moving_average(np.asarray([100.0, 100.0, 100.0, 0.0]), 1, 2, False)
        self.assertGreater(smoothed[0], smoothed[-1])
        grades = elevation.distance_gradient(np.asarray([0.0, 1.0, 2.0, 100.0]), 1, 2, False)
        self.assertGreater(grades[-1], 0)
        self.assertLess(grades[0], grades[-1])

    def test_point_to_point_source_labels_do_not_wrap_last_sample_to_start(self):
        labels = elevation.source_labels_at_distances([0, 10, 20, 30], ["1m", "8m", "8m", "8m"], 10, 30, False)
        self.assertEqual(["1m", "8m", "8m", "8m"], labels)

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
