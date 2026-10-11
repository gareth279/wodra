"""Ordering rules for overlapping LINZ elevation rasters."""


def prefer_highest_resolution(datasets):
    """Prefer the finest available DEM, with stable ordering for equal resolutions."""
    return sorted(datasets, key=lambda item: (
        abs(item[0].res[0]) * abs(item[0].res[1]),
        max(abs(item[0].res[0]), abs(item[0].res[1])),
        str(item[0].name),
    ))
