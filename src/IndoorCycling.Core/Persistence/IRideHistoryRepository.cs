using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Persistence;

public interface IRideHistoryRepository
    : IProfileRepository, IRideRepository, IMediaRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IProfileRepository
{
    Task<IReadOnlyList<RiderProfileRecord>> GetAllProfilesAsync(CancellationToken cancellationToken = default);
    Task<RiderProfileRecord> EnsureRiderProfileAsync(string defaultName, CancellationToken cancellationToken = default);
    Task<RiderProfileRecord?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(RiderProfileRecord profile, CancellationToken cancellationToken = default);
}

public interface IRideRepository
{
    Task SaveCompletedActivityAsync(RideActivityDraft activity, IReadOnlyCollection<RideActivityMedia> media, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RideActivity>> GetRecentActivitiesAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RideActivity>> GetRecentActivitiesForProfileAsync(Guid profileId, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RideActivity>> GetActivitiesForRouteAsync(string routeId, int limit = 50, CancellationToken cancellationToken = default);
    Task<RideActivity?> GetActivityAsync(Guid activityId, CancellationToken cancellationToken = default);
}

public interface IMediaRepository
{
    Task<IReadOnlyList<RideActivityMedia>> GetActivityMediaAsync(Guid activityId, CancellationToken cancellationToken = default);
}
