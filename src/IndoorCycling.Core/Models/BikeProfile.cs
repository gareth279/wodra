namespace IndoorCycling.Core.Models;

public sealed record BikeProfile(
    string Name,
    double WeightKg,
    double RollingResistanceCoefficient = 0.005,
    double DragCoefficientArea = 0.32
);