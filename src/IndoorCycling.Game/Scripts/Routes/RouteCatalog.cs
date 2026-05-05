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

		new RouteDefinition(
			Id: "dunedin-one-way-oval-gardens",
			Name: "Dunedin One-way: Oval to Gardens Loop",
			Description: "Real GPS-based circuit of the Dunedin SH1 one-way system from the Oval to the Botanic Garden / Gardens end and back.",
			DistanceMeters: 5850,
			Segments:
			[
				new RouteSegment(0, 277, 0.36, "Oval onto Cumberland"),
				new RouteSegment(277, 615, -0.30, "Cumberland south section"),
				new RouteSegment(615, 941, -0.31, "Cumberland to High Street"),
				new RouteSegment(941, 1512, 0.18, "Cumberland central"),
				new RouteSegment(1512, 1991, 0.21, "Cumberland to Albany"),
				new RouteSegment(1991, 2380, 0.51, "Cumberland gentle rise"),
				new RouteSegment(2380, 2882, 0.40, "Approach to Gardens"),
				new RouteSegment(2882, 2942, 0.00, "Turn at Gardens"),
				new RouteSegment(2942, 3467, -0.19, "Great King southbound"),
				new RouteSegment(3467, 3852, -0.26, "Great King to Albany"),
				new RouteSegment(3852, 4356, -0.20, "Castle central"),
				new RouteSegment(4356, 4915, -0.18, "Castle to High Street"),
				new RouteSegment(4915, 5298, 0.00, "Castle lower flat"),
				new RouteSegment(5298, 5729, -0.23, "Return to Oval"),
				new RouteSegment(5729, 5850, 0.00, "Oval finish")
			],
			Points:
			[
				new RoutePoint(0, -45.88355, 170.49850, 5),
				new RoutePoint(277, -45.88170, 170.50090, 6),
				new RoutePoint(615, -45.87925, 170.50347, 5),
				new RoutePoint(941, -45.87690, 170.50600, 4),
				new RoutePoint(1512, -45.87285, 170.51053, 5),
				new RoutePoint(1991, -45.86935, 170.51415, 6),
				new RoutePoint(2380, -45.86638, 170.51680, 8),
				new RoutePoint(2882, -45.86265, 170.52045, 10),
				new RoutePoint(2942, -45.86218, 170.52008, 10),
				new RoutePoint(3467, -45.86595, 170.51600, 9),
				new RoutePoint(3852, -45.86878, 170.51312, 8),
				new RoutePoint(4356, -45.87245, 170.50930, 7),
				new RoutePoint(4915, -45.87656, 170.50515, 6),
				new RoutePoint(5298, -45.87935, 170.50225, 6),
				new RoutePoint(5729, -45.88255, 170.49910, 5),
				new RoutePoint(5850, -45.88355, 170.49850, 5)
			]
		)
	];

	public static RouteDefinition? GetById(string id)
	{
		return Routes.FirstOrDefault(route => route.Id == id);
	}
}