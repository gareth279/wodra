using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;
using IndoorCycling.Trainer.Models;

namespace IndoorCycling.Trainer.Windows;

/// <summary>Bluetooth adapter for the Elite Sterzo Smart (not the mechanical Elite Sterzo).</summary>
public class EliteSterzoSmartClient : IAsyncDisposable
{
    private static readonly Guid ServiceId = Guid.Parse(SterzoSmartProtocol.ServiceUuid);
    private static readonly Guid AngleId = Guid.Parse(SterzoSmartProtocol.SteeringAngleCharacteristicUuid);
    private static readonly Guid ControlId = Guid.Parse(SterzoSmartProtocol.ControlPointCharacteristicUuid);
    private static readonly Guid ChallengeId = Guid.Parse(SterzoSmartProtocol.ChallengeCharacteristicUuid);
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _angle;
    private GattCharacteristic? _control;
    private GattCharacteristic? _challenge;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? _angleHandler;
    private TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? _challengeHandler;
    private TypedEventHandler<BluetoothLEDevice, object>? _connectionHandler;
    private TaskCompletionSource<byte[]> _challengeResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Lazy<byte[]> ChallengeTable = new(LoadChallengeTable);
    private int _disposed;

    public event Action? Disconnected;
    protected void RaiseDisconnected() => Disconnected?.Invoke();
    public virtual bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected && _angle is not null;
    public virtual string? ConnectedDeviceName => _device?.Name;
    public virtual float? LatestAngle { get; protected set; }
    public virtual DateTimeOffset? LastInputAt { get; protected set; }

    public virtual async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var found = new Dictionary<ulong, BleDiscoveredDevice>();
        var gate = new object();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> handler = (_, args) =>
        {
            var name = args.Advertisement.LocalName;
            if (!args.Advertisement.ServiceUuids.Any(id => id == ServiceId) &&
                !name.Contains("sterzo", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(name)) name = "Elite Sterzo Smart";
            lock (gate) found[args.BluetoothAddress] = new(args.BluetoothAddress, name,
                [ServiceId.ToString()], args.RawSignalStrengthInDBm);
        };
        watcher.Received += handler;
        _watcher = watcher;
        watcher.Start();
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            lock (gate) return found.Values.OrderByDescending(device => device.Rssi).ToArray();
        }
        finally
        {
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
            watcher.Received -= handler;
            if (ReferenceEquals(_watcher, watcher)) _watcher = null;
        }
    }

    public virtual async Task ConnectAsync(BleDiscoveredDevice candidate, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.BluetoothAddress).AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Windows could not open the selected steering device.");
        _device = device;
        _connectionHandler = (_, _) =>
        {
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) RaiseDisconnected();
        };
        device.ConnectionStatusChanged += _connectionHandler;
        var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException("The selected device did not expose the Sterzo Smart service.");
        _service = services.Services[0];
        var angleResult = await _service.GetCharacteristicsForUuidAsync(AngleId, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
        var controlResult = await _service.GetCharacteristicsForUuidAsync(ControlId, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
        var challengeResult = await _service.GetCharacteristicsForUuidAsync(ChallengeId, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
        if (angleResult.Status != GattCommunicationStatus.Success || angleResult.Characteristics.Count == 0 ||
            controlResult.Status != GattCommunicationStatus.Success || controlResult.Characteristics.Count == 0 ||
            challengeResult.Status != GattCommunicationStatus.Success || challengeResult.Characteristics.Count == 0)
            throw new InvalidOperationException("The Sterzo Smart steering characteristics were incomplete.");
        _angle = angleResult.Characteristics[0];
        _control = controlResult.Characteristics[0];
        _challenge = challengeResult.Characteristics[0];
        var angleMode = GetSubscriptionMode(_angle);
        var challengeMode = GetSubscriptionMode(_challenge);

        _challengeResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _angleHandler = OnAngleChanged;
        _challengeHandler = OnChallengeChanged;
        _challenge.ValueChanged += _challengeHandler;
        _angle.ValueChanged += _angleHandler;
        var challengeSubscription = await _challenge.WriteClientCharacteristicConfigurationDescriptorAsync(
            challengeMode).AsTask(cancellationToken).ConfigureAwait(false);
        if (challengeSubscription != GattCommunicationStatus.Success) throw new InvalidOperationException("Could not subscribe to the Sterzo Smart challenge.");
        var angleSubscription = await _angle.WriteClientCharacteristicConfigurationDescriptorAsync(
            angleMode).AsTask(cancellationToken).ConfigureAwait(false);
        if (angleSubscription != GattCommunicationStatus.Success) throw new InvalidOperationException("Could not subscribe to Sterzo Smart steering input.");

        await WriteAsync([0x03, 0x10], cancellationToken).ConfigureAwait(false);
        var challengeData = await _challengeResponse.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        var challenge = SterzoSmartProtocol.ParseChallenge(challengeData)
            ?? throw new InvalidOperationException("Sterzo Smart returned a malformed challenge.");
        if (!SterzoSmartProtocol.TryGetChallengeResponse(ChallengeTable.Value, challenge, out var first, out var second))
            throw new InvalidOperationException("Sterzo Smart challenge response data was unavailable.");
        await WriteAsync([0x03, 0x11, first, second], cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        await WriteAsync([0x02, 0x02], cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task DisconnectAsync()
    {
        if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
        if (_challenge is not null)
        {
            if (_challengeHandler is not null) _challenge.ValueChanged -= _challengeHandler;
            try { await _challenge.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
        }
        if (_angle is not null)
        {
            if (_angleHandler is not null) _angle.ValueChanged -= _angleHandler;
            try { await _angle.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
        }
        if (_device is not null)
        {
            if (_connectionHandler is not null) _device.ConnectionStatusChanged -= _connectionHandler;
            _device.Dispose();
        }
        _service?.Dispose();
        _watcher = null; _device = null; _service = null; _angle = null; _control = null; _challenge = null;
        _angleHandler = null; _challengeHandler = null; _connectionHandler = null;
        LatestAngle = null; LastInputAt = null;
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (_control is null) throw new InvalidOperationException("Sterzo Smart control connection is closed.");
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        var status = await _control.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Sterzo Smart command failed: {status}.");
    }

    private static GattClientCharacteristicConfigurationDescriptorValue GetSubscriptionMode(GattCharacteristic characteristic)
    {
        if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            return GattClientCharacteristicConfigurationDescriptorValue.Notify;
        if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            return GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        throw new InvalidOperationException("A Sterzo Smart measurement characteristic cannot send notifications or indications.");
    }

    private void OnChallengeChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = new byte[args.CharacteristicValue.Length];
        DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(bytes);
        _challengeResponse.TrySetResult(bytes);
    }

    private void OnAngleChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = new byte[args.CharacteristicValue.Length];
        DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(bytes);
        if (SterzoSmartProtocol.ParseAngle(bytes) is not { } angle) return;
        LatestAngle = angle;
        LastInputAt = DateTimeOffset.UtcNow;
    }

    private static byte[] LoadChallengeTable()
    {
        var name = "IndoorCycling.Trainer.Windows.Data.sterzo-challenge-codes.dat.b64";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The Sterzo Smart challenge table is missing from the application.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var bytes = Convert.FromBase64String(reader.ReadToEnd());
        if (bytes.Length != 131072) throw new InvalidDataException("The Sterzo Smart challenge table has an invalid size.");
        return bytes;
    }
}
