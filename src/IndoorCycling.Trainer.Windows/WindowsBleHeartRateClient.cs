using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IndoorCycling.Trainer.Models;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace IndoorCycling.Trainer.Windows;

/// <summary>Independent Bluetooth LE Heart Rate Service client; it never owns or resets the ANT+ adapter.</summary>
public class WindowsBleHeartRateClient : IDisposable
{
    public static readonly Guid HeartRateServiceUuid = Guid.Parse(HeartRateDiscoveryPolicy.ServiceUuid);
    private static readonly Guid HeartRateMeasurementUuid = new("00002a37-0000-1000-8000-00805f9b34fb");
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _measurement;
    private readonly object _gate = new();
    private int? _latestBpm;
    private DateTimeOffset _latestAt;
    private string? _name;

    public event Action? Disconnected;
    public event Action<int>? MeasurementReceived;
    protected void RaiseDisconnected() => Disconnected?.Invoke();
    public virtual bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected && _measurement is not null;
    public virtual string? ConnectedDeviceName => _name;
    public virtual DateTimeOffset LastMeasurementAt { get { lock (_gate) return _latestAt; } }
    public virtual int? GetFreshBpm(DateTimeOffset now, TimeSpan? maxAge = null)
    {
        lock (_gate) return _latestBpm is { } bpm && now >= _latestAt && now - _latestAt <= (maxAge ?? TimeSpan.FromSeconds(8)) ? bpm : null;
    }

    public virtual async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await StopScanAsync();
        var found = new Dictionary<ulong, BleDiscoveredDevice>();
        var gate = new object();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(HeartRateServiceUuid);
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> handler = (_, args) =>
        {
            if (!args.Advertisement.ServiceUuids.Any(id => id == HeartRateServiceUuid)) return;
            var name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? "Heart-rate monitor" : args.Advertisement.LocalName.Trim();
            lock (gate) found[args.BluetoothAddress] = new(args.BluetoothAddress, name, [HeartRateServiceUuid.ToString()], args.RawSignalStrengthInDBm);
        };
        watcher.Received += handler;
        _watcher = watcher;
        try { watcher.Start(); await Task.Delay(duration, cancellationToken); }
        finally
        {
            watcher.Received -= handler;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
            if (ReferenceEquals(_watcher, watcher)) _watcher = null;
        }
        lock (gate) return found.Values.OrderByDescending(device => device.Rssi).ToArray();
    }

    public virtual async Task ConnectAsync(BleDiscoveredDevice candidate, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.BluetoothAddress);
        if (device is null) throw new InvalidOperationException("Could not open the selected heart-rate monitor.");
        _device = device;
        _name = string.IsNullOrWhiteSpace(device.Name) ? candidate.Name : device.Name;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;
        var serviceResult = await device.GetGattServicesForUuidAsync(HeartRateServiceUuid, BluetoothCacheMode.Uncached);
        if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            throw new InvalidOperationException("The selected device no longer exposes the Heart Rate Service.");
        var service = serviceResult.Services[0];
        _service = service;
        var characteristicResult = await service.GetCharacteristicsForUuidAsync(HeartRateMeasurementUuid, BluetoothCacheMode.Uncached);
        if (characteristicResult.Status != GattCommunicationStatus.Success || characteristicResult.Characteristics.Count == 0)
            throw new InvalidOperationException("The selected device has no Heart Rate Measurement characteristic.");
        _measurement = characteristicResult.Characteristics[0];
        _measurement.ValueChanged += OnMeasurementChanged;
        var mode = _measurement.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        if (!_measurement.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) &&
            !_measurement.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            throw new InvalidOperationException("The Heart Rate Measurement characteristic does not support notifications.");
        var configured = await _measurement.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
        if (configured != GattCommunicationStatus.Success)
            throw new InvalidOperationException("Could not enable heart-rate notifications from the selected device.");
    }

    public virtual async Task DisconnectAsync()
    {
        if (_measurement is not null)
        {
            _measurement.ValueChanged -= OnMeasurementChanged;
            try { await _measurement.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
            _measurement = null;
        }
        _service?.Dispose();
        _service = null;
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
        _name = null;
        lock (_gate) { _latestBpm = null; _latestAt = DateTimeOffset.MinValue; }
    }

    private async Task StopScanAsync()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null && watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        await Task.CompletedTask;
    }

    private void OnMeasurementChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            var bpm = HeartRateMeasurementParser.Parse(bytes);
            if (bpm is not { } value) return;
            lock (_gate) { _latestBpm = value; _latestAt = DateTimeOffset.UtcNow; }
            MeasurementReceived?.Invoke(value);
        }
        catch { /* A malformed sensor notification is ignored; the card reports stale data. */ }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            if (_measurement is not null) _measurement.ValueChanged -= OnMeasurementChanged;
            _measurement = null;
            _service?.Dispose();
            _service = null;
            lock (_gate) { _latestBpm = null; _latestAt = DateTimeOffset.MinValue; }
            RaiseDisconnected();
        }
    }

    public virtual void Dispose()
    {
        _ = StopScanAsync();
        if (_measurement is not null) _measurement.ValueChanged -= OnMeasurementChanged;
        _service?.Dispose();
        _service = null;
        if (_device is not null) { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; _device.Dispose(); }
        _measurement = null;
        _device = null;
    }
}
