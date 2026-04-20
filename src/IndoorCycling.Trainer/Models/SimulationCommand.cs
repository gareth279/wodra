namespace IndoorCycling.Trainer.Models;

public sealed record SimulationCommand(
    double GradePercent,
    double RiderWeightKg,
    double BikeWeightKg
);