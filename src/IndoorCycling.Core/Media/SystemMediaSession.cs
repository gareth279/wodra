namespace IndoorCycling.Core.Media;

public enum SystemMediaPlaybackState
{
	Unavailable,
	Other,
	Playing,
	Paused
}

public enum SystemMediaCommand
{
	TogglePlayback,
	Next,
	Previous
}

public sealed record SystemMediaSessionSnapshot(
	bool IsAvailable,
	string? Title,
	string? Artist,
	SystemMediaPlaybackState PlaybackState,
	bool CanPlay,
	bool CanPause,
	bool CanSkipNext,
	bool CanSkipPrevious)
{
	public static SystemMediaSessionSnapshot Unavailable { get; } = new(
		false, null, null, SystemMediaPlaybackState.Unavailable, false, false, false, false);

	public bool CanTogglePlayback => PlaybackState switch
	{
		SystemMediaPlaybackState.Playing => CanPause,
		SystemMediaPlaybackState.Paused => CanPlay,
		_ => false
	};
}

/// <summary>Platform boundary for the OS-selected external media session. Implementations must not block ride threads.</summary>
public interface ISystemMediaSessionProvider : IAsyncDisposable
{
	Task<SystemMediaSessionSnapshot> ReadCurrentAsync(CancellationToken cancellationToken = default);
	Task<bool> TryExecuteAsync(SystemMediaCommand command, CancellationToken cancellationToken = default);
}
