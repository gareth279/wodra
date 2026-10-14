using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Strava linking and FIT uploads. All long-lived credentials stay in Windows Credential Manager.</summary>
public sealed class StravaIntegrationClient
{
    public static StravaIntegrationClient Shared { get; } = new();
    private const string RequiredScopes = "read,activity:write";
    private const string StravaAuthorize = "https://www.strava.com/oauth/authorize";
    private const string StravaApi = "https://www.strava.com/api/v3/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim UploadLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly WindowsCredentialStore _credentials = new();
    private readonly object _automaticQueueGate = new();
    private Task? _automaticQueueTask;
    public event Action? UploadJournalChanged;

    private string BrokerRoot => GameBootstrap.Settings.StravaClient.BrokerBaseUrl.TrimEnd('/') + "/";
    private string ClientId => GameBootstrap.Settings.StravaClient.ClientId;
    private string MetadataPath => Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "strava-profiles.json");
    private string UploadJournalPath => Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "strava-uploads.json");

    public StravaProfileLink? GetLink(Guid profileId) => ReadMetadata().Profiles.GetValueOrDefault(profileId.ToString("D"));

    public async Task SetAutomaticUploadAsync(Guid profileId, bool enabled)
    {
        UpdateMetadata(data =>
        {
            var key = profileId.ToString("D");
            if (!data.Profiles.TryGetValue(key, out var link))
            {
                if (enabled) throw new InvalidOperationException("Link Strava before enabling automatic uploads.");
                return;
            }
            data.Profiles[key] = link with { AutomaticUploadEnabled = enabled };
        });
        if (!enabled)
        {
            await UploadLock.WaitAsync();
            try { CancelQueuedAutomaticUploads(profileId); }
            finally { UploadLock.Release(); }
        }
        else ResumePendingAutomaticUploads();
    }

    public async Task QueueAutomaticUploadsAsync(Guid profileId, long athleteId, Guid activityId, string? fitPath)
    {
        var link = GetLink(profileId);
        if (link is null || !link.AutomaticUploadEnabled || link.AthleteId != athleteId || string.IsNullOrWhiteSpace(fitPath)) return;
        var segments = GetFitSegments(activityId, fitPath);
        if (segments.Count == 0) return;
        await UploadLock.WaitAsync();
        try
        {
            link = GetLink(profileId);
            if (link is null || !link.AutomaticUploadEnabled || link.AthleteId != athleteId) return;
            var journal = ReadJournal();
            var changed = false;
            foreach (var segment in segments)
            {
                if (!segment.IsValid) continue;
                using var input = File.OpenRead(segment.Path);
                var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                var key = StravaPolicies.JournalKey(profileId, activityId, segment.Index, hash);
                if (journal.Uploads.ContainsKey(key)) continue;
                var externalId = $"wodra-{activityId:N}-{segment.Index}-{hash[..16]}";
                journal.Uploads[key] = new StravaUploadEntry(key, profileId, activityId, segment.Index, hash,
                    externalId, StravaUploadStatus.Queued, null, null, null, null, DateTimeOffset.UtcNow,
                    AthleteId: athleteId, IsAutomatic: true, FitPath: segment.Path);
                changed = true;
            }
            if (changed) WriteJournal(journal);
        }
        finally { UploadLock.Release(); }
        ResumePendingAutomaticUploads();
    }

    public void ResumePendingAutomaticUploads()
    {
        lock (_automaticQueueGate)
        {
            if (_automaticQueueTask is { IsCompleted: false }) return;
            _automaticQueueTask = Task.Run(ProcessAutomaticQueueAsync);
        }
    }

    private void CancelQueuedAutomaticUploads(Guid profileId)
    {
        var journal = ReadJournal();
        var changed = false;
        foreach (var pair in journal.Uploads.ToArray())
        {
            if (pair.Value.ProfileId != profileId || !pair.Value.IsAutomatic || pair.Value.Status != StravaUploadStatus.Queued) continue;
            journal.Uploads[pair.Key] = pair.Value with { Status = StravaUploadStatus.Cancelled, UpdatedAtUtc = DateTimeOffset.UtcNow };
            changed = true;
        }
        if (changed) WriteJournal(journal);
    }

    private async Task ProcessAutomaticQueueAsync()
    {
        while (true)
        {
            StravaUploadEntry? entry;
            var journal = ReadJournal();
            var now = DateTimeOffset.UtcNow;
            entry = journal.Uploads.Values
                .Where(item => item.IsAutomatic && item.Status is StravaUploadStatus.Queued or StravaUploadStatus.Processing && item.FitPath is not null &&
                    StravaAutomaticUploadJournal.CanResume(item.Status, item.IsAutomatic,
                        GetLink(item.ProfileId)?.AutomaticUploadEnabled == true, item.AthleteId,
                        GetLink(item.ProfileId)?.AthleteId, item.Status == StravaUploadStatus.Processing ? null : item.NextAttemptAtUtc, now))
                .OrderBy(item => item.NextAttemptAtUtc ?? item.UpdatedAtUtc).FirstOrDefault();
            if (entry is null)
            {
                var due = journal.Uploads.Values.Where(item => item.IsAutomatic && item.Status == StravaUploadStatus.Queued &&
                    item.NextAttemptAtUtc is { } at && item.FitPath is not null &&
                    StravaAutomaticUploadJournal.CanResume(item.Status, item.IsAutomatic,
                        GetLink(item.ProfileId)?.AutomaticUploadEnabled == true, item.AthleteId,
                        GetLink(item.ProfileId)?.AthleteId, null, now)).Select(item => item.NextAttemptAtUtc!.Value).DefaultIfEmpty().Min();
                if (due == default) return;
                var wait = due > now ? due - now : TimeSpan.FromSeconds(1);
                await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait);
                continue;
            }
            try { await UploadAsync(entry.ProfileId, entry.ActivityId, entry.SegmentIndex, entry.FitPath!, null, automaticSubmission: true); }
            catch (Exception ex)
            {
                var current = ReadJournal().Uploads.GetValueOrDefault(entry.Key);
                if (current is null || current.Status is StravaUploadStatus.Unknown or StravaUploadStatus.Submitting or StravaUploadStatus.Complete or StravaUploadStatus.Cancelled) continue;
                if (current.Status == StravaUploadStatus.Failed && current.LastHttpStatus != 429) continue;
                var transient = StravaAutomaticUploadJournal.IsTransientFailure(current.Status,
                    ex is StravaHttpException httpFailure ? httpFailure.StatusCode : current.LastHttpStatus,
                    ex is HttpRequestException or TaskCanceledException or StravaTransientException);
                if (!transient)
                {
                    UpdateJournalEntry(current with { Status = StravaUploadStatus.Failed, SafeMessage = SafeAutomaticError(ex), UpdatedAtUtc = DateTimeOffset.UtcNow });
                    continue;
                }
                var attempts = current.RetryCount + 1;
                if (attempts > 5)
                    UpdateJournalEntry(current with { Status = StravaUploadStatus.Failed, RetryCount = attempts, SafeMessage = "Automatic retries were exhausted. The saved segment remains available for manual retry.", UpdatedAtUtc = DateTimeOffset.UtcNow });
                else
                    UpdateJournalEntry(current with { Status = StravaUploadStatus.Queued, RetryCount = attempts,
                        NextAttemptAtUtc = DateTimeOffset.UtcNow + StravaPolicies.AutomaticRetryDelay(attempts),
                        SafeMessage = SafeAutomaticError(ex), UpdatedAtUtc = DateTimeOffset.UtcNow });
            }
            await Task.Delay(100);
        }
    }

    private static string SafeAutomaticError(Exception exception) => exception switch
    {
        StravaHttpException http => http.Diagnostic.UserMessage,
        StravaAuthorizationException => "Strava authorization expired. Relink this rider profile.",
        StravaTransientException temporary => temporary.Message,
        InvalidOperationException invalid when invalid.Message.Contains("upload access", StringComparison.OrdinalIgnoreCase) => invalid.Message,
        InvalidOperationException invalid when invalid.Message.Contains("Strava authorization expired", StringComparison.OrdinalIgnoreCase) => invalid.Message,
        _ => "The upload could not be completed. Wodra will retry when the connection is available."
    };

    private void UpdateJournalEntry(StravaUploadEntry entry)
    {
        var journal = ReadJournal();
        journal.Uploads[entry.Key] = entry;
        WriteJournal(journal);
    }

    public async Task<StravaProfileLink> LinkAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (!IsConfigured(out var reason)) throw new InvalidOperationException(reason);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://localhost:{port}/strava/callback";
        var session = await PostJsonAsync<LinkStartResponse>("session/start", new { profile_id = profileId.ToString("D"), redirect_uri = redirectUri }, cancellationToken);
        try
        {
            var auth = new Uri(StravaAuthorize + "?" + string.Join("&", new Dictionary<string, string>
            {
                ["client_id"] = session.ClientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["approval_prompt"] = "auto",
                ["scope"] = RequiredScopes,
                ["state"] = session.State
            }.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
            Process.Start(new ProcessStartInfo(auth.ToString()) { UseShellExecute = true });
            var callback = await AcceptCallbackAsync(listener, cancellationToken);
            var outcome = StravaPolicies.ClassifyCallback(FixedEquals(session.State, callback.State ?? ""), callback.Error, callback.Code);
            if (outcome == StravaOAuthOutcome.Invalid) throw new InvalidOperationException("Strava response did not match this link request. Please try again.");
            if (outcome == StravaOAuthOutcome.Cancelled) throw new OperationCanceledException("Strava authorization was cancelled or denied.");
            if (outcome == StravaOAuthOutcome.Incomplete) throw new InvalidOperationException("Strava did not return an authorization code.");

            var completed = await PostJsonAsync<LinkCompleteResponse>("session/complete", new
            {
                session_id = session.SessionId, state = callback.State, code = callback.Code, redirect_uri = redirectUri
            }, cancellationToken);
            if (!string.Equals(completed.ProfileId, profileId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The link response did not match the selected rider profile.");
            var token = completed.Tokens;
            var scopes = ParseScopes(token.Scope);
            var scopesText = token.Scope ?? string.Join(",", scopes);
            if (!scopes.Contains("read") || !scopes.Contains("activity:write"))
                throw new InvalidOperationException("Strava did not grant both read and activity upload access. Disconnect and link again with both permissions.");
            if (token.Athlete?.Id is null) throw new InvalidOperationException("Strava linked without returning an athlete identity.");

            var name = string.Join(" ", new[] { token.Athlete.FirstName, token.Athlete.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var link = new StravaProfileLink(profileId, token.Athlete.Id.Value, string.IsNullOrWhiteSpace(name) ? $"Athlete {token.Athlete.Id}" : name, scopesText, false);
            await UploadLock.WaitAsync(cancellationToken);
            try
            {
                _credentials.Write(profileId, new StravaCredential(token.RefreshToken, scopesText));
                UpdateMetadata(data => data.Profiles[profileId.ToString("D")] = link);
                CancelQueuedAutomaticUploads(profileId);
            }
            finally { UploadLock.Release(); }
            return link;
        }
        catch
        {
            try { await PostJsonAsync<JsonElement>("session/cancel", new { session_id = session.SessionId }, CancellationToken.None); }
            catch { }
            throw;
        }
    }

    public async Task<bool> DisconnectAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var credential = _credentials.Read(profileId);
        _credentials.Delete(profileId);
        UpdateMetadata(data => data.Profiles.Remove(profileId.ToString("D")));
        await UploadLock.WaitAsync(cancellationToken);
        try { CancelQueuedAutomaticUploads(profileId); }
        finally { UploadLock.Release(); }
        var revokedRemotely = credential is null;
        if (credential is not null && IsConfigured(out _))
        {
            try { await PostJsonAsync<JsonElement>("oauth/revoke", new { refresh_token = credential.RefreshToken }, cancellationToken); revokedRemotely = true; }
            catch { revokedRemotely = false; /* Local disconnect must remain usable when the broker or Strava is offline. */ }
        }
        return revokedRemotely;
    }

    public bool IsConfigured(out string message)
    {
        if (!Guid.TryParse(ClientId, out _) && !long.TryParse(ClientId, out _))
        { message = "Strava client ID is not configured."; return false; }
        if (!Uri.TryCreate(BrokerRoot, UriKind.Absolute, out var broker) || broker.Scheme != Uri.UriSchemeHttps)
        { message = "Strava broker must be configured with an HTTPS address."; return false; }
        message = ""; return true;
    }

    public async Task<StravaUploadResult> UploadAsync(Guid profileId, Guid activityId, int segmentIndex,
        string fitPath, Action<string>? progress, CancellationToken cancellationToken = default, bool automaticSubmission = false)
    {
        await UploadLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(fitPath) || !string.Equals(Path.GetExtension(fitPath), ".fit", StringComparison.OrdinalIgnoreCase) || new FileInfo(fitPath).Length == 0)
                throw new InvalidOperationException("This saved FIT segment is missing or invalid. The saved ride remains available on this PC.");
            var credential = _credentials.Read(profileId) ?? throw new InvalidOperationException("Link Strava in Manage Profiles for the profile that recorded this ride.");
            if (!IsConfigured(out var configError)) throw new InvalidOperationException(configError);
            progress?.Invoke("Refreshing Strava authorization…");
            var tokens = await PostJsonAsync<TokenResponse>("oauth/refresh", new { refresh_token = credential.RefreshToken }, cancellationToken);
            var scopes = ParseScopes(tokens.Scope ?? credential.Scopes);
            if (!scopes.Contains("activity:write")) throw new InvalidOperationException("This Strava link no longer has upload access. Relink it in Manage Profiles.");
            _credentials.Write(profileId, new StravaCredential(tokens.RefreshToken, tokens.Scope ?? credential.Scopes));

            await using var input = File.OpenRead(fitPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            var key = StravaPolicies.JournalKey(profileId, activityId, segmentIndex, hash);
            var externalId = $"wodra-{activityId:N}-{segmentIndex}-{hash[..16]}";
            var journal = ReadJournal();
            journal.Uploads.TryGetValue(key, out var entry);
            var linkedAthleteId = GetLink(profileId)?.AthleteId;
            if (entry is { AthleteId: 0, Status: not StravaUploadStatus.Complete })
                throw new InvalidOperationException("This older saved upload has no recorded Strava athlete identity. Wodra will not risk transferring it after a relink; check Strava before taking any further action.");
            if (entry is not null && entry.AthleteId != 0 && linkedAthleteId != entry.AthleteId)
                throw new InvalidOperationException("This saved segment belongs to a different Strava athlete than the profile currently linked. Relinking cannot transfer pending uploads.");
            if (entry?.Status == StravaUploadStatus.Complete)
                return new(StravaUploadStatus.Complete, entry.StravaActivityId, "Already uploaded", entry.ActivityUrl);
            if (entry?.Status is StravaUploadStatus.Submitting or StravaUploadStatus.Unknown)
                throw new InvalidOperationException("The last submission did not return a receipt. Wodra has kept the saved file and will not submit it again automatically because that could duplicate the activity.");

            if (entry?.Status != StravaUploadStatus.Processing)
            {
                if (!StravaPolicies.MaySubmitAgain(entry?.Status))
                    throw new InvalidOperationException("This upload is already pending or its submission result is unknown. Check Strava before resolving it; resubmitting could create a duplicate.");
                if (automaticSubmission && (GetLink(profileId) is not { AutomaticUploadEnabled: true } currentLink || currentLink.AthleteId != entry?.AthleteId))
                {
                    if (entry is not null) UpdateJournalEntry(entry with { Status = StravaUploadStatus.Cancelled, UpdatedAtUtc = DateTimeOffset.UtcNow });
                    throw new InvalidOperationException("Automatic upload was disabled or this profile's linked athlete changed before submission.");
                }
                entry = (entry ?? new StravaUploadEntry(key, profileId, activityId, segmentIndex, hash, externalId,
                    StravaUploadStatus.Queued, null, null, null, null, DateTimeOffset.UtcNow,
                    AthleteId: linkedAthleteId ?? 0)) with { Status = StravaUploadStatus.Submitting,
                        IsAutomatic = automaticSubmission, UpdatedAtUtc = DateTimeOffset.UtcNow };
                journal.Uploads[key] = entry; WriteJournal(journal);
                progress?.Invoke("Uploading saved FIT segment…");
                try
                {
                    var receipt = await SubmitFitAsync(tokens.AccessToken, externalId, fitPath, cancellationToken);
                    entry = entry with { Status = StravaUploadStatus.Processing, UploadId = receipt.Id, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    journal.Uploads[key] = entry; WriteJournal(journal);
                }
                catch (StravaHttpException ex) when (ex.StatusCode is >= 400 and < 500)
                {
                    journal.Uploads[key] = entry with { Status = StravaUploadStatus.Failed, LastHttpStatus = ex.StatusCode, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    if (ex.StatusCode == 401) throw new StravaAuthorizationException();
                    throw new InvalidOperationException(ex.Diagnostic.UserMessage);
                }
                catch
                {
                    journal.Uploads[key] = entry with { Status = StravaUploadStatus.Unknown, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    throw;
                }
            }

            progress?.Invoke("Strava is processing this ride…");
            for (var poll = 0; poll < 20; poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await GetUploadStatusAsync(tokens.AccessToken, entry!.UploadId!.Value, cancellationToken);
                if (status.ActivityId is { } activity)
                {
                    var url = $"https://www.strava.com/activities/{activity}";
                    journal.Uploads[key] = entry with { Status = StravaUploadStatus.Complete, StravaActivityId = activity, ActivityUrl = url, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    return new(StravaUploadStatus.Complete, activity, "Upload complete", url);
                }
                if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    journal.Uploads[key] = entry with { Status = StravaUploadStatus.Failed, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    WriteJournal(journal);
                    var diagnostic = StravaUploadDiagnostic.ParseProcessingError(status.Error);
                    GameLogger.Log("strava_upload_processing_failure", new
                    {
                        diagnostic.HttpStatus,
                        category = diagnostic.Kind.ToString(),
                        diagnostic.Resource,
                        diagnostic.Field,
                        diagnostic.Code
                    });
                    throw new InvalidOperationException(diagnostic.UserMessage);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            return new(StravaUploadStatus.Processing, null, "Strava is still processing this upload. Choose Check Status to continue.", null);
        }
        catch (StravaAuthorizationException)
        {
            _credentials.Delete(profileId);
            UpdateMetadata(data => data.Profiles.Remove(profileId.ToString("D")));
            CancelQueuedAutomaticUploads(profileId);
            throw new InvalidOperationException("Strava authorization expired or was revoked. Link this profile again in Manage Profiles.");
        }
        catch (StravaHttpException ex) when (ex.StatusCode == 401)
        {
            _credentials.Delete(profileId);
            UpdateMetadata(data => data.Profiles.Remove(profileId.ToString("D")));
            CancelQueuedAutomaticUploads(profileId);
            throw new InvalidOperationException("Strava authorization expired or was revoked. Link this profile again in Manage Profiles.");
        }
        finally { UploadLock.Release(); }
    }

    private async Task<UploadReceipt> SubmitFitAsync(string accessToken, string externalId, string path, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("fit"), "data_type"); form.Add(new StringContent(externalId), "external_id");
        var file = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, StravaApi + "uploads") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw await CreateUploadHttpExceptionAsync(response, "submit", ct);
        var payload = await ReadBoundedBodyAsync(response.Content, 65536, ct);
        if (payload.Truncated) throw new InvalidOperationException("Strava returned an unexpectedly large upload receipt.");
        return JsonSerializer.Deserialize<UploadReceipt>(payload.Body, Json) ?? throw new InvalidOperationException("Strava returned an unreadable upload receipt.");
    }

    private async Task<UploadStatusResponse> GetUploadStatusAsync(string accessToken, long uploadId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, StravaApi + "uploads/" + uploadId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw await CreateUploadHttpExceptionAsync(response, "status", ct);
        var payload = await ReadBoundedBodyAsync(response.Content, 65536, ct);
        if (payload.Truncated) throw new InvalidOperationException("Strava returned an unexpectedly large upload status.");
        return JsonSerializer.Deserialize<UploadStatusResponse>(payload.Body, Json) ?? new(null, null, null);
    }

    private static async Task<StravaHttpException> CreateUploadHttpExceptionAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        const int errorBodyLimit = 8192;
        var responseBody = await ReadBoundedBodyAsync(response.Content, errorBodyLimit, ct);
        var diagnostic = StravaUploadDiagnostic.Parse((int)response.StatusCode, responseBody.Body, responseBody.Truncated);
        GameLogger.Log("strava_upload_http_failure", new
        {
            operation,
            diagnostic.HttpStatus,
            category = diagnostic.Kind.ToString(),
            diagnostic.Resource,
            diagnostic.Field,
            diagnostic.Code,
            responseBodyTruncated = responseBody.Truncated
        });
        return new StravaHttpException(diagnostic);
    }

    private async Task<T> PostJsonAsync<T>(string endpoint, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(BrokerRoot), endpoint))
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await ReadSafeBodyAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new StravaAuthorizationException();
            var safeMessage = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Strava or its broker is temporarily rate limited. Wodra will retry the saved segment.",
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError => "Strava broker or Strava is temporarily unavailable. Wodra will retry the saved segment.",
                _ => null
            };
            if (safeMessage is not null) throw new StravaTransientException(safeMessage);
            throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "Strava linking is temporarily rate limited. Wait a minute and retry.",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => "Strava broker or Strava is temporarily unavailable. Your ride is still saved; retry later.",
            _ => "Strava authorization could not be completed. Check the link and try again."
        });
        }
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidOperationException("The Strava broker returned an unreadable response.");
    }

    private static async Task<string> ReadSafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadBoundedBodyAsync(response.Content, 65536, ct);
        return body.Truncated ? throw new InvalidOperationException("The Strava response was unexpectedly large.") : body.Body;
    }

    private static async Task<(string Body, bool Truncated)> ReadBoundedBodyAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(Math.Min(limit, 8192));
        var chunk = new byte[8192];
        while (buffer.Length < limit)
        {
            var count = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, count), ct);
            if (read == 0) return (Encoding.UTF8.GetString(buffer.ToArray()), false);
            buffer.Write(chunk, 0, read);
        }
        var probe = new byte[1];
        var truncated = await stream.ReadAsync(probe.AsMemory(), ct) != 0;
        return (Encoding.UTF8.GetString(buffer.ToArray()), truncated);
    }

    private async Task<OAuthCallback> AcceptCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var first = await reader.ReadLineAsync(deadline.Token);
        if (string.IsNullOrWhiteSpace(first) || first.Length > 4096 || !first.StartsWith("GET ", StringComparison.Ordinal))
            throw new InvalidOperationException("The local Strava callback was invalid.");
        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token);
            if (line is null || line.Length > 4096) throw new InvalidOperationException("The local Strava callback was incomplete.");
            if (line.Length == 0) break;
        }
        var target = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
        var uri = new Uri("http://127.0.0.1" + target);
        if (uri.AbsolutePath != "/strava/callback") throw new InvalidOperationException("The local Strava callback path was invalid.");
        var query = ParseQuery(uri.Query);
        var callback = new OAuthCallback(query.GetValueOrDefault("state"), query.GetValueOrDefault("code"), query.GetValueOrDefault("error"));
        var text = callback.Error == "access_denied" ? "Strava access was declined. You can close this tab." : "Strava returned to Wodra. You can close this tab.";
        var html = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\nContent-Length: " + Encoding.UTF8.GetByteCount(text) + "\r\n\r\n" + text;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(html), deadline.Token);
        return callback;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[WebUtility.UrlDecode(parts[0])] = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "";
        }
        return result;
    }
    private static bool FixedEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static HashSet<string> ParseScopes(string? value) => (value ?? "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private StravaProfileMetadata ReadMetadata()
    {
        try { return JsonSerializer.Deserialize<StravaProfileMetadata>(File.ReadAllText(MetadataPath), Json) ?? new(); }
        catch { return new(); }
    }
    private void UpdateMetadata(Action<StravaProfileMetadata> update)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
        var metadata = ReadMetadata(); update(metadata);
        WriteAtomically(MetadataPath, JsonSerializer.Serialize(metadata, Json));
    }
    private StravaUploadJournal ReadJournal()
    {
        if (!File.Exists(UploadJournalPath)) return new();
        try { return JsonSerializer.Deserialize<StravaUploadJournal>(File.ReadAllText(UploadJournalPath), Json) ?? throw new JsonException(); }
        catch { throw new InvalidOperationException("Wodra could not read the saved Strava upload history. It has been left untouched to prevent duplicate uploads."); }
    }
    private void WriteJournal(StravaUploadJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UploadJournalPath)!);
        WriteAtomically(UploadJournalPath, JsonSerializer.Serialize(journal, Json));
        try { UploadJournalChanged?.Invoke(); } catch { /* UI refresh observers cannot affect upload safety. */ }
    }
    private static void WriteAtomically(string path, string value)
    {
        var temp = path + ".tmp"; File.WriteAllText(temp, value, Encoding.UTF8);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }

    private sealed record LinkStartResponse([property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("state")] string State,
        [property: System.Text.Json.Serialization.JsonPropertyName("client_id")] string ClientId,
        [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string Scope);
    private sealed record LinkCompleteResponse([property: System.Text.Json.Serialization.JsonPropertyName("profile_id")] string ProfileId,
        [property: System.Text.Json.Serialization.JsonPropertyName("tokens")] TokenResponse Tokens);
    private sealed record OAuthCallback(string? State, string? Code, string? Error);
    private sealed record AthleteSummary([property: System.Text.Json.Serialization.JsonPropertyName("id")] long? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("firstname")] string? FirstName,
        [property: System.Text.Json.Serialization.JsonPropertyName("lastname")] string? LastName);
    private sealed record TokenResponse([property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] long ExpiresAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string? Scope,
        [property: System.Text.Json.Serialization.JsonPropertyName("athlete")] AthleteSummary? Athlete);
    private sealed record UploadReceipt([property: System.Text.Json.Serialization.JsonPropertyName("id")] long Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string? Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("activity_id")] long? ActivityId);
    private sealed record UploadStatusResponse([property: System.Text.Json.Serialization.JsonPropertyName("activity_id")] long? ActivityId,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string? Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);
    private sealed class StravaHttpException(StravaUploadDiagnostic diagnostic) : Exception
    {
        public StravaUploadDiagnostic Diagnostic { get; } = diagnostic;
        public int StatusCode => Diagnostic.HttpStatus;
    }
    private sealed class StravaAuthorizationException : Exception { }
    private sealed class StravaTransientException(string message) : Exception(message);

    private StravaUploadJournal ReadUploadJournal() => ReadJournal();
    public StravaUploadEntry? GetUpload(Guid profileId, Guid activityId, int segmentIndex, string fitPath)
    {
        if (!File.Exists(fitPath)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fitPath))).ToLowerInvariant();
        return ReadUploadJournal().Uploads.GetValueOrDefault($"{profileId:D}/{activityId:N}/{segmentIndex}/{hash}");
    }

    public static string CredentialTargetForProfile(Guid profileId) => StravaPolicies.CredentialTarget(profileId);

    public static IReadOnlyList<StravaFitSegment> GetFitSegments(Guid activityId, string? fitPath)
        => StravaFitSegmentReader.Read(activityId, fitPath);
}

