namespace IndoorCycling.Trainer.Models;

/// <summary>Safe ANT+ troubleshooting detail. Payloads never contain ANT network-key bytes.</summary>
public sealed record AntFeCDiagnosticEvent(
    string Stage,
    string Operation,
    double ElapsedMilliseconds,
    string? CommandId = null,
    int? ResponseId = null,
    int? EventCode = null,
    int? DataPage = null,
    int? RequestedPage = null,
    string? Outcome = null,
    string? ErrorType = null,
    string? ErrorMessage = null,
    int? UsbDeviceCount = null,
    int? TrainerCount = null,
    ushort? DeviceNumber = null,
    byte? DeviceType = null,
    byte? TransmissionType = null);
