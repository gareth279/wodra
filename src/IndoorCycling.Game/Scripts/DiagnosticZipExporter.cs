using System.Linq;
using System.Threading.Tasks;

/// <summary>Compatibility facade used by the shared diagnostics panel.</summary>
public static class DiagnosticZipExporter
{
	public static Task<DiagnosticExportResult> ExportAsync(string sessionId, bool includeScreenshots, bool reduced = false) =>
		DiagnosticSessionExporter.ExportAsync(sessionId, includeScreenshots, reduced);

	public static Task<DiagnosticExportResult> ExportAsync()
	{
		var session = DiagnosticSessionExporter.ListSessions().FirstOrDefault();
		return session is null
			? Task.FromResult(new DiagnosticExportResult(false, "No diagnostic session is available."))
			: DiagnosticSessionExporter.ExportAsync(session.Id, includeScreenshots: false);
	}
}
