using Godot;
using IndoorCycling.Core.Models;
using QRCoder;
using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Optional downstream observer for the versioned read-only companion relay.</summary>
public partial class CompanionRelayClient : Node
{
	private static readonly System.Net.Http.HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
	private const double PublishIntervalSeconds = 3;
	private const double StatusIntervalSeconds = 1.5;
	private const int RequestTimeoutSeconds = 5;
	private RideSessionNode? _session;
	private string _apiUrl = "";
	private string _webRoot = "";
	private string? _inviteId;
	private string? _inviteToken;
	private string? _desktopToken;
	private long _sequence;
	private double _publishWait;
	private double _statusWait;
	private bool _requestBusy;
	private bool _enabled;
	private string _status = "Companion is off.";
	private string _manualCode = "";
	private ImageTexture? _qrTexture;
	private string _lastFailure = "";

	public string StatusText => string.IsNullOrEmpty(_lastFailure) ? _status : _lastFailure;
	public string ManualCode => _manualCode;
	public Texture2D? QrTexture => _qrTexture;
	public bool HasInvitation => !string.IsNullOrEmpty(_inviteId);
	public bool PhonePending { get; private set; }
	public bool IsPaired { get; private set; }

	public void Configure(RideSessionNode? session, bool enabled, string relayBaseUrl)
	{
		_session = session;
		var normalized = NormalizeRelayUrl(relayBaseUrl, GameBootstrap.DebugModeEnabled);
		if (!enabled || normalized is null)
		{
			if (HasInvitation) _ = CancelAsync();
			_enabled = false;
			_apiUrl = "";
			_webRoot = "";
			_lastFailure = "";
			_status = enabled ? "Enter the deployed HTTPS companion address in Options." : "Companion is off.";
			return;
		}
		var nextApiUrl = new Uri(new Uri(normalized), "api.php").ToString();
		if (HasInvitation && !string.Equals(_apiUrl, nextApiUrl, StringComparison.OrdinalIgnoreCase))
			_ = CancelAsync();
		_enabled = true;
		_webRoot = normalized;
		_apiUrl = nextApiUrl;
		if (!HasInvitation) _status = "Ready. Start a temporary pairing invitation.";
	}

	public override void _Process(double delta)
	{
		if (!_enabled || string.IsNullOrEmpty(_apiUrl) || _requestBusy) return;
		if (HasInvitation && !IsPaired)
		{
			_statusWait -= delta;
			if (_statusWait <= 0) { _statusWait = StatusIntervalSeconds; _ = PollInvitationAsync(); }
		}
		if (IsPaired)
		{
			_publishWait -= delta;
			if (_publishWait <= 0) { _publishWait = PublishIntervalSeconds; PublishSnapshotAsync(); }
		}
	}

	public async Task<bool> CreateInvitationAsync()
	{
		if (!_enabled || string.IsNullOrEmpty(_apiUrl)) { _status = "Companion relay is not configured."; return false; }
		if (HasInvitation) await CancelAsync();
		_lastFailure = "";
		var invite = NewToken(); var desktop = NewToken();
		try
		{
			var result = await PostAsync(new { action = "create", invite_hash = Hash(invite), desktop_hash = Hash(desktop) });
			_inviteId = result.GetProperty("id").GetString();
			_inviteToken = invite; _desktopToken = desktop;
			_manualCode = result.GetProperty("code").GetString() ?? "";
			var qrPayload = _webRoot + "#" + _manualCode;
			_qrTexture = CreateQrTexture(qrPayload);
			PhonePending = false; IsPaired = false; _sequence = 0;
			_status = "Invitation ready · expires in 3 minutes";
			_statusWait = 0;
			return true;
		}
		catch (Exception ex) { ClearSession(); _status = SafeFailure(ex); return false; }
	}

