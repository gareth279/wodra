namespace Wodra.Multiplayer.Protocol;

/// <summary>Pure decisions shared by EOS recovery and its regression tests.</summary>
public static class EosSessionRecoveryPolicy
{
    public static EosMembershipRecovery DecideMembership(bool identityMatches, bool lobbyHandleAvailable,
        bool localUserListed)
    {
        if (!identityMatches) return EosMembershipRecovery.IdentityChanged;
        if (!lobbyHandleAvailable || !localUserListed) return EosMembershipRecovery.RejoinRequired;
        return EosMembershipRecovery.MembershipRetained;
    }

    public static bool ShouldRetryAuthentication(int failedAttempt, int maximumAttempts = 3) =>
        failedAttempt > 0 && failedAttempt < maximumAttempts;

    public static TimeSpan RetryDelay(int failedAttempt) => failedAttempt switch
    {
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(10)
    };
}

public enum EosMembershipRecovery
{
    MembershipRetained,
    RejoinRequired,
    IdentityChanged
}
