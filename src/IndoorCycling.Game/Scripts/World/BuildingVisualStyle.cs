using System;
using System.Collections.Generic;

public readonly record struct BuildingRgb(float R, float G, float B);

/// <summary>Small deterministic OSM building style palette and tag interpretation.</summary>
public static class BuildingVisualStyle
{
    private static readonly BuildingRgb[] Palette =
    [
        new(.78f,.72f,.62f), new(.70f,.65f,.57f), new(.84f,.78f,.68f),
        new(.63f,.65f,.65f), new(.72f,.70f,.65f), new(.55f,.58f,.59f),
        new(.72f,.66f,.55f), new(.64f,.49f,.39f),
        new(.48f,.52f,.53f), new(.58f,.60f,.59f)
    ];

    public static int PaletteCount => Palette.Length;
    public static bool ShouldAddFacadeDetail(double routeDistanceMeters) =>
        double.IsFinite(routeDistanceMeters) && routeDistanceMeters >= 0 && routeDistanceMeters <= 80;

    public static string Classify(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null) return "generic";
        string Get(string key) => tags.TryGetValue(key, out var value) ? value.Trim().ToLowerInvariant() : "";
        var building = Get("building");
        var amenity = Get("amenity");
        if (!string.IsNullOrEmpty(Get("historic"))) return "historic";
        if (building is "university" or "college" or "school" or "kindergarten"
            || amenity is "university" or "college" or "school" or "research_institute"
            || !string.IsNullOrEmpty(Get("isced:level")) || !string.IsNullOrEmpty(Get("education"))) return "university/education";
        if (!string.IsNullOrEmpty(Get("office"))) return "office";
        if (!string.IsNullOrEmpty(Get("shop")) || building is "retail" or "commercial" or "supermarket"
            || amenity == "marketplace") return "commercial/retail";
        if (amenity is "townhall" or "courthouse" or "library" or "community_centre" or "hospital"
            or "clinic" or "fire_station" or "police" or "place_of_worship"
            || building is "public" or "civic" or "government") return "civic/public";
        if (building is "industrial" or "warehouse" or "service" or "works" or "farm_auxiliary") return "industrial/service";
        if (building is "house" or "apartments" or "residential" or "detached" or "semidetached_house"
            or "terrace" or "bungalow" or "dormitory") return "residential";
        return "generic";
    }

    public static int MaterialVariant(long osmId, string? category)
    {
        var family = category switch
        {
            "residential" => new[] { 0, 1, 2 },
            "commercial/retail" or "office" => new[] { 3, 4, 5 },
            "university/education" or "civic/public" => new[] { 6, 7, 4 },
            "historic" => new[] { 6, 7, 1 },
            "industrial/service" => new[] { 8, 9 },
            _ => new[] { 0, 2, 4, 5, 7, 9 }
        };
        return family[(int)(StableHash($"{osmId}:{category ?? "generic"}") % (uint)family.Length)];
    }

    /// <summary>
    /// Presentation-only height variation for buildings whose OSM data has no usable
    /// height or level count. The stored source height remains unchanged.
    /// </summary>
    public static float ResolveFallbackVisualHeight(long osmId, string? category, float footprintAreaSquareMeters,
        float fallbackHeightMeters)
    {
        if (!float.IsFinite(fallbackHeightMeters) || fallbackHeightMeters <= 0
            || !float.IsFinite(footprintAreaSquareMeters) || footprintAreaSquareMeters <= 0)
            return fallbackHeightMeters;
        if (fallbackHeightMeters <= 3.25f || category == "industrial/service") return fallbackHeightMeters;

        var (minimum, maximum) = footprintAreaSquareMeters switch
        {
            < 45 => (3.1f, 4.0f),
            < 100 => (3.3f, 4.8f),
            < 240 => (3.8f, 5.8f),
            < 600 => (4.8f, 7.2f),
            _ => (5.8f, 8.6f)
        };
        // Larger public and commercial buildings can read as more substantial, but
        // keep all inferred geometry within the existing fallback envelope.
        if (category is "commercial/retail" or "office" or "university/education" or "civic/public")
        {
            minimum += .5f;
            maximum += 1.0f;
        }
        maximum = Math.Min(maximum, fallbackHeightMeters);
        minimum = Math.Min(minimum, maximum);
        return minimum + (maximum - minimum) * StableUnit($"height:{osmId}");
    }

    /// <summary>Uses mapped roof shape when present; otherwise infers only a restrained low-rise mix.</summary>
    public static string ResolveRoofShape(long osmId, string? mappedShape, string? category,
        string? heightSource, float footprintAreaSquareMeters, float footprintWidthMeters)
    {
        if (!string.IsNullOrWhiteSpace(mappedShape)) return NormalizeRoofShape(mappedShape);
        if (!string.Equals(heightSource, "Fallback", StringComparison.OrdinalIgnoreCase)
            || !float.IsFinite(footprintAreaSquareMeters) || footprintAreaSquareMeters < 28
            || !float.IsFinite(footprintWidthMeters) || footprintWidthMeters < 4
            || category == "industrial/service") return "flat";

        var roll = StableHash($"roof:{osmId}") % 10;
        if (category == "residential")
            return roll < 6 ? "gabled" : roll < 8 ? "hipped" : "flat";
        if (category == "generic" && footprintAreaSquareMeters <= 240)
            return roll < 3 ? "gabled" : roll < 5 ? "hipped" : "flat";
        if (category is "commercial/retail" or "office" or "university/education" or "civic/public"
            && footprintAreaSquareMeters < 160)
            return roll == 0 ? "gabled" : "flat";
        return "flat";
    }

    public static uint FacadePattern(long osmId) => (uint)(StableHash($"windows:{osmId}") & 0xffffffffUL);

    private static float StableUnit(string key) => (StableHash(key) & 0xffffffUL) / 16777215f;

    private static ulong StableHash(string key)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var ch in key) { hash ^= ch; hash *= 1099511628211UL; }
            return hash;
        }
    }

    public static BuildingRgb FacadeColor(int materialVariant, string? explicitColour, string? mappedMaterial = null)
    {
        if (TryParseColour(explicitColour, out var parsed)) return parsed;
        if (TryParseMaterialColour(mappedMaterial, out parsed)) return parsed;
        return Palette[Math.Clamp(materialVariant, 0, Palette.Length - 1)];
    }

    public static BuildingRgb RoofColor(int materialVariant, string? explicitColour, string? mappedMaterial = null)
    {
        if (TryParseColour(explicitColour, out var parsed)) return parsed;
        if (TryParseMaterialColour(mappedMaterial, out parsed)) return parsed;
        var facade = Palette[Math.Clamp(materialVariant, 0, Palette.Length - 1)];
        return new BuildingRgb(facade.R * .74f, facade.G * .72f, facade.B * .70f);
    }

    private static bool TryParseMaterialColour(string? material, out BuildingRgb colour)
    {
        var key = material?.Trim().ToLowerInvariant();
        colour = key switch
        {
            "brick" or "bricks" or "brickwork" => new(.57f,.34f,.27f),
            "stone" or "sandstone" => new(.68f,.62f,.49f),
            "concrete" or "cement_block" => new(.53f,.55f,.54f),
            "wood" or "timber" => new(.47f,.32f,.20f),
            "metal" or "steel" or "corrugated_iron" => new(.44f,.49f,.50f),
            "glass" => new(.40f,.52f,.56f),
            "slate" => new(.31f,.37f,.42f),
            "tiles" or "tile" => new(.52f,.30f,.22f),
            "thatch" => new(.63f,.52f,.31f),
            _ => default
        };
        return key is "brick" or "bricks" or "brickwork" or "stone" or "sandstone"
            or "concrete" or "cement_block" or "wood" or "timber" or "metal" or "steel"
            or "corrugated_iron" or "glass" or "slate" or "tiles" or "tile" or "thatch";
    }

    public static bool TryParseColour(string? value, out BuildingRgb colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text[0] == '#')
        {
            var hex = text[1..];
            if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return false;
            colour = new BuildingRgb(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
            return true;
        }
        colour = text.ToLowerInvariant() switch
        {
            "white" => new(1,1,1), "black" => new(0,0,0), "red" => new(.8f,.12f,.1f),
            "brown" => new(.42f,.25f,.16f), "grey" or "gray" => new(.5f,.5f,.5f),
            "blue" => new(.12f,.28f,.55f), "green" => new(.18f,.38f,.2f),
            "yellow" => new(.78f,.68f,.28f), "beige" => new(.76f,.68f,.52f),
            _ => default
        };
        return text.Equals("white", StringComparison.OrdinalIgnoreCase) || text.Equals("black", StringComparison.OrdinalIgnoreCase)
            || text.Equals("red", StringComparison.OrdinalIgnoreCase) || text.Equals("brown", StringComparison.OrdinalIgnoreCase)
            || text.Equals("grey", StringComparison.OrdinalIgnoreCase) || text.Equals("gray", StringComparison.OrdinalIgnoreCase)
            || text.Equals("blue", StringComparison.OrdinalIgnoreCase) || text.Equals("green", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yellow", StringComparison.OrdinalIgnoreCase) || text.Equals("beige", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeRoofShape(string? shape) => shape?.Trim().ToLowerInvariant() switch
    {
        "flat" => "flat", "gabled" or "gable" => "gabled", "hipped" or "hip" => "hipped",
        _ => "flat"
    };

    public static float ResolveRoofHeight(float totalHeight, float footprintWidth,
        string? roofHeight, string? roofLevels)
    {
        var requested = ParseMeters(roofHeight);
        if (requested <= 0 && double.TryParse(roofLevels, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var levels) && double.IsFinite(levels) && levels > 0)
            requested = levels * 3;
        if (requested <= 0) requested = Math.Clamp(footprintWidth * .18, 1.5, 2.5);
        // Existing height is the full ground-to-roof envelope.
        return (float)Math.Clamp(requested, 0, Math.Max(0, totalHeight * .4));
    }

    public static int ResolveStoreys(string? buildingLevels, float totalHeight)
    {
        if (double.TryParse(buildingLevels, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var levels) && double.IsFinite(levels) && levels > 0)
            return Math.Clamp((int)Math.Ceiling(levels), 1, 6);
        return Math.Clamp((int)Math.Ceiling(totalHeight / 3f), 1, 6);
    }

    private static double ParseMeters(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var text = value.Trim();
        if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase)) text = text[..^1].Trim();
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed > 0 ? parsed : 0;
    }
}
