namespace IndoorCycling.Trainer.Models;

public sealed class GattConnectionUnavailableException : IOException
{
    public GattConnectionUnavailableException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// Starts GATT service discovery before requiring the OS connection flag. Creating a
/// BluetoothLEDevice can leave ConnectionStatus at Disconnected until GATT is requested.
/// </summary>
public static class GattConnectionSequence
{
    public const int MaximumDiscoveryAttempts = 3;

    public static TimeSpan DelayBeforeAttempt(int failedAttemptNumber) => failedAttemptNumber switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMilliseconds(500),
        2 => TimeSpan.FromSeconds(2),
        _ => TimeSpan.FromSeconds(4)
    };

    /// <summary>Runs a bounded, cancellable sequence. Failed attempts must release their owned resources before returning/throwing.</summary>
    public static async Task<T> RunWithRetryAsync<T>(
        Func<int, CancellationToken, Task<T>> attempt,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int maximumAttempts = MaximumDiscoveryAttempts)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isTransient);
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);
        for (var number = 1; ; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await attempt(number, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (number < maximumAttempts && isTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                var delay = DelayBeforeAttempt(number);
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task<T> DiscoverAndWaitForConnectionAsync<T>(
        Func<CancellationToken, Task<T>> discoverServices,
        Func<bool> isConnected,
        TimeSpan discoveryTimeout,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discoverServices);
        ArgumentNullException.ThrowIfNull(isConnected);
        if (discoveryTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(discoveryTimeout));
        if (connectionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectionTimeout));

        var services = await discoverServices(cancellationToken)
            .WaitAsync(discoveryTimeout, cancellationToken)
            .ConfigureAwait(false);

        var deadline = DateTime.UtcNow + connectionTimeout;
        while (!isConnected())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("GATT discovery completed, but Windows did not report the BLE link as connected.");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }

        return services;
    }
}
