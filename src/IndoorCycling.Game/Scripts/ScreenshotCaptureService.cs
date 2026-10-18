using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Viewport PNG capture. Kept independent from ride saving and Strava workflows.</summary>
public sealed class ScreenshotCaptureService
{
    private readonly HudController _hud;
    private readonly Func<Guid?>? _activityIdProvider;
    private readonly Func<string?>? _sessionIdProvider;
    private readonly List<CapturedScreenshot> _capturedMedia = [];
    private readonly HashSet<string> _reservedStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task<CapturedScreenshot?>> _pendingCaptures = [];
    private bool _captureInProgress;

    public IReadOnlyList<CapturedScreenshot> CapturedMedia => _capturedMedia;

    public ScreenshotCaptureService(HudController hud, Func<Guid?>? activityIdProvider = null,
        Func<string?>? sessionIdProvider = null)
    {
        _hud = hud;
        _activityIdProvider = activityIdProvider;
        _sessionIdProvider = sessionIdProvider;
    }

    public Task<CapturedScreenshot?> CaptureCleanAsync(RideDebugSnapshot? snapshot = null) =>
        Track(CaptureAsync(ScreenshotCaptureMode.Clean, snapshot, null));

    public Task<CapturedScreenshot?> CaptureHudAsync(RideDebugSnapshot? snapshot = null) =>
        Track(CaptureAsync(ScreenshotCaptureMode.Hud, snapshot, null));

    public Task<CapturedScreenshot?> CaptureDebugMarkerAsync(RideDebugSnapshot snapshot) =>
        Track(CaptureAsync(ScreenshotCapturePolicy.ForDebugMarker(), snapshot, snapshot.marker));

    private Task<CapturedScreenshot?> Track(Task<CapturedScreenshot?> capture)
    {
        _pendingCaptures.Add(capture);
        return capture;
    }

    public async Task FlushPendingCapturesAsync()
    {
        while (_pendingCaptures.Count > 0)
        {
            var pending = _pendingCaptures.ToArray();
            _pendingCaptures.Clear();
            await Task.WhenAll(pending);
        }
    }

    private async Task<CapturedScreenshot?> CaptureAsync(ScreenshotCaptureMode mode,
        RideDebugSnapshot? snapshot, int? markerNumber)
    {
        if (_captureInProgress)
        {
            GD.PushWarning("Screenshot skipped because another capture is still in progress.");
            return null;
        }
        _captureInProgress = true;
        var originalHudVisible = _hud.Visible;
        var originalDebugVisible = _hud.DebugOverlayCaptureVisible;
        byte[] png;
        var timestamp = DateTimeOffset.Now;
        try
        {
            if (mode == ScreenshotCaptureMode.Clean)
                _hud.Visible = false;
            else if (mode == ScreenshotCaptureMode.DebugMarker)
            {
                _hud.Visible = true;
                _hud.SetDebugOverlayForCapture(true);
            }

            // ProcessFrame lets visibility changes take effect; FramePostDraw
            // ensures the captured texture is the newly rendered frame.
            await _hud.ToSignal(_hud.GetTree(), SceneTree.SignalName.ProcessFrame);
            await _hud.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var image = _hud.GetViewport().GetTexture().GetImage();
            if (image is null || image.IsEmpty()) throw new InvalidOperationException("The ride viewport returned an empty image.");
            png = image.SavePngToBuffer();
            if (png.Length == 0) throw new InvalidOperationException("Godot returned an empty PNG buffer.");
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Screenshot capture failed: {ex.Message}");
            _hud.ShowScreenshotToast("Screenshot failed");
            return null;
        }
        finally
        {
            _hud.SetDebugOverlayForCapture(originalDebugVisible);
            _hud.Visible = originalHudVisible;
            _captureInProgress = false;
        }

        string directory;
        string stem;
        try
        {
            var pictures = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            directory = ScreenshotCaptureFileStore.CreateScreenshotDirectory(pictures, OS.GetUserDataDir());
            stem = ScreenshotCaptureFileStore.CreateUniqueStem(timestamp, mode, markerNumber,
                name => _reservedStems.Contains(name[..^4]) || File.Exists(Path.Combine(directory, name)));
            _reservedStems.Add(stem);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Could not create the Wodra screenshot directory: {ex.Message}");
            _hud.ShowScreenshotToast("Screenshot could not be saved");
            return null;
        }

        var imagePath = Path.Combine(directory, stem + ".png");
        var sidecarPath = snapshot is not null && mode != ScreenshotCaptureMode.Hud
            ? Path.Combine(directory, stem + ".json") : null;
        try
        {
            await using var stream = new FileStream(imagePath, FileMode.CreateNew, System.IO.FileAccess.Write,
                FileShare.None, 81920, useAsync: true);
            await stream.WriteAsync(png);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Could not save screenshot '{imagePath}': {ex.Message}");
            _hud.ShowScreenshotToast("Screenshot could not be saved");
            return null;
        }

        if (sidecarPath is not null && snapshot is not null)
        {
            try { await File.WriteAllTextAsync(sidecarPath, ScreenshotCaptureFileStore.SerializeSidecar(snapshot, _sessionIdProvider?.Invoke())); }
            catch (Exception ex) { GD.PushWarning($"Screenshot saved, but its metadata sidecar failed: {ex.Message}"); }
        }

        var captured = new CapturedScreenshot(imagePath, sidecarPath, timestamp, mode, markerNumber, _activityIdProvider?.Invoke());
        _capturedMedia.Add(captured);
        _hud.ShowScreenshotToast("Screenshot saved");
        GD.Print($"Screenshot saved: {imagePath}");
        return captured;
    }
}
