using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;

public static class RouteCatalog
{
	private static readonly List<string> _packageErrors = [];
	public static IReadOnlyList<string> PackageErrors => _packageErrors;
	public static IReadOnlyList<RouteDefinition> Routes { get; } = LoadRoutes();

	public static RouteDefinition? GetById(string id)
	{
		return Routes.FirstOrDefault(route => route.Id == id);
	}

	private static IReadOnlyList<RouteDefinition> LoadRoutes()
	{
		var routes = new List<RouteDefinition> { CreateDunedinRoute(), CreateVelodromeRoute() };
		var reservedTestIds = new HashSet<string>(System.StringComparer.Ordinal)
			{ "test-route", "flat-test-route", "climb-test-route" };
		foreach (var packageRoot in RoutePackageFiles.PackageDirectories.Distinct(System.StringComparer.OrdinalIgnoreCase))
		{
			if (!Directory.Exists(packageRoot)) continue;
			foreach (var directory in Directory.EnumerateDirectories(packageRoot).OrderBy(path => path, System.StringComparer.OrdinalIgnoreCase))
			{
				var packageId = Path.GetFileName(directory);
				// Packaged built-ins take precedence over a same-ID optional user installation.
				if (routes.Any(existing => existing.Id == packageId)) continue;
				var routePointsPath = Path.Combine(directory, $"{packageId}.route-points.json");
				if (!File.Exists(routePointsPath))
				{
					_packageErrors.Add($"{packageId}: missing {packageId}.route-points.json");
					continue;
				}
				try
				{
					var route = RouteDataLoader.LoadPackage(routePointsPath);
					if (reservedTestIds.Contains(route.Id))
						throw new InvalidDataException("Development fixture IDs are reserved and cannot be installed as playable routes.");
					if (!string.Equals(route.Id, packageId, System.StringComparison.Ordinal))
						throw new InvalidDataException("Package folder name does not match its route ID.");
					if (routes.Any(existing => existing.Id == route.Id))
						throw new InvalidDataException("Route ID already exists in the built-in catalogue.");
					routes.Add(route);
				}
				catch (Exception ex)
				{
					_packageErrors.Add($"{packageId}: {ex.Message}");
				}
			}
		}
		return routes;
	}

	private static RouteDefinition CreateDunedinRoute()
	{
		// Chainages follow the approved route's OSM way transitions; they are not scaled from the retired route.
		RouteSegment[] segments =
		[
			new(0, 740.377944606337, 0, "Crawford Street northbound"),
			new(740.377944606337, 998.6737252213936, 0, "Queens Gardens northbound"),
			new(998.6737252213936, 1925.1127519957927, 0, "Cumberland Street northbound"),
			new(1925.1127519957927, 2188.2277361055435, 0, "Malcolm Street"),
			new(2188.2277361055435, 2632.0259160793826, 0, "Great King Street to Gardens"),
			new(2632.0259160793826, 2752.237291802371, 0, "Gardens turnaround connector"),
			new(2752.237291802371, 3195.5065715925757, 0, "Cumberland Street southbound"),
			new(3195.5065715925757, 3455.974768929621, 0, "Gowland Street"),
			new(3455.974768929621, 4196.404172994895, 0, "Castle Street"),
			new(4196.404172994895, 4421.057440131015, 0, "Anzac Avenue"),
			new(4421.057440131015, 4634.551310392236, 0, "Queens Gardens southbound"),
			new(4634.551310392236, 5377.550175433017, 0, "Cumberland Street to Oval"),
			new(5377.550175433017, 5495.292819082555, 0, "Wolseley Street closure")
		];
		return RouteDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
			"Wodra.GameData.Routes.dunedin-one-way-oval-gardens.route-points.json",
			"dunedin-one-way-oval-gardens", "Dunedin One-way: Oval to Gardens Loop",
			"OSM SH1 one-way loop from the Oval to Gardens and back. Geometry © OpenStreetMap contributors, ODbL 1.0.",
			segments) with
		{
			RouteType = "Loop",
			Difficulty = "Easy",
			SourceAttribution = "© OpenStreetMap contributors, ODbL 1.0; elevation: LINZ hosted Otago Regional Council LiDAR DEM, CC-BY-4.0",
			RouteDataVersion = 1,
			PublishedTotalAscentMeters = 18.164954589432845
		};
	}

	private static RouteDefinition CreateVelodromeRoute()
	{
		// 250 m reference line: two 45 m straights plus two 80 m semicircles.
		// Radius is derived from circumference so authoritative chainage is exactly 250 m.
		const double straight = 45;
		const double radius = 80 / (2 * Math.PI);
		const double halfStraight = straight / 2;
		var points = new List<RoutePoint>(251);
		for (var metre = 0; metre <= 250; metre++)
		{
			double x, z;
			var d = (double)metre;
			if (d <= straight)
			{
				x = -radius; z = -halfStraight + d;
			}
			else if (d <= 125)
			{
				var angle = Math.PI - (d - straight) / radius;
				x = radius * Math.Cos(angle); z = halfStraight + radius * Math.Sin(angle);
			}
			else if (d <= 170)
			{
				x = radius; z = halfStraight - (d - 125);
			}
			else
			{
				var angle = -(d - 170) / radius;
				x = radius * Math.Cos(angle); z = -halfStraight + radius * Math.Sin(angle);
			}
			const double degreesPerMeter = 1.0 / 111_320.0;
			points.Add(new RoutePoint(d, -z * degreesPerMeter, x * degreesPerMeter,
				0, 0, 0));
		}
		return new RouteDefinition("wodra-velodrome-250", "250 m Indoor Velodrome",
			"Banked indoor track. The reference riding line measures 250 m per lap; banking is visual only.",
			250,
			[
				new(0, 45, 0, "Start/finish straight"),
				new(45, 125, 0, "North banked turn"),
				new(125, 170, 0, "Back straight"),
				new(170, 250, 0, "South banked turn")
			], points)
		{
			RouteType = "Loop",
			Difficulty = "Unrated",
			Environment = RouteEnvironment.Velodrome,
			Capabilities = RouteCapabilities.SoloVelodrome,
			SupportedModes = [RideActivityMode.FreeRide, RideActivityMode.LapTiming,
				RideActivityMode.Flying200, RideActivityMode.Standing500, RideActivityMode.Standing1000],
			SourceAttribution = "Original procedural track geometry; no real venue branding or survey geometry.",
			RouteDataVersion = 1,
			PublishedTotalAscentMeters = 0
		};
	}
}
