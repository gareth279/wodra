using ANT_Managed_Library;
using ANT_ReferenceLibrary = ANT_Managed_Library.ANT_ReferenceLibrary;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Models;

namespace IndoorCycling.Trainer.Windows;

/// <summary>ANT+ FE-C controller implemented through the Windows ANT PC SDK.</summary>
public sealed class AntFeCTrainerClient : ITrainerClient
{
    private const byte NetworkNumber = 0;
    private readonly TrainerSettings? _settings;
    private readonly AntFeCDeviceIdentity? _targetDevice;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private AntSdkDeviceSession? _session;
    private ANT_Channel? _channel;
    private TaskCompletionSource _ready = NewSignal();
    private TaskCompletionSource<AntFeCCommandStatus> _commandStatus = NewSignal<AntFeCCommandStatus>();
    private readonly AntFeCPowerAccumulator _power = new();
    private TrainerTelemetry _latest = new(DateTimeOffset.MinValue, 0, 0, 0,
        HasPowerMeasurement: false, HasCadenceMeasurement: false,
        HasReceivedMeasurement: false, HasSpeedMeasurement: false);
    private TrainerTelemetry? _generalPage;
    private TrainerTelemetry? _trainerPage;
    private byte? _capabilities;
    private bool _pageRequestSent;
    private bool _userConfigSent;
    private bool _isReady;
    private double? _lastRequestedGrade;
    private double? _lastAppliedGrade;

    public bool IsConnected { get; private set; }
    public bool IsReady => _isReady;
    public string ActiveProtocol => "ANT+ FE-C";
    public string? ConnectedDeviceName => IsConnected ? $"ANT+ Fitness Equipment {ConnectedDeviceNumber}" : null;
    public ushort? ConnectedDeviceNumber { get; private set; }
    public byte? ConnectedDeviceType { get; private set; }
    public byte? ConnectedTransmissionType { get; private set; }
    public byte? CapabilityFlags => _capabilities;
    public string CapabilitiesDescription
    {
        get
        {
            if (_capabilities is not { } flags) return "Waiting for capabilities";
            var modes = new List<string>();
            if ((flags & 0x04) != 0) modes.Add("simulation");
            if ((flags & 0x02) != 0) modes.Add("target power");
            if ((flags & 0x01) != 0) modes.Add("basic resistance");
            return modes.Count == 0 ? "no control modes reported" : string.Join(", ", modes);
        }
    }
    public double? LastRequestedSimulationGradePercent => _lastRequestedGrade;
    public double? LastAppliedSimulationGradePercent => _lastAppliedGrade;
    public event Action? Disconnected;

    public AntFeCTrainerClient(TrainerSettings? settings = null, AntFeCDeviceIdentity? targetDevice = null)
    {
        _settings = settings;
        _targetDevice = targetDevice;
    }

    /// <summary>Uses ANT continuous receive scan mode to discover distinct FE-C broadcast identities.</summary>
    public static async Task<IReadOnlyList<AntFeCDeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var key = LoadNetworkKey(NetworkKeyPath);
        try
        {
            using var session = await AntSdkDeviceSession.OpenFirstAsync(cancellationToken).ConfigureAwait(false);
            var device = session.Device;
            if (!device.enableRxExtendedMessages(true, 1000))
                throw new IOException("The ANT device could not enable source-identification messages needed to list trainers.");
            if (!device.setNetworkKey(NetworkNumber, key, 1000))
                throw new IOException("ANT rejected network configuration. Verify the private ANT+ network key file.");
            var channel = device.getChannel(0);
            if (!channel.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, NetworkNumber, 1000) ||
                !channel.setChannelID(0, true, AntFeCProtocol.DeviceType, 0, 1000) ||
                !channel.setChannelPeriod(AntFeCProtocol.ChannelPeriod, 1000) ||
                !channel.setChannelFreq(AntFeCProtocol.RadioFrequency, 1000))
                throw new IOException("ANT could not configure the FE-C trainer discovery scan.");

