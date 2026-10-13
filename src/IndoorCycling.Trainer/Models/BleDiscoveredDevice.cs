namespace IndoorCycling.Trainer.Models;

/// <summary>A Bluetooth LE advertisement that can be evaluated for trainer use.</summary>
public sealed record BleDiscoveredDevice(
    ulong BluetoothAddress,
    string Name,
    IReadOnlyList<string> ServiceUuids,
    short Rssi);
