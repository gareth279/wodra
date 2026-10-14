import importlib.util
import sys
import unittest
from pathlib import Path

ELEVATION = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ELEVATION))
SPEC = importlib.util.spec_from_file_location("generate_terrain", ELEVATION / "generate_terrain.py")
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)


class TerrainRasterPriorityTests(unittest.TestCase):
    def test_terrain_orders_fine_rasters_before_coarse_fallback(self):
        coarse = Sampler("CE17.tiff", (8.0, 8.0))
        fine = Sampler("CE17_10000_0403.tiff", (1.0, 1.0))

        ordered = GENERATOR.order_samplers([coarse, fine])

        self.assertEqual([fine, coarse], ordered)


class Sampler:
    def __init__(self, name, resolution):
        self.dataset = Dataset(name, resolution)


class Dataset:
    def __init__(self, name, resolution):
        self.name = name
        self.res = resolution


if __name__ == "__main__":
    unittest.main()
