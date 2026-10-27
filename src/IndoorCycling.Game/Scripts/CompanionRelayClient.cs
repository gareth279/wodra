using Godot;
using IndoorCycling.Core.Models;
using QRCoder;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Optional, read-only companion observer using profile-scoped trusted devices.</summary>
public partial class CompanionRelayClient : Node
{
	private static readonly System.Net.Http.HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
	private const double PublishIntervalSeconds = 3;
	private const double StatusIntervalSeconds = 2;
	private const double DeviceRefreshSeconds = 5;
	private const int RequestTimeoutSeconds = 5;
	private RideSessionNode? _session;
	private CompanionDesktopIdentityStore.Identity? _identity;
	private Guid? _identityProfileId;
	private string _apiUrl = "";
	private string _webRoot = "";
	private string? _inviteId;
	private string _manualCode = "";
	private ImageTexture? _qrTexture;
	private readonly List<TrustedDevice> _devices = [];
	private long _sequence;
	private double _publishWait;
	private double _statusWait;
	private double _deviceWait;
	private bool _requestBusy;
	private bool _enabled;
	private bool _hasPublishedRide;
	private string _status = "Companion is off.";
	private string _lastFailure = "";

	public sealed record TrustedDevice(string Id, string Label, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt);
	public string StatusText => string.IsNullOrEmpty(_lastFailure) ? _status : _lastFailure;
	public string ManualCode => _manualCode;
	public Texture2D? QrTexture => _qrTexture;
	public bool HasInvitation => !string.IsNullOrEmpty(_inviteId);
	public bool PhonePending => false;
	public bool IsPaired => _devices.Count > 0;
	public IReadOnlyList<TrustedDevice> Devices => _devices;

	public void Configure(RideSessionNode? session, bool enabled, string relayBaseUrl)
	{
		_session = session;
		var normalized = NormalizeRelayUrl(relayBaseUrl, GameBootstrap.DebugModeEnabled);
		if (!enabled || normalized is null)
		{
			if (_enabled && _hasPublishedRide) _ = SetIdleAsync();
			if (HasInvitation) _ = CancelAsync();
			_enabled = false;
			_apiUrl = "";
			_webRoot = "";
			_lastFailure = "";
			_status = enabled ? "Companion relay must use HTTPS." : "Companion is off.";
			return;
		}
		var nextApiUrl = new Uri(new Uri(normalized), "api.php").ToString();
		if (HasInvitation && !string.Equals(_apiUrl, nextApiUrl, StringComparison.OrdinalIgnoreCase)) _ = CancelAsync();
		_enabled = true;
		_webRoot = normalized;
		_apiUrl = nextApiUrl;
		_status = "Companion is on. Ride data is shared only while this option is enabled.";
		try
		{
			var profileId = GameBootstrap.Settings.ActiveProfileId;
			if (_identity is null || _identityProfileId != profileId)
			{
				_identity = CompanionDesktopIdentityStore.LoadOrCreate(profileId);
				_identityProfileId = profileId;
				_devices.Clear();
				_sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			}
			_deviceWait = 0;
		}
		catch { _lastFailure = "Could not load the protected companion identity. Riding is unaffected."; }
	}

	public override void _Process(double delta)
	{
		if (!_enabled || string.IsNullOrEmpty(_apiUrl) || _identity is null || _requestBusy) return;
		if (HasInvitation && !IsPaired)
		{
			_statusWait -= delta;
			if (_statusWait <= 0) { _statusWait = StatusIntervalSeconds; _ = PollInvitationAsync(); }
		}
		_deviceWait -= delta;
		if (_deviceWait <= 0) { _deviceWait = DeviceRefreshSeconds; _ = RefreshDevicesAsync(); }
		if (IsPaired)
		{
			if (_session?.IsRideActive == true)
			{
				_publishWait -= delta;
				if (_publishWait <= 0) { _publishWait = PublishIntervalSeconds; _ = PublishSnapshotAsync(); }
			}
			else if (_hasPublishedRide) _ = SetIdleAsync();
		}
	}

	public async Task<bool> CreateInvitationAsync()
	{
		if (!_enabled || string.IsNullOrEmpty(_apiUrl) || _identity is null) { _status = "Enable Companion and check its relay address in Options."; return false; }
		if (HasInvitation) await CancelAsync();
		_lastFailure = "";
		try
		{
			var result = await PostAsync(new { action = "remembered_create", desktop_id = _identity.DesktopId, desktop_hash = Hash(_identity.Secret) });
			_inviteId = result.GetProperty("id").GetString();
			_manualCode = result.GetProperty("code").GetString() ?? "";
			_qrTexture = CreateQrTexture(_webRoot + "#" + _manualCode);
			_sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			_status = "Connect this one new phone · code expires in 3 minutes";
			_statusWait = 0;
			return true;
		}
		catch (Exception ex) { _status = SafeFailure(ex); return false; }
	}

