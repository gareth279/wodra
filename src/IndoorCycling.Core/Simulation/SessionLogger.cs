using System.Diagnostics;
using System.Text.Json;

public sealed class SessionLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public string LogPath => _logPath;

    public SessionLogger(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
    }

    public void Log(string type, object payload)
    {
        var entry = new
        {
            ts = DateTimeOffset.Now,
            type,
            payload
        };

        var json = JsonSerializer.Serialize(entry);

        lock (_lock)
        {
            File.AppendAllText(_logPath, json + Environment.NewLine);
        }
    }

    public void OpenTailWindow()
    {
        var escaped = _logPath.Replace("'", "''");

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoExit -Command \"Get-Content '{escaped}' -Wait\"",
            UseShellExecute = true
        });
    }
}