using IndoorCycling.Core.Models;

namespace IndoorCycling.Trainer.Models;

public readonly record struct AntFeCDeviceIdentity(ushort DeviceNumber, byte DeviceType, byte TransmissionType)
{
    public string DisplayName => $"ANT+ FE-C · {DeviceNumber}/{DeviceType}/{TransmissionType}";
}

/// <summary>Prevents queued ANT commands and callbacks from crossing reconnect generations.</summary>
public static class AntFeCConnectionAttemptPolicy
{
    public static bool IsCurrent(long operationAttemptId, long activeAttemptId)
        => operationAttemptId == activeAttemptId;
}

public readonly record struct AntFeCReceiveChannelSettings(ushort DeviceNumber, bool PairingEnabled,
    byte DeviceType, byte TransmissionType, ushort Period, byte RadioFrequency);

/// <summary>Decoded ANT PC SDK receive callback frame metadata, with no raw payload retention.</summary>
public readonly record struct AntReceiveFrame(byte MessageId, bool IsExtended, int PayloadLength,
    byte? DataPage, AntFeCDeviceIdentity? Source, string? RejectionReason,
    ushort? ManufacturerId = null, ushort? ProductId = null);

/// <summary>Parses the messageContents layout used by ANT PC SDK ANT_Response.</summary>
public static class AntFeCScanFrameParser
{
    public static AntReceiveFrame Parse(byte messageId, ReadOnlySpan<byte> messageContents,
        AntFeCDeviceIdentity? channelIdentity = null)
    {
        var isExtended = messageContents.Length >= 13;
        var isLegacyData = messageId is 0x4E or 0x4F or 0x50;
        var isExtendedData = messageId is 0x5D or 0x5E or 0x5F;
        if (!isLegacyData && !isExtendedData)
            return new(messageId, isExtended, 0, null, null, "unsupported_message_id");

        var payloadOffset = isExtendedData ? 5 : 1;
        var hasExtendedId = isExtendedData && isExtended || isLegacyData && isExtended &&
            messageContents.Length >= 14 && (messageContents[9] & 0x80) != 0;
        if (messageContents.Length < payloadOffset + 8)
            return new(messageId, isExtended, Math.Max(0, messageContents.Length - payloadOffset), null, null, "payload_too_short");

        var sourceOffset = isExtendedData ? 1 : 10;
        AntFeCDeviceIdentity? source = null;
        if (hasExtendedId && messageContents.Length >= sourceOffset + 4)
        {
            source = new((ushort)(messageContents[sourceOffset] | messageContents[sourceOffset + 1] << 8),
                (byte)(messageContents[sourceOffset + 2] & 0x7F), messageContents[sourceOffset + 3]);
        }
        else if (isExtendedData)
            return new(messageId, isExtended, 8, messageContents[payloadOffset], null, "extended_source_id_missing");

        var page = messageContents[payloadOffset];
        var payloadLength = Math.Min(8, messageContents.Length - payloadOffset);
        if (payloadLength != 8) return new(messageId, isExtended, payloadLength, page, source, "payload_length_not_8");
        if (page is not (AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer or AntFeCProtocol.PageCapabilities or
            AntFeCProtocol.PageManufacturerInfo or AntFeCProtocol.PageProductInfo))
            return new(messageId, isExtended, payloadLength, page, source, "unsupported_page");
        ushort? manufacturerId = page == AntFeCProtocol.PageManufacturerInfo
            ? (ushort)(messageContents[payloadOffset + 1] | messageContents[payloadOffset + 2] << 8) : null;
        ushort? productId = page == AntFeCProtocol.PageProductInfo
            ? (ushort)(messageContents[payloadOffset + 3] | messageContents[payloadOffset + 4] << 8) : null;
        source ??= channelIdentity;
        if (source is not null && source.Value.DeviceType != AntFeCProtocol.DeviceType)
            return new(messageId, isExtended, payloadLength, page, source, "device_type_mismatch");
        if (source is { DeviceNumber: 0 })
            return new(messageId, isExtended, payloadLength, page, source, "device_number_zero");
        return new(messageId, isExtended, payloadLength, page, source, source is null ? "source_id_missing" : null,
            manufacturerId, productId);
    }

