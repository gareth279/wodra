#!/usr/bin/env python3
"""Generate Wodra application icons from the owner-approved PNG artwork.

Requires Pillow and NumPy/SciPy. The original source image is never modified.
"""

from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "images" / "Cobalt Helmet Tortoiseshell Cycling Icon.png"
OUTPUT = ROOT / "src" / "IndoorCycling.Game" / "Assets" / "Branding"
SIZES = (16, 24, 32, 48, 64, 128, 256, 512, 1024)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    pixels = np.asarray(source).copy()
    components, count = ndimage.label(pixels[:, :, 3] > 0, structure=np.ones((3, 3), dtype=np.uint8))
    if count < 1:
        raise SystemExit("The supplied icon has no visible pixels.")
    sizes = np.bincount(components.ravel())
    sizes[0] = 0
    main_component = int(sizes.argmax())
    stray_mask = (components != 0) & (components != main_component)
    removed = int(stray_mask.sum())
    pixels[stray_mask] = 0
    clean = Image.fromarray(pixels, "RGBA")

    OUTPUT.mkdir(parents=True, exist_ok=True)
    variants: dict[int, Image.Image] = {}
    for size in SIZES:
        variant = clean.resize((size, size), Image.Resampling.LANCZOS)
        variants[size] = variant
        variant.save(OUTPUT / f"wodra-icon-{size}.png", optimize=True)

    variants[256].save(OUTPUT / "wodra.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES])
    print(f"Generated {len(SIZES)} PNG sizes and {len(ICO_SIZES)} ICO sizes; cleared {removed} isolated transparent-outside pixels.")


if __name__ == "__main__":
    main()
