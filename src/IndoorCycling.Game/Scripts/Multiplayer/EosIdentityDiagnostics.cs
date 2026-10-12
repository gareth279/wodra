using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>Stable redaction for identity values used in diagnostics.</summary>
public static class EosIdentityDiagnostics
{
	public static string HashProductUserId(string? productUserId)
	{
		if (string.IsNullOrWhiteSpace(productUserId)) return "missing";
		var digest = SHA256.HashData(Encoding.UTF8.GetBytes(productUserId));
		return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
	}
}
