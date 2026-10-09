namespace IndoorCycling.Core.Simulation;

public static class DummyTrainerMath
{
    public static int CalculateWatts(string mode, int targetWatts, double wattsPerKg, double bodyWeightKg) =>
        mode == "W/kg" ? WattsFromKilograms(wattsPerKg, bodyWeightKg) : ValidateWatts(targetWatts);

    public static int WattsFromKilograms(double wattsPerKg, double bodyWeightKg)
    {
        if (!double.IsFinite(bodyWeightKg) || bodyWeightKg is < 30 or > 200)
            throw new ArgumentOutOfRangeException(nameof(bodyWeightKg), "Body weight must be 30–200 kg.");
        if (!double.IsFinite(wattsPerKg) || wattsPerKg is < 0 or > 12)
            throw new ArgumentOutOfRangeException(nameof(wattsPerKg), "W/kg must be 0–12.0.");
        return Math.Clamp((int)Math.Round(wattsPerKg * bodyWeightKg), 0, 1500);
    }

    public static int ValidateWatts(int watts) => Math.Clamp(watts, 0, 1500);

    public static int ValidateCadence(int rpm) => Math.Clamp(rpm, 0, 160);
}
