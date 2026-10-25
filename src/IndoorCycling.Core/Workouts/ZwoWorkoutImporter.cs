using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace IndoorCycling.Core.Workouts;

public sealed record ZwoImportResult(WorkoutDefinition? Workout, IReadOnlyList<string> Warnings, string? Error)
{
    public bool CanImport => Workout is not null && Error is null;
}

/// <summary>Safe bounded subset of Zwift .zwo: Warmup, Cooldown, SteadyState, Ramp and IntervalsT.</summary>
public static class ZwoWorkoutImporter
{
    public const int MaximumFileBytes = 512 * 1024;
    private const int MaximumExpandedSteps = 256;
    public static ZwoImportResult Import(Stream input, string stableId = "imported-zwo")
    {
        var warnings = new List<string>();
        try
        {
            if (input.CanSeek && input.Length > MaximumFileBytes) return new(null, warnings, "Workout file exceeds 512 KiB.");
            using var limited = new MemoryStream(); input.CopyTo(limited);
            if (limited.Length > MaximumFileBytes) return new(null, warnings, "Workout file exceeds 512 KiB.");
            limited.Position = 0;
            using var reader = XmlReader.Create(limited, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes, MaxCharactersFromEntities = 0 });
            var document = XDocument.Load(reader);
            var root = document.Root;
            var workout = root?.Element("workout");
            if (root?.Name.LocalName != "workout_file" || workout is null)
                return new(null, warnings, "File must contain a workout_file with a workout element.");
            var steps = new List<WorkoutStep>(); var number = 0;
            void Add(string name, WorkoutBlockKind kind, double duration, double low, double? high, int? cadence)
            {
                if (steps.Count >= MaximumExpandedSteps) throw new InvalidDataException("Workout expands beyond 256 steps.");
                steps.Add(new($"zwo-{++number:D3}", name, kind, duration,
                    new WorkoutTarget(low, WorkoutTargetUnit.FtpPercent), high is { } value ? new WorkoutTarget(value, WorkoutTargetUnit.FtpPercent) : null,
                    cadence));
            }
            foreach (var element in workout.Elements())
            {
                var type = element.Name.LocalName;
                var duration = Number(element, "Duration", 0);
                var cadence = element.Attribute("Cadence") is { } ca ? (int?)Math.Round(Number(ca.Value)) : null;
                switch (type)
                {
                    case "Warmup": Add("Warm-up", WorkoutBlockKind.WarmUp, duration, Number(element, "PowerLow", 0), Number(element, "PowerHigh", 0), cadence); break;
                    case "Cooldown": Add("Cool-down", WorkoutBlockKind.CoolDown, duration, Number(element, "PowerLow", 0), Number(element, "PowerHigh", 0), cadence); break;
                    case "SteadyState": Add("Steady work", WorkoutBlockKind.Work, duration, Number(element, "Power", 0), null, cadence); break;
                    case "Ramp": Add("Ramp", WorkoutBlockKind.Work, duration, Number(element, "PowerLow", 0), Number(element, "PowerHigh", 0), cadence); break;
                    case "IntervalsT":
                        var repeatValue = Number(element, "Repeat", 0);
                        if (repeatValue != Math.Truncate(repeatValue) || repeatValue is < 1 or > 64)
                            throw new InvalidDataException("IntervalsT repeat count must be a whole number from 1–64.");
                        var repeat = (int)repeatValue;
                        var onDuration = Number(element, "OnDuration", 0); var offDuration = Number(element, "OffDuration", 0);
                        var onPower = Number(element, "OnPower", 0); var offPower = Number(element, "OffPower", 0);
                        for (var i = 0; i < repeat; i++)
                        {
                            Add($"Interval {i + 1} work", WorkoutBlockKind.Work, onDuration, onPower, null, cadence);
                            Add($"Interval {i + 1} recovery", WorkoutBlockKind.Recovery, offDuration, offPower, null, cadence);
                        }
                        break;
                    default:
                        warnings.Add($"Unsupported timed step '{type}' was not imported. Import rejected to avoid changing workout timing.");
                        return new(null, warnings, $"Unsupported workout step: {type}.");
                }
            }
            if (steps.Count == 0) return new(null, warnings, "Workout contains no supported steps.");
            var name = (string?)root.Element("name") ?? (string?)root.Element("Name") ?? "Imported workout";
            var definition = new WorkoutDefinition(stableId, name.Trim(), "Imported from Zwift .zwo.", steps, "Local ZWO import");
            definition.Validate(ftpWatts: 200);
            return new(definition, warnings, null);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return new(null, warnings, ex.Message); }
    }

    private static double Number(XElement element, string attribute, double fallback) => Number((string?)element.Attribute(attribute) ?? fallback.ToString(CultureInfo.InvariantCulture));
    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidDataException("Workout contains an invalid numeric value.");
        if (value < 0) throw new InvalidDataException("Workout values may not be negative.");
        return value;
    }
}
