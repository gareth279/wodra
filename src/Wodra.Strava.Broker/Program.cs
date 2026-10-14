using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Wodra.Strava.Broker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("strava", client => client.BaseAddress = new Uri("https://www.strava.com/"));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("oauth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

app.MapPost("/oauth/exchange", async (ExchangeRequest request, IHttpClientFactory clients, IConfiguration config, HttpContext context) =>
{
    if (!RequireHttps(context) || !LoopbackRedirectPolicy.IsAllowed(request.RedirectUri) || string.IsNullOrWhiteSpace(request.Code))
        return Results.BadRequest(new { error = "invalid_authorization_response" });
    return await ExchangeAsync(clients, config, new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code", ["code"] = request.Code, ["redirect_uri"] = request.RedirectUri!
    });
}).RequireRateLimiting("oauth").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));

app.MapPost("/oauth/refresh", async (RefreshRequest request, IHttpClientFactory clients, IConfiguration config, HttpContext context) =>
{
    if (!RequireHttps(context) || string.IsNullOrWhiteSpace(request.RefreshToken))
        return Results.BadRequest(new { error = "invalid_refresh_request" });
    return await ExchangeAsync(clients, config, new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = request.RefreshToken
    });
}).RequireRateLimiting("oauth").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));

app.MapPost("/oauth/revoke", async (RevokeRequest request, IHttpClientFactory clients, IConfiguration config, HttpContext context) =>
{
    if (!RequireHttps(context) || string.IsNullOrWhiteSpace(request.Token) ||
        request.TokenTypeHint is not ("access_token" or "refresh_token"))
        return Results.BadRequest(new { error = "invalid_revoke_request" });
    if (!TryReadAppCredentials(config, out var clientId, out var secret))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{secret}"));
    using var message = new HttpRequestMessage(HttpMethod.Post, "oauth/revoke")
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = request.Token, ["token_type_hint"] = request.TokenTypeHint
        })
    };
    message.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    using var response = await clients.CreateClient("strava").SendAsync(message, context.RequestAborted);
    if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
    return Results.NoContent();
}).RequireRateLimiting("oauth").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));

app.Run();

static bool RequireHttps(HttpContext context) =>
    context.Request.IsHttps || context.Request.Host.Host is "localhost" or "127.0.0.1" or "::1";

static bool TryReadAppCredentials(IConfiguration config, out string clientId, out string secret)
{
    clientId = config["STRAVA_CLIENT_ID"] ?? "";
    secret = config["STRAVA_CLIENT_SECRET"] ?? "";
    return clientId.Length > 0 && secret.Length > 0;
}

static async Task<IResult> ExchangeAsync(IHttpClientFactory clients, IConfiguration config, Dictionary<string, string> fields)
{
    if (!TryReadAppCredentials(config, out var clientId, out var secret))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    fields["client_id"] = clientId;
    fields["client_secret"] = secret;
    using var response = await clients.CreateClient("strava").PostAsync("oauth/token", new FormUrlEncodedContent(fields));
    if (!response.IsSuccessStatusCode)
        return Results.Json(new { error = "strava_authorization_failed" }, statusCode: (int)response.StatusCode);
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
}

public sealed record ExchangeRequest(string? Code, string? RedirectUri);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record RevokeRequest(string? Token, string? TokenTypeHint);
