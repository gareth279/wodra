using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

public sealed class RideReplayer
{
    private readonly object _sync = new();

    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();
    private int _currentIndex;
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;

    public event Action<ReplayFrame>? FrameApplied;

    public bool IsLoaded => _frames.Count > 0;
    public bool IsPlaying { get; private set; }
    public double PlaybackRate { get; private set; } = 1.0;

    public ReplayFrame? CurrentFrame
    {
        get
        {
            lock (_sync)
            {
                if (_frames.Count == 0)
                {
                    return null;
                }

                if (_currentIndex < 0 || _currentIndex >= _frames.Count)
                {
                    return null;
                }

                return _frames[_currentIndex];
            }
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_sync)
            {
                return _frames.Count;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_sync)
            {
                return _frames.Count == 0 ? TimeSpan.Zero : _frames[^1].Elapsed;
            }
        }
    }

    public void Load(IReadOnlyList<ReplayFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        PauseInternal(waitForStop: true);

        lock (_sync)
        {
            _frames = frames;
            _currentIndex = 0;
        }

        if (_frames.Count > 0)
        {
            FrameApplied?.Invoke(_frames[0]);
        }
    }

    public void SetPlaybackRate(double rate)
    {
        if (rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "Playback rate must be greater than zero.");
        }

        PlaybackRate = rate;
    }

    public void Seek(TimeSpan elapsed)
    {
        PauseInternal(waitForStop: true);

        lock (_sync)
        {
            if (_frames.Count == 0)
            {
                return;
            }

            var idx = 0;
            for (var i = 0; i < _frames.Count; i++)
            {
                if (_frames[i].Elapsed > elapsed)
                {
                    break;
                }

                idx = i;
            }

            _currentIndex = idx;
            FrameApplied?.Invoke(_frames[_currentIndex]);
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            if (IsPlaying || _frames.Count == 0)
            {
                return;
            }

            _playbackCts = new CancellationTokenSource();
            IsPlaying = true;
            _playbackTask = Task.Run(() => PlaybackLoopAsync(_playbackCts.Token));
        }
    }

    public void Pause()
    {
        PauseInternal(waitForStop: true);
    }

    public void Stop()
    {
        PauseInternal(waitForStop: true);

        lock (_sync)
        {
            if (_frames.Count == 0)
            {
                return;
            }

            _currentIndex = 0;
            FrameApplied?.Invoke(_frames[0]);
        }
    }

    private void PauseInternal(bool waitForStop)
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_sync)
        {
            cts = _playbackCts;
            task = _playbackTask;
            _playbackCts = null;
            _playbackTask = null;
            IsPlaying = false;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        if (waitForStop && task is not null)
        {
            try
            {
                task.Wait();
            }
            catch
            {
            }
        }

        cts?.Dispose();
    }

    private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReplayFrame current;
            ReplayFrame? next = null;

            lock (_sync)
            {
                if (_frames.Count == 0 || _currentIndex >= _frames.Count)
                {
                    IsPlaying = false;
                    return;
                }

                current = _frames[_currentIndex];

                if (_currentIndex + 1 < _frames.Count)
                {
                    next = _frames[_currentIndex + 1];
                }
            }

            FrameApplied?.Invoke(current);

            if (next is null)
            {
                lock (_sync)
                {
                    IsPlaying = false;
                }

                return;
            }

            var delay = next.Elapsed - current.Elapsed;
            var scaledDelayMs = Math.Max(1, (int)(delay.TotalMilliseconds / PlaybackRate));

            try
            {
                await Task.Delay(scaledDelayMs, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                lock (_sync)
                {
                    IsPlaying = false;
                }

                return;
            }

            lock (_sync)
            {
                _currentIndex++;
            }
        }

        lock (_sync)
        {
            IsPlaying = false;
        }
    }
}