	public async Task<bool> DecideAsync(bool approve)
	{
		if (!HasInvitation || !PhonePending || string.IsNullOrEmpty(_inviteToken)) return false;
		try
		{
			await PostAsync(new { action = "decide", id = _inviteId, invite_hash = Hash(_inviteToken), approve });
			if (approve)
			{
				IsPaired = true; PhonePending = false; _status = "Phone paired · sharing read-only ride telemetry."; _publishWait = 0;
			}
			else { ClearSession(); _status = "Pairing declined."; }
			return true;
		}
		catch (Exception ex) { _status = SafeFailure(ex); return false; }
	}

	public async Task CancelAsync()
	{
		if (!HasInvitation) return;
		try
		{
			if (!string.IsNullOrEmpty(_inviteToken))
				await PostAsync(new { action = IsPaired ? "disconnect_desktop" : "cancel_invite", id = _inviteId,
					invite_hash = IsPaired ? null : Hash(_inviteToken), token_hash = IsPaired ? Hash(_desktopToken ?? "") : null });
		}
		catch { /* Best effort revocation; server expiry is bounded. Never interrupt riding. */ }
		ClearSession(); _lastFailure = ""; _status = "Phone disconnected.";
	}

	public override void _ExitTree()
	{
		if (IsPaired && HasInvitation && !string.IsNullOrEmpty(_desktopToken))
			_ = TryRevokeOnExitAsync(_inviteId!, _desktopToken);
		ClearSession();
	}

	private async Task TryRevokeOnExitAsync(string id, string token)
	{
		try { await PostAsync(new { action = "disconnect_desktop", id, token_hash = Hash(token) }); }
		catch { }
	}

	private async Task PollInvitationAsync()
	{
		if (_requestBusy || string.IsNullOrEmpty(_inviteId) || string.IsNullOrEmpty(_inviteToken)) return;
		_requestBusy = true;
		try
		{
			var result = await PostAsync(new { action = "invite_status", id = _inviteId, invite_hash = Hash(_inviteToken) });
			var state = result.GetProperty("status").GetString();
			PhonePending = state == "pending";
			if (PhonePending) _status = "A phone is requesting access · approve to share telemetry.";
			else if (state == "expired") { ClearSession(); _status = "Pairing invitation expired."; }
			else if (state == "active") { IsPaired = true; PhonePending = false; _status = "Phone paired · sharing read-only ride telemetry."; }
			_lastFailure = "";
		}
		catch (Exception ex)
		{
			_lastFailure = SafeFailure(ex);
			if (ex is HttpRequestException requestError && requestError.Message is "expired" or "not_found" or "unauthorized")
			{
				ClearSession();
				_status = "Pairing invitation expired or is no longer available.";
			}
		}
		finally { _requestBusy = false; }
	}

	private async void PublishSnapshotAsync()
	{
		if (_requestBusy || !IsPaired || string.IsNullOrEmpty(_inviteId) || string.IsNullOrEmpty(_desktopToken)) return;
		_requestBusy = true;
		try
		{
			var snapshot = CreateSnapshot(_session);
			await PostAsync(new { action = "publish", id = _inviteId, token_hash = Hash(_desktopToken), seq = ++_sequence, snapshot });
			_lastFailure = "";
		}
		catch (Exception ex)
		{
			if (ex is HttpRequestException requestError && requestError.Message is "expired" or "not_found" or "unauthorized")
			{
				ClearSession(); _lastFailure = ""; _status = "Phone disconnected or pairing expired.";
			}
			else _lastFailure = SafeFailure(ex);
		}
		finally { _requestBusy = false; }
	}

