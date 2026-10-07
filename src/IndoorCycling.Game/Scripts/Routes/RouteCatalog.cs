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
		const double oldRouteLengthMeters = 5850;
		RouteSegment[] segments =
		[
			new(0, 277, 0.36, "Oval onto Cumberland"),
			new(277, 615, -0.30, "Cumberland south section"),
			new(615, 941, -0.31, "Cumberland to High Street"),
			new(941, 1512, 0.18, "Cumberland central"),
			new(1512, 1991, 0.21, "Cumberland to Albany"),
			new(1991, 2380, 0.51, "Cumberland gentle rise"),
			new(2380, 2882, 0.40, "Approach to Gardens"),
			new(2882, 2942, 0.00, "Turn at Gardens"),
			new(2942, 3467, -0.19, "Great King southbound"),
			new(3467, 3852, -0.26, "Great King to Albany"),
			new(3852, 4356, -0.20, "Castle central"),
			new(4356, 4915, -0.18, "Castle to High Street"),
			new(4915, 5298, 0.00, "Castle lower flat"),
			new(5298, 5729, -0.23, "Return to Oval"),
			new(5729, 5850, 0.00, "Oval finish")
		];
		return RouteDataLoader.LoadEmbedded(typeof(RouteCatalog).Assembly,
			"GWift.GameData.Routes.dunedin-one-way-oval-gardens.route-points.json",
			"dunedin-one-way-oval-gardens", "Dunedin One-way: Oval to Gardens Loop",
			"OSM SH1 one-way loop from the Oval to Gardens and back. Geometry © OpenStreetMap contributors, ODbL 1.0.",
			oldRouteLengthMeters, segments);
	}
}
