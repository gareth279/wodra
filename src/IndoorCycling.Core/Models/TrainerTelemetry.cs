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
	bool HasSpeedMeasurement = true)
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
}
