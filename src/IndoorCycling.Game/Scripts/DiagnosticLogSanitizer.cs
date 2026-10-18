using System;
using System.Text.RegularExpressions;

public static class DiagnosticLogSanitizer
{
	private static readonly Regex SensitiveQuery = new(@"(?i)([?&](?:access_token|refresh_token|token|key|secret|code|password|signature)=)[^&#\s]*", RegexOptions.Compiled);
	private static readonly Regex SensitiveAssignment = new(@"(?i)([""']?\b(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|ant[_-]?network[_-]?key|network[_-]?key|ant[_-]?key|api[_-]?key|password|authorization)\b[""']?\s*[:=]\s*[""']?)(?:Bearer\s+)?[^,""'\s}\]]+", RegexOptions.Compiled);
	private static readonly Regex BearerToken = new(@"(?i)(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled);

	public static string Redact(string text, string? userProfilePath = null)
	{
		text = SensitiveQuery.Replace(text, "$1[REDACTED]");
		text = SensitiveAssignment.Replace(text, "$1[REDACTED]");
		text = BearerToken.Replace(text, "$1[REDACTED]");
		if (!string.IsNullOrWhiteSpace(userProfilePath)) text = text.Replace(userProfilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
		return text;
	}
}
