using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>EOS public client configuration resolved from environment and local JSON files.</summary>
public sealed record EosClientConfiguration(string ProductId, string SandboxId, string DeploymentId,
	string ClientId, string ClientSecret, string? SourcePath = null, string? LoadError = null)
{
	private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
	private static readonly (string EnvironmentName, Func<EosClientConfiguration, string> Read,
		Action<EosClientConfigurationValues, string?> Write)[] Fields =
	{
		("WODRA_EOS_PRODUCT_ID", c => c.ProductId, (v, s) => v.ProductId = s),
		("WODRA_EOS_SANDBOX_ID", c => c.SandboxId, (v, s) => v.SandboxId = s),
		("WODRA_EOS_DEPLOYMENT_ID", c => c.DeploymentId, (v, s) => v.DeploymentId = s),
		("WODRA_EOS_CLIENT_ID", c => c.ClientId, (v, s) => v.ClientId = s),
		("WODRA_EOS_CLIENT_SECRET", c => c.ClientSecret, (v, s) => v.ClientSecret = s)
	};

	public IReadOnlyList<string> MissingVariables => Fields
		.Where(field => string.IsNullOrWhiteSpace(field.Read(this)))
		.Select(field => field.EnvironmentName)
		.ToArray();
	public bool IsConfigured => LoadError is null && MissingVariables.Count == 0;
	public bool IsInvalid => LoadError is not null;

	public static EosClientConfiguration LoadCurrent(string projectDirectory, bool runningInEditor)
	{
		var environment = Fields.ToDictionary(field => field.EnvironmentName,
			field => Environment.GetEnvironmentVariable(field.EnvironmentName), StringComparer.Ordinal);
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var processDirectory = runningInEditor ? null : Path.GetDirectoryName(Environment.ProcessPath);
		var assemblyDirectory = Path.GetDirectoryName(typeof(EosClientConfiguration).Assembly.Location);
		var candidates = new[]
		{
			string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "Wodra", "eos-client.json"),
			string.IsNullOrWhiteSpace(processDirectory) ? null : Path.Combine(processDirectory, "eos-client.json"),
			string.IsNullOrWhiteSpace(AppContext.BaseDirectory) ? null : Path.Combine(AppContext.BaseDirectory, "eos-client.json"),
			string.IsNullOrWhiteSpace(assemblyDirectory) ? null : Path.Combine(assemblyDirectory, "eos-client.json"),
			string.IsNullOrWhiteSpace(projectDirectory) ? null : Path.Combine(projectDirectory, "eos-client.json")
		}.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!));
		return LoadFromSources(environment, candidates);
	}

	/// <summary>Source-injected loader used by regression tests; the first existing file wins.</summary>
	public static EosClientConfiguration LoadFromSources(IReadOnlyDictionary<string, string?> environment,
		IEnumerable<string> candidateFiles)
	{
		var fileValues = new EosClientConfigurationValues();
		string? sourcePath = null;
		foreach (var candidate in candidateFiles)
		{
			if (!File.Exists(candidate)) continue;
			sourcePath = Path.GetFullPath(candidate);
			try
			{
				var parsed = JsonSerializer.Deserialize<EosClientConfigurationValues>(File.ReadAllText(sourcePath), JsonOptions);
				if (parsed is null)
					return Empty(sourcePath, "The EOS configuration file contains no JSON object.");
				fileValues = parsed;
			}
			catch (JsonException)
			{
				return Empty(sourcePath, "The EOS configuration file is not valid JSON.");
			}
			catch (IOException)
			{
				return Empty(sourcePath, "The EOS configuration file could not be read.");
			}
			catch (UnauthorizedAccessException)
			{
				return Empty(sourcePath, "The EOS configuration file could not be read.");
			}
			break;
		}

		var resolved = new EosClientConfigurationValues
		{
			ProductId = fileValues.ProductId,
			SandboxId = fileValues.SandboxId,
			DeploymentId = fileValues.DeploymentId,
			ClientId = fileValues.ClientId,
			ClientSecret = fileValues.ClientSecret
		};
		foreach (var field in Fields)
			if (environment.TryGetValue(field.EnvironmentName, out var value) && !string.IsNullOrWhiteSpace(value))
				field.Write(resolved, value);

		return new EosClientConfiguration(resolved.ProductId ?? "", resolved.SandboxId ?? "",
			resolved.DeploymentId ?? "", resolved.ClientId ?? "", resolved.ClientSecret ?? "", sourcePath);
	}

	public void Validate()
	{
		if (LoadError is not null)
			throw new InvalidOperationException("EOS configuration is invalid. See the Wodra log for the file path and diagnostic.");
		if (MissingVariables.Count > 0)
			throw new InvalidOperationException($"EOS configuration is missing required fields: {string.Join(", ", MissingVariables)}.");
	}

	private static EosClientConfiguration Empty(string path, string error) =>
		new("", "", "", "", "", path, error);

	private sealed class EosClientConfigurationValues
	{
		public string? ProductId { get; set; }
		public string? SandboxId { get; set; }
		public string? DeploymentId { get; set; }
		public string? ClientId { get; set; }
		public string? ClientSecret { get; set; }
	}
}
