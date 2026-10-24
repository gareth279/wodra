using System;
using System.Threading;
using System.Threading.Tasks;
using IndoorCycling.Core.Media;
using Windows.Media.Control;

/// <summary>Windows implementation of the OS-selected Global System Media Transport Controls session.</summary>
public sealed class WindowsMediaSessionProvider : ISystemMediaSessionProvider
{
	private static readonly TimeSpan OperationTimeout = TimeSpan.FromMilliseconds(900);
	private readonly SemaphoreSlim _nativeOperation = new(1, 1);
	private volatile bool _disposed;

	public async Task<SystemMediaSessionSnapshot> ReadCurrentAsync(CancellationToken cancellationToken = default)
	{
		if (!OperatingSystem.IsWindows() || _disposed) return SystemMediaSessionSnapshot.Unavailable;
		return await RunBoundedAsync(async () =>
		{
			var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
			var session = manager.GetCurrentSession();
			if (session is null) return SystemMediaSessionSnapshot.Unavailable;

			var playback = session.GetPlaybackInfo();
			var properties = await session.TryGetMediaPropertiesAsync();
			var latestSession = manager.GetCurrentSession();
			if (latestSession is null || latestSession.SourceAppUserModelId != session.SourceAppUserModelId)
				return SystemMediaSessionSnapshot.Unavailable;

			var playbackState = playback.PlaybackStatus switch
			{
				GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => SystemMediaPlaybackState.Playing,
				GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => SystemMediaPlaybackState.Paused,
				_ => SystemMediaPlaybackState.Other
			};
			return new SystemMediaSessionSnapshot(true, EmptyToNull(properties.Title), EmptyToNull(properties.Artist),
				playbackState, playback.Controls.IsPlayEnabled, playback.Controls.IsPauseEnabled,
				playback.Controls.IsNextEnabled, playback.Controls.IsPreviousEnabled);
		}, SystemMediaSessionSnapshot.Unavailable, cancellationToken);
	}

	public async Task<bool> TryExecuteAsync(SystemMediaCommand command, CancellationToken cancellationToken = default)
	{
		if (!OperatingSystem.IsWindows() || _disposed) return false;
		return await RunBoundedAsync(async () =>
		{
			var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
			var session = manager.GetCurrentSession();
			if (session is null) return false;
			var playback = session.GetPlaybackInfo();
			return command switch
			{
				SystemMediaCommand.TogglePlayback when playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && playback.Controls.IsPauseEnabled
					=> await session.TryPauseAsync(),
				SystemMediaCommand.TogglePlayback when playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused && playback.Controls.IsPlayEnabled
					=> await session.TryPlayAsync(),
				SystemMediaCommand.Next when playback.Controls.IsNextEnabled => await session.TrySkipNextAsync(),
				SystemMediaCommand.Previous when playback.Controls.IsPreviousEnabled => await session.TrySkipPreviousAsync(),
				_ => false
			};
		}, false, cancellationToken);
	}

	private async Task<T> RunBoundedAsync<T>(Func<Task<T>> operation, T fallback, CancellationToken cancellationToken)
	{
		if (!await _nativeOperation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return fallback;
		Task<T> pending;
		try { pending = Task.Run(operation); }
		catch { _nativeOperation.Release(); return fallback; }
		_ = pending.ContinueWith(_ => _nativeOperation.Release(), CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		try { return await pending.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false); }
		catch (OperationCanceledException) { return fallback; }
		catch (TimeoutException) { return fallback; }
		catch { return fallback; }
	}

	private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	public ValueTask DisposeAsync()
	{
		_disposed = true;
		return ValueTask.CompletedTask;
	}
}
