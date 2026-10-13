using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Models;

namespace IndoorCycling.Trainer.Abstractions;

public interface ITrainerClient
{
    bool IsConnected { get; }
    bool IsReady => IsConnected;
    double? LastRequestedSimulationGradePercent => null;
    double? LastAppliedSimulationGradePercent => null;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SetSimulationAsync(
        SimulationCommand command,
        CancellationToken cancellationToken = default);

    Task SetResistanceAsync(
        ResistanceCommand command,
        CancellationToken cancellationToken = default);

    TrainerTelemetry GetLatestTelemetry();
}
