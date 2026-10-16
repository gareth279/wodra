using System.Text.Json.Serialization;

namespace IndoorCycling.Trainer.Models;

/// <summary>Per-category protocol preference. Unsupported values are rejected by each category's policy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceProtocolPreference
{
    Auto = 0,
    Bluetooth = 1,
    AntPlus = 2
}

public enum DeviceProtocolCategory
{
    Trainer,
    HeartRate,
    Steering
}

public sealed class OptionalDeviceProtocolPreferences
{
    public DeviceProtocolPreference HeartRate { get; set; } = DeviceProtocolPreference.Auto;
    public DeviceProtocolPreference Steering { get; set; } = DeviceProtocolPreference.Auto;

    public DeviceProtocolPreference Get(DeviceProtocolCategory category) => category switch
    {
        DeviceProtocolCategory.HeartRate => HeartRate,
        DeviceProtocolCategory.Steering => Steering,
        _ => throw new ArgumentOutOfRangeException(nameof(category), "Trainer preference remains in TrainerSettings.")
    };

    public void Set(DeviceProtocolCategory category, DeviceProtocolPreference value)
    {
        switch (category)
        {
            case DeviceProtocolCategory.HeartRate:
                HeartRate = DeviceProtocolPreferencePolicy.Normalize(value, category);
                break;
            case DeviceProtocolCategory.Steering:
                Steering = DeviceProtocolPreferencePolicy.Normalize(value, category);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category), "Trainer preference remains in TrainerSettings.");
        }
    }
}

public static class DeviceProtocolPreferencePolicy
{
    public static bool IsSupported(DeviceProtocolPreference preference, DeviceProtocolCategory category) => preference switch
    {
        DeviceProtocolPreference.Auto or DeviceProtocolPreference.Bluetooth => true,
        DeviceProtocolPreference.AntPlus => category == DeviceProtocolCategory.Trainer,
        _ => false
    };

    public static DeviceProtocolPreference Normalize(DeviceProtocolPreference preference, DeviceProtocolCategory category) =>
        IsSupported(preference, category) ? preference : DeviceProtocolPreference.Auto;

    public static DeviceProtocolPreference MigrateTrainer(TrainerProtocolPreference oldPreference) => oldPreference switch
    {
        TrainerProtocolPreference.BluetoothFtms => DeviceProtocolPreference.Bluetooth,
        TrainerProtocolPreference.AntFeC => DeviceProtocolPreference.AntPlus,
        _ => DeviceProtocolPreference.Auto
    };
}

/// <summary>Decoder and protocol constants for the Elite Sterzo Smart BLE service.</summary>
public static class SterzoSmartProtocol
{
    public const string ServiceUuid = "347b0001-7635-408b-8918-8ff3949ce592";
    public const string SteeringAngleCharacteristicUuid = "347b0030-7635-408b-8918-8ff3949ce592";
    public const string ControlPointCharacteristicUuid = "347b0031-7635-408b-8918-8ff3949ce592";
    public const string ChallengeCharacteristicUuid = "347b0032-7635-408b-8918-8ff3949ce592";

    public static float? ParseAngle(ReadOnlySpan<byte> value)
    {
        if (value.Length < sizeof(float)) return null;
        var angle = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value));
        return float.IsFinite(angle) && Math.Abs(angle) <= 90 ? angle : null;
    }

    public static ushort? ParseChallenge(ReadOnlySpan<byte> value) => value.Length >= 4
        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2))
        : null;

    public static bool TryGetChallengeResponse(ReadOnlySpan<byte> table, ushort challenge, out byte first, out byte second)
    {
        var offset = challenge * 2;
        if (offset + 1 >= table.Length) { first = second = 0; return false; }
        first = table[offset];
        second = table[offset + 1];
        return true;
    }
}
