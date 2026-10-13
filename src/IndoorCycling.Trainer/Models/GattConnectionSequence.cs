namespace IndoorCycling.Trainer.Models;

/// <summary>
/// Starts GATT service discovery before requiring the OS connection flag. Creating a
/// BluetoothLEDevice can leave ConnectionStatus at Disconnected until GATT is requested.
/// </summary>
public static class GattConnectionSequence
{
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