	public Task<bool> DecideAsync(bool approve) => Task.FromResult(false);

	public async Task CancelAsync()
	{
		if (!HasInvitation) return;
		try { await PostDesktopAsync(new { action = "remembered_cancel_invite", invite_id = _inviteId }); }
		catch { /* Invitation expires automatically; ride operation remains local. */ }
		_inviteId = null; _manualCode = ""; _qrTexture = null; _lastFailure = "";
		_status = "Pairing invitation cancelled. Remembered phones remain trusted.";
	}

	public async Task<bool> RevokeDeviceAsync(string deviceId)
	{
		if (!_enabled || _identity is null) return false;
		try
		{
			await PostDesktopAsync(new { action = "remembered_revoke", device_id = deviceId });
			_devices.RemoveAll(device => device.Id == deviceId);
			_status = "Phone access revoked.";
			return true;
		}
		catch (Exception ex) { _lastFailure = SafeFailure(ex); return false; }
	}

	public async Task<bool> RevokeAllDevicesAsync()
	{
		if (!_enabled || _identity is null) return false;
		try
		{
			await PostDesktopAsync(new { action = "remembered_revoke_all" });
			_devices.Clear();
			_status = "All remembered phone access was revoked.";
			return true;
		}
		catch (Exception ex) { _lastFailure = SafeFailure(ex); return false; }
	}

	private async Task PollInvitationAsync()
	{
		if (_requestBusy || _identity is null || string.IsNullOrEmpty(_inviteId)) return;
		_requestBusy = true;
		try
		{
			var result = await PostDesktopAsync(new { action = "remembered_invite_status", invite_id = _inviteId });
			var state = result.GetProperty("status").GetString();
			if (state == "claimed")
			{
				_inviteId = null; _manualCode = ""; _qrTexture = null;
				_status = "Phone remembered · it reconnects automatically when a ride starts.";
				_deviceWait = 0;
				await RefreshDevicesCoreAsync();
			}
			else if (state is "cancelled" or "expired")
			{
				_inviteId = null; _manualCode = ""; _qrTexture = null; _status = "Pairing invitation expired or was cancelled.";
			}
			_lastFailure = "";
		}
		catch (Exception ex) { _lastFailure = SafeFailure(ex); }
		finally { _requestBusy = false; }
	}

	private async Task RefreshDevicesAsync()
	{
		if (_requestBusy || !_enabled || _identity is null) return;
		_requestBusy = true;
		try { await RefreshDevicesCoreAsync(); }
		catch (Exception ex) { _lastFailure = SafeFailure(ex); }
		finally { _requestBusy = false; }
	}

	private async Task RefreshDevicesCoreAsync()
	{
		if (_identity is null) return;
		var result = await PostDesktopAsync(new { action = "remembered_devices" });
		_devices.Clear();
		foreach (var device in result.GetProperty("devices").EnumerateArray())
			_devices.Add(new TrustedDevice(device.GetProperty("id").GetString() ?? "",
				device.GetProperty("label").GetString() ?? "Phone",
				DateTimeOffset.FromUnixTimeSeconds(device.GetProperty("last_seen").GetInt64()),
				DateTimeOffset.FromUnixTimeSeconds(device.GetProperty("expires_at").GetInt64())));
	}

	private async Task PublishSnapshotAsync()
	{
		if (_requestBusy || !_enabled || _session?.IsRideActive != true || _identity is null) return;
		_requestBusy = true;
		try
		{
			await PostDesktopAsync(new { action = "remembered_publish", seq = ++_sequence, snapshot = CreateSnapshot(_session) });
			_hasPublishedRide = true; _lastFailure = "";
		}
		catch (Exception ex) { _lastFailure = SafeFailure(ex); }
		finally { _requestBusy = false; }
	}

	private async Task SetIdleAsync()
	{
		if (!_enabled || _identity is null || !_hasPublishedRide || _requestBusy) return;
		_requestBusy = true;
		try { await PostDesktopAsync(new { action = "remembered_idle" }); _hasPublishedRide = false; }
		catch { /* Stale snapshots are hidden by the phone after twelve seconds. */ }
		finally { _requestBusy = false; }
	}

	private Task<JsonElement> PostDesktopAsync(object body)
	{
		if (_identity is null) throw new InvalidOperationException("Companion identity is unavailable.");
		var payload = JsonSerializer.SerializeToNode(body) ?? new JsonObject();
		payload["protocol"] = 2;
		payload["desktop_id"] = _identity.DesktopId;
		payload["desktop_hash"] = Hash(_identity.Secret);
		return PostNodeAsync(payload);
	}

