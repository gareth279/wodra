namespace IndoorCycling.Core.Models;

/// <summary>Pure startup choice for the local profile picker and first-profile prompt.</summary>
public sealed record RiderProfileStartupSelection(RiderProfileRecord? ActiveProfile, bool OpenCreateProfile)
{
    public static bool ShouldAutoOpenCreateEditor(int profileCount, bool isInitialProfileLoad) =>
        isInitialProfileLoad && profileCount == 0;

    public static bool ShouldActivateProfileAfterSave(bool isNewProfile, Guid? currentActiveProfileId) =>
        isNewProfile || currentActiveProfileId is null;

    public static RiderProfileStartupSelection Resolve(
        IReadOnlyList<RiderProfileRecord> profiles, Guid? savedActiveProfileId)
    {
        if (profiles.Count == 0) return new(null, true);
        var active = profiles.FirstOrDefault(profile => profile.Id == savedActiveProfileId) ?? profiles[0];
        return new(active, false);
    }
}
