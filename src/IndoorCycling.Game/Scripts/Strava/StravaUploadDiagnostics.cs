using System;
using System.Collections.Generic;
using System.Text.Json;

public enum StravaUploadFailureKind
{
    Unknown,
    ApplicationAccessRestricted,
    MissingActivityWriteScope,
    ExpiredAuthorization,
    RateLimited,
    DuplicateOrFileProcessing
}

/// <summary>Safe, bounded diagnostics extracted from a Strava upload HTTP error.</summary>
public sealed record StravaUploadDiagnostic(int HttpStatus, StravaUploadFailureKind Kind,
    string? Resource, string? Field, string? Code)
{
    public static StravaUploadDiagnostic Parse(int status, string? responseBody, bool truncated = false)
    {
        var entries = new List<(string? Resource, string? Field, string? Code)>();
        string? message = null;
        if (!truncated && !string.IsNullOrEmpty(responseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    message = GetString(root, "message");
                    if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                        foreach (var item in errors.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object)
                                entries.Add((Recognize(GetString(item, "resource"), Resources),
                                    Recognize(GetString(item, "field"), Fields),
                                    Recognize(GetString(item, "code"), Codes)));
                }
            }
            catch (JsonException) { }
        }

        string? resource = null, field = null, code = null;
        foreach (var entry in entries)
        {
            resource ??= entry.Resource;
            field ??= entry.Field;
            code ??= entry.Code;
        }
        return new(status, Classify(status, resource, field, code, message), resource, field, code);
    }

    public static StravaUploadDiagnostic ParseProcessingError(string? error) =>
        new(200, Classify(400, null, null, null, error), null, null, null);

    public string UserMessage => Kind switch
    {
        StravaUploadFailureKind.ExpiredAuthorization => "Strava authorization expired or was revoked. Link this profile again.",
        StravaUploadFailureKind.RateLimited => "Strava rate limited this upload. Wait before retrying the saved segment.",
        StravaUploadFailureKind.MissingActivityWriteScope => "Strava reports that upload permission is missing. Check this profile's activity:write access and relink if needed.",
        StravaUploadFailureKind.ApplicationAccessRestricted => "Strava reports an application access restriction. Check the app's active status and access settings.",
        StravaUploadFailureKind.DuplicateOrFileProcessing => "Strava reports a duplicate or file processing problem. Check the saved segment before retrying.",
        _ when HttpStatus == 200 => "Strava reported a processing error but did not identify a recognized cause. Check the saved segment before retrying.",
        _ => $"Strava returned HTTP {HttpStatus} for the upload. The response did not identify a recognized cause; the saved segment is available to retry after you investigate."
    };

    private static StravaUploadFailureKind Classify(int status, string? resource, string? field, string? code, string? message)
    {
        if (status == 401) return StravaUploadFailureKind.ExpiredAuthorization;
        if (status == 429) return StravaUploadFailureKind.RateLimited;
        var text = message ?? "";
        if (status == 403 && (resource == "Application" && code is "not_authorized" or "forbidden" or "inactive" or "deactivated" ||
            ContainsAny(text, "application is inactive", "application has been deactivated", "application is deactivated", "application access is restricted")))
            return StravaUploadFailureKind.ApplicationAccessRestricted;
        if (status is 400 or 403 && (field is "activity:write" or "scope" && code is "missing" or "invalid" ||
            ContainsAny(text, "activity:write", "activity write scope", "missing upload permission")))
            return StravaUploadFailureKind.MissingActivityWriteScope;
        if (status == 400 && (code == "duplicate" || field is "file" or "data_type" && code is "invalid" or "processing" or "unprocessable" ||
            ContainsAny(text, "duplicate", "unable to process", "invalid file", "file could not be processed")))
            return StravaUploadFailureKind.DuplicateOrFileProcessing;
        return StravaUploadFailureKind.Unknown;
    }

    private static bool ContainsAny(string value, params string[] phrases)
    {
        foreach (var phrase in phrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Recognize(string? value, IReadOnlyDictionary<string, string> known) =>
        value is not null && known.TryGetValue(value, out var canonical) ? canonical : null;

    private static readonly IReadOnlyDictionary<string, string> Resources = Map("Activity", "Athlete", "Application", "Upload", "RateLimit", "Authorization");
    private static readonly IReadOnlyDictionary<string, string> Fields = Map("access", "activity:write", "scope", "file", "data_type", "external_id", "type", "token", "application", "resource");
    private static readonly IReadOnlyDictionary<string, string> Codes = Map("invalid", "missing", "duplicate", "exceeded", "not_authorized", "forbidden", "rate_limit_exceeded", "processing", "unprocessable", "blocked", "deactivated", "inactive", "unauthorized");

    private static IReadOnlyDictionary<string, string> Map(params string[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) result.Add(value, value);
        return result;
    }
}
