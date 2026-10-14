import importlib.util
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

ELEVATION = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ELEVATION))
SPEC = importlib.util.spec_from_file_location("sample_streets", ELEVATION / "sample_streets.py")
SAMPLE_STREETS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SAMPLE_STREETS)


class StreetRasterPriorityTests(unittest.TestCase):
    def test_resampling_uses_finest_valid_raster_and_records_version(self):
        datasets = {
            "CE17.tiff": FakeDataset("CE17.tiff", 8.0, 19.0),
            "CE17_10000_0403.tiff": FakeDataset("CE17_10000_0403.tiff", 1.0, 3.0),
        }
        data = {"streets": [{"index": 0, "points": [{"latitude": -45.9,
                                                       "longitude": 170.5}]}]}
        with patch.object(SAMPLE_STREETS.rasterio, "open", side_effect=lambda path: datasets[Path(path).name]), \
             patch.object(SAMPLE_STREETS.Transformer, "from_crs", return_value=IdentityTransformer()):
            result = SAMPLE_STREETS.resample(data, FakeRasterDirectory(list(datasets)))

        self.assertAlmostEqual(3.0, result["elevations"][0][0], places=5)
        self.assertEqual(2, result["elevationSamplerVersion"])

    def test_bilinear_pixel_center_sampling_interpolates_raster_values(self):
        dataset = FakeDataset("test.tiff", 1.0, 0,
                              np.array([[0, 2], [4, 6]], dtype=np.float32))
        value = SAMPLE_STREETS.sample(dataset, IdentityTransformer(), 0, 0)
        self.assertAlmostEqual(3.0, value, places=5)


class FakeTransform:
    def __invert__(self):
        return self

    def __mul__(self, coordinate):
        return (1, 1)


class FakeDataset:
    def __init__(self, name, resolution, value, values=None):
        self.name = name
        self.res = (resolution, resolution)
        self.crs = "EPSG:2193"
        self.nodata = -9999
        self.transform = FakeTransform()
        self.values = values if values is not None else np.full((2, 2), value, dtype=float)

    def read(self, band, window, boundless):
        return self.values

    def close(self):
        pass


class FakeRasterDirectory:
    def __init__(self, names):
        self.names = names

    def glob(self, pattern):
        return [Path(name) for name in self.names] if pattern == "*.tiff" else []


class IdentityTransformer:
    def transform(self, lon, lat):
        return lon, lat


if __name__ == "__main__":
    unittest.main()
