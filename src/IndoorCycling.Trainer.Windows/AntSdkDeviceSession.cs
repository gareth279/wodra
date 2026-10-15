using ANT_Managed_Library;
using System.Diagnostics;
using System.Collections.Concurrent;
using IndoorCycling.Trainer.Models;

namespace IndoorCycling.Trainer.Windows;

/// <summary>Owns the ANT SDK USB handle. An active session is exclusive to this app process.</summary>
public sealed class AntSdkDeviceSession : IDisposable
{
    private ANT_Device? _device;
    private readonly Action<AntFeCDiagnosticEvent>? _diagnostic;
    private readonly ANT_Device.dDeviceResponseHandler? _responseHandler;
    private readonly ConcurrentDictionary<string, byte> _reportedResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedCallbackErrors = new(StringComparer.Ordinal);
    private readonly Stopwatch _sessionTimer = Stopwatch.StartNew();

    private AntSdkDeviceSession(ANT_Device device, int deviceIndex,
        Action<AntFeCDiagnosticEvent>? diagnostic)
    {
        _device = device;
        DeviceIndex = deviceIndex;
        _diagnostic = diagnostic;
        if (diagnostic is not null)
        {
            _responseHandler = OnDeviceResponse;
            device.deviceResponse += _responseHandler;
        }
    }

    public int DeviceIndex { get; }
    public bool IsOpen => _device is not null;
    internal ANT_Device Device => _device ?? throw new ObjectDisposedException(nameof(AntSdkDeviceSession));

