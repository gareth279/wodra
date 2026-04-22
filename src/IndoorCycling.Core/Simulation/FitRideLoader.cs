using Dynastream.Fit;
using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed class FitRideLoader
{
    public IReadOnlyList<ReplayFrame> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("FIT file not found.", path);
        }

        using var fitStream = File.OpenRead(path);

        if (!Decode.IsFIT(fitStream))
        {
            throw new InvalidOperationException("File is not a valid FIT file.");
        }

        fitStream.Position = 0;

        if (!Decode.CheckIntegrity(fitStream))
        {
            throw new InvalidOperationException("FIT file failed integrity check.");
        }

        fitStream.Position = 0;

        var decode = new Decode();
        var broadcaster = new MesgBroadcaster(decode);

        var frames = new List<ReplayFrame>();
        DateTimeOffset? firstTimestamp = null;

        broadcaster.RecordMesgEvent += (_, _, mesg) =>
        {
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

        decode.Read(fitStream, broadcaster, broadcaster);

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
        return semicircles.HasValue
            ? Conversions.SemicirclesToDegrees(semicircles.Value)
            : null;
    }
}