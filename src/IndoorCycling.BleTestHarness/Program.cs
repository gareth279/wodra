using System.Globalization;

var trainer = new WindowsBleTrainerClient();

Console.WriteLine("GWift BLE Test Harness");
Console.WriteLine("----------------------");
Console.WriteLine("Commands:");
Console.WriteLine("  scan                 - scan for BLE devices for 10 seconds");
Console.WriteLine("  connect              - connect to the last likely trainer seen");
Console.WriteLine("  dump                 - dump GATT services on the connected device");
Console.WriteLine("  subs                 - subscribe to FTMS indoor bike data notifications");
Console.WriteLine("  cp                   - subscribe to FTMS control point indications");
Console.WriteLine("  req                  - request FTMS control");
Console.WriteLine("  tele                 - print current cached telemetry");
Console.WriteLine("  pow <watts>          - set target power");
Console.WriteLine("  ramp                 - ramp target power 150 -> 250 W");
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
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (cmd)
        {
            case "scan":
                await trainer.ScanForDevicesAsync(TimeSpan.FromSeconds(10));
                break;

            case "connect":
                await trainer.ConnectToLastSeenDeviceAsync();
                break;

            case "dump":
                await trainer.DumpServicesAsync();
                break;

            case "subs":
                await trainer.SubscribeFtmsTelemetryAsync();
                break;

            case "cp":
                await trainer.SubscribeControlPointAsync();
                break;

            case "req":
                await trainer.RequestControlAsync();
                break;

            case "tele":
                var t = trainer.GetLatestTelemetry();
                Console.WriteLine(
                    $"Timestamp={t.Timestamp:u}, Power={t.PowerWatts}W, Cadence={t.CadenceRpm}rpm, Speed={t.SpeedKph.ToString("0.0", CultureInfo.InvariantCulture)}kph");
                break;

            case "pow":
                if (parts.Length < 2 || !short.TryParse(parts[1], out var watts))
                {
                    Console.WriteLine("Usage: pow <watts>");
                    break;
                }

                await trainer.SetTargetPowerAsync(watts);
                break;

            case "ramp":
                await trainer.RampTargetPowerAsync(
                    startWatts: 150,
                    endWatts: 250,
                    stepWatts: 10,
                    delay: TimeSpan.FromSeconds(8));
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