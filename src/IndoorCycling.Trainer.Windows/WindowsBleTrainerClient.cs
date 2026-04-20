using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Models;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

public sealed class WindowsBleTrainerClient : ITrainerClient
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private TrainerTelemetry _latestTelemetry = new(
        Timestamp: DateTimeOffset.UtcNow,
        PowerWatts: 0,
        CadenceRpm: 0,
        SpeedKph: 0,
        HeartRateBpm: null
    );

    private ulong? _lastSeenAddress;
    private string? _lastSeenName;

    public bool IsConnected => _device is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // For now, this just means:
        // - if we already discovered a likely trainer, connect to it
        // - later we will expand this to proper FTMS service checks
        return ConnectToLastSeenDeviceAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopScanAsync();

        _device?.Dispose();
        _device = null;

        Console.WriteLine("BLE trainer disconnected.");
    }

    public Task SetSimulationAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default)
    {
        // Stub for later FTMS write
        Console.WriteLine($"[SIM] Grade={command.GradePercent:0.0}% Rider={command.RiderWeightKg}kg Bike={command.BikeWeightKg}kg");
        return Task.CompletedTask;
    }

    public Task SetResistanceAsync(
        ResistanceCommand command,
        CancellationToken cancellationToken = default)
    {
        // Stub for later FTMS write
        Console.WriteLine($"[RESISTANCE] Level={command.ResistanceLevel:0.00}");
        return Task.CompletedTask;
    }

    public TrainerTelemetry GetLatestTelemetry()
    {
        return _latestTelemetry;
    }

    public Task StartScanAsync()
    {
        if (_watcher is not null)
        {
            return Task.CompletedTask;
        }

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
        _watcher.Start();

        Console.WriteLine("BLE scan started.");
        return Task.CompletedTask;
    }

    public Task StopScanAsync()
    {
        if (_watcher is null)
        {
            return Task.CompletedTask;
        }

        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;

        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started ||
            _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created)
        {
            _watcher.Stop();
        }

        _watcher = null;

        Console.WriteLine("BLE scan stopped.");
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BleDiscoveredDevice>> ScanForDevicesAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<ulong, BleDiscoveredDevice>();

        void Handler(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName?.Trim() ?? string.Empty;
            var uuids = args.Advertisement.ServiceUuids.Select(u => u.ToString()).ToArray();

            var item = new BleDiscoveredDevice(
                BluetoothAddress: args.BluetoothAddress,
                Name: name,
                ServiceUuids: uuids,
                Rssi: args.RawSignalStrengthInDBm
            );

            found[args.BluetoothAddress] = item;

            if (LooksLikeTrainer(name, uuids))
            {
                _lastSeenAddress = args.BluetoothAddress;
                _lastSeenName = string.IsNullOrWhiteSpace(name) ? "(unnamed device)" : name;
            }
        }

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += Handler;
        watcher.Start();

        Console.WriteLine($"Scanning for BLE devices for {duration.TotalSeconds:0} seconds...");

        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= Handler;
        }

        var results = found.Values
            .OrderByDescending(x => x.Rssi)
            .ToList();

        Console.WriteLine($"Found {results.Count} BLE device(s).");

        foreach (var d in results)
        {
            Console.WriteLine($"- {FormatAddress(d.BluetoothAddress)} | {d.Name} | RSSI {d.Rssi} | Services: {string.Join(", ", d.ServiceUuids)}");
        }

        return results;
    }

    public async Task ConnectToLastSeenDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (_lastSeenAddress is null)
        {
            Console.WriteLine("No BLE trainer candidate has been discovered yet.");
            return;
        }

        await ConnectToDeviceAsync(_lastSeenAddress.Value, cancellationToken);
    }

    public async Task ConnectToDeviceAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        _device?.Dispose();
        _device = null;

        Console.WriteLine($"Connecting to {FormatAddress(bluetoothAddress)} ...");

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);

        if (device is null)
        {
            Console.WriteLine("Failed to create BluetoothLEDevice.");
            return;
        }

        _device = device;

        Console.WriteLine($"Connected to device: {device.Name}");

        // Later:
        // - enumerate GATT services
        // - find FTMS service
        // - subscribe to indoor bike data / trainer data
        await DumpServicesAsync(cancellationToken);
    }

    public async Task DumpServicesAsync(CancellationToken cancellationToken = default)
    {
        if (_device is null)
        {
            Console.WriteLine("No BLE device connected.");
            return;
        }

        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);

        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"GetGattServicesAsync failed: {servicesResult.Status}");
            return;
        }

        Console.WriteLine($"Services for {_device.Name}:");

        foreach (var service in servicesResult.Services)
        {
            Console.WriteLine($"  Service: {service.Uuid}");

            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                Console.WriteLine($"    Could not read characteristics: {charsResult.Status}");
                continue;
            }

            foreach (var characteristic in charsResult.Characteristics)
            {
                Console.WriteLine($"    Characteristic: {characteristic.Uuid} | Properties: {characteristic.CharacteristicProperties}");
            }
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName?.Trim() ?? string.Empty;
        var uuids = args.Advertisement.ServiceUuids.Select(u => u.ToString()).ToArray();

        if (LooksLikeTrainer(name, uuids))
        {
            _lastSeenAddress = args.BluetoothAddress;
            _lastSeenName = string.IsNullOrWhiteSpace(name) ? "(unnamed device)" : name;

            Console.WriteLine($"Trainer candidate seen: {FormatAddress(args.BluetoothAddress)} | {_lastSeenName} | RSSI {args.RawSignalStrengthInDBm}");
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        Console.WriteLine($"BLE watcher stopped. Error={args.Error}");
    }

    private static bool LooksLikeTrainer(string? name, IReadOnlyList<string> serviceUuids)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();

        if (n.Contains("tacx") || n.Contains("flux") || n.Contains("neo") || n.Contains("trainer"))
        {
            return true;
        }

        // FTMS service UUID = 00001826-0000-1000-8000-00805f9b34fb
        if (serviceUuids.Any(x => x.Equals("00001826-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address).Take(6).Reverse().ToArray();
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}

public sealed record BleDiscoveredDevice(
    ulong BluetoothAddress,
    string Name,
    IReadOnlyList<string> ServiceUuids,
    short Rssi
);