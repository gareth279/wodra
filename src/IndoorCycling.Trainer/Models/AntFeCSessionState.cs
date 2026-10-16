namespace IndoorCycling.Trainer.Models;

public enum AntFeCCapabilitySupport { Unknown, Supported, Unsupported }
public enum AntFeCTransportState { NotAttempted, Acknowledged, Failed }
public enum AntFeCTrainerCommandState { Unknown, Accepted, Failed, Rejected, Pending, Uninitialized }

/// <summary>
/// Thread-safe FE-C session facts shared by the ANT client and its simulated-transport tests.
/// Physical resistance is deliberately never inferred from packet delivery or page 71.
/// </summary>
public sealed class AntFeCSessionState
{
    private readonly object _gate = new();
    private bool _general;
    private bool _trainer;
    private byte? _capabilities;
    private byte? _pendingCommand;
    private AntFeCTransportState _transport;
    private AntFeCTrainerCommandState _trainerCommand;

    public bool IsTelemetryReady { get { lock (_gate) return _general && _trainer; } }
    public byte? CapabilityFlags { get { lock (_gate) return _capabilities; } }
    public AntFeCCapabilitySupport SimulationSupport => ModeSupport(0x04);
    public AntFeCCapabilitySupport BasicResistanceSupport => ModeSupport(0x01);
    public AntFeCTransportState TransportState { get { lock (_gate) return _transport; } }
    public AntFeCTrainerCommandState TrainerCommandState { get { lock (_gate) return _trainerCommand; } }
    public bool PhysicalResistanceVerified => false;

    public AntFeCCapabilitySupport ModeSupport(byte flag)
    {
        lock (_gate)
        {
            if (_capabilities is not { } flags) return AntFeCCapabilitySupport.Unknown;
            return (flags & flag) != 0 ? AntFeCCapabilitySupport.Supported : AntFeCCapabilitySupport.Unsupported;
        }
    }

    public void ObserveTelemetryPage(byte page)
    {
        lock (_gate)
        {
            if (page == AntFeCProtocol.PageGeneral) _general = true;
            if (page == AntFeCProtocol.PageTrainer) _trainer = true;
        }
    }

    public bool ObserveCapabilities(ReadOnlySpan<byte> page)
    {
        if (!AntFeCProtocol.TryDecodeCapabilities(page, out var flags)) return false;
        lock (_gate) _capabilities = flags;
        return true;
    }

    public void BeginControl(byte commandId)
    {
        lock (_gate)
        {
            _pendingCommand = commandId;
            _transport = AntFeCTransportState.NotAttempted;
            _trainerCommand = AntFeCTrainerCommandState.Unknown;
        }
    }

    public void RecordTransportResult(bool acknowledged)
    {
        lock (_gate) _transport = acknowledged ? AntFeCTransportState.Acknowledged : AntFeCTransportState.Failed;
    }

    public bool ObserveCommandStatus(AntFeCCommandStatus status)
    {
        lock (_gate)
        {
            if (_pendingCommand != status.CommandId) return false;
            _trainerCommand = status.Status switch
            {
                0 => AntFeCTrainerCommandState.Accepted,
                1 => AntFeCTrainerCommandState.Failed,
                2 => AntFeCTrainerCommandState.Rejected,
                3 => AntFeCTrainerCommandState.Rejected,
                4 => AntFeCTrainerCommandState.Pending,
                255 => AntFeCTrainerCommandState.Uninitialized,
                _ => AntFeCTrainerCommandState.Unknown
            };
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _general = _trainer = false;
            _capabilities = _pendingCommand = null;
            _transport = AntFeCTransportState.NotAttempted;
            _trainerCommand = AntFeCTrainerCommandState.Unknown;
        }
    }

    public async Task SendControlAsync(byte[] page, byte[]? userConfiguration,
        Func<byte[], CancellationToken, Task<bool>> sendAcknowledged,
        Action? userConfigurationAccepted = null, CancellationToken cancellationToken = default)
    {
        if (userConfiguration is not null)
        {
            if (!await sendAcknowledged(userConfiguration, cancellationToken).ConfigureAwait(false))
                throw new IOException("FE-C user configuration was not acknowledged by the ANT transport.");
            userConfigurationAccepted?.Invoke();
        }

        BeginControl(page[0]);
        bool acknowledged;
        try { acknowledged = await sendAcknowledged(page, cancellationToken).ConfigureAwait(false); }
        catch
        {
            RecordTransportResult(false);
            throw;
        }
        RecordTransportResult(acknowledged);
        if (!acknowledged) throw new IOException("FE-C control was not acknowledged by the ANT transport.");
    }
}
