namespace IndoorCycling.Core.Models;

public sealed record TrainerTelemetry(
    DateTimeOffset Timestamp,
    int PowerWatts,
    int CadenceRpm,
    double SpeedKph,
	int? HeartRateBpm = null)
{
	public bool IsFreshAt(DateTimeOffset now, TimeSpan? maximumAge = null)
	{
		var age = now - Timestamp;
		return age >= TimeSpan.Zero && age <= (maximumAge ?? TimeSpan.FromSeconds(5));
	}
}
