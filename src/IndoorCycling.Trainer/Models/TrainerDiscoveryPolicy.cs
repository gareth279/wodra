namespace IndoorCycling.Trainer.Models;

public enum TrainerDiscoveryOutcome { None, ConnectAutomatically, Choose }

public sealed record TrainerDiscoverySelection(
    TrainerDiscoveryOutcome Outcome,
    IReadOnlyList<BleDiscoveredDevice> Candidates,
    BleDiscoveredDevice? Selected);

/// <summary>Selection policy shared by Bluetooth now and future ANT+ discovery.</summary>
public static class TrainerDiscoveryPolicy
{
    public const string FtmsServiceUuid = "00001826-0000-1000-8000-00805f9b34fb";

    public static bool IsCompatible(BleDiscoveredDevice device)
    {
        if (device.ServiceUuids.Any(uuid => string.Equals(uuid, FtmsServiceUuid, StringComparison.OrdinalIgnoreCase)))
            return true;
        var name = device.Name.Trim();
        return name.Contains("tacx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("flux", StringComparison.OrdinalIgnoreCase)
            || name.Contains("neo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("trainer", StringComparison.OrdinalIgnoreCase);
    }

    public static TrainerDiscoverySelection Select(IEnumerable<BleDiscoveredDevice> discovered, ulong? preferredAddress)
    {
        var compatible = discovered.Where(IsCompatible)
            .GroupBy(device => device.BluetoothAddress)
            .Select(group => group.OrderByDescending(device => device.Rssi).First())
            .OrderByDescending(device => device.Rssi)
            .ToArray();
        if (compatible.Length == 0)
            return new(TrainerDiscoveryOutcome.None, compatible, null);
        var preferred = preferredAddress is { } address
            ? compatible.FirstOrDefault(device => device.BluetoothAddress == address)
            : null;
        if (preferred is not null)
            return new(TrainerDiscoveryOutcome.ConnectAutomatically, compatible, preferred);
        return compatible.Length == 1
            ? new(TrainerDiscoveryOutcome.ConnectAutomatically, compatible, compatible[0])
            : new(TrainerDiscoveryOutcome.Choose, compatible, null);
    }
}
