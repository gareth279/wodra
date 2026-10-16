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

    public static HeartRateDiscoverySelection Select(IEnumerable<BleDiscoveredDevice> devices)
    {
        var compatible = devices.Where(IsCompatible)
            .GroupBy(device => device.BluetoothAddress)
            .Select(group => group.OrderByDescending(device => device.Rssi).First())
            .OrderByDescending(device => device.Rssi).ToArray();
        return compatible.Length switch
        {
            0 => new(HeartRateDiscoveryOutcome.None, compatible, null),
            1 => new(HeartRateDiscoveryOutcome.ConnectAutomatically, compatible, compatible[0]),
            _ => new(HeartRateDiscoveryOutcome.Choose, compatible, null)
        };
    }
}
