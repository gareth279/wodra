# Wodra 0.1.0-alpha.7

## Route surface and scenery alignment

- Background OSM street ribbons are now sampled from the finest valid cached elevation raster and interpolated at pixel centers. This fixes routes being displaced vertically when coarse regional elevation tiles were chosen ahead of detailed local tiles.
- Regenerated the baked street elevation sidecars for all four built-in routes and marked the sampling contract as version 2 so route packages can identify the corrected data.
- Tree canopy placement now uses each canopy mesh's actual bounds, joining broadleaf and conifer canopies to their trunks.
- Route chainage, road elevation profiles, trainer gradient, and FIT recording are unchanged.

## Verification status

Automated raster-priority/interpolation, route geometry, tree bounds, route packaging, and physics regressions pass. Real exported-build visual verification on the cycling PC is still pending. Please retest the South Coastal markers listed in the release announcement/issues; issues #17 and #18 remain open pending that ride check.
