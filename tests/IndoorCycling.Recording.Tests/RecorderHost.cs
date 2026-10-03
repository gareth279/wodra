// The recorder has no Godot API dependency. Supply only its settings/logging host
// so these tests exercise the production recorder without starting Godot.
public static class GameBootstrap
{
    public static TestSettings Settings { get; } = new();
}

public sealed class TestSettings
{
    public TestRecordingSettings Recording { get; } = new();
}

public sealed class TestRecordingSettings
{
    public string OutputFolder { get; set; } = "";
}

public static class GameLogger
{
    public static void Log(string type, object? payload = null) { }
}
