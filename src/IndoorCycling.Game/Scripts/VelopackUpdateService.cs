using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

/// <summary>Update state shared by startup and Settings presenters.</summary>
public enum UpdaterStatus
{
    Unavailable,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Staged,
    Applying,
    Failed
}

public sealed record UpdateOffer(string Version, string ReleaseNotes, object? Native);
public sealed record UpdaterSnapshot(
    UpdaterStatus Status,
    string CurrentVersion,
    string Channel,
    bool IsInstalled,
    UpdateOffer? Offer,
    int ProgressPercent,
    string Message,
    string? FailureKind = null);

public interface IUpdaterBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    string? Channel { get; }
    UpdateOffer? PendingUpdate { get; }
    Task<UpdateOffer?> CheckForUpdatesAsync();
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken);
    void ApplyAndRestart(UpdateOffer offer);
}

/// <summary>
/// Owns the single Velopack operation for the application lifetime. It has no
/// dependency on any Control, Window, or startup/settings panel visibility.
/// </summary>
public sealed class VelopackUpdateService
{
    public static readonly TimeSpan FeedCheckTimeout = TimeSpan.FromSeconds(9);

    private readonly IUpdaterBackend _backend;
    private readonly Func<bool> _canApply;
    private readonly Action<string, object?> _log;
    private readonly SynchronizationContext? _uiContext;
    private readonly TimeSpan _checkTimeout;
    private readonly object _sync = new();
    private Task<UpdaterSnapshot>? _checkTask;
    private Task<UpdaterSnapshot>? _downloadTask;
    private CancellationTokenSource? _downloadCancellation;
    private UpdateOffer? _offer;
    private bool _operationActive;
    private bool _started;

    public event Action<UpdaterSnapshot>? StateChanged;

    public UpdaterSnapshot Snapshot { get; private set; }

    public VelopackUpdateService(
        IUpdaterBackend backend,
        string applicationVersion,
        string channel,
        Func<bool> canApply,
        Action<string, object?> log,
        SynchronizationContext? uiContext = null,
        TimeSpan? checkTimeout = null)
    {
        _backend = backend;
        _canApply = canApply;
        _log = log;
        _uiContext = uiContext ?? SynchronizationContext.Current;
        _checkTimeout = checkTimeout ?? FeedCheckTimeout;
        var pending = backend.PendingUpdate;
        _offer = pending;
        Snapshot = new UpdaterSnapshot(
            backend.IsInstalled ? (pending is null ? UpdaterStatus.Unavailable : UpdaterStatus.Staged) : UpdaterStatus.Unavailable,
            string.IsNullOrWhiteSpace(backend.CurrentVersion) ? applicationVersion : backend.CurrentVersion,
            backend.Channel ?? channel,
            backend.IsInstalled,
            pending,
            0,
            !backend.IsInstalled
                ? "Automatic installation is unavailable in this portable or development build."
                : pending is null
                    ? "Updates are checked when online."
                    : $"Wodra {pending.Version} is downloaded and ready to apply.");
    }

    public void StartAutomaticCheck()
    {
        lock (_sync)
        {
            if (_started)
            {
                Log("update_startup_check_skipped", new { reason = "already-started", status = Snapshot.Status.ToString() });
                return;
            }
            _started = true;
        }

        if (Snapshot.Status == UpdaterStatus.Staged)
        {
            Log("update_pending_restart", new { version = Snapshot.Offer?.Version, channel = Snapshot.Channel });
            Publish(Snapshot);
            return;
        }

        if (!Snapshot.IsInstalled)
        {
            Log("update_unavailable", new { reason = "not-installed", channel = Snapshot.Channel });
            Publish(Snapshot);
            return;
        }

        _ = CheckForUpdatesAsync();
    }

    public Task<UpdaterSnapshot> CheckForUpdatesAsync()
    {
        lock (_sync)
        {
            if (_checkTask is { IsCompleted: false })
            {
                Log("update_check_coalesced", new { status = Snapshot.Status.ToString(), channel = Snapshot.Channel });
                return _checkTask;
            }
            if (_operationActive)
            {
                Log("update_check_skipped_operation_active", new { status = Snapshot.Status.ToString(), channel = Snapshot.Channel });
                return Task.FromResult(Snapshot);
            }
            if (!Snapshot.IsInstalled)
            {
                Log("update_check_skipped_not_installed", new { currentVersion = Snapshot.CurrentVersion, channel = Snapshot.Channel });
                return Task.FromResult(Snapshot);
            }

            _operationActive = true;
            _checkTask = CheckCoreAsync();
            return _checkTask;
        }
    }

