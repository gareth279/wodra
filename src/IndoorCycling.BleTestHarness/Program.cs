using System.Globalization;

var trainer = new WindowsBleTrainerClient();

Console.WriteLine("GWift BLE Test Harness");
Console.WriteLine("----------------------");
Console.WriteLine("Commands:");
Console.WriteLine("  scan                 - scan for BLE devices for 10 seconds");
Console.WriteLine("  connect-last         - connect to the last likely trainer seen");
Console.WriteLine("  dump-services        - dump GATT services on the connected device");
Console.WriteLine("  telemetry            - print current cached telemetry");
Console.WriteLine("  quit                 - exit");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;

    if (string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    try
    {
        switch (input.ToLowerInvariant())
        {
            case "scan":
                await trainer.ScanForDevicesAsync(TimeSpan.FromSeconds(10));
                break;

            case "connect-last":
                await trainer.ConnectToLastSeenDeviceAsync();
                break;

            case "dump-services":
                await trainer.DumpServicesAsync();
                break;

            case "telemetry":
                var t = trainer.GetLatestTelemetry();
                Console.WriteLine(
                    $"Timestamp={t.Timestamp:u}, Power={t.PowerWatts}W, Cadence={t.CadenceRpm}rpm, Speed={t.SpeedKph.ToString("0.0", CultureInfo.InvariantCulture)}kph");
                break;

            default:
                Console.WriteLine("Unknown command.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("ERROR:");
        Console.WriteLine(ex.ToString());
    }
}

await trainer.DisconnectAsync();
Console.WriteLine("Done.");