    /// <summary>Reads the source identity reported by ANT for a wildcard receive channel.</summary>
    public static AntFeCDeviceIdentity? ParseChannelIdentity(byte messageId, ReadOnlySpan<byte> messageContents)
    {
        if (messageId != 0x51 || messageContents.Length < 5) return null;
        var number = (ushort)(messageContents[1] | messageContents[2] << 8);
        var type = (byte)(messageContents[3] & 0x7F);
        var transmission = messageContents[4];
        return number == 0 ? null : new(number, type, transmission);
    }
}

/// <summary>Coordinates wildcard FE-C telemetry and the ANT channel-ID request response for one scan attempt.</summary>
public sealed class AntFeCDiscoveryCoordinator(byte expectedChannel = 0)
{
    public const int MaximumIdentityRequests = 3;
    private const int MaximumPendingFrames = 32;
    private readonly object _gate = new();
    private readonly HashSet<AntFeCDeviceIdentity> _candidates = [];
    private readonly List<(byte MessageId, byte[] MessageContents)> _pendingFrames = [];
    private AntFeCDeviceIdentity? _identity;
    private bool _active = true;
    private bool _identityRequestNeeded;
    private int _identityRequestAttempts;

    public bool HasTrainerDataReceived { get { lock (_gate) return _pendingFrames.Count > 0 || _candidates.Count > 0; } }
    public bool HasResolvedIdentity { get { lock (_gate) return _identity.HasValue; } }
    public AntFeCDeviceIdentity? ResolvedIdentity { get { lock (_gate) return _identity; } }
    public int IdentityRequestAttempts { get { lock (_gate) return _identityRequestAttempts; } }
    public int BufferedTelemetryFrameCount { get { lock (_gate) return _pendingFrames.Count; } }
    public IReadOnlyList<AntFeCDeviceIdentity> Candidates
    {
        get { lock (_gate) return _candidates.OrderBy(x => x.DeviceNumber).ThenBy(x => x.TransmissionType).ToArray(); }
    }

    public bool RequestIdentity()
    {
        lock (_gate)
        {
            if (!_active || _identity.HasValue || _identityRequestAttempts >= MaximumIdentityRequests) return false;
            _identityRequestNeeded = true;
            return true;
        }
    }

    public bool TryBeginIdentityRequest(out int attempt)
    {
        lock (_gate)
        {
            if (!_active || _identity.HasValue || !_identityRequestNeeded || _identityRequestAttempts >= MaximumIdentityRequests)
            {
                attempt = _identityRequestAttempts;
                return false;
            }
            _identityRequestNeeded = false;
            attempt = ++_identityRequestAttempts;
            return true;
        }
    }

    /// <summary>Marks a request attempt as timed out or failed, allowing a bounded retry.</summary>
    public bool MarkIdentityRequestIncomplete(int attempt)
    {
        lock (_gate)
        {
            if (!_active || _identity.HasValue || attempt != _identityRequestAttempts) return false;
            if (_identityRequestAttempts < MaximumIdentityRequests) _identityRequestNeeded = true;
            return _identityRequestNeeded;
        }
    }

