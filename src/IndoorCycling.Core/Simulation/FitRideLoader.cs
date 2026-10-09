using Dynastream.Fit;
using IndoorCycling.Core.Models;
using File = System.IO.File;

namespace IndoorCycling.Core.Simulation;

public sealed class FitRideLoader
{
    public IReadOnlyList<(DateTimeOffset Timestamp, bool Running)> LoadTimerEvents(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var decode = new Decode();
        if (!decode.IsFIT(stream)) throw new InvalidOperationException("File is not a valid FIT file.");
        stream.Position = 0;
        if (!decode.CheckIntegrity(stream)) throw new InvalidOperationException("FIT file failed integrity check.");
        stream.Position = 0;
        var broadcaster = new MesgBroadcaster();
        var events = new List<(DateTimeOffset Timestamp, bool Running)>();
        decode.MesgEvent += broadcaster.OnMesg;
        decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        broadcaster.EventMesgEvent += (_, e) =>
        {
            if (e?.mesg is not EventMesg message || message.GetEvent() != Event.Timer) return;
            var timestamp = ToDateTimeOffset(message.GetTimestamp());
            if (timestamp is null) return;
            var type = message.GetEventType();
            if (type == EventType.Start) events.Add((timestamp.Value, true));
            else if (type == EventType.Stop || type == EventType.StopAll) events.Add((timestamp.Value, false));
        };
        decode.Read(stream);
        return events.OrderBy(e => e.Timestamp).ToArray();
    }

    public IReadOnlyList<ReplayFrame> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("FIT file not found.", path);
        }

        using var fitStream = File.OpenRead(path);

        var decode = new Decode();

        if (!decode.IsFIT(fitStream))
        {
            throw new InvalidOperationException("File is not a valid FIT file.");
        }

        fitStream.Position = 0;

        if (!decode.CheckIntegrity(fitStream))
        {
            throw new InvalidOperationException("FIT file failed integrity check.");
        }

        fitStream.Position = 0;

        var broadcaster = new MesgBroadcaster();

        var frames = new List<ReplayFrame>();
        DateTimeOffset? firstTimestamp = null;

        decode.MesgEvent += broadcaster.OnMesg;
        decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;

        broadcaster.RecordMesgEvent += (sender, e) =>
        {
            if (e?.mesg is not RecordMesg mesg)
            {
                return;
            }

            var timestamp = ToDateTimeOffset(mesg.GetTimestamp());
            if (timestamp is null)
            {
                return;
            }

            firstTimestamp ??= timestamp.Value;

            frames.Add(new ReplayFrame
            {
                Timestamp = timestamp.Value,
                Elapsed = timestamp.Value - firstTimestamp.Value,

                SpeedMps = ToNullableDouble(mesg.GetSpeed()),
                DistanceMeters = ToNullableDouble(mesg.GetDistance()),
                PowerWatts = ToNullableInt(mesg.GetPower()),
                CadenceRpm = ToNullableInt(mesg.GetCadence()),
                HeartRateBpm = ToNullableInt(mesg.GetHeartRate()),

                Latitude = ToDegreesNullable(mesg.GetPositionLat()),
                Longitude = ToDegreesNullable(mesg.GetPositionLong()),
                ElevationMeters = ToNullableDouble(mesg.GetAltitude())
            });
        };

        decode.Read(fitStream);

        return frames;
    }

    private static DateTimeOffset? ToDateTimeOffset(Dynastream.Fit.DateTime? fitDateTime)
    {
        if (fitDateTime is null)
        {
            return null;
        }

        var dt = fitDateTime.GetDateTime();
        var utc = System.DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static double? ToNullableDouble(float? value)
    {
        return value.HasValue ? value.Value : null;
    }

    private static int? ToNullableInt(byte? value)
    {
        return value.HasValue ? value.Value : null;
    }

    private static int? ToNullableInt(ushort? value)
    {
        return value.HasValue ? value.Value : null;
    }

    private static double? ToDegreesNullable(int? semicircles)
    {
        if (!semicircles.HasValue)
        {
            return null;
        }

        return semicircles.Value * (180.0 / 2147483648.0);
    }
}