    private async Task<UpdaterSnapshot> CheckCoreAsync()
    {
        var current = _backend.CurrentVersion;
        SetState(UpdaterStatus.Checking, "Checking for updates…", offer: null, progress: 0);
        Log("update_check_started", new { installedVersion = current, channel = Snapshot.Channel });

        try
        {
            // GithubSource uses a bounded metadata downloader; WaitAsync also
            // bounds the application-level operation if a backend regresses.
            var offer = await _backend.CheckForUpdatesAsync().WaitAsync(_checkTimeout);
            if (offer is null || !UpdateVersionPolicy.IsNewer(
                    Velopack.SemanticVersion.Parse(current),
                    Velopack.SemanticVersion.Parse(offer.Version)))
            {
                _offer = null;
                SetState(UpdaterStatus.UpToDate, $"Wodra is up to date ({current}).", null, 0);
                Log("update_check_complete", new { installedVersion = current, candidateVersion = offer?.Version, isGenuinelyNewer = false });
                return Snapshot;
            }

            _offer = offer;
            SetState(UpdaterStatus.Available, $"Wodra {offer.Version} is available.", offer, 0);
            Log("update_available", new { installedVersion = current, candidateVersion = offer.Version, channel = Snapshot.Channel });
            return Snapshot;
        }
        catch (TimeoutException ex)
        {
            SetState(UpdaterStatus.Failed, "The update check timed out. You can retry or continue riding.", _offer, 0, "timeout");
            Log("update_check_timed_out", new { installedVersion = current, channel = Snapshot.Channel, exceptionType = ex.GetType().Name });
            return Snapshot;
        }
        catch (Exception ex)
        {
            SetState(UpdaterStatus.Failed, "Could not check for updates. You can retry or continue riding.", _offer, 0, ex.GetType().Name);
            Log("update_check_failed", new { installedVersion = current, channel = Snapshot.Channel, exceptionType = ex.GetType().Name, message = ex.Message });
            return Snapshot;
        }
        finally
        {
            lock (_sync) _operationActive = false;
        }
    }

    public Task<UpdaterSnapshot> DownloadAndInstallAsync()
    {
        lock (_sync)
        {
            if (_downloadTask is { IsCompleted: false })
            {
                Log("update_download_coalesced", new { status = Snapshot.Status.ToString() });
                return _downloadTask;
            }
            if (_operationActive)
            {
                Log("update_download_skipped_operation_active", new { status = Snapshot.Status.ToString() });
                return Task.FromResult(Snapshot);
            }
            if (_offer is null)
            {
                Log("update_download_skipped_no_offer", new { status = Snapshot.Status.ToString(), installedVersion = Snapshot.CurrentVersion });
                SetState(UpdaterStatus.Failed, "There is no staged update to install. Check for updates and retry.", null, 0, "no-update");
                return Task.FromResult(Snapshot);
            }
            _operationActive = true;
            _downloadCancellation = new CancellationTokenSource();
            _downloadTask = DownloadCoreAsync(_offer, _downloadCancellation.Token);
            return _downloadTask;
        }
    }

