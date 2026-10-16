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
    private ANT_Device.dDeviceResponseHandler? _deviceResponseHandler;
    private dChannelResponseHandler? _channelResponseHandler;
    private long _connectionAttemptId;
    private AntFeCCapabilityResponseTracker? _capabilityResponses;
    private TaskCompletionSource _ready = NewSignal();
    private TaskCompletionSource<AntFeCCommandStatus> _commandStatus = NewSignal<AntFeCCommandStatus>();
    private readonly AntFeCPowerAccumulator _power = new();
    private readonly AntFeCSessionState _state = new();
    private TrainerTelemetry _latest = new(DateTimeOffset.MinValue, 0, 0, 0,
        HasPowerMeasurement: false, HasCadenceMeasurement: false,
        HasReceivedMeasurement: false, HasSpeedMeasurement: false);
    private TrainerTelemetry? _generalPage;
    private TrainerTelemetry? _trainerPage;
    private byte? _capabilities;
    private AntFeCDeviceIdentity? _configuredDeviceIdentity;
    private bool _pageRequestSent;
    private int _capabilityRequestAttempts;
    private bool _userConfigSent;
    private int _identityRequestSent;
    private bool _isReady;
    private readonly Action<AntFeCDiagnosticEvent>? _diagnostic;
    private readonly HashSet<byte> _reportedPages = [];
    private readonly Stopwatch _connectionTimer = new();
    private TimeSpan _nextConnectionSearchTimeout = TimeSpan.FromSeconds(40);
    private double? _lastRequestedGrade;
    private double? _lastTransportAcknowledgedGrade;
    private CancellationTokenSource? _telemetryWatchdogCancellation;

    public bool IsConnected { get; private set; }
    public bool IsReady => _isReady;
    public string ActiveProtocol => "ANT+ FE-C";
    public string? ConnectedDeviceName => IsConnected ? $"ANT+ Fitness Equipment {ConnectedDeviceNumber}" : null;
    public ushort? ConnectedDeviceNumber { get; private set; }
    public byte? ConnectedDeviceType { get; private set; }
    public byte? ConnectedTransmissionType { get; private set; }
    public AntFeCDeviceIdentity? ConfiguredDeviceIdentity => _configuredDeviceIdentity;
    /// <summary>Identity observed in discovery, an extended source ID, or a channel-ID reply.</summary>
    public AntFeCDeviceIdentity? VerifiedDeviceIdentity { get; private set; }
    public byte? CapabilityFlags => _capabilities;
    public bool CanStartControlledRide => _state.SimulationSupport != AntFeCCapabilitySupport.Unsupported;
    public string CapabilitiesDescription
    {
        get
        {
            if (_capabilities is not { } flags) return "unknown · compatibility mode";
            var modes = new List<string>();
            if ((flags & 0x04) != 0) modes.Add("simulation");
            if ((flags & 0x02) != 0) modes.Add("target power");
            if ((flags & 0x01) != 0) modes.Add("basic resistance");
            return modes.Count == 0 ? "no control modes reported" : string.Join(", ", modes);
        }
    }
    public string ControlEvidenceDescription => _state.TransportState switch
    {
        AntFeCTransportState.NotAttempted => "Control not yet attempted; physical resistance unverified",
        AntFeCTransportState.Failed => "Control transport failed; physical resistance unverified",
        _ => $"Control transport acknowledged; trainer status {FormatTrainerStatus(_state.TrainerCommandState)}; physical resistance unverified"
    };
    public double? LastRequestedSimulationGradePercent => _lastRequestedGrade;
    public double? LastAppliedSimulationGradePercent => _lastTransportAcknowledgedGrade;
    public double? LastTransportAcknowledgedSimulationGradePercent => _lastTransportAcknowledgedGrade;
    public event Action? Disconnected;

    public AntFeCTrainerClient(TrainerSettings? settings = null, AntFeCDeviceIdentity? targetDevice = null,
        Action<AntFeCDiagnosticEvent>? diagnostic = null)
    {
        _settings = settings;
        _targetDevice = targetDevice;
        VerifiedDeviceIdentity = targetDevice;
        _diagnostic = diagnostic;
    }

    /// <summary>Uses a wildcard slave receive channel to discover FE-C broadcasts, matching the ANTware receive path.</summary>
    public static async Task<IReadOnlyList<AntFeCDeviceIdentity>> DiscoverAsync(
        CancellationToken cancellationToken = default, Action<AntFeCDiagnosticEvent>? diagnostic = null,
        Action<TimeSpan>? progress = null)
    {
        IReadOnlyList<AntFeCDeviceIdentity> scanCandidates;
        try
        {
            scanCandidates = await DiscoverPassAsync(continuousScan: true, cancellationToken, diagnostic, progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Emit(diagnostic, new("fe_c_discovery", "continuous_scan_fallback", 0,
                Outcome: "scan_mode_unavailable_using_proven_wildcard_receive", ErrorType: ex.GetType().Name,
                ErrorMessage: ex.Message));
            return await DiscoverPassAsync(continuousScan: false, cancellationToken, diagnostic, progress)
                .ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!AntFeCInitialConnectionPolicy.ShouldFallbackFromContinuousScan(scanCandidates.Count, cancellationToken.IsCancellationRequested))
            return scanCandidates;
        Emit(diagnostic, new("fe_c_discovery", "continuous_scan_fallback", 0,
            Outcome: "no_eligible_source_seen_using_proven_wildcard_receive"));
        return await DiscoverPassAsync(continuousScan: false, cancellationToken, diagnostic, progress)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AntFeCDeviceIdentity>> DiscoverPassAsync(bool continuousScan,
        CancellationToken cancellationToken, Action<AntFeCDiagnosticEvent>? diagnostic, Action<TimeSpan>? progress)
    {
        var discoveryTimer = Stopwatch.StartNew();
        Emit(diagnostic, new("user_configuration", "load_network_key", 0, Outcome: "started"));
        byte[] key;
        try { key = AntNetworkKeyMaterial.Load(); }
        catch (Exception ex)
        {
            Emit(diagnostic, new("user_configuration", "load_network_key", discoveryTimer.Elapsed.TotalMilliseconds,
                Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
            throw;
        }
        Emit(diagnostic, new("user_configuration", "load_network_key", discoveryTimer.Elapsed.TotalMilliseconds,
            Outcome: "loaded_configured_key"));
        try
        {
            using var session = await AntSdkDeviceSession.OpenFirstAsync(cancellationToken, diagnostic).ConfigureAwait(false);
            var device = session.Device;
            RunCommand(diagnostic, "network_configuration", "NETWORK_KEY_0x46",
                () => device.setNetworkKey(NetworkNumber, key, 1000));
            var channel = device.getChannel(0);
            var discoveryChannel = AntFeCProtocol.DiscoveryChannel;
            RunCommand(diagnostic, "channel_configuration", "ASSIGN_CHANNEL_0x42",
                () => channel.assignChannel(ANT_ReferenceLibrary.ChannelType.BASE_Slave_Receive_0x00, NetworkNumber, 1000));
            // ANT's RX scan channel must be profile-agnostic and uses scan mode rather than
            // a wildcard FE-C channel. The normal receive path remains the proven fallback.
            RunCommand(diagnostic, "channel_configuration", "CHANNEL_ID_0x51",
                () => continuousScan
                    ? channel.setChannelID(0, false, 0, 0, 1000)
                    : channel.setChannelID(discoveryChannel.DeviceNumber, discoveryChannel.PairingEnabled,
                        discoveryChannel.DeviceType, discoveryChannel.TransmissionType, 1000));
            if (!continuousScan)
            {
                RunCommand(diagnostic, "channel_configuration", "CHANNEL_MESG_PERIOD_0x43",
                    () => channel.setChannelPeriod(discoveryChannel.Period, 1000));
            }
            RunCommand(diagnostic, "channel_configuration", "CHANNEL_RADIO_FREQ_0x45",
                () => channel.setChannelFreq(discoveryChannel.RadioFrequency, 1000));
            if (continuousScan)
                RunCommand(diagnostic, "extended_source_id", "RX_EXT_MESGS_ENABLE_0x66",
                    () => device.enableRxExtendedMessages(true, 1000));
            Emit(diagnostic, new("channel_configuration", "effective_settings", discoveryTimer.Elapsed.TotalMilliseconds,
                Outcome: continuousScan ? "profile_agnostic_rx_scan;network=0;frequency=57" :
                    $"normal_slave_receive;network={NetworkNumber};device_number={discoveryChannel.DeviceNumber};pairing=off;device_type={discoveryChannel.DeviceType};transmission_type={discoveryChannel.TransmissionType};period={discoveryChannel.Period};frequency={discoveryChannel.RadioFrequency}"));

            var gate = new object();
            var coordinator = new AntFeCDiscoveryCoordinator();
            var scanAttemptId = Guid.NewGuid().ToString("N");
            var receiveCounts = new AntReceiveDiagnosticCounts();
            var receivedRadioFrames = 0;
            using var discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? identityRequestTask = null;
            void StartIdentityRequestLoop()
            {
                lock (gate)
                {
                    if (identityRequestTask is not null) return;
                    identityRequestTask = Task.Run(async () =>
                    {
                        while (!discoveryLifetime.IsCancellationRequested && coordinator.TryBeginIdentityRequest(out var attempt))
                        {
                            try
                            {
                                // The no-wait SDK API sends ANT Request Message (0x4D, CHANNEL_ID 0x51).
                                // The SDK routes its response through ANT_Device.deviceResponse.
                                device.requestMessage(0, ANT_ReferenceLibrary.RequestMessageID.CHANNEL_ID_0x51);
                                Emit(diagnostic, new("trainer_identification", "channel_id_request_sent",
                                    discoveryTimer.Elapsed.TotalMilliseconds, CommandId: "REQUEST_MESSAGE_0x4D",
                                    ResponseId: 0x51, Outcome: $"attempt_{attempt};response_wait_is_async",
                                    CallbackSource: "deviceResponse", AttemptId: scanAttemptId));
                            }
                            catch (Exception ex)
                            {
                                Emit(diagnostic, new("trainer_identification", "channel_id_request_failed",
                                    discoveryTimer.Elapsed.TotalMilliseconds, CommandId: "REQUEST_MESSAGE_0x4D",
                                    ResponseId: 0x51, Outcome: $"attempt_{attempt}", ErrorType: ex.GetType().Name,
                                    ErrorMessage: ex.Message, AttemptId: scanAttemptId));
                                coordinator.MarkIdentityRequestIncomplete(attempt);
                                continue;
                            }

                            try { await Task.Delay(TimeSpan.FromMilliseconds(400), discoveryLifetime.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (discoveryLifetime.IsCancellationRequested) { break; }
                            if (coordinator.HasResolvedIdentity) break;
                            var retry = coordinator.MarkIdentityRequestIncomplete(attempt);
                            Emit(diagnostic, new("trainer_identification", "channel_id_response_timeout",
                                discoveryTimer.Elapsed.TotalMilliseconds, CommandId: "REQUEST_MESSAGE_0x4D",
                                ResponseId: 0x51, Outcome: retry ? $"attempt_{attempt};retry_scheduled" : $"attempt_{attempt};retries_exhausted",
                                ErrorType: "TimeoutException", AttemptId: scanAttemptId));
                            if (!retry) break;
                        }
                    }, CancellationToken.None);
                }
            }
            void ObserveScanResponse(ANT_Response response, string callbackSource)
            {
                try
                {
                    if (response.responseID is 0x4E or 0x4F or 0x50 or 0x5D or 0x5E or 0x5F)
                        lock (gate) receivedRadioFrames++;
                    var observation = coordinator.Observe(response.responseID, response.antChannel, response.messageContents);
                    if (response.responseID == (byte)ANT_ReferenceLibrary.ANTMessageID.CHANNEL_ID_0x51)
                    {
                        var identity = observation.Identity;
                        lock (gate) receiveCounts.Add(response.responseID, callbackSource, false,
                            Math.Max(0, (response.messageContents?.Length ?? 0) - 1), null, observation.Outcome);
                        Emit(diagnostic, new("trainer_identification", "channel_id_response",
                            discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            Outcome: observation.Outcome, CallbackSource: callbackSource,
                            DeviceNumber: identity?.DeviceNumber, DeviceType: identity?.DeviceType,
                            TransmissionType: identity?.TransmissionType, AttemptId: scanAttemptId));
                        return;
                    }
                    if (observation.Outcome == "device_info_page" && observation.Identity is { } infoIdentity)
                    {
                        var infoFrame = AntFeCScanFrameParser.Parse(response.responseID, response.messageContents,
                            coordinator.ResolvedIdentity);
                        Emit(diagnostic, new("fe_c_discovery", "device_info_received",
                            discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            DataPage: infoFrame.DataPage, Outcome: "manufacturer_product_metadata",
                            CallbackSource: callbackSource, DeviceNumber: infoIdentity.DeviceNumber,
                            DeviceType: infoIdentity.DeviceType, TransmissionType: infoIdentity.TransmissionType,
                            ManufacturerId: infoFrame.ManufacturerId, ProductId: infoFrame.ProductId,
                            AttemptId: scanAttemptId));
                        return;
                    }
                    if (observation.IdentityRequestNeeded && !continuousScan) StartIdentityRequestLoop();
                    if (observation.Outcome is "trainer_page_pending_identity" or "trainer_page_identified")
                    {
                        var pendingFrame = AntFeCScanFrameParser.Parse(response.responseID, response.messageContents,
                            coordinator.ResolvedIdentity);
                        lock (gate) receiveCounts.Add(pendingFrame.MessageId, callbackSource, pendingFrame.IsExtended,
                            pendingFrame.PayloadLength, pendingFrame.DataPage,
                            observation.Outcome == "trainer_page_identified" ? "accepted_fe_c_page" : "awaiting_channel_identity");
                        Emit(diagnostic, new("fe_c_discovery", "trainer_data_received",
                            discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            DataPage: pendingFrame.DataPage, Outcome: observation.Outcome,
                            CallbackSource: callbackSource, IsExtended: pendingFrame.IsExtended,
                            PayloadLength: pendingFrame.PayloadLength, DeviceNumber: observation.Identity?.DeviceNumber,
                            DeviceType: observation.Identity?.DeviceType,
                            TransmissionType: observation.Identity?.TransmissionType, AttemptId: scanAttemptId));
                        return;
                    }
                    if (observation.Outcome == "identity_resolved")
                    {
                        var identity = observation.Identity;
                        Emit(diagnostic, new("trainer_identification", "channel_id_resolved",
                            discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            Outcome: observation.TrainerIdentified ? "trainer_data_identified" : "identity_waiting_for_trainer_data",
                            CallbackSource: callbackSource, DeviceNumber: identity?.DeviceNumber,
                            DeviceType: identity?.DeviceType, TransmissionType: identity?.TransmissionType,
                            AttemptId: scanAttemptId));
                        return;
                    }
                    var frame = AntFeCScanFrameParser.Parse(response.responseID, response.messageContents,
                        coordinator.ResolvedIdentity);
                    var rejection = frame.RejectionReason ?? "accepted_fe_c_page";
                    lock (gate) receiveCounts.Add(frame.MessageId, callbackSource, frame.IsExtended, frame.PayloadLength, frame.DataPage, rejection);
                    if (observation.Identity is { } sourceIdentity && observation.TrainerIdentified)
                        Emit(diagnostic, new("fe_c_discovery", "trainer_page_received",
                            discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            DataPage: frame.DataPage, Outcome: "matching_fe_c_page", CallbackSource: callbackSource,
                            IsExtended: frame.IsExtended, PayloadLength: frame.PayloadLength,
                            DeviceNumber: sourceIdentity.DeviceNumber, DeviceType: sourceIdentity.DeviceType,
                            TransmissionType: sourceIdentity.TransmissionType));
                }
                catch (Exception ex)
                {
                    lock (gate) receiveCounts.Add(response.responseID, callbackSource, response.messageContents?.Length >= 13,
                        Math.Max(0, (response.messageContents?.Length ?? 0) - 1), null, $"callback_exception_{ex.GetType().Name}");
                    Emit(diagnostic, new("fe_c_discovery", "receive_callback_failed",
                        discoveryTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                        Outcome: "callback_exception", CallbackSource: callbackSource,
                        ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                }
            }

            void OnScanChannelResponse(ANT_Response response) => ObserveScanResponse(response, "channelResponse");
            void OnScanDeviceResponse(ANT_Response response)
            {
                // ANT PC SDK routes CHANNEL_ID (0x51) to deviceResponse. Scan adapters may
                // also route broadcast frames here; accept both callbacks and dedupe identities.
                if (response.responseID == (byte)ANT_ReferenceLibrary.ANTMessageID.CHANNEL_ID_0x51 || continuousScan)
                    ObserveScanResponse(response, "deviceResponse");
            }

            channel.channelResponse += OnScanChannelResponse;
            device.deviceResponse += OnScanDeviceResponse;
            var opened = false;
            try
            {
                if (continuousScan)
                {
                    RunCommand(diagnostic, "fe_c_discovery", "OPEN_RX_SCAN_0x5B", () => device.openRxScanMode(1000));
                }
                else RunCommand(diagnostic, "fe_c_discovery", "OPEN_CHANNEL_0x4B", () => channel.openChannel(1000));
                opened = true;
                Emit(diagnostic, new("fe_c_discovery", "scan_window_started", discoveryTimer.Elapsed.TotalMilliseconds,
                    Outcome: "wildcard_receive_listening", AttemptId: scanAttemptId));
                var nextProgress = TimeSpan.Zero;
                TimeSpan? firstCandidateAt = null;
                var discoveryEndReason = "overall_window_expired";
                while (discoveryTimer.Elapsed < AntInitialConnectionPolicy.DiscoveryWindow)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidates = coordinator.Candidates;
                    if (candidates.Count > 0 && firstCandidateAt is null)
                        firstCandidateAt = discoveryTimer.Elapsed;
                    if (firstCandidateAt is { } first && AntInitialConnectionPolicy.ShouldCompleteDiscovery(
                            candidates.Count, discoveryTimer.Elapsed, first))
                    {
                        discoveryEndReason = candidates.Count == 1 ? "single_verified_candidate_settled" : "multiple_verified_candidates_settled";
                        break;
                    }
                    var remaining = AntInitialConnectionPolicy.DiscoveryWindow - discoveryTimer.Elapsed;
                    var slice = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                    await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
                    if (discoveryTimer.Elapsed >= nextProgress + TimeSpan.FromSeconds(1))
                    {
                        nextProgress = TimeSpan.FromSeconds((int)discoveryTimer.Elapsed.TotalSeconds);
                        progress?.Invoke(discoveryTimer.Elapsed);
                        Emit(diagnostic, new("fe_c_discovery", "search_progress",
                            discoveryTimer.Elapsed.TotalMilliseconds, Outcome: "listening_for_fe_c_broadcasts",
                            AttemptId: scanAttemptId));
                    }
                }
                Emit(diagnostic, new("fe_c_discovery", "selection_window_completed", discoveryTimer.Elapsed.TotalMilliseconds,
                    Outcome: discoveryEndReason, TrainerCount: coordinator.Candidates.Count, AttemptId: scanAttemptId));
            }
            finally
            {
                coordinator.Deactivate();
                discoveryLifetime.Cancel();
                if (identityRequestTask is not null)
                {
                    try { await identityRequestTask.ConfigureAwait(false); }
                    catch (Exception ex) { Emit(diagnostic, new("trainer_identification", "identity_request_worker_failed",
                        discoveryTimer.Elapsed.TotalMilliseconds, Outcome: "failed", ErrorType: ex.GetType().Name)); }
                }
                device.deviceResponse -= OnScanDeviceResponse;
                channel.channelResponse -= OnScanChannelResponse;
                if (opened)
                {
                    try { channel.closeChannel(1000); }
                    catch (Exception ex) { Emit(diagnostic, new("fe_c_discovery", "close_channel_failed", discoveryTimer.Elapsed.TotalMilliseconds, Outcome: "failed", ErrorType: ex.GetType().Name)); }
                }
                try { channel.unassignChannel(1000); }
                catch (Exception ex) { Emit(diagnostic, new("fe_c_discovery", "unassign_channel_failed", discoveryTimer.Elapsed.TotalMilliseconds, Outcome: "failed", ErrorType: ex.GetType().Name)); }
            }
            lock (gate)
            {
                foreach (var item in receiveCounts.Snapshot())
                    Emit(diagnostic, new("fe_c_receive", "frame_summary", discoveryTimer.Elapsed.TotalMilliseconds,
                        ResponseId: item.MessageId, Outcome: "bounded_receive_count", CallbackSource: item.CallbackSource,
                        IsExtended: item.Extended, PayloadLength: item.PayloadLength, DataPage: item.DataPage,
                        RejectionReason: item.RejectionReason, Count: item.Count));
                Emit(diagnostic, new("fe_c_receive", "radio_frame_summary", discoveryTimer.Elapsed.TotalMilliseconds,
                    Outcome: receivedRadioFrames == 0 ? "no_radio_frames_received" : "radio_frames_received",
                    CallbackSource: "channelResponse", Count: receivedRadioFrames));
            }
            var result = coordinator.Candidates.ToArray();
            Emit(diagnostic, new("fe_c_discovery", "scan_window_completed", discoveryTimer.Elapsed.TotalMilliseconds,
                Outcome: result.Length > 0 ? continuousScan ? "trainer_found_continuous_scan" : "trainer_found" : coordinator.HasTrainerDataReceived
                    ? "trainer_data_received_identification_failed" : "no_trainer_pages",
                TrainerCount: result.Length, AttemptId: scanAttemptId));
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
        var connectionAttemptId = Interlocked.Increment(ref _connectionAttemptId);
        _ready = NewSignal();
        _commandStatus = NewSignal<AntFeCCommandStatus>();
        _power.Reset();
        _generalPage = _trainerPage = null;
        _capabilities = null;
        _state.Reset();
        _capabilityResponses = new(expectedChannel: 0, attemptId: connectionAttemptId);
        _pageRequestSent = false;
        _identityRequestSent = 0;
        _capabilityRequestAttempts = 0;
        _configuredDeviceIdentity = null;
        _reportedPages.Clear();
        _connectionTimer.Restart();
        _userConfigSent = false;
        _isReady = false;

        Emit(_diagnostic, new("fe_c_search", "connect_started", 0, Outcome: "started"));
            Emit(_diagnostic, new("fe_c_search", "search_timeout_policy", 0,
                Outcome: $"{_nextConnectionSearchTimeout.TotalSeconds:0}s;initial_only_when_requested"));
        var session = await AntSdkDeviceSession.OpenFirstAsync(cancellationToken, _diagnostic).ConfigureAwait(false);
        _session = session;
        try
        {
            var device = session.Device;
            _deviceResponseHandler = response => OnDeviceResponse(response, connectionAttemptId);
            device.deviceResponse += _deviceResponseHandler;
            var channel = device.getChannel(0);
            _channel = channel;
            _channelResponseHandler = response => OnChannelResponse(response, connectionAttemptId);
            channel.channelResponse += _channelResponseHandler;
            RunCommand(_diagnostic, "extended_source_id", "RX_EXT_MESGS_ENABLE_0x66",
                () => device.enableRxExtendedMessages(true, 1000));
            var keyLoadTimer = Stopwatch.StartNew();
            byte[] key;
            try { key = AntNetworkKeyMaterial.Load(); }
            catch (Exception ex)
            {
                Emit(_diagnostic, new("user_configuration", "load_network_key", keyLoadTimer.Elapsed.TotalMilliseconds,
                    Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw;
            }
            Emit(_diagnostic, new("user_configuration", "load_network_key", keyLoadTimer.Elapsed.TotalMilliseconds,
                Outcome: "loaded_configured_key"));
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
            _configuredDeviceIdentity = _targetDevice ?? (knownNumber.HasValue
                ? new AntFeCDeviceIdentity(targetNumber, targetType, targetTransmission) : null);
            Emit(_diagnostic, new("channel_configuration", "effective_identity", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: knownNumber.HasValue ? "saved_or_selected_exact_target" : "wildcard_pairing_search",
                DeviceNumber: knownNumber, DeviceType: targetType, TransmissionType: targetTransmission));
            Emit(_diagnostic, new("trainer_identification", "identity_provenance", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: _targetDevice is not null ? "eligible_identity_from_fresh_discovery" :
                    knownNumber.HasValue ? "unverified_saved_target_until_source_response" : "wildcard_target_unresolved" ,
                DeviceNumber: knownNumber, DeviceType: targetType, TransmissionType: targetTransmission));
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
            var searchTimeout = _nextConnectionSearchTimeout;
            _nextConnectionSearchTimeout = TimeSpan.FromSeconds(40);
            timeout.CancelAfter(searchTimeout);
            var progressTask = ReportSearchProgressAsync(connectionAttemptId, timeout.Token);
            try { await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Emit(_diagnostic, new("fe_c_readiness", "timed_out", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: $"general={_generalPage is not null};trainer={_trainerPage is not null};capabilities={_capabilities.HasValue}"));
                throw new TimeoutException("FE-C readiness timed out. Check ANT diagnostics for trainer pages, capabilities requests, and channel events.");
            }
            finally
            {
                timeout.Cancel();
                try { await progressTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            Emit(_diagnostic, new("fe_c_readiness", "ready", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: _capabilities.HasValue ? "telemetry_ready_capabilities_known" : "telemetry_ready_capabilities_unknown_compatibility"));
            _telemetryWatchdogCancellation?.Cancel();
            _telemetryWatchdogCancellation?.Dispose();
            _telemetryWatchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = MonitorTelemetryFreshnessAsync(connectionAttemptId, _telemetryWatchdogCancellation.Token);
        }
        catch
        {
            Emit(_diagnostic, new("ant_connect", "failed", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: "failed"));
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Uses a shorter timeout for the first remembered-target attempt; recovery remains unchanged.</summary>
    public Task ConnectInitialAsync(CancellationToken cancellationToken = default)
    {
        _nextConnectionSearchTimeout = AntInitialConnectionPolicy.RememberedTargetTimeout;
        return ConnectAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectionAttemptId);
        _telemetryWatchdogCancellation?.Cancel();
        _telemetryWatchdogCancellation?.Dispose();
        _telemetryWatchdogCancellation = null;
        _isReady = false;
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            if (_channelResponseHandler is { } handler) channel.channelResponse -= handler;
            try { channel.closeChannel(1000); } catch (ANT_Exception) { }
            try { channel.unassignChannel(1000); } catch (ANT_Exception) { }
        }
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            try { if (_deviceResponseHandler is { } handler) session.Device.deviceResponse -= handler; }
            catch (ObjectDisposedException) { }
        }
        _channelResponseHandler = null;
        _deviceResponseHandler = null;
        session?.Dispose();
        IsConnected = false;
        ConnectedDeviceNumber = null;
        ConnectedDeviceType = null;
        ConnectedTransmissionType = null;
        _configuredDeviceIdentity = null;
        _generalPage = _trainerPage = null;
        _capabilities = null;
        _state.Reset();
        _latest = _latest with { Timestamp = DateTimeOffset.MinValue, HasReceivedMeasurement = false,
            HasPowerMeasurement = false, HasCadenceMeasurement = false, HasSpeedMeasurement = false };
        _ready.TrySetCanceled();
        _commandStatus.TrySetCanceled();
    }

    public async Task SetSimulationAsync(SimulationCommand command, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (_state.SimulationSupport == AntFeCCapabilitySupport.Unsupported)
            throw new NotSupportedException("This FE-C trainer reports that simulation mode is unsupported.");
        if (_state.SimulationSupport == AntFeCCapabilitySupport.Unknown)
            Emit(_diagnostic, new("trainer_control", "capability_unknown_compatibility_attempt",
                _connectionTimer.Elapsed.TotalMilliseconds, DataPage: 51, Outcome: "simulation_control_will_be_attempted"));
        _lastRequestedGrade = command.GradePercent;
        await SendControlAsync(AntFeCProtocol.BuildTrackResistance(command.GradePercent), cancellationToken,
            AntFeCProtocol.BuildUserConfiguration(command.RiderWeightKg, command.BikeWeightKg)).ConfigureAwait(false);
        _lastTransportAcknowledgedGrade = Math.Clamp(command.GradePercent, -200, 200);
    }

    public async Task SetResistanceAsync(ResistanceCommand command, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (_state.BasicResistanceSupport == AntFeCCapabilitySupport.Unsupported)
            throw new NotSupportedException("This FE-C trainer reports that basic resistance is unsupported.");
        var level = (byte)Math.Clamp((int)Math.Round(Math.Clamp(command.ResistanceLevel, 0, 100) * 2), 0, 200);
        await SendControlAsync([48, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, level], cancellationToken).ConfigureAwait(false);
    }

    public TrainerTelemetry GetLatestTelemetry()
    {
        lock (_gate) return _latest;
    }

    private async Task SendControlAsync(byte[] page, CancellationToken cancellationToken, byte[]? preamble = null)
    {
        var commandAttemptId = Volatile.Read(ref _connectionAttemptId);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!AntFeCConnectionAttemptPolicy.IsCurrent(commandAttemptId, Volatile.Read(ref _connectionAttemptId)) || !_isReady)
                throw new OperationCanceledException("The queued FE-C command belonged to a previous connection attempt.");
            var channel = _channel ?? throw new IOException("The FE-C channel is closed.");
            _commandStatus = NewSignal<AntFeCCommandStatus>();
            await _state.SendControlAsync(page, preamble is not null && !_userConfigSent ? preamble : null,
                async (outgoing, token) =>
                {
                    if (!AntFeCConnectionAttemptPolicy.IsCurrent(commandAttemptId, Volatile.Read(ref _connectionAttemptId)) || !_isReady)
                        throw new OperationCanceledException("The FE-C command was cancelled after its connection was lost.");
                    var configuration = outgoing.Length > 0 && outgoing[0] == 55;
                    var result = await SendAcknowledgedPageAsync(channel, outgoing,
                        configuration ? "trainer_configuration" : "trainer_control",
                        3000, token).ConfigureAwait(false);
                    if (configuration && result == ANT_ReferenceLibrary.MessagingReturnCode.Pass)
                        Emit(_diagnostic, new("trainer_configuration", "transport_acknowledged",
                            _connectionTimer.Elapsed.TotalMilliseconds, DataPage: outgoing[0],
                            Outcome: "user_configuration_delivered_to_trainer"));
                    return result == ANT_ReferenceLibrary.MessagingReturnCode.Pass;
                },
                () => _userConfigSent = true,
                cancellationToken).ConfigureAwait(false);
            Emit(_diagnostic, new("trainer_control", "transport_acknowledged", _connectionTimer.Elapsed.TotalMilliseconds,
                DataPage: page[0], Outcome: "ANT acknowledged-data accepted; physical resistance remains unverified"));
            // Page 71 can provide richer trainer-side evidence, but requesting and waiting for
            // it is optional and caused otherwise successful FE-C controls to time out.
            if (_commandStatus.Task.IsCompletedSuccessfully)
            {
                var status = _commandStatus.Task.Result;
                if (status.CommandId == page[0] && !status.IsSuccessful)
                    throw new IOException($"The trainer reported FE-C command status {status.Status} for page {status.CommandId}.");
            }
        }
        finally { _commandLock.Release(); }
    }

    private void OnChannelResponse(ANT_Response response, long connectionAttemptId)
    {
        try
        {
            if (!AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId, Volatile.Read(ref _connectionAttemptId))) return;
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
                var sourceIdentity = new AntFeCDeviceIdentity(id.deviceNumber, id.deviceTypeID, id.transmissionTypeID);
                if (id.deviceTypeID != AntFeCProtocol.DeviceType ||
                    _configuredDeviceIdentity is { } expectedSource && expectedSource != sourceIdentity)
                {
                    Emit(_diagnostic, new("trainer_identification", "source_identity_rejected",
                        _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                        Outcome: id.deviceTypeID != AntFeCProtocol.DeviceType ? "ineligible_device_type" : "source_did_not_match_configured_target",
                        CallbackSource: "extended_source_id", DeviceNumber: id.deviceNumber,
                        DeviceType: id.deviceTypeID, TransmissionType: id.transmissionTypeID));
                    return;
                }
                ConnectedDeviceNumber = id.deviceNumber;
                ConnectedDeviceType = id.deviceTypeID;
                ConnectedTransmissionType = id.transmissionTypeID;
                if (id.deviceNumber > 0) VerifiedDeviceIdentity = sourceIdentity;
            }
            else if (_configuredDeviceIdentity is { } configuredIdentity)
            {
                // The exact channel ID configured for this receive channel also identifies ordinary pages.
                ConnectedDeviceNumber = configuredIdentity.DeviceNumber;
                ConnectedDeviceType = configuredIdentity.DeviceType;
                ConnectedTransmissionType = configuredIdentity.TransmissionType;
            }
            IsConnected = true;
            if (_configuredDeviceIdentity is not null && VerifiedDeviceIdentity is null &&
                Interlocked.Exchange(ref _identityRequestSent, 1) == 0)
                RequestChannelIdentity(response.antChannel, connectionAttemptId);
            var now = new DateTimeOffset(response.timeReceived.ToUniversalTime());
            lock (_gate)
            {
                if (page[0] == AntFeCProtocol.PageGeneral)
                    _generalPage = AntFeCProtocol.DecodeTelemetry(page, now, _power);
                else if (page[0] == AntFeCProtocol.PageTrainer)
                    _trainerPage = AntFeCProtocol.DecodeTelemetry(page, now, _power);
                else if (page[0] == AntFeCProtocol.PageCapabilities)
                {
                    var receiveChannel = response.messageContents.Length > 0 ? response.messageContents[0] : byte.MaxValue;
                    var outcome = _capabilityResponses?.Observe(connectionAttemptId, receiveChannel, page) ?? "stale_attempt";
                    if (outcome == "capabilities_received" && _state.ObserveCapabilities(page))
                    {
                        _capabilities = _capabilityResponses!.CapabilityFlags;
                        Emit(_diagnostic, new("fe_c_capabilities", "response_accepted",
                            _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            DataPage: page[0], Outcome: "valid_capability_flags",
                            Count: _capabilities.GetValueOrDefault()));
                        Emit(_diagnostic, new("fe_c_capabilities", "support_updated",
                            _connectionTimer.Elapsed.TotalMilliseconds, DataPage: page[0],
                            Outcome: _state.SimulationSupport.ToString().ToLowerInvariant()));
                    }
                    else
                        Emit(_diagnostic, new("fe_c_capabilities", "response_rejected",
                            _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                            DataPage: page[0], Outcome: outcome));
                }
                else if (AntFeCCommandStatus.TryParse(page) is { } status)
                {
                    Emit(_diagnostic, new("control_acknowledgement", "command_status_received",
                        _connectionTimer.Elapsed.TotalMilliseconds, DataPage: page[0],
                        Outcome: $"command_{status.CommandId:X2};status_{status.Status:X2}"));
                    if (_state.ObserveCommandStatus(status)) _commandStatus.TrySetResult(status);
                }

                if ((page[0] is AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer) &&
                    (_generalPage is not null || _trainerPage is not null))
                {
                    var g = _generalPage;
                    var t = _trainerPage;
                    // FE-C page 25 carries the live trainer power/cadence. Page 16 can continue
                    // arriving while page 25 is lost, so it must not refresh trainer freshness.
                    _latest = new(t?.Timestamp ?? DateTimeOffset.MinValue, t?.PowerWatts ?? 0,
                        t?.CadenceRpm ?? 0, g?.SpeedKph ?? 0,
                        g?.HeartRateBpm, t?.HasPowerMeasurement == true, t?.HasCadenceMeasurement == true,
                        t is not null, g?.HasSpeedMeasurement == true);
                }
                if (page[0] is AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer)
                    _state.ObserveTelemetryPage(page[0]);
                if (_state.IsTelemetryReady)
                {
                    _isReady = true;
                    _ready.TrySetResult();
                }
                if (IsConnected && !_pageRequestSent && page[0] is AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer)
                {
                    _pageRequestSent = true;
                    var channel = _channel;
                    if (channel is not null) _ = RequestCapabilitiesAsync(channel, connectionAttemptId);
                }
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(new IOException("FE-C channel processing failed.", ex));
        }
    }

    private void OnDeviceResponse(ANT_Response response, long connectionAttemptId)
    {
        if (!AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId, Volatile.Read(ref _connectionAttemptId))) return;
        if (response.responseID != (byte)ANT_ReferenceLibrary.ANTMessageID.CHANNEL_ID_0x51) return;
        try
        {
            var identity = AntFeCScanFrameParser.ParseChannelIdentity(response.responseID, response.messageContents);
            if (response.antChannel != 0 || response.messageContents.Length == 0 || response.messageContents[0] != 0 ||
                identity is null || identity.Value.DeviceType != AntFeCProtocol.DeviceType)
            {
                Emit(_diagnostic, new("trainer_identification", "channel_id_response_rejected",
                    _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                    Outcome: response.antChannel != 0 || response.messageContents.Length == 0 || response.messageContents[0] != 0
                        ? "wrong_channel" : identity is null ? "unresolved_wildcard_identity" : "device_type_mismatch",
                    CallbackSource: "deviceResponse"));
                return;
            }

            if (_configuredDeviceIdentity is { } target && target != identity.Value)
            {
                Emit(_diagnostic, new("trainer_identification", "channel_id_response_rejected",
                    _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                    Outcome: "selected_identity_mismatch", CallbackSource: "deviceResponse",
                    DeviceNumber: identity.Value.DeviceNumber, DeviceType: identity.Value.DeviceType,
                    TransmissionType: identity.Value.TransmissionType));
                return;
            }

            ConnectedDeviceNumber = identity.Value.DeviceNumber;
            ConnectedDeviceType = identity.Value.DeviceType;
            ConnectedTransmissionType = identity.Value.TransmissionType;
            VerifiedDeviceIdentity = identity.Value;
            Emit(_diagnostic, new("trainer_identification", "channel_id_received",
                _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                Outcome: "identified", CallbackSource: "deviceResponse",
                DeviceNumber: ConnectedDeviceNumber, DeviceType: ConnectedDeviceType,
                TransmissionType: ConnectedTransmissionType));
        }
        catch (Exception ex)
        {
            Emit(_diagnostic, new("trainer_identification", "channel_id_callback_failed",
                _connectionTimer.Elapsed.TotalMilliseconds, ResponseId: response.responseID,
                Outcome: "callback_exception", CallbackSource: "deviceResponse",
                ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
        }
    }

    private void RequestChannelIdentity(byte channelNumber, long connectionAttemptId)
    {
        var device = _session?.Device;
        if (device is null) return;
        _ = Task.Run(() =>
        {
            if (!AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId, Volatile.Read(ref _connectionAttemptId))) return;
            try
            {
                device.requestMessage(channelNumber, ANT_ReferenceLibrary.RequestMessageID.CHANNEL_ID_0x51);
                Emit(_diagnostic, new("trainer_identification", "channel_id_request_sent",
                    _connectionTimer.Elapsed.TotalMilliseconds, CommandId: "REQUEST_MESSAGE_0x4D",
                    ResponseId: 0x51, Outcome: "awaiting_identity_verification", CallbackSource: "deviceResponse"));
            }
            catch (Exception ex)
            {
                Emit(_diagnostic, new("trainer_identification", "channel_id_request_failed",
                    _connectionTimer.Elapsed.TotalMilliseconds, CommandId: "REQUEST_MESSAGE_0x4D",
                    ResponseId: 0x51, Outcome: "identity_remains_unverified", ErrorType: ex.GetType().Name,
                    ErrorMessage: ex.Message));
            }
        });
    }

    private void MarkDisconnected()
    {
        var wasConnected = IsConnected || _isReady;
        _isReady = false;
        IsConnected = false;
        _state.Reset();
        _capabilities = null;
        lock (_gate)
        {
            _generalPage = _trainerPage = null;
            _latest = _latest with { Timestamp = DateTimeOffset.MinValue, HasReceivedMeasurement = false,
                HasPowerMeasurement = false, HasCadenceMeasurement = false, HasSpeedMeasurement = false };
        }
        if (wasConnected) Disconnected?.Invoke();
    }

    private async Task MonitorTelemetryFreshnessAsync(long connectionAttemptId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId, Volatile.Read(ref _connectionAttemptId)))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (!IsConnected) continue;
                DateTimeOffset lastFresh;
                lock (_gate) lastFresh = _trainerPage?.Timestamp ?? DateTimeOffset.MinValue;
                var now = DateTimeOffset.UtcNow;
                var age = now - lastFresh;
                if (!TrainerTelemetryFreshnessPolicy.HasExpired(lastFresh, now,
                        TrainerRecoveryPolicy.AntTelemetryLossTimeout)) continue;
                Emit(_diagnostic, new("telemetry_watchdog", "freshness_expired", _connectionTimer.Elapsed.TotalMilliseconds,
                    Outcome: $"telemetry_missing_or_stale;last_fresh_age_ms={(long)age.TotalMilliseconds}",
                    AttemptId: connectionAttemptId.ToString()));
                MarkDisconnected();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Emit(_diagnostic, new("telemetry_watchdog", "failed", _connectionTimer.Elapsed.TotalMilliseconds,
                Outcome: "watchdog_failed", ErrorType: ex.GetType().Name));
        }
    }

    private async Task ReportSearchProgressAsync(long connectionAttemptId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId, Volatile.Read(ref _connectionAttemptId)) && !_ready.Task.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (_ready.Task.IsCompleted) return;
                Emit(_diagnostic, new("fe_c_search", "target_search_progress", _connectionTimer.Elapsed.TotalMilliseconds,
                    Outcome: "waiting_for_selected_trainer_telemetry", DeviceNumber: _configuredDeviceIdentity?.DeviceNumber,
                    AttemptId: connectionAttemptId.ToString()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void EnsureReady()
    {
        if (!_isReady || _channel is null) throw new InvalidOperationException("The FE-C trainer is not connected and ready.");
    }

    private static string FormatTrainerStatus(AntFeCTrainerCommandState status) => status switch
    {
        AntFeCTrainerCommandState.Accepted => "accepted (reported)",
        AntFeCTrainerCommandState.Failed => "failed (reported)",
        AntFeCTrainerCommandState.Rejected => "rejected (reported)",
        AntFeCTrainerCommandState.Pending => "pending (reported)",
        AntFeCTrainerCommandState.Uninitialized => "uninitialized (reported)",
        _ => "unknown"
    };

    private async Task RequestCapabilitiesAsync(ANT_Channel channel, long connectionAttemptId)
    {
        var request = AntFeCProtocol.BuildPageRequest(AntFeCProtocol.PageCapabilities);
        while (AntFeCProtocol.ShouldRetryCapabilityRequest(_capabilities.HasValue, _capabilityRequestAttempts) && IsConnected &&
               AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId, Volatile.Read(ref _connectionAttemptId)))
        {
            var attempt = ++_capabilityRequestAttempts;
            var timer = Stopwatch.StartNew();
            try
            {
                await _commandLock.WaitAsync().ConfigureAwait(false);
                ANT_ReferenceLibrary.MessagingReturnCode result;
                try
                {
                    if (!IsConnected || !AntFeCConnectionAttemptPolicy.IsCurrent(connectionAttemptId,
                            Volatile.Read(ref _connectionAttemptId))) break;
                    result = await SendAcknowledgedPageAsync(channel, request,
                        "capability_request", 1500, CancellationToken.None).ConfigureAwait(false);
                }
                finally { _commandLock.Release(); }
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
