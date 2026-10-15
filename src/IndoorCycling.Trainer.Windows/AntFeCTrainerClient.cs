using ANT_Managed_Library;
using ANT_ReferenceLibrary = ANT_Managed_Library.ANT_ReferenceLibrary;
using System.Diagnostics;
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
    private int _capabilityRequestAttempts;
    private bool _userConfigSent;
    private bool _isReady;
    private readonly Action<AntFeCDiagnosticEvent>? _diagnostic;
    private readonly HashSet<byte> _reportedPages = [];
    private readonly Stopwatch _connectionTimer = new();
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

    public AntFeCTrainerClient(TrainerSettings? settings = null, AntFeCDeviceIdentity? targetDevice = null,
        Action<AntFeCDiagnosticEvent>? diagnostic = null)
    {
        _settings = settings;
        _targetDevice = targetDevice;
        _diagnostic = diagnostic;
    }

    /// <summary>Uses ANT continuous receive scan mode to discover distinct FE-C broadcast identities.</summary>
    public static async Task<IReadOnlyList<AntFeCDeviceIdentity>> DiscoverAsync(
        CancellationToken cancellationToken = default, Action<AntFeCDiagnosticEvent>? diagnostic = null)
    {
        var discoveryTimer = Stopwatch.StartNew();
        Emit(diagnostic, new("embedded_configuration", "load_network_key", 0, Outcome: "started"));
        byte[] key;
        try { key = AntNetworkKeyMaterial.LoadEmbedded(typeof(AntFeCTrainerClient).Assembly); }
        catch (Exception ex)
        {
            Emit(diagnostic, new("embedded_configuration", "load_network_key", discoveryTimer.Elapsed.TotalMilliseconds,
                Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
            throw;
        }
        Emit(diagnostic, new("embedded_configuration", "load_network_key", discoveryTimer.Elapsed.TotalMilliseconds,
            Outcome: "loaded_8_bytes"));
        try
        {
            using var session = await AntSdkDeviceSession.OpenFirstAsync(cancellationToken, diagnostic).ConfigureAwait(false);
            var device = session.Device;
            RunCommand(diagnostic, "extended_source_id", "RX_EXT_MESGS_ENABLE_0x66", () => device.enableRxExtendedMessages(true, 1000));
            RunCommand(diagnostic, "network_configuration", "NETWORK_KEY_0x46",
                () => device.setNetworkKey(NetworkNumber, key, 1000));
            var channel = device.getChannel(0);
            RunCommand(diagnostic, "channel_configuration", "ASSIGN_CHANNEL_0x42",
                () => channel.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, NetworkNumber, 1000));
            RunCommand(diagnostic, "channel_configuration", "CHANNEL_ID_0x51",
                () => channel.setChannelID(0, true, AntFeCProtocol.DeviceType, 0, 1000));
            RunCommand(diagnostic, "channel_configuration", "CHANNEL_MESG_PERIOD_0x43",
                () => channel.setChannelPeriod(AntFeCProtocol.ChannelPeriod, 1000));
            RunCommand(diagnostic, "channel_configuration", "CHANNEL_RADIO_FREQ_0x45",
                () => channel.setChannelFreq(AntFeCProtocol.RadioFrequency, 1000));

            var gate = new object();
            var found = new HashSet<AntFeCDeviceIdentity>();
            var reportedTrainerPages = new HashSet<(AntFeCDeviceIdentity Identity, byte Page)>();
            var receiveCounts = new AntReceiveDiagnosticCounts();
            void OnScanChannelResponse(ANT_Response response)
            {
                try
                {
                    var frame = AntFeCScanFrameParser.Parse(response.responseID, response.messageContents);
                    var callbackSource = "channelResponse";
                    var rejection = frame.RejectionReason ?? "accepted_fe_c_page";
                    lock (gate) receiveCounts.Add(frame.MessageId, callbackSource, frame.IsExtended, frame.PayloadLength, frame.DataPage, rejection);
                    if (frame.RejectionReason is not null || frame.DataPage is not { } page || frame.Source is not { } identity) return;
                    bool firstPage;
                    lock (gate)
                    {
                        found.Add(identity);
                        firstPage = reportedTrainerPages.Add((identity, page));
                    }
                    if (firstPage)
                        Emit(diagnostic, new("fe_c_discovery", "trainer_page_received",
                            discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            DataPage: page, Outcome: "matching_fe_c_page", CallbackSource: callbackSource,
                            IsExtended: frame.IsExtended, PayloadLength: frame.PayloadLength,
                            DeviceNumber: identity.DeviceNumber, DeviceType: identity.DeviceType,
                            TransmissionType: identity.TransmissionType));
                }
                catch (Exception ex)
                {
                    lock (gate) receiveCounts.Add(response.responseID, "channelResponse", response.messageContents?.Length >= 13,
                        Math.Max(0, (response.messageContents?.Length ?? 0) - 1), null, $"callback_exception_{ex.GetType().Name}");
                }
            }

            // The ANT PC SDK dispatches all channel transmission messages (0x4E/0x5D etc.)
            // to ANT_Channel.channelResponse. deviceResponse is reserved for protocol/device messages.
            channel.channelResponse += OnScanChannelResponse;
            try
            {
                RunCommand(diagnostic, "fe_c_discovery", "OPEN_RX_SCAN_0x5B", () => device.openRxScanMode(1000));
                Emit(diagnostic, new("fe_c_discovery", "scan_window_started", discoveryTimer.Elapsed.TotalMilliseconds,
                    Outcome: "listening"));
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                channel.channelResponse -= OnScanChannelResponse;
            }
            lock (gate)
            {
                foreach (var item in receiveCounts.Snapshot())
                    Emit(diagnostic, new("fe_c_receive", "frame_summary", discoveryTimer.Elapsed.TotalMilliseconds,
                        ResponseId: item.MessageId, Outcome: "bounded_receive_count", CallbackSource: item.CallbackSource,
                        IsExtended: item.Extended, PayloadLength: item.PayloadLength, DataPage: item.DataPage,
                        RejectionReason: item.RejectionReason, Count: item.Count));
            }
            AntFeCDeviceIdentity[] result;
            lock (gate) result = found.OrderBy(item => item.DeviceNumber).ThenBy(item => item.TransmissionType).ToArray();
            Emit(diagnostic, new("fe_c_discovery", "scan_window_completed", discoveryTimer.Elapsed.TotalMilliseconds,
                Outcome: result.Length == 0 ? "no_trainer_pages" : "trainer_found", TrainerCount: result.Length));
            return result;
        }
        catch (Exception ex)
        {
            Emit(diagnostic, new("ant_discovery", "failed", discoveryTimer.Elapsed.TotalMilliseconds,
                Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
            throw;
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null) await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _ready = NewSignal();
        _commandStatus = NewSignal<AntFeCCommandStatus>();
        _power.Reset();
        _generalPage = _trainerPage = null;
        _capabilities = null;
        _pageRequestSent = false;
        _capabilityRequestAttempts = 0;
        _reportedPages.Clear();
        _connectionTimer.Restart();
        _userConfigSent = false;
        _isReady = false;

        Emit(_diagnostic, new("fe_c_search", "connect_started", 0, Outcome: "started"));
        var session = await AntSdkDeviceSession.OpenFirstAsync(cancellationToken, _diagnostic).ConfigureAwait(false);
        _session = session;
        try
        {
            var device = session.Device;
            var channel = device.getChannel(0);
            _channel = channel;
            channel.channelResponse += OnChannelResponse;
            RunCommand(_diagnostic, "extended_source_id", "RX_EXT_MESGS_ENABLE_0x66",
                () => device.enableRxExtendedMessages(true, 1000));
            var keyLoadTimer = Stopwatch.StartNew();
            byte[] key;
            try { key = AntNetworkKeyMaterial.LoadEmbedded(typeof(AntFeCTrainerClient).Assembly); }
            catch (Exception ex)
            {
                Emit(_diagnostic, new("embedded_configuration", "load_network_key", keyLoadTimer.Elapsed.TotalMilliseconds,
                    Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw;
            }
            Emit(_diagnostic, new("embedded_configuration", "load_network_key", keyLoadTimer.Elapsed.TotalMilliseconds,
                Outcome: "loaded_8_bytes"));
            try
            {
                RunCommand(_diagnostic, "network_configuration", "NETWORK_KEY_0x46",
                    () => device.setNetworkKey(NetworkNumber, key, 1000));
            }
            finally { Array.Clear(key, 0, key.Length); }
            var knownNumber = _targetDevice?.DeviceNumber ?? _settings?.LastAntDeviceNumber;
            var knownType = _targetDevice?.DeviceType ?? _settings?.LastAntDeviceType;
            var knownTransmission = _targetDevice?.TransmissionType ?? _settings?.LastAntTransmissionType;
            var targetNumber = knownNumber.GetValueOrDefault();
            var pairingEnabled = !knownNumber.HasValue;
            var targetType = knownType ?? AntFeCProtocol.DeviceType;
            var targetTransmission = knownTransmission ?? (byte)0;
            RunCommand(_diagnostic, "channel_configuration", "ASSIGN_CHANNEL_0x42",
                () => channel.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, NetworkNumber, 1000));
            RunCommand(_diagnostic, "channel_configuration", "CHANNEL_ID_0x51",
                () => channel.setChannelID(targetNumber, pairingEnabled, targetType, targetTransmission, 1000));
            RunCommand(_diagnostic, "channel_configuration", "CHANNEL_MESG_PERIOD_0x43",
                () => channel.setChannelPeriod(AntFeCProtocol.ChannelPeriod, 1000));
            RunCommand(_diagnostic, "channel_configuration", "CHANNEL_RADIO_FREQ_0x45",
                () => channel.setChannelFreq(AntFeCProtocol.RadioFrequency, 1000));
            RunCommand(_diagnostic, "channel_configuration", "CHANNEL_SEARCH_TIMEOUT_0x44",
                () => channel.setChannelSearchTimeout(12, 1000));
            RunCommand(_diagnostic, "fe_c_search", "OPEN_CHANNEL_0x4B", () => channel.openChannel(1000));
            Emit(_diagnostic, new("fe_c_search", "search_started", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: "listening"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try { await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Emit(_diagnostic, new("fe_c_readiness", "timed_out", _connectionTimer.Elapsed.TotalMilliseconds,
                    Outcome: $"general={_generalPage is not null};trainer={_trainerPage is not null};capabilities={_capabilities.HasValue}"));
                throw new TimeoutException("FE-C readiness timed out. Check ANT diagnostics for trainer pages, capabilities requests, and channel events.");
            }
            Emit(_diagnostic, new("fe_c_readiness", "ready", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: "telemetry_and_capabilities_received"));
        }
        catch
        {
            Emit(_diagnostic, new("ant_connect", "failed", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: "failed"));
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
                var configResult = await SendAcknowledgedPageAsync(channel, preamble,
                    "trainer_configuration", 3000, cancellationToken).ConfigureAwait(false);
                if (configResult != ANT_ReferenceLibrary.MessagingReturnCode.Pass)
                    throw new IOException("The FE-C trainer did not acknowledge rider and bicycle weight configuration.");
                _userConfigSent = true;
            }
            _commandStatus = NewSignal<AntFeCCommandStatus>();
            var result = await SendAcknowledgedPageAsync(channel, page,
                "trainer_control", 3000, cancellationToken).ConfigureAwait(false);
            if (result != ANT_ReferenceLibrary.MessagingReturnCode.Pass)
                throw new IOException($"FE-C acknowledged control failed ({result}).");
            var statusRequest = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCommandStatus);
            var requestResult = await SendAcknowledgedPageAsync(channel, statusRequest,
                "control_acknowledgement", 2000, cancellationToken).ConfigureAwait(false);
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
                Emit(_diagnostic, new("channel_event", "response_event", _connectionTimer.Elapsed.TotalMilliseconds,
                    ResponseId: response.responseID, EventCode: (byte)eventCode,
                    Outcome: $"message_id_{(byte)response.getMessageID():X2}"));
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
                Emit(_diagnostic, new("trainer_identification", "channel_id_received",
                    _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                    Outcome: "identified", DeviceNumber: ConnectedDeviceNumber,
                    DeviceType: ConnectedDeviceType, TransmissionType: ConnectedTransmissionType));
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
            if (_reportedPages.Add(page[0]))
                Emit(_diagnostic, new("fe_c_pages", "data_page_received", _connectionTimer.Elapsed.TotalMilliseconds,
                    ResponseId: response.responseID, DataPage: page[0], Outcome: "first_page"));
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
                {
                    Emit(_diagnostic, new("control_acknowledgement", "command_status_received",
                        _connectionTimer.Elapsed.TotalMilliseconds, DataPage: page[0],
                        Outcome: $"command_{status.CommandId:X2};status_{status.Status:X2}"));
                    _commandStatus.TrySetResult(status);
                }

                if (_generalPage is not null || _trainerPage is not null)
                {
                    var g = _generalPage;
                    var t = _trainerPage;
                    _latest = new(now, t?.PowerWatts ?? 0, t?.CadenceRpm ?? 0, g?.SpeedKph ?? 0,
                        g?.HeartRateBpm, t?.HasPowerMeasurement == true, t?.HasCadenceMeasurement == true,
                        true, g?.HasSpeedMeasurement == true);
                }
                if (AntFeCProtocol.IsReady(_generalPage is not null, _trainerPage is not null, _capabilities.HasValue))
                {
                    _isReady = true;
                    _ready.TrySetResult();
                }
                if (IsConnected && !_pageRequestSent && page[0] is AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer)
                {
                    _pageRequestSent = true;
                    var channel = _channel;
                    if (channel is not null) _ = RequestCapabilitiesAsync(channel);
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

    private void EnsureReady()
    {
        if (!_isReady || _channel is null) throw new InvalidOperationException("The FE-C trainer is not connected and ready.");
    }

    private async Task RequestCapabilitiesAsync(ANT_Channel channel)
    {
        var request = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCapabilities);
        while (AntFeCProtocol.ShouldRetryCapabilityRequest(_capabilities.HasValue, _capabilityRequestAttempts) && IsConnected)
        {
            var attempt = ++_capabilityRequestAttempts;
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await SendAcknowledgedPageAsync(channel, request,
                    "capability_request", 1500, CancellationToken.None).ConfigureAwait(false);
                Emit(_diagnostic, new("capability_request", "attempt_completed", timer.Elapsed.TotalMilliseconds,
                    Outcome: $"attempt_{attempt}_request_page_{AntFeCProtocol.PageCapabilities}_{result}"));
            }
            catch (Exception ex)
            {
                Emit(_diagnostic, new("capability_request", "request_page_54", timer.Elapsed.TotalMilliseconds,
                    CommandId: "ACKNOWLEDGED_DATA_0x4F", DataPage: AntFeCProtocol.PageRequest,
                    Outcome: $"attempt_{attempt}_failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        if (!_capabilities.HasValue)
            Emit(_diagnostic, new("capability_request", "retries_exhausted", _connectionTimer.Elapsed.TotalMilliseconds,
                DataPage: AntFeCProtocol.PageCapabilities, Outcome: "capabilities_not_received"));
    }

    private static bool RunCommand(Action<AntFeCDiagnosticEvent>? diagnostic, string stage,
        string commandId, Func<bool> command)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var succeeded = command();
            Emit(diagnostic, new(stage, "command_completed", timer.Elapsed.TotalMilliseconds,
                CommandId: commandId, Outcome: succeeded ? "accepted" : "rejected"));
            if (!succeeded) throw new IOException($"ANT command {commandId} was rejected.");
            return true;
        }
        catch (Exception ex)
        {
            Emit(diagnostic, new(stage, "command_failed", timer.Elapsed.TotalMilliseconds,
                CommandId: commandId, Outcome: "failed", ErrorType: ex.GetType().Name,
                ErrorMessage: ex.Message));
            throw;
        }
    }

    private async Task<ANT_ReferenceLibrary.MessagingReturnCode> SendAcknowledgedPageAsync(
        ANT_Channel channel, byte[] page, string stage, uint timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => channel.sendAcknowledgedData(page, timeoutMilliseconds),
                cancellationToken).ConfigureAwait(false);
            Emit(_diagnostic, new(stage, "acknowledged_data_sent", timer.Elapsed.TotalMilliseconds,
                CommandId: "ACKNOWLEDGED_DATA_0x4F", DataPage: page.Length > 0 ? page[0] : null,
                RequestedPage: page.Length > 6 && page[0] == AntFeCProtocol.PageRequest ? page[6] : null,
                Outcome: result.ToString()));
            return result;
        }
        catch (Exception ex)
        {
            Emit(_diagnostic, new(stage, "acknowledged_data_failed", timer.Elapsed.TotalMilliseconds,
                CommandId: "ACKNOWLEDGED_DATA_0x4F", DataPage: page.Length > 0 ? page[0] : null,
                RequestedPage: page.Length > 6 && page[0] == AntFeCProtocol.PageRequest ? page[6] : null,
                Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
            throw;
        }
    }

    private static void Emit(Action<AntFeCDiagnosticEvent>? diagnostic, AntFeCDiagnosticEvent entry)
    {
        try { diagnostic?.Invoke(entry); }
        catch { /* Diagnostics must not change ANT discovery or ride behavior. */ }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
