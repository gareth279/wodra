namespace IndoorCycling.Trainer.Models;

public enum HeartRateDiscoveryOutcome { None, ConnectAutomatically, Choose }

public sealed record HeartRateDiscoverySelection(
    HeartRateDiscoveryOutcome Outcome,
    IReadOnlyList<BleDiscoveredDevice> Candidates,
    BleDiscoveredDevice? Selected);

public static class HeartRateDiscoveryPolicy
{
    public const string ServiceUuid = "0000180d-0000-1000-8000-00805f9b34fb";

    public static bool IsCompatible(BleDiscoveredDevice device) =>
        device.ServiceUuids.Any(uuid => string.Equals(uuid, ServiceUuid, StringComparison.OrdinalIgnoreCase));

    public static HeartRateDiscoverySelection Select(IEnumerable<BleDiscoveredDevice> devices, ulong? preferredAddress = null)
    {
        var compatible = devices.Where(IsCompatible)
            .GroupBy(device => device.BluetoothAddress)
            .Select(group => group.OrderByDescending(device => device.Rssi).First())
            .OrderByDescending(device => device.Rssi).ToArray();
        var preferred = preferredAddress is { } address
            ? compatible.FirstOrDefault(device => device.BluetoothAddress == address)
            : null;
        if (preferred is not null)
            return new(HeartRateDiscoveryOutcome.ConnectAutomatically, compatible, preferred);
        return compatible.Length switch
        {
            0 => new(HeartRateDiscoveryOutcome.None, compatible, null),
            1 => new(HeartRateDiscoveryOutcome.ConnectAutomatically, compatible, compatible[0]),
            _ => new(HeartRateDiscoveryOutcome.Choose, compatible, null)
        };
    }
}