	public static object CreateSnapshot(RideSessionNode? session)
	{
		var trainer = GameBootstrap.TrainerClient;
		var ride = session?.CurrentRideState ?? RideState.Initial;
		var telemetry = trainer?.GetLatestTelemetry();
		var now = DateTimeOffset.UtcNow;
		var connected = trainer?.IsConnected == true;
		var fresh = connected && telemetry is { HasReceivedMeasurement: true } && telemetry.IsFreshAt(now);
		var heartFresh = fresh && telemetry?.HeartRateBpm is not null &&
			(telemetry.HeartRateTimestamp ?? telemetry.Timestamp) <= now &&
			now - (telemetry.HeartRateTimestamp ?? telemetry.Timestamp) <= TimeSpan.FromSeconds(5);
		var route = session?.CurrentRoute;
		var paused = session?.IsRidePaused == true;
		var state = session?.IsRideActive == true ? (paused ? "paused" : "riding")
			: session?.LastCompletedActivityId is not null ? "ended" : "idle";
		var gradient = route is null ? (double?)null : route.GetSampleAt(ride.DistanceMeters).GradientPercent;
		double? speed = fresh && telemetry?.HasSpeedMeasurement == true ? ride.VirtualSpeedKph : null;
		int? power = fresh && telemetry?.HasPowerMeasurement == true ? telemetry.PowerWatts : null;
		int? cadence = fresh && telemetry?.HasCadenceMeasurement == true ? telemetry.CadenceRpm : null;
		return new
		{
			state, connected, paused, data_stale = connected && (telemetry is null || !telemetry.HasReceivedMeasurement || !fresh),
			route = route?.Name,
			speed_kph = speed,
			power_w = power,
			cadence_rpm = cadence,
			heart_rate_bpm = heartFresh ? telemetry!.HeartRateBpm : null,
			distance_m = session?.IsRideActive == true || state == "ended" ? ride.DistanceMeters : (double?)null,
			elapsed_s = session?.IsRideActive == true || state == "ended" ? ride.ElapsedSeconds : (double?)null,
			gradient_pct = gradient,
			captured_at = now.ToString("O", CultureInfo.InvariantCulture)
		};
	}

	private async Task<JsonElement> PostAsync(object body)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
		var payload = JsonSerializer.SerializeToNode(body) ?? new JsonObject();
		payload["protocol"] = 1;
		using var response = await Http.PostAsJsonAsync(_apiUrl, payload, timeout.Token);
		var text = await response.Content.ReadAsStringAsync(timeout.Token);
		using var parsed = JsonDocument.Parse(text);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException(parsed.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "relay_unavailable");
		return parsed.RootElement.Clone();
	}

	private static ImageTexture CreateQrTexture(string text)
	{
		using var qr = new QRCodeGenerator();
		using var data = qr.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
		var modules = data.ModuleMatrix;
		var pixelsPerModule = 5;
		var size = modules.Count * pixelsPerModule;
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		image.Fill(Colors.White);
		for (var y = 0; y < modules.Count; y++) for (var x = 0; x < modules[y].Length; x++)
			if (modules[y][x]) for (var py = 0; py < pixelsPerModule; py++) for (var px = 0; px < pixelsPerModule; px++)
				image.SetPixel(x * pixelsPerModule + px, y * pixelsPerModule + py, Colors.Black);
		return ImageTexture.CreateFromImage(image);
	}

	private static string? NormalizeRelayUrl(string value, bool debug)
	{
		if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
			!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
		var local = debug && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
		if (uri.Scheme != Uri.UriSchemeHttps && !local) return null;
		var root = uri.ToString();
		return root.EndsWith('/') ? root : root + "/";
	}
	private static byte[] NewBytes() { var b = new byte[32]; System.Security.Cryptography.RandomNumberGenerator.Fill(b); return b; }
	private static string NewToken() => Convert.ToBase64String(NewBytes()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
	private static string SafeFailure(Exception ex) => ex is TaskCanceledException ? "Relay request timed out. Riding is unaffected." : "Relay unavailable. Riding is unaffected.";
	private void ClearSession()
	{
		_inviteId = _inviteToken = _desktopToken = null; _manualCode = ""; _qrTexture = null;
		PhonePending = false; IsPaired = false; _sequence = 0; _requestBusy = false;
	}
}
