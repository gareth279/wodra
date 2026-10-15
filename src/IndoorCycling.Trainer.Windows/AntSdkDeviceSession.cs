using ANT_Managed_Library;

namespace IndoorCycling.Trainer.Windows;

/// <summary>Owns the ANT SDK USB handle. An active session is exclusive to this app process.</summary>
public sealed class AntSdkDeviceSession : IDisposable
{
    private ANT_Device? _device;

    private AntSdkDeviceSession(ANT_Device device, int deviceIndex)
    {
        _device = device;
        DeviceIndex = deviceIndex;
    }

    public int DeviceIndex { get; }
    public bool IsOpen => _device is not null;
    internal ANT_Device Device => _device ?? throw new ObjectDisposedException(nameof(AntSdkDeviceSession));

    /// <summary>
    /// Opens the first SDK-enumerated USB stick away from the Godot/UI thread.
    /// Cancellation is observed before opening and after the synchronous SDK call returns.
    /// </summary>
    public static async Task<AntSdkDeviceSession> OpenFirstAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var count = ANT_Common.getNumDetectedUSBDevices();
                if (count == 0)
                    throw new AntSdkOpenException(AntSdkOpenFailure.DeviceNotEnumerated,
                        "The ANT SDK enumerated no compatible USB sticks. Check whether the dongle is connected and Windows has a started ANTUSB driver.");
                var device = new ANT_Device(0, 57600);
                return new AntSdkDeviceSession(device, 0);
            }
            catch (AntSdkOpenException) { throw; }
            catch (DllNotFoundException ex)
            {
                throw new AntSdkOpenException(AntSdkOpenFailure.NativeLibraryMissing,
                    "The ANT native library or its libusb dependency could not be loaded.", ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new AntSdkOpenException(AntSdkOpenFailure.ArchitectureMismatch,
                    "The ANT native library architecture does not match this process.", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new AntSdkOpenException(AntSdkOpenFailure.NativeLibraryMismatch,
                    "The installed ANT native library does not expose the SDK entry points required by this app.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new AntSdkOpenException(AntSdkOpenFailure.AccessDenied,
                    "Windows denied access to the ANTUSB-m. Check device permissions and close any app that owns it.", ex);
            }
            catch (ANT_Exception ex)
            {
                throw new AntSdkOpenException(AntSdkOpenFailure.DeviceOpenFailed,
                    "The ANT SDK enumerated a USB stick but could not open it. Close ANTware/Garmin apps and verify the driver is started; the SDK's USB error cannot distinguish a busy device from a driver open failure.", ex);
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
        ANT_Device.shutdownDeviceInstance(ref device);
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
