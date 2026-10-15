using System.Text.Json.Serialization;
using System.Text.Json;

namespace IndoorCycling.Trainer.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrainerProtocolPreference
{
    Auto,
    BluetoothFtms,
    AntFeC
}

public enum TrainerProtocol
{
    BluetoothFtms,
    AntFeC
}

public sealed class TrainerSettings
{
    public TrainerProtocolPreference ProtocolPreference { get; set; } = TrainerProtocolPreference.Auto;
    public ushort? LastAntDeviceNumber { get; set; }
    public byte? LastAntDeviceType { get; set; }
    public byte? LastAntTransmissionType { get; set; }
}

public sealed record TrainerProtocolSelection(TrainerProtocol? Protocol, string? UnavailableReason)
{
    public bool IsAvailable => Protocol is not null;
}

public static class TrainerProtocolPreferencePolicy
{
    /// <summary>Old Wodra settings had only Bluetooth support, so keep existing riders on Bluetooth.</summary>
    public static bool MigrateLegacyPreference(string settingsJson, TrainerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settingsJson);
        ArgumentNullException.ThrowIfNull(settings);
        var hasPersistedPreference = false;
        using (var document = JsonDocument.Parse(settingsJson))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var trainer = document.RootElement.EnumerateObject()
                    .FirstOrDefault(property => string.Equals(property.Name, "Trainer", StringComparison.OrdinalIgnoreCase));
                if (trainer.Value.ValueKind == JsonValueKind.Object)
                    hasPersistedPreference = trainer.Value.EnumerateObject()
                        .Any(property => string.Equals(property.Name, "ProtocolPreference", StringComparison.OrdinalIgnoreCase));
            }
        }
        if (hasPersistedPreference) return false;
        settings.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
        return true;
    }

    public static void Normalize(TrainerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.ProtocolPreference))
            settings.ProtocolPreference = TrainerProtocolPreference.Auto;
    }

    /// <summary>Auto has a stable Bluetooth-first order; explicit choices never silently change protocol.</summary>
    public static TrainerProtocolSelection Select(
        TrainerProtocolPreference preference,
        bool bluetoothAvailable,
        bool antFeCAvailable)
    {
        return preference switch
        {
            TrainerProtocolPreference.Auto when bluetoothAvailable => new(TrainerProtocol.BluetoothFtms, null),
            TrainerProtocolPreference.Auto when antFeCAvailable => new(TrainerProtocol.AntFeC, null),
            TrainerProtocolPreference.Auto => new(null, "No supported trainer protocol is available."),
            TrainerProtocolPreference.BluetoothFtms when bluetoothAvailable => new(TrainerProtocol.BluetoothFtms, null),
            TrainerProtocolPreference.BluetoothFtms => new(null, "Bluetooth FTMS is unavailable. Choose Auto or another available protocol."),
            TrainerProtocolPreference.AntFeC when antFeCAvailable => new(TrainerProtocol.AntFeC, null),
            TrainerProtocolPreference.AntFeC => new(null, "ANT+ FE-C is unavailable. Choose Auto or another available protocol."),
            _ => new(null, "The saved trainer protocol preference is invalid.")
        };
    }
}