    /// <summary>Consumes one ANT SDK channel callback. Only the active scan's configured channel can resolve identity.</summary>
    public AntFeCDiscoveryObservation Observe(byte messageId, byte channelNumber, ReadOnlySpan<byte> messageContents)
    {
        lock (_gate)
        {
            if (!_active) return new("stale_attempt", null, false, false);
            if (messageId == 0x51)
            {
                if (channelNumber != expectedChannel || messageContents.Length == 0 || messageContents[0] != expectedChannel)
                    return new("wrong_channel", null, false, false);
                if (_identityRequestAttempts == 0)
                    return new("identity_response_without_request", null, false, false);
                var identity = AntFeCScanFrameParser.ParseChannelIdentity(messageId, messageContents);
                if (identity is null)
                    return new("unresolved_wildcard_identity", null, false, false);
                if (identity.Value.DeviceType != AntFeCProtocol.DeviceType)
                    return new("device_type_mismatch", identity, false, false);
                _identity = identity;
                foreach (var pending in _pendingFrames)
                {
                    var replayed = AntFeCScanFrameParser.Parse(pending.MessageId, pending.MessageContents, identity);
                    if (replayed.RejectionReason is null && replayed.Source == identity) _candidates.Add(identity.Value);
                }
                var pendingCount = _pendingFrames.Count;
                _pendingFrames.Clear();
                return new("identity_resolved", identity, _candidates.Contains(identity.Value) && pendingCount > 0, false);
            }

            var frame = AntFeCScanFrameParser.Parse(messageId, messageContents, _identity);
            if (frame.RejectionReason is null && frame.DataPage is AntFeCProtocol.PageManufacturerInfo or AntFeCProtocol.PageProductInfo)
                return new("device_info_page", frame.Source, false, false);
            if (frame.RejectionReason == "source_id_missing" && frame.DataPage is { } page &&
                page is AntFeCProtocol.PageGeneral or AntFeCProtocol.PageTrainer or AntFeCProtocol.PageCapabilities)
            {
                if (_pendingFrames.Count < MaximumPendingFrames)
                    _pendingFrames.Add((messageId, messageContents.ToArray()));
                if (_identity.HasValue) _candidates.Add(_identity.Value);
                else if (_identityRequestAttempts < MaximumIdentityRequests) _identityRequestNeeded = true;
                return new(_identity.HasValue ? "trainer_page_identified" : "trainer_page_pending_identity",
                    _identity, _identity.HasValue, !_identity.HasValue && _identityRequestNeeded);
            }
            if (frame.RejectionReason is null && frame.Source is { } source)
            {
                _candidates.Add(source);
                return new(frame.IsExtended ? "extended_identity_page" : "trainer_page_identified", source, true, false);
            }
            return new(frame.RejectionReason ?? "frame_rejected", frame.Source, false, false);
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _active = false;
            _identityRequestNeeded = false;
        }
    }
}

public readonly record struct AntFeCDiscoveryObservation(string Outcome, AntFeCDeviceIdentity? Identity,
    bool TrainerIdentified, bool IdentityRequestNeeded);

/// <summary>Accepts FE-C capability pages only from the active attempt and configured receive channel.</summary>
public sealed class AntFeCCapabilityResponseTracker(byte expectedChannel, long attemptId)
{
    public byte? CapabilityFlags { get; private set; }
    public bool HasCapabilities => CapabilityFlags.HasValue;

    public string Observe(long responseAttemptId, byte channel, ReadOnlySpan<byte> page)
    {
        if (responseAttemptId != attemptId) return "stale_attempt";
        if (channel != expectedChannel) return "wrong_channel";
        if (!AntFeCProtocol.TryDecodeCapabilities(page, out var flags)) return "malformed_page";
        CapabilityFlags = flags;
        return "capabilities_received";
    }
}

/// <summary>Bounded aggregation for scan callback metadata. Does not retain payload bytes.</summary>
public sealed class AntReceiveDiagnosticCounts
{
    private const int MaximumBuckets = 64;
    private readonly Dictionary<(byte MessageId, string Source, bool Extended, int Length, byte? Page, string Reason), int> _counts = [];
    private int _overflow;

    public void Add(byte messageId, string callbackSource, bool extended, int payloadLength, byte? dataPage, string rejectionReason)
    {
        var key = (messageId, callbackSource, extended, payloadLength, dataPage, rejectionReason);
        if (_counts.TryGetValue(key, out var count)) _counts[key] = count + 1;
        else if (_counts.Count < MaximumBuckets) _counts.Add(key, 1);
        else _overflow++;
    }

    public IEnumerable<(byte MessageId, string CallbackSource, bool Extended, int PayloadLength, byte? DataPage, string RejectionReason, int Count)> Snapshot()
    {
        foreach (var (key, count) in _counts)
            yield return (key.MessageId, key.Source, key.Extended, key.Length, key.Page, key.Reason, count);
        if (_overflow > 0) yield return (0, "multiple", false, 0, null, "diagnostic_bucket_limit", _overflow);
    }
}

public enum AntFeCDiscoveryOutcome
{
    None,
    Connect,
    Choose
}

public sealed record AntFeCDiscoverySelection(AntFeCDiscoveryOutcome Outcome,
    IReadOnlyList<AntFeCDeviceIdentity> Candidates, AntFeCDeviceIdentity? Selected);

