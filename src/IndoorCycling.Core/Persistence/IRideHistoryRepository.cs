using IndoorCycling.Core.Models;
using IndoorCycling.Core.Progress;

namespace IndoorCycling.Core.Persistence;

public interface IRideHistoryRepository
    : IProfileRepository, IRideRepository, IMediaRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<RideHistoryPage> GetHistoryPageAsync(Guid profileId, string? search, string? routeId, DateTimeOffset? fromUtc, DateTimeOffset? toUtcExclusive, RideHistorySort sort, int offset, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RideHistoryBucket>> GetHistoryTrendAsync(Guid profileId, DateTimeOffset fromUtc, DateTimeOffset toUtcExclusive, bool monthly, string? search = null, string? routeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string RouteId, string RouteName)>> GetHistoryRoutesAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<AchievementActivityResult> SaveCompletedActivityWithAchievementsAsync(RideActivityDraft activity,
        IReadOnlyCollection<RideActivityMedia> media, AchievementRideEvidence evidence,
        CancellationToken cancellationToken = default);
    Task<AchievementProgressSnapshot> GetAchievementProgressAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetPersonalGoalAsync(Guid profileId, PersonalGoalMetric metric, double targetValue,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    Task ClearPersonalGoalAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public enum RideHistorySort { Newest, Oldest, Distance, Duration, Elevation }
public sealed record RideHistoryPage(IReadOnlyList<RideActivity> Activities, int TotalCount);
public sealed record RideHistoryBucket(DateTimeOffset StartUtc, int RideCount, double DistanceMeters, double DurationSeconds, double ElevationGainMeters);

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