            var gate = new object();
            var found = new HashSet<AntFeCDeviceIdentity>();
            void OnDeviceResponse(ANT_Response response)
            {
                try
                {
                    if (response.responseID != (byte)ANT_ReferenceLibrary.ANTMessageID.EXT_BROADCAST_DATA_0x5D ||
                        !response.isExtended()) return;
                    var payload = response.getDataPayload();
                    if (payload.Length != 8 || payload[0] is not (AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer or AntFeCProtocol.PageCapabilities)) return;
                    var source = response.getDeviceIDfromExt();
                    if (source.deviceTypeID != AntFeCProtocol.DeviceType || source.deviceNumber == 0) return;
                    lock (gate) found.Add(new(source.deviceNumber, source.deviceTypeID, source.transmissionTypeID));
                }
                catch
                {
                    // Ignore malformed/non-FE-C scan frames; the discovery window remains bounded.
                }
            }

            device.deviceResponse += OnDeviceResponse;
            try
            {
                if (!device.openRxScanMode(1000))
                    throw new NotSupportedException("This ANTUSB-m or driver does not support continuous scan mode required to list multiple FE-C trainers.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                device.deviceResponse -= OnDeviceResponse;
            }
            lock (gate) return found.OrderBy(item => item.DeviceNumber).ThenBy(item => item.TransmissionType).ToArray();
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

    public static string NetworkKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wodra", "antplus-network-key.bin");

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null) await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _ready = NewSignal();
        _commandStatus = NewSignal<AntFeCCommandStatus>();
        _power.Reset();
        _generalPage = _trainerPage = null;
        _capabilities = null;
        _pageRequestSent = false;
        _userConfigSent = false;
        _isReady = false;

