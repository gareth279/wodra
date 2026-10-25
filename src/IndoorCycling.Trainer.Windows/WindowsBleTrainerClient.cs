using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Models;
//using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace IndoorCycling.Trainer.Windows
{

public class WindowsBleTrainerClient : ITrainerClient, IWorkoutTrainerControl
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEAdvertisementWatcher? _activeScanWatcher;
    private CancellationTokenSource? _activeScanCancellation;
    private BluetoothLEDevice? _device;
    private TrainerTelemetry _latestTelemetry = new(
        Timestamp: DateTimeOffset.MinValue,
        PowerWatts: 0,
        CadenceRpm: 0,
        SpeedKph: 0,
        HeartRateBpm: null,
        HasPowerMeasurement: false,
        HasCadenceMeasurement: false,
        HasReceivedMeasurement: false,
        HasSpeedMeasurement: false
    );

    private ulong? _lastSeenAddress;
    private string? _lastSeenName;
    public virtual string? ConnectedDeviceName => _deviceName;
    public virtual string ActiveProtocol => "Bluetooth FTMS";
    private string? _deviceName;
    public ulong? ConnectedBluetoothAddress { get; private set; }
    public string? LastConnectionFailureStage { get; private set; }
    private string _operationStage = "idle";
    public string CurrentOperationStage => _operationStage;

    public void SetOperationStage(string stage)
    {
        _operationStage = stage;
        LastConnectionFailureStage = stage;
    }

    public virtual bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public virtual bool IsReady { get; private set; }

    public virtual void MarkReady()
    {
        EnsureConnected();
        IsReady = true;
    }

    public event Action? Disconnected;
    protected void RaiseDisconnected() => Disconnected?.Invoke();

    public event Action<object>? DebugEvent;

    private GattCharacteristic? _ftmsIndoorBikeDataCharacteristic;   // 2AD2 notify
    private GattCharacteristic? _ftmsFitnessMachineStatusCharacteristic; // 2ADA notify
    private GattCharacteristic? _ftmsControlPointCharacteristic;     // 2AD9 write/indicate
    private (double Minimum, double Maximum)? _supportedInclinationRangePercent;
    private FtmsWorkoutCapabilities _workoutCapabilities = FtmsWorkoutCapabilities.Unknown;
    private TaskCompletionSource<byte[]>? _pendingControlPointResponse;
    public double? LastRequestedSimulationGradePercent { get; private set; }
    public double? LastAppliedSimulationGradePercent { get; private set; }
    private readonly SemaphoreSlim _controlPointWriteLock = new(1, 1);
    private readonly SemaphoreSlim _connectionSetupLock = new(1, 1);
    private readonly SemaphoreSlim _workoutCommandLock = new(1, 1);
    private long _workoutSessionId;

    public WorkoutTrainerControlStatus WorkoutControlStatus => _workoutCapabilities.HasFeatureData &&
        _workoutCapabilities.SupportsTargetPower
        ? new(WorkoutTrainerControlKind.Erg, "FTMS power target and Release Control are advertised.",
            _workoutCapabilities.MinimumPowerWatts, _workoutCapabilities.MaximumPowerWatts, _workoutCapabilities.PowerIncrementWatts)
        : new(WorkoutTrainerControlKind.TargetsOnly,
            !_workoutCapabilities.HasFeatureData ? "FTMS control features were not discovered; targets only." :
            "Trainer does not advertise power target control; targets only.");

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // For now, this just means:
        // - if we already discovered a likely trainer, connect to it
        // - later we will expand this to proper FTMS service checks
        return ConnectToLastSeenDeviceAsync(cancellationToken);
    }

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsReady = false;
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
        _workoutCapabilities = FtmsWorkoutCapabilities.Unknown;
        Interlocked.Exchange(ref _workoutSessionId, 0);
        LastRequestedSimulationGradePercent = null;
        LastAppliedSimulationGradePercent = null;
        LastConnectionFailureStage = null;
        _operationStage = "idle";
        _pendingControlPointResponse?.TrySetCanceled(cancellationToken);
        _pendingControlPointResponse = null;

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
        }
        _device = null;
        _deviceName = null;
        ConnectedBluetoothAddress = null;
        _latestTelemetry = _latestTelemetry with
        {
            Timestamp = DateTimeOffset.MinValue,
            HasPowerMeasurement = false,
            HasCadenceMeasurement = false,
            HasReceivedMeasurement = false,
            HasSpeedMeasurement = false
        };

        Console.WriteLine("BLE trainer disconnected.");
        EmitDebug("disconnect", new { });
    }

    public virtual async Task SetSimulationAsync(
    SimulationCommand command,
    CancellationToken cancellationToken = default)
    {
        EnsureConnected();
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

    public virtual Task SetResistanceAsync(
        ResistanceCommand command,
        CancellationToken cancellationToken = default)
    {
        // Stub for later FTMS write
        Console.WriteLine($"[RESISTANCE] Level={command.ResistanceLevel:0.00}");
        return Task.CompletedTask;
    }

    public virtual TrainerTelemetry GetLatestTelemetry()
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
        if (_activeScanWatcher is { } active)
        {
            _activeScanCancellation?.Cancel();
            active.Received -= OnAdvertisementReceived;
            if (active.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created)
                active.Stop();
            _activeScanWatcher = null;
        }
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

    public virtual async Task<IReadOnlyList<BleDiscoveredDevice>> ScanForDevicesAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (_activeScanWatcher is not null)
            throw new InvalidOperationException("Bluetooth trainer discovery is already running.");
        _lastSeenAddress = null;
        _lastSeenName = null;
        var found = new Dictionary<ulong, BleDiscoveredDevice>();
        var foundLock = new object();

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

            lock (foundLock)
            {
                if (!found.TryGetValue(args.BluetoothAddress, out var previous) || item.Rssi > previous.Rssi || string.IsNullOrWhiteSpace(previous.Name))
                    found[args.BluetoothAddress] = item;
            }

        }

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += Handler;
        _activeScanWatcher = watcher;
        var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeScanCancellation = scanCancellation;
        try
        {
            watcher.Start();
            Console.WriteLine($"Scanning for BLE devices for {duration.TotalSeconds:0} seconds...");
            await Task.Delay(duration, scanCancellation.Token);
        }
        finally
        {
            if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created)
                watcher.Stop();
            watcher.Received -= Handler;
            if (ReferenceEquals(_activeScanWatcher, watcher)) _activeScanWatcher = null;
            if (ReferenceEquals(_activeScanCancellation, scanCancellation)) _activeScanCancellation = null;
            scanCancellation.Dispose();
        }

        BleDiscoveredDevice[] candidates;
        lock (foundLock) candidates = TrainerDiscoveryPolicy.Select(found.Values, null).Candidates.ToArray();
        if (candidates.Length == 0) EmitDebug("trainer_not_found", new { discoveredDeviceCount = found.Count });
        foreach (var candidate in candidates)
            EmitDebug("trainer_discovered", new { address = FormatAddress(candidate.BluetoothAddress), name = candidate.Name, rssi = candidate.Rssi, services = candidate.ServiceUuids });

        Console.WriteLine($"Found {found.Count} BLE device(s), {candidates.Length} compatible trainer(s).");
        foreach (var d in candidates)
        {
            Console.WriteLine($"- {FormatAddress(d.BluetoothAddress)} | {d.Name} | RSSI {d.Rssi} | Services: {string.Join(", ", d.ServiceUuids)}");
        }

        return candidates;
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

    public virtual async Task ConnectToDeviceAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        await _connectionSetupLock.WaitAsync(cancellationToken);
        try
        {
            await ConnectToDeviceCoreAsync(bluetoothAddress, cancellationToken);
        }
        finally { _connectionSetupLock.Release(); }
    }

    private async Task ConnectToDeviceCoreAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        ReleaseDevice();
        _lastSeenAddress = bluetoothAddress;

        var stage = "ble_device_creation";
        _operationStage = stage;
        LastConnectionFailureStage = null;
        EmitDebug("trainer_connection_stage_started", new { stage, address = FormatAddress(bluetoothAddress) });
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);

            if (device is null)
                throw new InvalidOperationException("Windows could not create a BLE device for the discovered trainer.");

            _device = device;
            _deviceName = string.IsNullOrWhiteSpace(device.Name) ? _lastSeenName ?? "Trainer" : device.Name;
            ConnectedBluetoothAddress = bluetoothAddress;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            EmitDebug("ble_device_created", new { address = FormatAddress(bluetoothAddress), name = device.Name, connectionStatus = device.ConnectionStatus.ToString() });
            Console.WriteLine($"BLE device created: {device.Name}, status={device.ConnectionStatus}");

            // Windows does not necessarily establish the BLE link when the device
            // object is created. Uncached GATT discovery is the operation that
            // initiates the connection, so do that before requiring Connected.
            stage = "ftms_gatt_service_discovery";
            _operationStage = stage;
            EmitDebug("trainer_connection_stage_started", new { stage, address = FormatAddress(bluetoothAddress), connectionStatus = device.ConnectionStatus.ToString() });
            await ResolveFtmsCharacteristicsAsync(cancellationToken);
            EnsureConnected();
            _lastSeenName = _deviceName;
            EmitDebug("connect", new { address = FormatAddress(bluetoothAddress), name = device.Name, connectionStatus = device.ConnectionStatus.ToString() });
        }
        catch (Exception ex)
        {
            LastConnectionFailureStage = _operationStage;
            EmitDebug("trainer_connection_stage_failed", new { stage = _operationStage, reason = ex.Message, exceptionType = ex.GetType().FullName, connectionStatus = _device?.ConnectionStatus.ToString() });
            if (_device is not null)
            {
                ReleaseDevice();
            }
            throw;
        }

        // Later:
        // - enumerate GATT services
        // - find FTMS service
        // - subscribe to indoor bike data / trainer data
        //await DumpServicesAsync(cancellationToken);
    }

    private void ReleaseDevice()
    {
        IsReady = false;
        if (_ftmsIndoorBikeDataCharacteristic is not null)
            _ftmsIndoorBikeDataCharacteristic.ValueChanged -= OnFtmsIndoorBikeDataChanged;
        if (_ftmsControlPointCharacteristic is not null)
            _ftmsControlPointCharacteristic.ValueChanged -= OnFtmsControlPointChanged;
        _ftmsIndoorBikeDataCharacteristic = null;
        _ftmsFitnessMachineStatusCharacteristic = null;
        _ftmsControlPointCharacteristic = null;
        _supportedInclinationRangePercent = null;
        LastRequestedSimulationGradePercent = null;
        LastAppliedSimulationGradePercent = null;
        _pendingControlPointResponse?.TrySetCanceled();
        _pendingControlPointResponse = null;
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
        }
        _device = null;
        _deviceName = null;
        ConnectedBluetoothAddress = null;
        _latestTelemetry = new TrainerTelemetry(DateTimeOffset.MinValue, 0, 0, 0,
            HasPowerMeasurement: false, HasCadenceMeasurement: false, HasReceivedMeasurement: false, HasSpeedMeasurement: false);
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
        var fitnessMachineFeatureUuid = Guid.Parse("00002acc-0000-1000-8000-00805f9b34fb");
        var supportedPowerRangeUuid = Guid.Parse("00002ad8-0000-1000-8000-00805f9b34fb");

        GattDeviceServicesResult servicesResult;
        _operationStage = "ftms_gatt_service_discovery";
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

        _operationStage = "ftms_gatt_characteristic_discovery";
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
        var fitnessMachineFeature = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == fitnessMachineFeatureUuid);
        var supportedPowerRange = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == supportedPowerRangeUuid);
        _supportedInclinationRangePercent = await ReadSupportedInclinationRangeAsync(supportedInclinationRange, cancellationToken);
        var featureBytes = await ReadCharacteristicBytesAsync(fitnessMachineFeature, cancellationToken);
        var powerRangeBytes = await ReadCharacteristicBytesAsync(supportedPowerRange, cancellationToken);
        _workoutCapabilities = FtmsWorkoutCapabilities.Parse(featureBytes, powerRangeBytes);

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
            , workoutControl = WorkoutControlStatus.Message,
            targetPowerMinimumWatts = _workoutCapabilities.MinimumPowerWatts,
            targetPowerMaximumWatts = _workoutCapabilities.MaximumPowerWatts
        });
    }

    private async Task<byte[]> ReadCharacteristicBytesAsync(GattCharacteristic? characteristic, CancellationToken cancellationToken)
    {
        if (characteristic is null) return [];
        try
        {
            var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (result.Status != GattCommunicationStatus.Success) return [];
            var data = global::Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
            var bytes = new byte[checked((int)data.UnconsumedBufferLength)]; data.ReadBytes(bytes); data.Dispose(); return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { EmitDebug("ftms_optional_capability_read_failed", new { characteristic = characteristic.Uuid.ToString(), message = ex.Message }); return []; }
    }

    public Task BeginWorkoutControlAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        if (!IsReady || sessionId <= 0) throw new InvalidOperationException("Trainer is not ready for a workout session.");
        Interlocked.Exchange(ref _workoutSessionId, sessionId);
        return AcquireWorkoutControlAsync(sessionId, cancellationToken);
    }

    private async Task AcquireWorkoutControlAsync(long sessionId, CancellationToken cancellationToken)
    {
        await _workoutCommandLock.WaitAsync(cancellationToken);
        try
        {
            if (sessionId != Interlocked.Read(ref _workoutSessionId) || !IsReady)
                throw new OperationCanceledException("The workout control request belongs to an inactive session.");
            await ExecuteWorkoutControlPointAsync([0x00], 0x00, cancellationToken);
        }
        catch
        {
            Interlocked.CompareExchange(ref _workoutSessionId, 0, sessionId);
            throw;
        }
        finally { _workoutCommandLock.Release(); }
    }

    public async Task SetWorkoutTargetPowerAsync(int watts, long sessionId, CancellationToken cancellationToken = default)
    {
        await _workoutCommandLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsReady || sessionId != Interlocked.Read(ref _workoutSessionId))
                throw new OperationCanceledException("The queued ERG target belongs to an inactive workout session.");
            var status = WorkoutControlStatus;
            if (!status.CanControlPower) throw new NotSupportedException(status.Message);
            if (watts < status.MinimumWatts || watts > status.MaximumWatts)
                throw new ArgumentOutOfRangeException(nameof(watts), $"Target must be {status.MinimumWatts}–{status.MaximumWatts} W for this trainer.");
            var increment = (int)Math.Max(1, Math.Round(status.MinimumIncrementWatts ?? 1));
            var actualWatts = status.MinimumWatts + (int)Math.Round((watts - status.MinimumWatts) / (double)increment) * increment;
            await ExecuteWorkoutControlPointAsync([0x05, (byte)(actualWatts & 0xFF), (byte)(actualWatts >> 8)], 0x05, cancellationToken);
            EmitDebug("ftms_workout_target_acknowledged", new { requestedWatts = watts, appliedWatts = actualWatts, sessionId, physicalResistanceVerified = false });
        }
        finally { _workoutCommandLock.Release(); }
    }

    public async Task ReleaseWorkoutControlAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        await _workoutCommandLock.WaitAsync(cancellationToken);
        try
        {
            if (sessionId != Interlocked.Read(ref _workoutSessionId))
                throw new OperationCanceledException("The FTMS Release Control belongs to an inactive workout or connection; release was not confirmed.");
            if (!IsReady) throw new IOException("Trainer is disconnected; ERG release was requested but could not be confirmed.");
            await ExecuteWorkoutControlPointAsync([0x08], 0x08, cancellationToken);
            Interlocked.CompareExchange(ref _workoutSessionId, 0, sessionId);
        }
        finally { _workoutCommandLock.Release(); }
    }

    private async Task ExecuteWorkoutControlPointAsync(byte[] payload, byte expectedOpcode, CancellationToken cancellationToken)
    {
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlPointResponse = response;
        try
        {
            await WriteControlPointAsync(payload, cancellationToken);
            byte[] data;
            try { data = await response.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (TimeoutException ex) { throw new InvalidOperationException($"Trainer did not acknowledge FTMS workout command 0x{expectedOpcode:X2}.", ex); }
            if (data.Length < 3 || data[0] != 0x80 || data[1] != expectedOpcode || data[2] != 0x01)
                throw new InvalidOperationException($"Trainer rejected FTMS workout command 0x{expectedOpcode:X2} ({BitConverter.ToString(data)}).");
        }
        finally { if (ReferenceEquals(_pendingControlPointResponse, response)) _pendingControlPointResponse = null; }
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

    public virtual async Task SubscribeFtmsTelemetryAsync(CancellationToken cancellationToken = default)
    {
        _operationStage = "ftms_telemetry_subscription";
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

    public virtual async Task SubscribeControlPointAsync(CancellationToken cancellationToken = default)
    {
        _operationStage = "ftms_control_point_subscription";
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

    public virtual async Task RequestControlAsync(CancellationToken cancellationToken = default)
    {
        _operationStage = "ftms_control_request_acknowledgement";
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
        if (!ReferenceEquals(sender, _device)) return;
        EmitDebug("ble_connection_status_changed", new { address = FormatAddress(sender.BluetoothAddress), name = sender.Name, connectionStatus = sender.ConnectionStatus.ToString() });
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            IsReady = false;
            EmitDebug("connection_lost", new { name = sender.Name });
            RaiseDisconnected();
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Trainer is not connected.");
    }

    private void OnFtmsIndoorBikeDataChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _ftmsIndoorBikeDataCharacteristic)) return;
        try
        {
            var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            reader.Dispose();
            var timestamp = DateTimeOffset.UtcNow;
            if (!FtmsIndoorBikeDataParser.TryParse(data, timestamp, out var telemetry))
            {
                Console.WriteLine($"FTMS packet too short: {BitConverter.ToString(data)}");
                EmitDebug("ftms_packet_too_short", new
                {
                    data = BitConverter.ToString(data)
                });
                return;
            }
            _latestTelemetry = telemetry;

            Console.WriteLine(
                $"FTMS speed={telemetry.SpeedKph:0.0} kph cadence={(telemetry.HasCadenceMeasurement ? telemetry.CadenceRpm.ToString() : "n/a")} rpm power={(telemetry.HasPowerMeasurement ? telemetry.PowerWatts.ToString() : "n/a")} W");

            EmitDebug("ftms_telemetry", new
            {
                timestamp,
                powerSupported = telemetry.HasPowerMeasurement,
                cadenceSupported = telemetry.HasCadenceMeasurement,
                telemetry.SpeedKph,
                telemetry.CadenceRpm,
                telemetry.PowerWatts,
                telemetry.HeartRateBpm
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
}