public static class AntFeCDiscoveryPolicy
{
    public static AntFeCDiscoverySelection Select(IEnumerable<AntFeCDeviceIdentity> discovered,
        AntFeCDeviceIdentity? preferredIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        var candidates = discovered.Where(device => device.DeviceNumber != 0 && device.DeviceType == AntFeCProtocol.DeviceType)
            .Distinct()
            .OrderBy(device => preferredIdentity.HasValue && device == preferredIdentity.Value ? 0 : 1)
            .ThenBy(device => device.DeviceNumber).ThenBy(device => device.TransmissionType).ToArray();
        return candidates.Length switch
        {
            0 => new(AntFeCDiscoveryOutcome.None, candidates, null),
            1 => new(AntFeCDiscoveryOutcome.Connect, candidates, candidates[0]),
            _ => new(AntFeCDiscoveryOutcome.Choose, candidates, null)
        };
    }
}

/// <summary>Initial connections may try a saved FE-C identity, but recovery stays pinned to it.</summary>
public static class AntFeCInitialConnectionPolicy
{
    public static bool ShouldFallbackToDiscovery(bool hasRememberedIdentity, bool isRecovery, bool cancelled) =>
        hasRememberedIdentity && !isRecovery && !cancelled;

    public static bool ShouldFallbackFromContinuousScan(int eligibleCandidateCount, bool cancelled) =>
        eligibleCandidateCount == 0 && !cancelled;

    public static bool CanPersistIdentity(AntFeCDeviceIdentity? resolvedIdentity, AntFeCDeviceIdentity? configuredTarget) =>
        resolvedIdentity is { DeviceNumber: > 0, DeviceType: AntFeCProtocol.DeviceType } identity &&
        (configuredTarget is null || configuredTarget.Value == identity);
}

/// <summary>FE-C page layouts and conversions from ANT+ Fitness Equipment Profile revision 5.</summary>
public static class AntFeCProtocol
{
    public const byte DeviceType = 17;
    public const ushort ChannelPeriod = 8192;
    public const byte RadioFrequency = 57;
    public const byte PageGeneral = 16;
    public const byte PageTrainer = 25;
    public const byte PageCapabilities = 54;
    public const byte PageCommandStatus = 71;
    public const byte PageRequest = 70;
    public const byte PageManufacturerInfo = 80;
    public const byte PageProductInfo = 81;
    public const int MaximumCapabilityRequestAttempts = 3;
    public static AntFeCReceiveChannelSettings DiscoveryChannel => new(
        DeviceNumber: 0, PairingEnabled: false, DeviceType: DeviceType, TransmissionType: 0,
        Period: ChannelPeriod, RadioFrequency: RadioFrequency);

    // FE-C page 54 is required by the profile on request, but some otherwise
    // interoperable trainers do not provide it. Telemetry readiness must not
    // depend on this metadata; callers retain Unknown and use compatibility mode.
    public static bool IsReady(bool hasGeneralPage, bool hasTrainerPage, bool hasCapabilities) =>
        hasGeneralPage && hasTrainerPage;

    public static bool ShouldRetryCapabilityRequest(bool hasCapabilities, int attempts) =>
        !hasCapabilities && attempts < MaximumCapabilityRequestAttempts;

    public static byte[] BuildPageRequest(byte requestedPage) =>
        // Common Page 70: command type 0x01 is slave-to-master, so bytes 1-2
        // (the responding slave serial number) must be invalid. Byte 5 asks
        // for one broadcast response; byte 6 is the requested page.
        [PageRequest, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, requestedPage, 0x01];

    /// <summary>Validates and decodes FE-C page 54 using profile revision 5.</summary>
    public static bool TryDecodeCapabilities(ReadOnlySpan<byte> page, out byte flags)
    {
        flags = 0;
        if (page.Length != 8 || page[0] != PageCapabilities ||
            page[1] != 0xFF || page[2] != 0xFF || page[3] != 0xFF || page[4] != 0xFF ||
            (page[7] & 0xF8) != 0)
            return false;
        flags = page[7];
        return true;
    }

    public static byte[] BuildTrackResistance(double gradePercent, double rollingResistance = 0.004)
    {
        if (!double.IsFinite(gradePercent) || !double.IsFinite(rollingResistance))
            throw new ArgumentOutOfRangeException(nameof(gradePercent));
        var grade = (ushort)Math.Clamp((int)Math.Round((Math.Clamp(gradePercent, -200, 200) + 200) * 100), 0, 40000);
        var crr = (byte)Math.Clamp((int)Math.Round(Math.Clamp(rollingResistance, 0, 0.0127) / 0.00005), 0, 254);
        return [51, 0xFF, 0xFF, 0xFF, 0xFF, (byte)grade, (byte)(grade >> 8), crr];
    }

