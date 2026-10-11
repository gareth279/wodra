import importlib.util
import sys
import unittest
from pathlib import Path


ELEVATION = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ELEVATION))
SCRIPT = ELEVATION / "sample_buildings.py"
SPEC = importlib.util.spec_from_file_location("sample_buildings", SCRIPT)
SAMPLE_BUILDINGS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SAMPLE_BUILDINGS)
VEGETATION_SPEC = importlib.util.spec_from_file_location("sample_vegetation", ELEVATION / "sample_vegetation.py")
SAMPLE_VEGETATION = importlib.util.module_from_spec(VEGETATION_SPEC)
VEGETATION_SPEC.loader.exec_module(SAMPLE_VEGETATION)


class Dataset:
    def __init__(self, name, resolution):
        self.name = name
        self.res = resolution


class BuildingRasterPriorityTests(unittest.TestCase):
    def test_overlapping_cached_rasters_use_one_metre_dem_before_eight_metre_fallback(self):
        fallback = (Dataset("CE17.tiff", (8.0, 8.0)), object())
        primary = (Dataset("CE17_10000_0303.tiff", (1.0, 1.0)), object())
        ordered = SAMPLE_BUILDINGS.prefer_highest_resolution([fallback, primary])
        self.assertEqual("CE17_10000_0303.tiff", ordered[0][0].name)
        self.assertEqual("CE17.tiff", ordered[1][0].name)

    def test_ties_are_deterministic(self):
        later = (Dataset("b.tif", (1.0, 1.0)), object())
        earlier = (Dataset("a.tif", (1.0, 1.0)), object())
        self.assertEqual(["a.tif", "b.tif"], [item[0].name for item in
            SAMPLE_BUILDINGS.prefer_highest_resolution([later, earlier])])

    def test_vegetation_uses_the_same_highest_resolution_priority(self):
        fallback = (Dataset("CE17.tiff", (8.0, 8.0)), object())
        primary = (Dataset("CE17_10000_0303.tiff", (1.0, 1.0)), object())
        ordered = SAMPLE_VEGETATION.prefer_highest_resolution([fallback, primary])
        self.assertEqual("CE17_10000_0303.tiff", ordered[0][0].name)


if __name__ == "__main__":
    unittest.main()
