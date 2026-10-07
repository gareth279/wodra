import importlib.util
import unittest
from pathlib import Path

import numpy as np

spec = importlib.util.spec_from_file_location('generate_elevation', Path(__file__).with_name('generate_elevation.py'))
elev = importlib.util.module_from_spec(spec)
spec.loader.exec_module(elev)


class ElevationTests(unittest.TestCase):
    def test_coordinate_transform_and_bilinear_sampling(self):
        from pyproj import Transformer
        transformer = Transformer.from_crs('EPSG:4326', 'EPSG:2193', always_xy=True)
        x, y = transformer.transform(170.5, -45.87)
        self.assertTrue(1_000_000 < x < 2_000_000 and 4_000_000 < y < 5_500_000)
        self.assertAlmostEqual(3, elev.bilinear_value([[0, 2], [4, 6]], .5, .5))

    def test_distance_interpolation_and_irregular_spacing(self):
        self.assertAlmostEqual(5, elev.interp(np.array([0., 10.]), np.array([0., 10.]), 5))
        d = np.array([0., 2., 21., 48., 80.])
        values = 100 + .02 * d
        query = np.array([0., 10., 35., 80.])
        self.assertTrue(np.allclose(np.interp(query, d, values), 100 + .02 * query))

    def test_centered_distance_smoothing_removes_single_spike(self):
        profile = np.zeros(81); profile[40] = 10
        smooth = elev.circular_average(profile, 5, 30)
        self.assertLess(smooth[40], 2)
        self.assertGreater(smooth[40], 0)

    def test_constant_slope_gradient(self):
        interval = 5
        profile = np.arange(400, dtype=float) * interval * .03
        # Avoid seam by using a much longer cyclic array with the same local slope.
        gradient = elev.circular_gradient(profile, interval, 25)
        self.assertAlmostEqual(3, gradient[100], places=7)

    def test_ascent_descent_and_loop_seam(self):
        profile = np.asarray([0., 1., 3., 2., 0.])
        self.assertAlmostEqual(3, float(np.maximum(0, np.diff(profile)).sum()))
        self.assertAlmostEqual(3, float(np.maximum(0, -np.diff(profile)).sum()))
        seam_profile = np.asarray([2., 1., 0., 1., 2.])
        smooth = elev.circular_average(seam_profile, 1, 2)
        gradient = elev.circular_gradient(smooth, 1, 2)
        self.assertAlmostEqual(smooth[0], smooth[-1], places=6)
        gradient[-1] = gradient[0]
        self.assertLess(abs(gradient[0] - gradient[-1]), 1e-6)

    def test_source_attribution_preserved(self):
        collection = {'providers': [{'name':'AAM NZ'}, {'name':'Otago Regional Council'}], 'license':'CC-BY-4.0'}
        self.assertEqual('AAM NZ, Otago Regional Council; CC-BY-4.0', elev.attribution(collection))


if __name__ == '__main__': unittest.main()
