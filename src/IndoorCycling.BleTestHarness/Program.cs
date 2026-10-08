using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Windows;

var trainer = new WindowsBleTrainerClient();



var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
var logPath = Path.Combine(logDir, $"wodra-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
var logger = new SessionLogger(logPath);

trainer.DebugEvent += evt => logger.Log("trainer_event", evt);

logger.Log("session_start", new { logPath });

var fitLoader = new FitRideLoader();
var rideReplayer = new RideReplayer();
ReplayFrame? latestReplayFrame = null;
string? loadedFitPath = null;

rideReplayer.FrameApplied += frame =>
{
    latestReplayFrame = frame;

    logger.Log("fit_frame", new
    {
        elapsed = frame.Elapsed,
        timestamp = frame.Timestamp,
        powerWatts = frame.PowerWatts,
        cadenceRpm = frame.CadenceRpm,
        heartRateBpm = frame.HeartRateBpm,
        speedMps = frame.SpeedMps,
        distanceMeters = frame.DistanceMeters,
        elevationMeters = frame.ElevationMeters,
        latitude = frame.Latitude,
        longitude = frame.Longitude
    });
};

Console.WriteLine("Wodra BLE Test Harness");
Console.WriteLine("----------------------");
Console.WriteLine("Commands:");
Console.WriteLine("  scan                 - scan for BLE devices for 10 seconds");
Console.WriteLine("  connect              - connect to the last likely trainer seen");
Console.WriteLine("  dump                 - dump GATT services on the connected device");
Console.WriteLine("  tail                 - open a second window to tail the log");
Console.WriteLine("  subs                 - subscribe to FTMS indoor bike data notifications");
Console.WriteLine("  cp                   - subscribe to FTMS control point indications");
Console.WriteLine("  req                  - request FTMS control");
Console.WriteLine("  tele                 - print current cached telemetry");
Console.WriteLine("  load-fit <path>      - load a FIT file for replay");
Console.WriteLine("  fit-info             - show loaded FIT replay info");
Console.WriteLine("  fit-play             - start FIT replay");
Console.WriteLine("  fit-pause            - pause FIT replay");
Console.WriteLine("  fit-stop             - stop FIT replay and rewind");
Console.WriteLine("  fit-rate <value>     - set FIT replay rate (e.g. 1, 2, 4)");
Console.WriteLine("  fit-seek <hh:mm:ss>  - seek FIT replay to elapsed time");
Console.WriteLine("  fit-tele             - print current FIT replay telemetry");
Console.WriteLine("  pow <watts>          - set target power");
Console.WriteLine("  ramp                 - ramp target power 150 -> 250 W");
Console.WriteLine("  quit                 - exit");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;
    logger.Log("command", new { input });

    if (string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
    {
        rideReplayer.Pause();
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
                logger.Log("scan_complete", new { seconds = 10 });
                break;

            case "connect":
                await trainer.ConnectToLastSeenDeviceAsync();
                logger.Log("connect", new { result = "success" });
                break;

            case "dump":
                await trainer.DumpServicesAsync();
                logger.Log("dump", new { result = "success" });
                break;

            case "tail":   
                logger.OpenTailWindow();
                Console.WriteLine($"Tailing log: {logger.LogPath}");
                logger.Log("tail_opened", new { logger.LogPath });
                break;

            case "subs":
                await trainer.SubscribeFtmsTelemetryAsync();
                logger.Log("subs", new { result = "success" });
                break;

            case "cp":
                await trainer.SubscribeControlPointAsync();
                logger.Log("cp", new { result = "success" });
                break;

            case "req":
                await trainer.RequestControlAsync();
                logger.Log("req", new { result = "success" });
                break;

            case "tele":
                var t = trainer.GetLatestTelemetry();
                Console.WriteLine(
                    $"Timestamp={t.Timestamp:u}, Power={t.PowerWatts}W, Cadence={t.CadenceRpm}rpm, Speed={t.SpeedKph.ToString("0.0", CultureInfo.InvariantCulture)}kph");

                logger.Log("telemetry_snapshot", new
                {
                    timestamp = t.Timestamp,
                    powerWatts = t.PowerWatts,
                    cadenceRpm = t.CadenceRpm,
                    speedKph = t.SpeedKph
                });
                break;

            case "load-fit":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: load-fit <path>");
                    break;
                }

                var fitPath = input["load-fit".Length..].Trim().Trim('"');
                TryLoadFit(fitPath);
                break;

            case "fit-info":
                if (!rideReplayer.IsLoaded)
                {
                    Console.WriteLine("No FIT ride loaded.");
                    break;
                }

                Console.WriteLine($"File: {loadedFitPath}");
                Console.WriteLine($"Frames: {rideReplayer.FrameCount}");
                Console.WriteLine($"Duration: {rideReplayer.Duration}");
                Console.WriteLine($"Playback rate: {rideReplayer.PlaybackRate:0.##}x");
                Console.WriteLine($"Playing: {rideReplayer.IsPlaying}");
                break;

            case "fit-play":
                if (!rideReplayer.IsLoaded)
                {
                    Console.WriteLine("Load a FIT file first.");
                    break;
                }

                rideReplayer.Play();
                Console.WriteLine("FIT replay started.");
                logger.Log("fit_play", new { rate = rideReplayer.PlaybackRate });
                break;

            case "fit-pause":
                rideReplayer.Pause();
                Console.WriteLine("FIT replay paused.");
                logger.Log("fit_pause", new { });
                break;

            case "fit-stop":
                rideReplayer.Stop();
                Console.WriteLine("FIT replay stopped and rewound.");
                logger.Log("fit_stop", new { });
                break;

            case "fit-rate":
                if (parts.Length < 2 ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var replayRate) ||
                    replayRate <= 0)
                {
                    Console.WriteLine("Usage: fit-rate <value>");
                    break;
                }

                rideReplayer.SetPlaybackRate(replayRate);
                Console.WriteLine($"FIT replay rate set to {rideReplayer.PlaybackRate:0.##}x");
                logger.Log("fit_rate", new { rate = replayRate });
                break;

            case "fit-seek":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: fit-seek <hh:mm:ss>");
                    break;
                }

                var seekText = input["fit-seek".Length..].Trim();
                if (!TimeSpan.TryParse(seekText, CultureInfo.InvariantCulture, out var seekElapsed))
                {
                    Console.WriteLine("Usage: fit-seek <hh:mm:ss>");
                    break;
                }

                rideReplayer.Seek(seekElapsed);
                Console.WriteLine($"FIT replay seeked to {seekElapsed}");
                logger.Log("fit_seek", new { elapsed = seekElapsed });
                break;

            case "fit-tele":
                PrintReplayTelemetry(latestReplayFrame, rideReplayer);
                if (latestReplayFrame is not null)
                {
                    logger.Log("fit_telemetry_snapshot", new
                    {
                        elapsed = latestReplayFrame.Elapsed,
                        timestamp = latestReplayFrame.Timestamp,
                        powerWatts = latestReplayFrame.PowerWatts,
                        cadenceRpm = latestReplayFrame.CadenceRpm,
                        heartRateBpm = latestReplayFrame.HeartRateBpm,
                        speedMps = latestReplayFrame.SpeedMps,
                        distanceMeters = latestReplayFrame.DistanceMeters
                    });
                }
                break;

            case "pow":
                if (parts.Length < 2 || !short.TryParse(parts[1], out var watts))
                {
                    Console.WriteLine("Usage: pow <watts>");
                    break;
                }

                await trainer.SetTargetPowerAsync(watts);
                logger.Log("set_target_power", new { watts });
                break;

            case "ramp":
                await trainer.RampTargetPowerAsync(
                    startWatts: 150,
                    endWatts: 250,
                    stepWatts: 10,
                    delay: TimeSpan.FromSeconds(3));
                logger.Log("ramp_target_power", new
                {
                    startWatts = 150,
                    endWatts = 250,
                    stepWatts = 10,
                    delaySeconds = 3
                });
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
        logger.Log("error", new
        {
            input,
            ex.Message,
            ex.StackTrace
        });
    }
}