    private async Task<UpdaterSnapshot> DownloadCoreAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        try
        {
            SetState(UpdaterStatus.Downloading, $"Downloading Wodra {offer.Version}…", offer, 0);
            Log("update_download_started", new { version = offer.Version, channel = Snapshot.Channel });

            await _backend.DownloadAsync(offer, ReportProgress, cancellationToken);
            SetState(UpdaterStatus.Staged, $"Wodra {offer.Version} is downloaded and ready to apply.", offer, 100);
            Log("update_download_staged", new { version = offer.Version });

            if (!_canApply())
            {
                SetState(UpdaterStatus.Staged, "Download complete. Finish your ride or replay, then choose Apply and Restart.", offer, 100);
                Log("update_apply_deferred_active_session", new { version = offer.Version });
                return Snapshot;
            }

            ApplyStagedUpdate(offer);
            return Snapshot;
        }
        catch (OperationCanceledException)
        {
            SetState(UpdaterStatus.Failed, "The update download was interrupted. Retry when you are ready.", offer, Snapshot.ProgressPercent, "cancelled");
            Log("update_download_cancelled", new { version = offer.Version, progress = Snapshot.ProgressPercent });
            return Snapshot;
        }
        catch (Exception ex)
        {
            SetState(UpdaterStatus.Failed, $"Could not install the update. Your current install and rider data are unchanged. {Summarize(ex.Message)} Retry or continue riding.", offer, Snapshot.ProgressPercent, ex.GetType().Name);
            Log("update_download_failed", new { version = offer.Version, progress = Snapshot.ProgressPercent, exceptionType = ex.GetType().Name, message = ex.Message });
            return Snapshot;
        }
        finally
        {
            lock (_sync)
            {
                _downloadCancellation?.Dispose();
                _downloadCancellation = null;
                _operationActive = false;
            }
        }
    }

    public UpdaterSnapshot ApplyStagedUpdate()
    {
        if (_offer is null)
        {
            Log("update_apply_skipped_no_offer", new { status = Snapshot.Status.ToString() });
            SetState(UpdaterStatus.Failed, "There is no downloaded update ready to apply.", null, 0, "no-staged-update");
            return Snapshot;
        }
        if (!_canApply())
        {
            Log("update_apply_deferred_active_session", new { version = _offer.Version });
            SetState(UpdaterStatus.Staged, "Finish your ride or replay before applying this update.", _offer, Snapshot.ProgressPercent);
            return Snapshot;
        }

        ApplyStagedUpdate(_offer);
        return Snapshot;
    }

    private void ApplyStagedUpdate(UpdateOffer offer)
    {
        if (!_canApply())
        {
            Log("update_apply_deferred_active_session", new { version = offer.Version });
            SetState(UpdaterStatus.Staged, "Finish your ride or replay before applying this update.", offer, Snapshot.ProgressPercent);
            return;
        }

        SetState(UpdaterStatus.Applying, $"Applying Wodra {offer.Version} and restarting…", offer, 100);
        Log("update_apply_started", new { version = offer.Version, channel = Snapshot.Channel });
        try
        {
            _backend.ApplyAndRestart(offer);
            // Velopack is expected to exit/restart the process. If it returns,
            // leave the explanation visible instead of pretending it succeeded.
            SetState(UpdaterStatus.Failed, "Velopack returned without restarting. Retry Apply and Restart or continue riding.", offer, 100, "restart-not-started");
            Log("update_apply_returned_unexpectedly", new { version = offer.Version });
        }
        catch (Exception ex)
        {
            SetState(UpdaterStatus.Failed, $"The update could not be applied. Your current install and rider data are unchanged. {Summarize(ex.Message)} Retry Apply and Restart.", offer, 100, ex.GetType().Name);
            Log("update_apply_failed", new { version = offer.Version, exceptionType = ex.GetType().Name, message = ex.Message });
        }
    }

    public void CancelDownload()
    {
        lock (_sync)
        {
            if (_downloadCancellation is null)
            {
                Log("update_download_cancel_skipped", new { reason = "no-active-download" });
                return;
            }
            Log("update_download_cancel_requested", new { progress = Snapshot.ProgressPercent });
            _downloadCancellation.Cancel();
        }
    }

    private void ReportProgress(int percentage)
    {
        var progress = Math.Clamp(percentage, 0, 100);
        Post(() =>
        {
            if (Snapshot.Status != UpdaterStatus.Downloading) return;
            SetState(UpdaterStatus.Downloading, $"Downloading Wodra {_offer?.Version}… {progress}%", _offer, progress);
        });
    }

    private void SetState(UpdaterStatus status, string message, UpdateOffer? offer, int progress, string? failureKind = null)
    {
        var next = Snapshot with { Status = status, Offer = offer, ProgressPercent = progress, Message = message, FailureKind = failureKind };
        Post(() =>
        {
            Snapshot = next;
            StateChanged?.Invoke(next);
        });
    }

    private void Publish(UpdaterSnapshot snapshot)
    {
        Post(() => StateChanged?.Invoke(snapshot));
    }

    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            action();
        else
            _uiContext.Post(_ => action(), null);
    }

    private void Log(string eventName, object? payload) => _log(eventName, payload);

    public void LogUiEvent(string eventName, object? payload) => Log(eventName, payload);

    private static string Summarize(string message)
    {
        var compact = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..177] + "…";
    }
}

