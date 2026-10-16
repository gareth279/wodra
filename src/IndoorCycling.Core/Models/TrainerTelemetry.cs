namespace IndoorCycling.Core.Models;

public sealed record TrainerTelemetry(
    DateTimeOffset Timestamp,
    int PowerWatts,
    int CadenceRpm,
    double SpeedKph,
	int? HeartRateBpm = null,
	bool HasPowerMeasurement = true,
	bool HasCadenceMeasurement = true,
	bool HasReceivedMeasurement = true,
	bool HasSpeedMeasurement = true,
	DateTimeOffset? HeartRateTimestamp = null)
{
	public bool IsFreshAt(DateTimeOffset now, TimeSpan? maximumAge = null)
	{
		var age = now - Timestamp;
		return age >= TimeSpan.Zero && age <= (maximumAge ?? TimeSpan.FromSeconds(5));
	}

	/// <summary>Preserves the original sample time while preventing stale sensor values from driving a ride.</summary>
	public TrainerTelemetry ForRiderInput(DateTimeOffset now, TimeSpan? maximumAge = null)
	{
		if (!IsFreshAt(now, maximumAge))
			return this with { PowerWatts = 0, CadenceRpm = 0, HasPowerMeasurement = false, HasCadenceMeasurement = false };
		return this with
		{
			PowerWatts = HasPowerMeasurement ? PowerWatts : 0,
			CadenceRpm = HasCadenceMeasurement ? CadenceRpm : 0
		};
	}

	/// <summary>Prevents stale sensor values from being written as fresh FIT samples.</summary>
	public TrainerTelemetry ForRecording(DateTimeOffset now, TimeSpan? maximumAge = null)
	{
		var heartRateTime = HeartRateTimestamp ?? Timestamp;
		var heartRateFresh = HeartRateBpm.HasValue && heartRateTime <= now && now - heartRateTime <= (maximumAge ?? TimeSpan.FromSeconds(5));
		return IsFreshAt(now, maximumAge)
			? this with { HeartRateBpm = heartRateFresh ? HeartRateBpm : null }
			: this with
			{
				PowerWatts = 0,
				CadenceRpm = 0,
				SpeedKph = 0,
				HeartRateBpm = heartRateFresh ? HeartRateBpm : null,
				HasPowerMeasurement = false,
				HasCadenceMeasurement = false,
				HasSpeedMeasurement = false
			};
	}
}