    /// <summary>
    /// Opens the first SDK-enumerated USB stick away from the Godot/UI thread.
    /// Cancellation is observed before opening and after the synchronous SDK call returns.
    /// </summary>
    public static async Task<AntSdkDeviceSession> OpenFirstAsync(CancellationToken cancellationToken = default,
        Action<AntFeCDiagnosticEvent>? diagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var managedLibraryPath = typeof(ANT_Common).Assembly.Location;
                var wrappedInBaseDirectory = File.Exists(Path.Combine(AppContext.BaseDirectory, "ANT_WrappedLib.dll"));
                var wrappedInWorkingDirectory = File.Exists(Path.Combine(Environment.CurrentDirectory, "ANT_WrappedLib.dll"));
                Emit(diagnostic, new("native_dependencies", "sdk_file_inventory", 0,
                    Outcome: $"process_{(Environment.Is64BitProcess ? "64" : "32")}_bit;managed_present={File.Exists(managedLibraryPath)};wrapped_base={wrappedInBaseDirectory};wrapped_cwd={wrappedInWorkingDirectory}"));
                var enumerationTimer = Stopwatch.StartNew();
                uint count;
                try { count = ANT_Common.getNumDetectedUSBDevices(); }
                catch (Exception ex)
                {
                    Emit(diagnostic, new("native_dependencies", "sdk_usb_enumeration", enumerationTimer.Elapsed.TotalMilliseconds,
                        CommandId: "SDK_ENUMERATE_USB", Outcome: "api_failed",
                        ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                    throw;
                }
                Emit(diagnostic, new("usb_enumeration", "getNumDetectedUSBDevices",
                    enumerationTimer.Elapsed.TotalMilliseconds, CommandId: "SDK_ENUMERATE_USB",
                    Outcome: count == 0 ? "api_succeeded_zero_devices" : "devices_found", UsbDeviceCount: (int)count));
                if (count == 0)
                    throw new AntSdkOpenException(AntSdkOpenFailure.DeviceNotEnumerated,
                        "The ANT SDK USB enumeration call succeeded but returned zero compatible devices. Check Windows device and driver status; see ANT diagnostics for dependency and enumeration details.");
                var openTimer = Stopwatch.StartNew();
                var device = new ANT_Device(0, 57600);
                Emit(diagnostic, new("usb_open", "ANT_Device", openTimer.Elapsed.TotalMilliseconds,
                    CommandId: "SDK_OPEN_DEVICE", Outcome: "opened", UsbDeviceCount: (int)count));
                return new AntSdkDeviceSession(device, 0, diagnostic);
            }
            catch (AntSdkOpenException) { throw; }
            catch (DllNotFoundException ex)
            {
                Emit(diagnostic, new("native_dependencies", "load_ant_libraries", 0,
                    Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw new AntSdkOpenException(AntSdkOpenFailure.NativeLibraryMissing,
                    "The ANT native library or its libusb dependency could not be loaded.", ex);
            }
            catch (BadImageFormatException ex)
            {
                Emit(diagnostic, new("native_dependencies", "load_ant_libraries", 0,
                    Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw new AntSdkOpenException(AntSdkOpenFailure.ArchitectureMismatch,
                    "The ANT native library architecture does not match this process.", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                Emit(diagnostic, new("native_dependencies", "load_ant_libraries", 0,
                    Outcome: "failed", ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw new AntSdkOpenException(AntSdkOpenFailure.NativeLibraryMismatch,
                    "The installed ANT native library does not expose the SDK entry points required by this app.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Emit(diagnostic, new("usb_open", "ANT_Device", 0,
                    CommandId: "SDK_OPEN_DEVICE", Outcome: "failed",
                    ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw new AntSdkOpenException(AntSdkOpenFailure.AccessDenied,
                    "Windows denied access to the ANTUSB-m. Check device permissions and close any app that owns it.", ex);
            }
            catch (ANT_Exception ex)
            {
                Emit(diagnostic, new("usb_open", "ANT_Device", 0,
                    CommandId: "SDK_OPEN_DEVICE", Outcome: "failed",
                    ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw new AntSdkOpenException(AntSdkOpenFailure.DeviceOpenFailed,
                    "The ANT SDK enumerated a USB stick but could not open it. Close ANTware/Garmin apps and verify the driver is started; the SDK's USB error cannot distinguish a busy device from a driver open failure.", ex);
            }
            catch (Exception ex)
            {
                Emit(diagnostic, new("usb_open", "ANT_Device", 0,
                    CommandId: "SDK_OPEN_DEVICE", Outcome: "failed",
                    ErrorType: ex.GetType().Name, ErrorMessage: ex.Message));
                throw;
            }
        }, CancellationToken.None).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            session.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return session;
    }

    public void Dispose()
    {
        var device = Interlocked.Exchange(ref _device, null);
        if (device is null) return;
        if (_responseHandler is not null) device.deviceResponse -= _responseHandler;
        var timer = Stopwatch.StartNew();
        ANT_Device.shutdownDeviceInstance(ref device);
        Emit(_diagnostic, new("usb_release", "shutdownDeviceInstance", timer.Elapsed.TotalMilliseconds,
            CommandId: "SDK_RELEASE_DEVICE", Outcome: "released"));
    }

    private void OnDeviceResponse(ANT_Response response)
    {
        try
        {
            var responseId = response.responseID;
            int? eventCode = null;
            int? eventMessageId = null;
            int? pageNumber = null;
            ushort? deviceNumber = null;
            byte? deviceType = null;
            byte? transmissionType = null;
            if (responseId == (byte)ANT_ReferenceLibrary.ANTMessageID.RESPONSE_EVENT_0x40 &&
                response.messageContents.Length >= 3)
            {
                eventCode = (byte)response.getChannelEventCode();
                eventMessageId = (byte)response.getMessageID();
            }
            else if (responseId is (byte)ANT_ReferenceLibrary.ANTMessageID.BROADCAST_DATA_0x4E or
                     (byte)ANT_ReferenceLibrary.ANTMessageID.ACKNOWLEDGED_DATA_0x4F or
                     (byte)ANT_ReferenceLibrary.ANTMessageID.BURST_DATA_0x50 or
                     (byte)ANT_ReferenceLibrary.ANTMessageID.EXT_BROADCAST_DATA_0x5D or
                     (byte)ANT_ReferenceLibrary.ANTMessageID.EXT_ACKNOWLEDGED_DATA_0x5E or
                     (byte)ANT_ReferenceLibrary.ANTMessageID.EXT_BURST_DATA_0x5F)
            {
                var data = response.getDataPayload();
                if (data.Length == 8) pageNumber = data[0];
                if (response.isExtended())
                {
                    var identity = response.getDeviceIDfromExt();
                    deviceNumber = identity.deviceNumber;
                    deviceType = identity.deviceTypeID;
                    transmissionType = identity.transmissionTypeID;
                }
            }
            var responseKey = $"{responseId}:{eventCode}:{eventMessageId}:{pageNumber}:{deviceNumber}:{deviceType}:{transmissionType}";
            if (!_reportedResponses.TryAdd(responseKey, 0)) return;
            Emit(_diagnostic, new("ant_response", "deviceResponse", _sessionTimer.Elapsed.TotalMilliseconds,
                ResponseId: responseId, EventCode: eventCode, DataPage: pageNumber,
                Outcome: eventMessageId.HasValue ? $"message_id_{eventMessageId.Value:X2}" : null,
                DeviceNumber: deviceNumber, DeviceType: deviceType,
                TransmissionType: transmissionType));
        }
        catch (Exception ex)
        {
            var type = ex.GetType().Name;
            if (_reportedCallbackErrors.TryAdd(type, 0))
                Emit(_diagnostic, new("ant_response", "device_response_callback_exception",
                    _sessionTimer.Elapsed.TotalMilliseconds, Outcome: "callback_exception",
                    ErrorType: type));
        }
    }

    private static void Emit(Action<AntFeCDiagnosticEvent>? diagnostic, AntFeCDiagnosticEvent entry)
    {
        try { diagnostic?.Invoke(entry); }
        catch { /* Diagnostics must never break device discovery or release. */ }
    }
}

public enum AntSdkOpenFailure
{
    DeviceNotEnumerated,
    NativeLibraryMissing,
    ArchitectureMismatch,
    NativeLibraryMismatch,
    AccessDenied,
    DeviceOpenFailed
}

public sealed class AntSdkOpenException : IOException
{
    public AntSdkOpenException(AntSdkOpenFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public AntSdkOpenFailure Failure { get; }
}
