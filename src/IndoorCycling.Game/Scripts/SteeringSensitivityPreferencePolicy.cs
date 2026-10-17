using System;
using System.Linq;
using System.Text.Json;
using IndoorCycling.Core.Models;

/// <summary>One-time migration for the alpha.36 shared sensitivity default.</summary>
public static class SteeringSensitivityPreferencePolicy
{
    public const int CurrentVersion = 1;

    public static bool Migrate(string settingsJson, SteeringControlSettings steering, int savedVersion)
    {
        if (savedVersion >= CurrentVersion) return false;

        using var document = JsonDocument.Parse(settingsJson);
        var steeringElement = FindProperty(document.RootElement, "Steering");
        var sensitivityElement = steeringElement is { ValueKind: JsonValueKind.Object } value
            ? FindProperty(value, "Sensitivity")
            : null;
        if (sensitivityElement is { ValueKind: JsonValueKind.Number } saved &&
            saved.TryGetDouble(out var oldSensitivity) && Math.Abs(oldSensitivity - 1.0) < 1e-9)
        {
            steering.Sensitivity = SteeringControlSettings.DefaultSensitivity;
        }

        // Persist the new version even when the value was custom or absent, so explicit 1.0× choices stay put.
        return true;
    }

    private static JsonElement? FindProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value
            : null;
}