public sealed record StravaCredential(string RefreshToken, string Scopes);
public sealed record StravaUploadResult(StravaUploadStatus Status, long? ActivityId, string Message, string? ActivityUrl);
public sealed class StravaProfileMetadata { public Dictionary<string, StravaProfileLink> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
public sealed class StravaUploadJournal { public Dictionary<string, StravaUploadEntry> Uploads { get; set; } = new(StringComparer.Ordinal); }

internal sealed class WindowsCredentialStore
{
    private const uint Generic = 1, LocalMachine = 2, NotFound = 1168;
    public void Write(Guid profileId, StravaCredential value)
    {
        var target = StravaIntegrationClient.CredentialTargetForProfile(profileId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var targetPtr = Marshal.StringToHGlobalUni(target); var userPtr = Marshal.StringToHGlobalUni(profileId.ToString("D"));
        var blobPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);
            var credential = new NativeCredential { Type = Generic, TargetName = targetPtr, UserName = userPtr,
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blobPtr, Persist = LocalMachine };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("Windows could not securely store the Strava refresh credential.");
        }
        finally { Marshal.FreeHGlobal(targetPtr); Marshal.FreeHGlobal(userPtr); Marshal.FreeHGlobal(blobPtr); CryptographicOperations.ZeroMemory(bytes); }
    }
    public StravaCredential? Read(Guid profileId)
    {
        if (!CredRead(StravaIntegrationClient.CredentialTargetForProfile(profileId), Generic, 0, out var ptr)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(ptr);
            var bytes = new byte[credential.CredentialBlobSize]; Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return JsonSerializer.Deserialize<StravaCredential>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(ptr); }
    }
    public void Delete(Guid profileId)
    {
        if (!CredDelete(StravaIntegrationClient.CredentialTargetForProfile(profileId), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new InvalidOperationException("Windows could not remove the Strava credential.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NativeCredential
    {
        public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment; public long LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount;
        public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName;
    }
    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("Advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