/// <summary>Production Velopack adapter. Metadata uses a short timeout; package downloads retain Velopack's normal timeout and cancellation.</summary>
public sealed class VelopackUpdateBackend : IUpdaterBackend
{
    private sealed record NativeOffer(UpdateInfo? UpdateInfo, VelopackAsset Asset);
    private const double FeedTimeoutMinutes = 8d / 60d;
    private readonly UpdateManager _manager;
    private readonly string _channel;

    public VelopackUpdateBackend(string repositoryUrl, string channel, string? localFeedDirectory = null)
    {
        _channel = channel;
        var downloader = new BoundedFeedDownloader(new HttpClientFileDownloader());
        IUpdateSource source;
#if WODRA_LOCAL_UPDATE_FEED_TEST
        source = !string.IsNullOrWhiteSpace(localFeedDirectory)
            ? new SimpleFileSource(new DirectoryInfo(localFeedDirectory))
            : new GithubSource(repositoryUrl, accessToken: null, prerelease: true, downloader);
#else
        _ = localFeedDirectory;
        source = new GithubSource(repositoryUrl, accessToken: null, prerelease: true, downloader);
#endif
        _manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = channel });
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? string.Empty;
    public string? Channel => _channel;

    public UpdateOffer? PendingUpdate => _manager.UpdatePendingRestart is { } pending
        ? new UpdateOffer(pending.Version.ToString(), "A downloaded update is ready to apply.", new NativeOffer(null, pending))
        : null;

    public async Task<UpdateOffer?> CheckForUpdatesAsync()
    {
        var info = await _manager.CheckForUpdatesAsync();
        if (info is null) return null;
        var release = info.TargetFullRelease;
        return new UpdateOffer(release.Version.ToString(), release.NotesMarkdown ?? string.Empty, new NativeOffer(info, release));
    }

    public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
    {
        if (offer.Native is not NativeOffer { UpdateInfo: { } info } native)
            throw new InvalidOperationException("The available Velopack update information is missing.");
        await _manager.DownloadUpdatesAsync(info, progress, cancellationToken);
    }

    public void ApplyAndRestart(UpdateOffer offer)
    {
        if (offer.Native is not NativeOffer { Asset: { } asset })
            throw new InvalidOperationException("The staged Velopack package is missing.");
        _manager.ApplyUpdatesAndRestart(asset, Array.Empty<string>());
    }

    private sealed class BoundedFeedDownloader(IFileDownloader inner) : IFileDownloader
    {
        public Task DownloadFile(string url, string targetFile, Action<int> progress, System.Collections.Generic.IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
            => inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);

        public Task<byte[]> DownloadBytes(string url, System.Collections.Generic.IDictionary<string, string>? headers, double timeout)
            => inner.DownloadBytes(url, headers, Bound(timeout));

        public Task<string> DownloadString(string url, System.Collections.Generic.IDictionary<string, string>? headers, double timeout)
            => inner.DownloadString(url, headers, Bound(timeout));

        private static double Bound(double timeout) => timeout <= 0 ? FeedTimeoutMinutes : Math.Min(timeout, FeedTimeoutMinutes);
    }
}

public sealed class UnavailableUpdaterBackend(string currentVersion, string channel, bool isInstalled, Exception error) : IUpdaterBackend
{
    public bool IsInstalled => isInstalled;
    public string CurrentVersion => currentVersion;
    public string? Channel => channel;
    public UpdateOffer? PendingUpdate => null;
    public Task<UpdateOffer?> CheckForUpdatesAsync() => Task.FromException<UpdateOffer?>(error);
    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken) => Task.FromException(error);
    public void ApplyAndRestart(UpdateOffer offer) => throw new InvalidOperationException("The Velopack update manager is unavailable.", error);
}