	private async Task<JsonElement> PostAsync(object body)
	{
		var payload = JsonSerializer.SerializeToNode(body) ?? new JsonObject();
		payload["protocol"] = 2;
		return await PostNodeAsync(payload);
	}

	private async Task<JsonElement> PostNodeAsync(JsonNode payload)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
		using var response = await Http.PostAsJsonAsync(_apiUrl, payload, timeout.Token);
		var text = await response.Content.ReadAsStringAsync(timeout.Token);
		using var parsed = JsonDocument.Parse(text);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException(parsed.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "relay_unavailable");
		return parsed.RootElement.Clone();
	}

	public static object CreateSnapshot(RideSessionNode? session)
	{
		var trainer = GameBootstrap.TrainerClient;
		var ride = session?.CurrentRideState ?? RideState.Initial;
		var telemetry = trainer?.GetLatestTelemetry();
		var now = DateTimeOffset.UtcNow;
		var connected = trainer?.IsConnected == true;
		var fresh = connected && telemetry is { HasReceivedMeasurement: true } && telemetry.IsFreshAt(now);
		var heartFresh = fresh && telemetry?.HeartRateBpm is not null && (telemetry.HeartRateTimestamp ?? telemetry.Timestamp) <= now
			&& now - (telemetry.HeartRateTimestamp ?? telemetry.Timestamp) <= TimeSpan.FromSeconds(5);
		var route = session?.CurrentRoute;
		var paused = session?.IsRidePaused == true;
		var state = session?.IsRideActive == true ? (paused ? "paused" : "riding") : "idle";
		var gradient = route is null ? (double?)null : route.GetSampleAt(ride.DistanceMeters).GradientPercent;
		double? speed = fresh && telemetry?.HasSpeedMeasurement == true ? ride.VirtualSpeedKph : null;
		int? power = fresh && telemetry?.HasPowerMeasurement == true ? telemetry.PowerWatts : null;
		int? cadence = fresh && telemetry?.HasCadenceMeasurement == true ? telemetry.CadenceRpm : null;
		return new { state, connected, paused, data_stale = connected && (telemetry is null || !telemetry.HasReceivedMeasurement || !fresh),
			route = route?.Name, speed_kph = speed, power_w = power, cadence_rpm = cadence,
			heart_rate_bpm = heartFresh ? telemetry!.HeartRateBpm : null,
			distance_m = session?.IsRideActive == true ? ride.DistanceMeters : (double?)null,
			elapsed_s = session?.IsRideActive == true ? ride.ElapsedSeconds : (double?)null,
			gradient_pct = gradient, captured_at = now.ToString("O", CultureInfo.InvariantCulture) };
	}

	private static ImageTexture CreateQrTexture(string text)
	{
		using var qr = new QRCodeGenerator();
		using var data = qr.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
		var modules = data.ModuleMatrix; const int pixelsPerModule = 5; var size = modules.Count * pixelsPerModule;
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8); image.Fill(Colors.White);
		for (var y = 0; y < modules.Count; y++) for (var x = 0; x < modules[y].Length; x++)
			if (modules[y][x]) for (var py = 0; py < pixelsPerModule; py++) for (var px = 0; px < pixelsPerModule; px++)
				image.SetPixel(x * pixelsPerModule + px, y * pixelsPerModule + py, Colors.Black);
		return ImageTexture.CreateFromImage(image);
	}

	private static string? NormalizeRelayUrl(string value, bool debug)
	{
		if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
			|| !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
		var local = debug && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
		if (uri.Scheme != Uri.UriSchemeHttps && !local) return null;
		var root = uri.ToString(); return root.EndsWith('/') ? root : root + '/';
	}
	private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
	private static string SafeFailure(Exception ex)
	{
		if (ex is TaskCanceledException) return "Relay request timed out. Riding is unaffected.";
		if (ex is HttpRequestException request)
		{
			var detail = request.Message switch
			{
				"unauthorized" => "desktop identity was rejected",
				"rate_limited" => "pairing requests are temporarily rate limited",
				"not_found" or "expired" => "pairing invitation expired or is no longer available",
				"conflict" => "relay rejected an out-of-date update",
				"origin_rejected" => "relay rejected its configured website origin",
				"temporarily_unavailable" => "service is temporarily unavailable",
				"https_required" => "relay requires HTTPS",
				_ => "service is unavailable"
			};
			return $"Relay unavailable: {detail}. Riding is unaffected.";
		}
		return "Relay unavailable. Riding is unaffected.";
	}
}
