namespace IndoorCycling.Trainer.Models;

public enum SteeringDiscoveryOutcome { None, ConnectAutomatically, Choose }

public sealed record SteeringDiscoverySelection(
    SteeringDiscoveryOutcome Outcome,
    IReadOnlyList<BleDiscoveredDevice> Candidates,
    BleDiscoveredDevice? Selected);

/// <summary>Filters supported Elite Sterzo Smart advertisements and prefers the rider's saved identity.</summary>
public static class SteeringDiscoveryPolicy
{
    public const string ServiceUuid = "347b0001-7635-408b-8918-8ff3949ce592";

    public static bool IsCompatible(BleDiscoveredDevice device) =>
        device.ServiceUuids.Any(uuid => string.Equals(uuid, ServiceUuid, StringComparison.OrdinalIgnoreCase)) ||
        device.Name.Contains("sterzo", StringComparison.OrdinalIgnoreCase);

    public static SteeringDiscoverySelection Select(IEnumerable<BleDiscoveredDevice> devices, ulong? preferredAddress = null)
    {
        var compatible = devices.Where(IsCompatible)
            .GroupBy(device => device.BluetoothAddress)
            .Select(group => group.OrderByDescending(device => device.Rssi).First())
            .OrderByDescending(device => device.Rssi).ToArray();
        var preferred = preferredAddress is { } address
            ? compatible.FirstOrDefault(device => device.BluetoothAddress == address)
            : null;
        if (preferred is not null)
            return new(SteeringDiscoveryOutcome.ConnectAutomatically, compatible, preferred);
        return compatible.Length switch
        {
            0 => new(SteeringDiscoveryOutcome.None, compatible, null),
            1 => new(SteeringDiscoveryOutcome.ConnectAutomatically, compatible, compatible[0]),
            _ => new(SteeringDiscoveryOutcome.Choose, compatible, null)
        };
    }
}