        var session = await AntSdkDeviceSession.OpenFirstAsync(cancellationToken).ConfigureAwait(false);
        _session = session;
        try
        {
            var device = session.Device;
            var channel = device.getChannel(0);
            _channel = channel;
            channel.channelResponse += OnChannelResponse;
            if (!device.enableRxExtendedMessages(true, 1000))
                throw new IOException("The ANT device could not enable source-identification messages.");
            var key = LoadNetworkKey(NetworkKeyPath);
            try
            {
                if (!device.setNetworkKey(NetworkNumber, key, 1000))
                    throw new IOException("ANT rejected network configuration. Verify the private ANT+ network key file.");
            }
            finally { Array.Clear(key, 0, key.Length); }
            var knownNumber = _targetDevice?.DeviceNumber ?? _settings?.LastAntDeviceNumber;
            var knownType = _targetDevice?.DeviceType ?? _settings?.LastAntDeviceType;
            var knownTransmission = _targetDevice?.TransmissionType ?? _settings?.LastAntTransmissionType;
            var targetNumber = knownNumber.GetValueOrDefault();
            var pairingEnabled = !knownNumber.HasValue;
            var targetType = knownType ?? AntFeCProtocol.DeviceType;
            var targetTransmission = knownTransmission ?? (byte)0;
            if (!channel.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, NetworkNumber, 1000) ||
                !channel.setChannelID(targetNumber, pairingEnabled, targetType, targetTransmission, 1000) ||
                !channel.setChannelPeriod(AntFeCProtocol.ChannelPeriod, 1000) ||
                !channel.setChannelFreq(AntFeCProtocol.RadioFrequency, 1000) ||
                !channel.setChannelSearchTimeout(12, 1000))
                throw new IOException("ANT could not configure an FE-C search channel.");
            if (!channel.openChannel(1000))
                throw new IOException("ANT could not open the FE-C search channel.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try { await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("No FE-C trainer supplied general, trainer, and capability data. Wake the trainer and retry.");
            }
        }
        catch
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isReady = false;
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            channel.channelResponse -= OnChannelResponse;
            try { channel.closeChannel(1000); } catch (ANT_Exception) { }
            try { channel.unassignChannel(1000); } catch (ANT_Exception) { }
        }
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
        IsConnected = false;
        ConnectedDeviceNumber = null;
        ConnectedDeviceType = null;
        ConnectedTransmissionType = null;
        _generalPage = _trainerPage = null;
        _capabilities = null;
        _latest = _latest with { Timestamp = DateTimeOffset.MinValue, HasReceivedMeasurement = false,
            HasPowerMeasurement = false, HasCadenceMeasurement = false, HasSpeedMeasurement = false };
        _ready.TrySetCanceled();
        _commandStatus.TrySetCanceled();
    }

    public async Task SetSimulationAsync(SimulationCommand command, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if ((_capabilities.GetValueOrDefault() & 0x04) == 0)
            throw new NotSupportedException("This FE-C trainer does not report simulation-mode support.");
        _lastRequestedGrade = command.GradePercent;
        await SendControlAsync(AntFeCProtocol.BuildTrackResistance(command.GradePercent), cancellationToken,
            AntFeCProtocol.BuildUserConfiguration(command.RiderWeightKg, command.BikeWeightKg)).ConfigureAwait(false);
        _lastAppliedGrade = Math.Clamp(command.GradePercent, -200, 200);
    }

    public async Task SetResistanceAsync(ResistanceCommand command, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if ((_capabilities.GetValueOrDefault() & 0x01) == 0)
            throw new NotSupportedException("This FE-C trainer does not report basic-resistance support.");
        var level = (byte)Math.Clamp((int)Math.Round(Math.Clamp(command.ResistanceLevel, 0, 100) * 2), 0, 200);
        await SendControlAsync([48, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, level], cancellationToken).ConfigureAwait(false);
    }

    public TrainerTelemetry GetLatestTelemetry()
    {
        lock (_gate) return _latest;
    }

    private async Task SendControlAsync(byte[] page, CancellationToken cancellationToken, byte[]? preamble = null)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = _channel ?? throw new IOException("The FE-C channel is closed.");
            if (preamble is not null && !_userConfigSent)
            {
                var configResult = await Task.Run(() => channel.sendAcknowledgedData(preamble, 3000), cancellationToken).ConfigureAwait(false);
                if (configResult != ANT_ReferenceLibrary.MessagingReturnCode.Pass)
                    throw new IOException("The FE-C trainer did not acknowledge rider and bicycle weight configuration.");
                _userConfigSent = true;
            }
            _commandStatus = NewSignal<AntFeCCommandStatus>();
            var result = await Task.Run(() => channel.sendAcknowledgedData(page, 3000), cancellationToken).ConfigureAwait(false);
            if (result != ANT_ReferenceLibrary.MessagingReturnCode.Pass)
                throw new IOException($"FE-C acknowledged control failed ({result}).");
            var statusRequest = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCommandStatus);
            var requestResult = await Task.Run(() => channel.sendAcknowledgedData(statusRequest, 2000), cancellationToken).ConfigureAwait(false);
            if (requestResult != ANT_ReferenceLibrary.MessagingReturnCode.Pass)
                throw new IOException("The trainer accepted the control packet but did not acknowledge a status request.");
            var status = await _commandStatus.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (status.CommandId != page[0] || !status.IsSuccessful)
                throw new IOException($"The trainer reported FE-C command status {status.Status} for page {status.CommandId}.");
        }
        finally { _commandLock.Release(); }
    }

    private void OnChannelResponse(ANT_Response response)
    {
        try
        {
            if (response.responseID == (byte)ANT_ReferenceLibrary.ANTMessageID.RESPONSE_EVENT_0x40 &&
                response.messageContents.Length >= 3)
            {
                var eventCode = response.getChannelEventCode();
                if (eventCode is ANT_ReferenceLibrary.ANTEventID.EVENT_RX_SEARCH_TIMEOUT_0x01 or
                    ANT_ReferenceLibrary.ANTEventID.EVENT_CHANNEL_CLOSED_0x07 or
                    ANT_ReferenceLibrary.ANTEventID.EVENT_RX_FAIL_GO_TO_SEARCH_0x08)
                {
                    MarkDisconnected();
                }
                return;
            }
            if (response.responseID == (byte)ANT_ReferenceLibrary.ANTMessageID.CHANNEL_ID_0x51 && response.messageContents.Length >= 5)
            {
                ConnectedDeviceNumber = (ushort)(response.messageContents[2] << 8 | response.messageContents[1]);
                ConnectedDeviceType = response.messageContents[3];
                ConnectedTransmissionType = response.messageContents[4];
                IsConnected = true;
                return;
            }
            var messageId = (ANT_ReferenceLibrary.ANTMessageID)response.responseID;
            if (messageId != ANT_ReferenceLibrary.ANTMessageID.BROADCAST_DATA_0x4E &&
                messageId != ANT_ReferenceLibrary.ANTMessageID.ACKNOWLEDGED_DATA_0x4F &&
                messageId != ANT_ReferenceLibrary.ANTMessageID.BURST_DATA_0x50 &&
                messageId != ANT_ReferenceLibrary.ANTMessageID.EXT_BROADCAST_DATA_0x5D &&
                messageId != ANT_ReferenceLibrary.ANTMessageID.EXT_ACKNOWLEDGED_DATA_0x5E &&
                messageId != ANT_ReferenceLibrary.ANTMessageID.EXT_BURST_DATA_0x5F) return;
            var page = response.getDataPayload();
            if (page.Length != 8) return;
            if (response.isExtended())
            {
                var id = response.getDeviceIDfromExt();
                ConnectedDeviceNumber = id.deviceNumber;
                ConnectedDeviceType = id.deviceTypeID;
                ConnectedTransmissionType = id.transmissionTypeID;
            }
            IsConnected = true;
            var now = new DateTimeOffset(response.timeReceived.ToUniversalTime());
            lock (_gate)
            {
                if (page[0] == AntFeCProtocol.PageGeneral)
                    _generalPage = AntFeCProtocol.DecodeTelemetry(page, now, _power);
                else if (page[0] == AntFeCProtocol.PageTrainer)
                    _trainerPage = AntFeCProtocol.DecodeTelemetry(page, now, _power);
                else if (page[0] == AntFeCProtocol.PageCapabilities)
                    _capabilities = page[7];
                else if (AntFeCCommandStatus.TryParse(page) is { } status)
                    _commandStatus.TrySetResult(status);

                if (_generalPage is not null || _trainerPage is not null)
                {
                    var g = _generalPage;
                    var t = _trainerPage;
                    _latest = new(now, t?.PowerWatts ?? 0, t?.CadenceRpm ?? 0, g?.SpeedKph ?? 0,
                        g?.HeartRateBpm, t?.HasPowerMeasurement == true, t?.HasCadenceMeasurement == true,
                        true, g?.HasSpeedMeasurement == true);
                }
                if (_generalPage is not null && _trainerPage is not null && _capabilities.HasValue)
                {
                    _isReady = true;
                    _ready.TrySetResult();
                }
                if (IsConnected && !_pageRequestSent && page[0] is AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer)
                {
                    _pageRequestSent = true;
                    var channel = _channel;
                    if (channel is not null) _ = Task.Run(() => channel.sendAcknowledgedData(
                        AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCapabilities), 1000));
                }
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(new IOException("FE-C channel processing failed.", ex));
        }
    }

    private void MarkDisconnected()
    {
        var wasConnected = IsConnected || _isReady;
        _isReady = false;
        IsConnected = false;
        lock (_gate)
        {
            _generalPage = _trainerPage = null;
            _latest = _latest with { Timestamp = DateTimeOffset.MinValue, HasReceivedMeasurement = false,
                HasPowerMeasurement = false, HasCadenceMeasurement = false, HasSpeedMeasurement = false };
        }
        if (wasConnected) Disconnected?.Invoke();
    }

    private static byte[] LoadNetworkKey(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ANT+ FE-C requires an 8-byte private network-key file. See docs/trainer-connectivity.md.", path);
        var key = File.ReadAllBytes(path);
        if (key.Length != 8)
        {
            Array.Clear(key, 0, key.Length);
            throw new InvalidDataException("The private ANT+ network-key file must contain exactly 8 bytes.");
        }
        return key;
    }

    private void EnsureReady()
    {
        if (!_isReady || _channel is null) throw new InvalidOperationException("The FE-C trainer is not connected and ready.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
