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
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            var key = $"{osmId}:{category ?? "generic"}";
            foreach (var ch in key) { hash ^= ch; hash *= 1099511628211UL; }
            return family[(int)(hash % (uint)family.Length)];
        }
    }

    public static BuildingRgb FacadeColor(int materialVariant, string? explicitColour) =>
        TryParseColour(explicitColour, out var parsed) ? parsed : Palette[Math.Clamp(materialVariant, 0, Palette.Length - 1)];

    public static BuildingRgb RoofColor(int materialVariant, string? explicitColour)
    {
        if (TryParseColour(explicitColour, out var parsed)) return parsed;
        var facade = Palette[Math.Clamp(materialVariant, 0, Palette.Length - 1)];
        return new BuildingRgb(facade.R * .74f, facade.G * .72f, facade.B * .70f);
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