    public static byte[] BuildTargetPower(double watts)
    {
        if (!double.IsFinite(watts)) throw new ArgumentOutOfRangeException(nameof(watts));
        var raw = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(watts, 0, 1000) * 4), 0, 4000);
        return [49, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, (byte)raw, (byte)(raw >> 8)];
    }

    public static byte[] BuildUserConfiguration(double riderWeightKg, double bicycleWeightKg)
    {
        if (!double.IsFinite(riderWeightKg) || !double.IsFinite(bicycleWeightKg))
            throw new ArgumentOutOfRangeException(nameof(riderWeightKg));
        var rider = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(riderWeightKg, 0, 655.34) * 100), 0, 65534);
        var bike = (ushort)Math.Clamp((int)Math.Round(Math.Clamp(bicycleWeightKg, 0, 50) * 20), 0, 1000);
        return [55, (byte)rider, (byte)(rider >> 8), 0xFF, (byte)(0x0F | ((bike & 0x0F) << 4)),
            (byte)(bike >> 4), 0xFF, 0x00];
    }

    public static TrainerTelemetry DecodeTelemetry(ReadOnlySpan<byte> page, DateTimeOffset timestamp,
        AntFeCPowerAccumulator accumulator)
    {
        if (page.Length != 8) throw new ArgumentException("FE-C data page must contain 8 bytes.", nameof(page));
        return page[0] switch
        {
            PageGeneral => DecodeGeneral(page, timestamp),
            PageTrainer => DecodeTrainer(page, timestamp, accumulator),
            _ => throw new ArgumentOutOfRangeException(nameof(page), "Unsupported FE-C telemetry page.")
        };
    }

    private static TrainerTelemetry DecodeGeneral(ReadOnlySpan<byte> p, DateTimeOffset timestamp)
    {
        var speed = (ushort)(p[4] | p[5] << 8);
        int? hr = p[6] == 0xFF ? null : p[6];
        return new(timestamp, 0, 0, speed == 0xFFFF ? 0 : speed * 0.0036, hr,
            HasPowerMeasurement: false, HasCadenceMeasurement: false,
            HasReceivedMeasurement: true, HasSpeedMeasurement: speed != 0xFFFF);
    }

    private static TrainerTelemetry DecodeTrainer(ReadOnlySpan<byte> p, DateTimeOffset timestamp,
        AntFeCPowerAccumulator accumulator)
    {
        int? cadence = p[2] == 0xFF ? null : p[2];
        var instantPower = (ushort)(p[5] | (p[6] & 0x0F) << 8);
        var accumulated = (ushort)(p[3] | p[4] << 8);
        var power = accumulator.Observe(p[1], accumulated, instantPower);
        return new(timestamp, power ?? 0, cadence ?? 0, 0, null,
            HasPowerMeasurement: power.HasValue, HasCadenceMeasurement: cadence.HasValue,
            HasReceivedMeasurement: true, HasSpeedMeasurement: false);
    }
}

/// <summary>Calculates interval power from rolling FE-C event and accumulated-power counters.</summary>
public sealed class AntFeCPowerAccumulator
{
    private byte? _event;
    private ushort? _power;

    public int? Observe(byte eventCount, ushort accumulatedPower, ushort instantaneousPower)
    {
        if (instantaneousPower == 0x0FFF)
        {
            // The profile defines this as invalid for both instantaneous and accumulated power.
            Reset();
            return null;
        }
        int? result = instantaneousPower;
        if (_event.HasValue && _power.HasValue)
        {
            var events = (eventCount - _event.Value + 256) & 0xFF;
            if (events > 0)
            {
                var delta = (accumulatedPower - _power.Value + 65536) & 0xFFFF;
                result = (int)Math.Round((double)delta / events);
            }
        }
        _event = eventCount;
        _power = accumulatedPower;
        return result;
    }

    public void Reset() { _event = null; _power = null; }
}

public readonly record struct AntFeCCommandStatus(byte CommandId, byte Sequence, byte Status)
{
    public bool IsSuccessful => Status == 0;
    public static AntFeCCommandStatus? TryParse(ReadOnlySpan<byte> page) =>
        page.Length == 8 && page[0] == AntFeCProtocol.PageCommandStatus
            ? new(page[1], page[2], page[3]) : null;
}
