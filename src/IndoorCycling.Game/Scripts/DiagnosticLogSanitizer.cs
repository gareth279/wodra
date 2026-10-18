using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class DiagnosticLogSanitizer
{
	private static readonly Regex SensitiveQuery = new(@"(?i)([?&](?:access_token|refresh_token|token|key|secret|code|password|signature)=)[^&#\s]*", RegexOptions.Compiled);
	private static readonly Regex SensitiveAssignment = new(@"(?i)([\""']?\b(?:access[_-]?token|refresh[_-]?token|token|client[_-]?secret|ant[_-]?network[_-]?key|network[_-]?key|ant[_-]?key|api[_-]?key|password|authorization)\b[\""']?\s*[:=]\s*[\""']?)(?:Bearer\s+)?[^,\""'\s}\]]+", RegexOptions.Compiled);
	private static readonly Regex BearerToken = new(@"(?i)(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled);
	private static readonly Regex SensitiveProperty = new(@"(?i)^(?:access[_-]?token|refresh[_-]?token|token|client[_-]?secret|ant[_-]?network[_-]?key|network[_-]?key|ant[_-]?key|api[_-]?key|password|authorization)$", RegexOptions.Compiled);
	private static readonly ConcurrentDictionary<string, Regex> UserProfilePatterns = new(StringComparer.OrdinalIgnoreCase);

	public static string Redact(string text, string? userProfilePath = null) => RedactPlainText(text, userProfilePath);

	public static string RedactJsonLine(string jsonLine, string? userProfilePath = null)
	{
		try
		{
			var node = JsonNode.Parse(jsonLine);
			return SanitizeNode(node, userProfilePath)?.ToJsonString() ?? "null";
		}
		catch (JsonException)
		{
			return RedactPlainText(jsonLine, userProfilePath);
		}
	}

	private static string RedactPlainText(string text, string? userProfilePath)
	{
		text = SensitiveQuery.Replace(text, "$1[REDACTED]");
		text = SensitiveAssignment.Replace(text, "$1[REDACTED]");
		text = BearerToken.Replace(text, "$1[REDACTED]");
		if (!string.IsNullOrWhiteSpace(userProfilePath))
			text = UserProfilePatterns.GetOrAdd(userProfilePath, CreateUserProfilePattern).Replace(text, "%USERPROFILE%");
		return text;
	}

	private static JsonNode? SanitizeNode(JsonNode? node, string? userProfilePath)
	{
		if (node is JsonObject obj)
		{
			var sanitized = new JsonObject();
			foreach (var property in obj)
				sanitized[property.Key] = SensitiveProperty.IsMatch(property.Key)
					? JsonValue.Create("[REDACTED]")
					: SanitizeNode(property.Value, userProfilePath);
			return sanitized;
		}
		if (node is JsonArray array)
		{
			var sanitized = new JsonArray();
			foreach (var value in array) sanitized.Add(SanitizeNode(value, userProfilePath));
			return sanitized;
		}
		if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var valueText))
			return JsonValue.Create(RedactPlainText(valueText, userProfilePath));
		return node?.DeepClone();
	}

	private static Regex CreateUserProfilePattern(string userProfilePath)
	{
		var components = userProfilePath.TrimEnd('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
		if (components.Length == 0) return new Regex("(?!)", RegexOptions.Compiled);
		var pathPattern = string.Join(@"[\\/]{1,2}", components.Select(Regex.Escape));
		return new Regex(pathPattern + @"(?=$|[\\/]|[\""'\s,;:)\]])", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}
}