await trainer.DisconnectAsync();
logger.Log("session_end", new { });
Console.WriteLine("Done.");

void TryLoadFit(string path)
{
    try
    {
        var frames = fitLoader.Load(path);
        rideReplayer.Load(frames);
        loadedFitPath = path;

        Console.WriteLine($"Loaded FIT: {path}");
        Console.WriteLine($"Frames: {rideReplayer.FrameCount}");
        Console.WriteLine($"Duration: {rideReplayer.Duration}");

        logger.Log("fit_loaded", new
        {
            path,
            frameCount = rideReplayer.FrameCount,
            duration = rideReplayer.Duration
        });

        PrintReplayTelemetry(latestReplayFrame, rideReplayer);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to load FIT: {ex.Message}");
        logger.Log("fit_load_failed", new { path, ex.Message, ex.StackTrace });
    }
}

static void PrintReplayTelemetry(ReplayFrame? frame, RideReplayer rideReplayer)
{
    if (frame is null)
    {
        Console.WriteLine("No FIT replay telemetry available.");
        return;
    }

    Console.WriteLine($"Elapsed:       {frame.Elapsed}");
    Console.WriteLine($"Timestamp:     {frame.Timestamp:u}");
    Console.WriteLine($"Power:         {FormatInt(frame.PowerWatts, "W")}");
    Console.WriteLine($"Cadence:       {FormatInt(frame.CadenceRpm, "rpm")}");
    Console.WriteLine($"Heart rate:    {FormatInt(frame.HeartRateBpm, "bpm")}");
    Console.WriteLine($"Speed:         {FormatSpeed(frame.SpeedMps)}");
    Console.WriteLine($"Distance:      {FormatDouble(frame.DistanceMeters, "m")}");
    Console.WriteLine($"Elevation:     {FormatDouble(frame.ElevationMeters, "m")}");
    Console.WriteLine($"Latitude:      {FormatDouble(frame.Latitude, "")}");
    Console.WriteLine($"Longitude:     {FormatDouble(frame.Longitude, "")}");
    Console.WriteLine($"Playing:       {rideReplayer.IsPlaying}");
    Console.WriteLine($"Playback rate: {rideReplayer.PlaybackRate:0.##}x");
}

static string FormatInt(int? value, string unit)
{
    return value.HasValue ? $"{value.Value} {unit}".Trim() : "-";
}

static string FormatDouble(double? value, string unit)
{
    return value.HasValue ? $"{value.Value:0.##} {unit}".Trim() : "-";
}

static string FormatSpeed(double? speedMps)
{
    if (!speedMps.HasValue)
    {
        return "-";
    }

    var kph = speedMps.Value * 3.6;
    return $"{kph:0.##} kph";
}