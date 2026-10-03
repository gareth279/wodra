Run the focused recording tests from the repository root:

```powershell
dotnet run --project tests/IndoorCycling.Recording.Tests -c Debug
dotnet run --project tests/IndoorCycling.Recording.Tests -c Release
```

This dependency-free executable returns a nonzero exit code on failure. It tests
the Core accounting helper and links the production FIT recorder with small
settings/logging host substitutes, so no Godot runtime or BLE device is needed.
FIT round trips use the existing Garmin SDK and FIT loader, checking record data,
session summaries, finalization, pause/resume, retained paths, and empty rides.
