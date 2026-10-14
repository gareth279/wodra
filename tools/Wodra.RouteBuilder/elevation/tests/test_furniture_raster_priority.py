import importlib.util
import sys
import unittest
from pathlib import Path

import numpy as np

ELEVATION = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ELEVATION))
SPEC = importlib.util.spec_from_file_location(
    "resample_street_furniture_package", ELEVATION / "resample_street_furniture_package.py")
RESAMPLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RESAMPLER)


class FurnitureRasterPriorityTests(unittest.TestCase):
    def test_furniture_uses_fine_valid_raster_and_records_sampler_version(self):
        coarse = FakeDataset("CE17.tiff", 8.0, 14.4)
        fine = FakeDataset("CE17_10000_0403.tiff", 1.0, 3.9)
        data = {"items": [{"osmType": "node", "osmId": 12730294938,
                           "longitude": 170.5200119, "latitude": -45.9037752,
                           "elevationMeters": 14.4}]}

        result = RESAMPLER.resample(data, [(coarse, IdentityTransformer()), (fine, IdentityTransformer())])

        self.assertAlmostEqual(3.9, result["items"][0]["elevationMeters"], places=5)
        self.assertEqual(2, result["elevationSamplerVersion"])
        self.assertEqual(1, result["statistics"]["elevationRasterCount"])


class FakeTransform:
    def __invert__(self):
        return self

    def __mul__(self, coordinate):
        return (1, 1)


class FakeDataset:
    def __init__(self, name, resolution, value):
        self.name = name
        self.res = (resolution, resolution)
        self.transform = FakeTransform()
        self.nodata = -9999
        self.values = np.full((2, 2), value, dtype=float)

    def read(self, band, window, boundless):
        return self.values


class IdentityTransformer:
    def transform(self, lon, lat):
        return lon, lat


if __name__ == "__main__":
    unittest.main()
