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

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public event Action? Disconnected;

    public event Action<object>? DebugEvent;

    private GattCharacteristic? _ftmsIndoorBikeDataCharacteristic;   // 2AD2 notify
    private GattCharacteristic? _ftmsFitnessMachineStatusCharacteristic; // 2ADA notify
    private GattCharacteristic? _ftmsControlPointCharacteristic;     // 2AD9 write/indicate
    private (double Minimum, double Maximum)? _supportedInclinationRangePercent;
    private TaskCompletionSource<byte[]>? _pendingControlPointResponse;
    public double? LastRequestedSimulationGradePercent { get; private set; }
    public double? LastAppliedSimulationGradePercent { get; private set; }
    private readonly SemaphoreSlim _controlPointWriteLock = new(1, 1);

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
        _supportedInclinationRangePercent = null;
        LastRequestedSimulationGradePercent = null;
        LastAppliedSimulationGradePercent = null;
        _pendingControlPointResponse?.TrySetCanceled(cancellationToken);
        _pendingControlPointResponse = null;

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
        }
        _device = null;

        Console.WriteLine("BLE trainer disconnected.");
        EmitDebug("disconnect", new { });
    }

    public async Task SetSimulationAsync(
    SimulationCommand command,
    CancellationToken cancellationToken = default)
    {
        LastRequestedSimulationGradePercent = command.GradePercent;
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
        short gradeRaw = FtmsSimulationGrade.Encode(command.GradePercent, _supportedInclinationRangePercent);
        var appliedGradePercent = gradeRaw / 100.0;

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
            $"[SIM WRITE] Requested={command.GradePercent:0.00}% Applied={appliedGradePercent:0.00}% RawGrade={gradeRaw} Payload={BitConverter.ToString(payload)}");

        EmitDebug("set_simulation", new
        {
            requestedGradePercent = command.GradePercent,
            appliedGradePercent,
            gradeRaw,
            supportedInclinationRangePercent = _supportedInclinationRangePercent,
            windSpeedRaw,
            rollingResistanceRaw,
            windResistanceRaw,
            payload = BitConverter.ToString(payload)
        });

        await WriteControlPointAsync(payload, cancellationToken);
        LastAppliedSimulationGradePercent = appliedGradePercent;
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
        _lastSeenAddress = null;
        _lastSeenName = null;
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

        var compatible = results.Where(x => LooksLikeTrainer(x.Name, x.ServiceUuids)).ToList();
        if (compatible.Count > 0)
        {
            _lastSeenAddress = compatible[0].BluetoothAddress;
            _lastSeenName = string.IsNullOrWhiteSpace(compatible[0].Name) ? "(unnamed device)" : compatible[0].Name;
            EmitDebug("trainer_selected", new { address = FormatAddress(compatible[0].BluetoothAddress), name = _lastSeenName, rssi = compatible[0].Rssi, services = compatible[0].ServiceUuids });
        }
        else EmitDebug("trainer_not_found", new { discoveredDeviceCount = results.Count });

        foreach (var candidate in compatible)
            EmitDebug("trainer_discovered", new { address = FormatAddress(candidate.BluetoothAddress), name = candidate.Name, rssi = candidate.Rssi, services = candidate.ServiceUuids });

        Console.WriteLine($"Found {results.Count} BLE device(s), {compatible.Count} compatible trainer(s).");

        foreach (var d in results)
        {
            Console.WriteLine($"- {FormatAddress(d.BluetoothAddress)} | {d.Name} | RSSI {d.Rssi} | Services: {string.Join(", ", d.ServiceUuids)}");
        }

        return compatible;
    }

    public async Task ConnectToLastSeenDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (_lastSeenAddress is null)
        {
            EmitDebug("trainer_connection_stage_failed", new { stage = "selection", reason = "no_compatible_trainer" });
            throw new InvalidOperationException("No compatible trainer was found during scanning.");
        }

        await ConnectToDeviceAsync(_lastSeenAddress.Value, cancellationToken);
    }

    public async Task ConnectToDeviceAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
        }
        _device = null;

        var stage = "ble_device_creation";
        EmitDebug("trainer_connection_stage_started", new { stage, address = FormatAddress(bluetoothAddress) });
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);

            if (device is null)
                throw new InvalidOperationException("Windows could not create a BLE device for the discovered trainer.");

            _device = device;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            EmitDebug("ble_device_created", new { address = FormatAddress(bluetoothAddress), name = device.Name, connectionStatus = device.ConnectionStatus.ToString() });
            Console.WriteLine($"BLE device created: {device.Name}, status={device.ConnectionStatus}");

            // Windows does not necessarily establish the BLE link when the device
            // object is created. Uncached GATT discovery is the operation that
            // initiates the connection, so do that before requiring Connected.
            stage = "ftms_gatt_service_discovery";
            EmitDebug("trainer_connection_stage_started", new { stage, address = FormatAddress(bluetoothAddress), connectionStatus = device.ConnectionStatus.ToString() });
            await ResolveFtmsCharacteristicsAsync(cancellationToken);
            EnsureConnected();
            EmitDebug("connect", new { address = FormatAddress(bluetoothAddress), name = device.Name, connectionStatus = device.ConnectionStatus.ToString() });
        }
        catch (Exception ex)
        {
            EmitDebug("trainer_connection_stage_failed", new { stage, reason = ex.Message, exceptionType = ex.GetType().FullName, connectionStatus = _device?.ConnectionStatus.ToString() });
            if (_device is not null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
                _device = null;
            }
            throw;
        }

        // Later:
        // - enumerate GATT services
        // - find FTMS service
        // - subscribe to indoor bike data / trainer data
        //await DumpServicesAsync(cancellationToken);
    }


    public async Task ResolveFtmsCharacteristicsAsync(CancellationToken cancellationToken = default)
    {
        var device = _device;
        if (device is null)
        {
            throw new InvalidOperationException("Cannot configure FTMS because the trainer is disconnected.");
        }

        var ftmsUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
        var indoorBikeDataUuid = Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb");
        var fitnessMachineStatusUuid = Guid.Parse("00002ada-0000-1000-8000-00805f9b34fb");
        var fitnessMachineControlPointUuid = Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb");
        var supportedInclinationRangeUuid = Guid.Parse("00002ad5-0000-1000-8000-00805f9b34fb");

        GattDeviceServicesResult servicesResult;
        try
        {
            servicesResult = await GattConnectionSequence.DiscoverAndWaitForConnectionAsync(
                async token =>
                {
                    EmitDebug("gatt_discovery_started", new { stage = "ftms_service", serviceUuid = ftmsUuid, cacheMode = "Uncached", connectionStatus = device.ConnectionStatus.ToString() });
                    var result = await device.GetGattServicesForUuidAsync(ftmsUuid, BluetoothCacheMode.Uncached)
                        .AsTask(token)
                        .WaitAsync(TimeSpan.FromSeconds(15), token);
                    EmitDebug("gatt_discovery_result", new { stage = "ftms_service", status = result.Status.ToString(), serviceCount = result.Services.Count, communicationError = result.ProtocolError, connectionStatus = device.ConnectionStatus.ToString() });
                    if (result.Status != GattCommunicationStatus.Success)
                        throw new InvalidOperationException($"FTMS GATT service discovery failed ({result.Status}, protocol error {result.ProtocolError?.ToString() ?? "none"}).");
                    return result;
                },
                () => device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                TimeSpan.FromSeconds(18),
                TimeSpan.FromSeconds(6),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitDebug("gatt_discovery_error", new { stage = "ftms_service", reason = ex.Message, exceptionType = ex.GetType().FullName, connectionStatus = device.ConnectionStatus.ToString() });
            throw;
        }

        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
        {
            Console.WriteLine($"Could not access FTMS service: {servicesResult.Status}");
            throw new InvalidOperationException($"Trainer FTMS service is unavailable ({servicesResult.Status}).");
        }

        var ftmsService = servicesResult.Services[0];
        Console.WriteLine("FTMS service found.");
        EmitDebug("ftms_service_found", new { serviceUuid = ftmsUuid });

        EmitDebug("gatt_discovery_started", new { stage = "ftms_characteristics", serviceUuid = ftmsUuid, cacheMode = "Uncached" });
        var charsResult = await ftmsService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
        EmitDebug("gatt_discovery_result", new { stage = "ftms_characteristics", status = charsResult.Status.ToString(), characteristicCount = charsResult.Characteristics.Count, communicationError = charsResult.ProtocolError });

        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"Could not access FTMS characteristics: {charsResult.Status}");
            throw new InvalidOperationException($"Could not read trainer FTMS characteristics ({charsResult.Status}).");
        }

        _ftmsIndoorBikeDataCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == indoorBikeDataUuid);
        _ftmsFitnessMachineStatusCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == fitnessMachineStatusUuid);
        _ftmsControlPointCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == fitnessMachineControlPointUuid);
        var supportedInclinationRange = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == supportedInclinationRangeUuid);
        _supportedInclinationRangePercent = await ReadSupportedInclinationRangeAsync(supportedInclinationRange, cancellationToken);

        EmitDebug("ftms_required_characteristics", new
        {
            indoorBikeDataFound = _ftmsIndoorBikeDataCharacteristic is not null,
            indoorBikeDataProperties = _ftmsIndoorBikeDataCharacteristic?.CharacteristicProperties.ToString(),
            controlPointFound = _ftmsControlPointCharacteristic is not null,
            controlPointProperties = _ftmsControlPointCharacteristic?.CharacteristicProperties.ToString()
        });

        if (_ftmsIndoorBikeDataCharacteristic is null)
            throw new InvalidOperationException("Trainer is missing the required FTMS Indoor Bike Data characteristic.");
        if (_ftmsControlPointCharacteristic is null)
            throw new InvalidOperationException("Trainer is missing the required FTMS Control Point characteristic.");

        Console.WriteLine($"Indoor Bike Data char found: {_ftmsIndoorBikeDataCharacteristic is not null}");
        Console.WriteLine($"Fitness Machine Status char found: {_ftmsFitnessMachineStatusCharacteristic is not null}");
        Console.WriteLine($"Control Point char found: {_ftmsControlPointCharacteristic is not null}");

        EmitDebug("ftms_characteristics_resolved", new
        {
            indoorBikeData = _ftmsIndoorBikeDataCharacteristic is not null,
            fitnessMachineStatus = _ftmsFitnessMachineStatusCharacteristic is not null,
            controlPoint = _ftmsControlPointCharacteristic is not null,
            supportedInclinationRangePercent = _supportedInclinationRangePercent
        });
    }

    private static async Task<(double Minimum, double Maximum)?> ReadSupportedInclinationRangeAsync(
        GattCharacteristic? characteristic, CancellationToken cancellationToken)
    {
        if (characteristic is null) return null;
        try
        {
            var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (result.Status != GattCommunicationStatus.Success || result.Value.Length < 6) return null;
            var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
            var bytes = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(bytes);
            reader.Dispose();
            return FtmsSimulationGrade.TryDecodeSupportedInclinationRange(bytes, out var range) ? range : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Could not read FTMS Supported Inclination Range: {ex.Message}");
            return null;
        }
    }

    public async Task SubscribeFtmsTelemetryAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_ftmsIndoorBikeDataCharacteristic is null)
        {
            throw new InvalidOperationException("FTMS Indoor Bike Data characteristic is not resolved.");
        }

        _ftmsIndoorBikeDataCharacteristic.ValueChanged -= OnFtmsIndoorBikeDataChanged;
        _ftmsIndoorBikeDataCharacteristic.ValueChanged += OnFtmsIndoorBikeDataChanged;

        var status = await _ftmsIndoorBikeDataCharacteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Console.WriteLine($"Subscribe FTMS telemetry result: {status}");
        EmitDebug("subscribe_ftms_telemetry", new { status = status.ToString() });
        EmitDebug(status == GattCommunicationStatus.Success ? "ftms_telemetry_subscription_succeeded" : "ftms_telemetry_subscription_failed", new { status = status.ToString(), communicationError = status.ToString() });
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Trainer telemetry subscription failed ({status}).");
    }

    public async Task SubscribeControlPointAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_ftmsControlPointCharacteristic is null)
        {
            throw new InvalidOperationException("FTMS Control Point characteristic is not resolved.");
        }

        _ftmsControlPointCharacteristic.ValueChanged -= OnFtmsControlPointChanged;
        _ftmsControlPointCharacteristic.ValueChanged += OnFtmsControlPointChanged;

        var status = await _ftmsControlPointCharacteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate)
            .AsTask(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Console.WriteLine($"Subscribe FTMS control point result: {status}");
        EmitDebug("subscribe_control_point", new { status = status.ToString() });
        EmitDebug(status == GattCommunicationStatus.Success ? "ftms_control_subscription_succeeded" : "ftms_control_subscription_failed", new { status = status.ToString(), communicationError = status.ToString() });
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Trainer control subscription failed ({status}).");
    }

    public async Task RequestControlAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlPointResponse = response;
        try
        {
            await WriteControlPointAsync(new byte[] { 0x00 }, cancellationToken);
            var data = await response.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (data.Length < 3 || data[0] != 0x80 || data[1] != 0x00 || data[2] != 0x01)
            {
                EmitDebug("ftms_control_acknowledgement_failed", new { response = BitConverter.ToString(data), opcode = data.Length > 1 ? data[1] : (byte?)null, result = data.Length > 2 ? data[2] : (byte?)null });
                throw new InvalidOperationException($"Trainer rejected FTMS control request ({BitConverter.ToString(data)}).");
            }
            EmitDebug("ftms_control_acknowledgement_succeeded", new { opcode = (byte)0x00, result = (byte)0x01 });
        }
        catch (TimeoutException ex)
        {
            EmitDebug("ftms_control_acknowledgement_failed", new { reason = "timeout", timeoutSeconds = 5 });
            throw new InvalidOperationException("Trainer did not acknowledge the FTMS control request.", ex);
        }
        finally
        {
            if (ReferenceEquals(_pendingControlPointResponse, response)) _pendingControlPointResponse = null;
        }
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
        EnsureConnected();
        if (_ftmsControlPointCharacteristic is null)
            throw new InvalidOperationException("FTMS Control Point characteristic is not resolved.");

        await _controlPointWriteLock.WaitAsync(cancellationToken);

        try
        {
            var writer = new global::Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(payload);

            var status = await _ftmsControlPointCharacteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithResponse)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Console.WriteLine($"Control point write [{BitConverter.ToString(payload)}] => {status}");

            EmitDebug("control_point_write", new
            {
                payload = BitConverter.ToString(payload),
                status = status.ToString()
            });
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Trainer control command failed ({status}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Control point write failed [{BitConverter.ToString(payload)}]: {ex.Message}");
            EmitDebug("control_point_write_failed", new { payload = BitConverter.ToString(payload), message = ex.Message, exceptionType = ex.GetType().FullName });
            throw;
        }
        finally
        {
            _controlPointWriteLock.Release();
        }
    }

    private void OnFtmsControlPointChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            Console.WriteLine($"FTMS Control Point response: {BitConverter.ToString(data)}");
            if (data.Length >= 3 && data[0] == 0x80)
                _pendingControlPointResponse?.TrySetResult(data);
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

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        EmitDebug("ble_connection_status_changed", new { address = FormatAddress(sender.BluetoothAddress), name = sender.Name, connectionStatus = sender.ConnectionStatus.ToString() });
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            EmitDebug("connection_lost", new { name = sender.Name });
            Disconnected?.Invoke();
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Trainer is not connected.");
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
