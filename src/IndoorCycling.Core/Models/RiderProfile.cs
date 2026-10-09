namespace IndoorCycling.Core.Models;

public sealed record RiderProfile(
    string Name,
    double WeightKg,
    Guid ProfileId = default,
    double BikeWeightKg = 8,
    bool IsTestProfile = false,
    DateTimeOffset CreatedAtUtc = default,
    DateTimeOffset UpdatedAtUtc = default)
{
    public string DisplayName => Name;
    public double BodyWeightKg => WeightKg;
    public double TotalMassKg => BodyWeightKg + BikeWeightKg;
}
