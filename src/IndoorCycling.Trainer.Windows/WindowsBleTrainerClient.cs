using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Models;
//using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace IndoorCycling.Trainer.Windows
{

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

    public event Action<object>? DebugEvent;

    private GattCharacteristic? _ftmsIndoorBikeDataCharacteristic;   // 2AD2 notify
    private GattCharacteristic? _ftmsFitnessMachineStatusCharacteristic; // 2ADA notify
    private GattCharacteristic? _ftmsControlPointCharacteristic;     // 2AD9 write/indicate

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

        if (_ftmsIndoorBikeDataCharacteristic is not null)
        {
            _ftmsIndoorBikeDataCharacteristic.ValueChanged -= OnFtmsIndoorBikeDataChanged;
        }

        if (_ftmsControlPointCharacteristic is not null)
        {
            _ftmsControlPointCharacteristic.ValueChanged -= OnFtmsControlPointChanged;
        }

        _ftmsIndoorBikeDataCharacteristic = null;
        _ftmsFitnessMachineStatusCharacteristic = null;
        _ftmsControlPointCharacteristic = null;

        _device?.Dispose();
        _device = null;

        Console.WriteLine("BLE trainer disconnected.");
        EmitDebug("disconnect", new { });
    }

    public async Task SetSimulationAsync(
    SimulationCommand command,
    CancellationToken cancellationToken = default)
    {
        if (_ftmsControlPointCharacteristic is null)
        {
            Console.WriteLine("FTMS Control Point characteristic is not resolved. Cannot set simulation.");
            EmitDebug("set_simulation_skipped", new
            {
                reason = "control_point_not_resolved",
                gradePercent = command.GradePercent,
                riderWeightKg = command.RiderWeightKg,
                bikeWeightKg = command.BikeWeightKg
            });
            return;
        }

        // FTMS Set Indoor Bike Simulation Parameters
        // Opcode 0x11
        // Wind speed: sint16, little-endian
        // Grade: sint16, usually grade percent * 100
        // Crr: uint8
        // Wind resistance coefficient: uint8
        short windSpeedRaw = 0;
        short gradeRaw = (short)Math.Clamp(
            Math.Round(command.GradePercent * 100.0),
            short.MinValue,
            short.MaxValue
        );

        // Reasonable default road-ish values.
        // These may need tuning by trainer model.
        byte rollingResistanceRaw = 40;
        byte windResistanceRaw = 51;

        var payload = new byte[]
        {
            0x11,

            (byte)(windSpeedRaw & 0xFF),
            (byte)((windSpeedRaw >> 8) & 0xFF),

            (byte)(gradeRaw & 0xFF),
            (byte)((gradeRaw >> 8) & 0xFF),

            rollingResistanceRaw,
            windResistanceRaw
        };

        Console.WriteLine(
            $"[SIM WRITE] Grade={command.GradePercent:0.0}% RawGrade={gradeRaw} Payload={BitConverter.ToString(payload)}");

        EmitDebug("set_simulation", new
        {
            gradePercent = command.GradePercent,
            gradeRaw,
            windSpeedRaw,
            rollingResistanceRaw,
            windResistanceRaw,
            payload = BitConverter.ToString(payload)
        });

        await WriteControlPointAsync(payload, cancellationToken);
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

        EmitDebug("connect", new
        {
            address = FormatAddress(bluetoothAddress),
            name = device.Name
        });

        await ResolveFtmsCharacteristicsAsync(cancellationToken);

        // Later:
        // - enumerate GATT services
        // - find FTMS service
        // - subscribe to indoor bike data / trainer data
        //await DumpServicesAsync(cancellationToken);
    }


    public async Task ResolveFtmsCharacteristicsAsync(CancellationToken cancellationToken = default)
    {
        if (_device is null)
        {
            Console.WriteLine("No BLE device connected.");
            return;
        }

        var ftmsUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
        var indoorBikeDataUuid = Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb");
        var fitnessMachineStatusUuid = Guid.Parse("00002ada-0000-1000-8000-00805f9b34fb");
        var fitnessMachineControlPointUuid = Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb");

        var servicesResult = await _device.GetGattServicesForUuidAsync(ftmsUuid, BluetoothCacheMode.Cached);

        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
        {
            Console.WriteLine($"Could not access FTMS service: {servicesResult.Status}");
            return;
        }

        var ftmsService = servicesResult.Services[0];
        Console.WriteLine("FTMS service found.");
        EmitDebug("ftms_service_found", new { serviceUuid = ftmsUuid });

        var charsResult = await ftmsService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);

        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"Could not access FTMS characteristics: {charsResult.Status}");
            return;
        }

        _ftmsIndoorBikeDataCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == indoorBikeDataUuid);
        _ftmsFitnessMachineStatusCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == fitnessMachineStatusUuid);
        _ftmsControlPointCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == fitnessMachineControlPointUuid);

        Console.WriteLine($"Indoor Bike Data char found: {_ftmsIndoorBikeDataCharacteristic is not null}");
        Console.WriteLine($"Fitness Machine Status char found: {_ftmsFitnessMachineStatusCharacteristic is not null}");
        Console.WriteLine($"Control Point char found: {_ftmsControlPointCharacteristic is not null}");

        EmitDebug("ftms_characteristics_resolved", new
        {
            indoorBikeData = _ftmsIndoorBikeDataCharacteristic is not null,
            fitnessMachineStatus = _ftmsFitnessMachineStatusCharacteristic is not null,
            controlPoint = _ftmsControlPointCharacteristic is not null
        });
    }

    public async Task SubscribeFtmsTelemetryAsync(CancellationToken cancellationToken = default)
    {
        if (_ftmsIndoorBikeDataCharacteristic is null)
        {
            Console.WriteLine("FTMS Indoor Bike Data characteristic is not resolved.");
            return;
        }

        _ftmsIndoorBikeDataCharacteristic.ValueChanged -= OnFtmsIndoorBikeDataChanged;
        _ftmsIndoorBikeDataCharacteristic.ValueChanged += OnFtmsIndoorBikeDataChanged;

        var status = await _ftmsIndoorBikeDataCharacteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

        Console.WriteLine($"Subscribe FTMS telemetry result: {status}");
        EmitDebug("subscribe_ftms_telemetry", new { status = status.ToString() });
    }

    public async Task SubscribeControlPointAsync(CancellationToken cancellationToken = default)
    {
        if (_ftmsControlPointCharacteristic is null)
        {
            Console.WriteLine("FTMS Control Point characteristic is not resolved.");
            return;
        }

        _ftmsControlPointCharacteristic.ValueChanged -= OnFtmsControlPointChanged;
        _ftmsControlPointCharacteristic.ValueChanged += OnFtmsControlPointChanged;

        var status = await _ftmsControlPointCharacteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate);

        Console.WriteLine($"Subscribe FTMS control point result: {status}");
        EmitDebug("subscribe_control_point", new { status = status.ToString() });
    }

    public async Task RequestControlAsync(CancellationToken cancellationToken = default)
    {
        await WriteControlPointAsync(new byte[] { 0x00 }, cancellationToken);
    }

    public async Task SetTargetPowerAsync(short watts, CancellationToken cancellationToken = default)
    {
        var payload = new byte[]
        {
            0x05,
            (byte)(watts & 0xFF),
            (byte)((watts >> 8) & 0xFF)
        };

        await WriteControlPointAsync(payload, cancellationToken);
    }

    public async Task RampTargetPowerAsync(
        short startWatts,
        short endWatts,
        short stepWatts,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (stepWatts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepWatts), "stepWatts must be > 0");
        }

        if (startWatts <= endWatts)
        {
            for (short w = startWatts; w <= endWatts; w += stepWatts)
            {
                Console.WriteLine($"Setting target power to {w} W");
                await SetTargetPowerAsync(w, cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }
        else
        {
            for (short w = startWatts; w >= endWatts; w -= stepWatts)
            {
                Console.WriteLine($"Setting target power to {w} W");
                await SetTargetPowerAsync(w, cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task WriteControlPointAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (_ftmsControlPointCharacteristic is null)
        {
            Console.WriteLine("FTMS Control Point characteristic is not resolved.");
            return;
        }

        var writer = new global::Windows.Storage.Streams.DataWriter();
        writer.WriteBytes(payload);

        var status = await _ftmsControlPointCharacteristic.WriteValueAsync(
            writer.DetachBuffer(),
            GattWriteOption.WriteWithResponse);

        Console.WriteLine($"Control point write [{BitConverter.ToString(payload)}] => {status}");
        EmitDebug("control_point_write", new
        {
            payload = BitConverter.ToString(payload),
            status = status.ToString()
        });
    }

    private void OnFtmsControlPointChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            Console.WriteLine($"FTMS Control Point response: {BitConverter.ToString(data)}");
            EmitDebug("control_point_response", new
            {
                data = BitConverter.ToString(data)
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading control point indication: {ex.Message}");
            EmitDebug("control_point_error", new
            {
                message = ex.Message
            });
        }
    }

    private void OnFtmsIndoorBikeDataChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            if (data.Length < 8)
            {
                Console.WriteLine($"FTMS packet too short: {BitConverter.ToString(data)}");
                EmitDebug("ftms_packet_too_short", new
                {
                    data = BitConverter.ToString(data)
                });
                return;
            }

            var flags = BitConverter.ToUInt16(data, 0);
            var speedRaw = BitConverter.ToUInt16(data, 2);
            var cadenceRaw = BitConverter.ToUInt16(data, 4);
            var powerRaw = BitConverter.ToInt16(data, 6);

            var speedKph = speedRaw / 100.0;
            var cadenceRpm = (int)Math.Round(cadenceRaw / 2.0);
            var powerWatts = (int)powerRaw;

            var timestamp = DateTimeOffset.UtcNow;

            _latestTelemetry = new TrainerTelemetry(
                Timestamp: timestamp,
                PowerWatts: powerWatts,
                CadenceRpm: cadenceRpm,
                SpeedKph: speedKph,
                HeartRateBpm: null
            );

            Console.WriteLine(
                $"FTMS flags=0x{flags:X4} speed={speedKph:0.0} kph cadence={cadenceRpm} rpm power={powerWatts} W");

            EmitDebug("ftms_telemetry", new
            {
                timestamp,
                flags = $"0x{flags:X4}",
                speedKph,
                cadenceRpm,
                powerWatts
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing FTMS notification: {ex.Message}");
            EmitDebug("ftms_parse_error", new
            {
                message = ex.Message
            });
        }
    }

    public async Task DumpServicesAsync(CancellationToken cancellationToken = default)
    {
        if (_device is null)
        {
            Console.WriteLine("No BLE device connected.");
            return;
        }

        //var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Cached);

        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"GetGattServicesAsync failed: {servicesResult.Status}");
            return;
        }

        Console.WriteLine($"Services for {_device.Name}:");

        foreach (var service in servicesResult.Services)
        {
            Console.WriteLine($"  Service: {service.Uuid}");

            //var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Cached);

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
            EmitDebug("trainer_candidate_seen", new
            {
                address = FormatAddress(args.BluetoothAddress),
                name = _lastSeenName,
                rssi = args.RawSignalStrengthInDBm
            });
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        Console.WriteLine($"BLE watcher stopped. Error={args.Error}");
        EmitDebug("watcher_stopped", new
        {
            error = args.Error.ToString()
        });
    }


    private void EmitDebug(string type, object payload)
    {
        DebugEvent?.Invoke(new
        {
            type,
            ts = DateTimeOffset.UtcNow,
            payload
        });
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
}