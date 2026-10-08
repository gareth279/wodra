using System.Collections.Generic;
using System.Linq;

public static class RouteCatalog
{
	public static IReadOnlyList<RouteDefinition> Routes { get; } =
	[
		new RouteDefinition(
			Id: "test-route",
			Name: "Test Route",
			Description: "Simple rolling test route.",
			DistanceMeters: 3500,
			Segments:
			[
				new RouteSegment(0, 500, 0, "Warm-up flat"),
				new RouteSegment(500, 1500, 3, "Gentle climb"),
				new RouteSegment(1500, 2500, 6, "Main climb"),
				new RouteSegment(2500, 3500, -2, "Descent")
			],
			Points:
			[
				new RoutePoint(0, -45.8640, 170.5140, 100),
				new RoutePoint(500, -45.8638, 170.5180, 100),
				new RoutePoint(1500, -45.8620, 170.5240, 130),
				new RoutePoint(2500, -45.8600, 170.5300, 190),
				new RoutePoint(3500, -45.8640, 170.5140, 170)
			]
		),

		new RouteDefinition(
			Id: "flat-test-route",
			Name: "Flat Test Route",
			Description: "Mostly flat route for testing cadence, recording and UI without trainer resistance changes.",
			DistanceMeters: 3000,
			Segments:
			[
				new RouteSegment(0, 750, 0, "Flat start"),
				new RouteSegment(750, 1500, 0.5, "Slight rise"),
				new RouteSegment(1500, 2250, 0, "Flat middle"),
				new RouteSegment(2250, 3000, -0.5, "Slight drop")
			]
		),

		new RouteDefinition(
			Id: "climb-test-route",
			Name: "Climb Test Route",
			Description: "Short climbing route to test trainer resistance and elevation recording.",
			DistanceMeters: 3200,
			Segments:
			[
				new RouteSegment(0, 400, 0, "Warm-up flat"),
				new RouteSegment(400, 1000, 3, "Opening climb"),
				new RouteSegment(1000, 1700, 6, "Steady climb"),
				new RouteSegment(1700, 2400, 9, "Hard ramp"),
				new RouteSegment(2400, 2800, 4, "Easing climb"),
				new RouteSegment(2800, 3200, -3, "Recovery descent")
			]
		),

		CreateDunedinRoute()
	];

	public static RouteDefinition? GetById(string id)
	{
		return Routes.FirstOrDefault(route => route.Id == id);
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
			"GWift.GameData.Routes.dunedin-one-way-oval-gardens.route-points.json",
			"dunedin-one-way-oval-gardens", "Dunedin One-way: Oval to Gardens Loop",
			"OSM SH1 one-way loop from the Oval to Gardens and back. Geometry © OpenStreetMap contributors, ODbL 1.0.",
			segments);
	}
}
