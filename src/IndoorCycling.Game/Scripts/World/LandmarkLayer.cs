using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public readonly record struct LandmarkDebugState(int NearbyCount, string? Name, double? RouteDistanceMeters,
	Vector3? WorldPosition, double? RiderDistanceMeters);

/// <summary>Small static OSM landmark proxies positioned in the route's local world frame.</summary>
public partial class LandmarkLayer : Node3D
{
	private const double ObjectVisibilityMeters = 700;
	private const double LabelVisibilityMeters = 220;
	private const double NearbyDiagnosticMeters = 700;
	private readonly List<Entry> _entries = [];
	private float _originElevationMeters;
	private double _routeLengthMeters;
	private bool _configured;
	private int _proxyMeshCount;
	private sealed record Entry(RouteLandmarkData Data, Node3D Root, Label3D Label, Vector3 Position);

	public int CreatedLandmarkCount => _entries.Count;
	public int CreatedNodeCount => _entries.Count * 2 + _proxyMeshCount;

	public void Configure(VisualRoute visualRoute, RouteDefinition route, IReadOnlyList<RouteLandmarkData> landmarks,
		IReadOnlyList<RouteBuildingData>? buildings = null)
	{
		if (_configured) throw new InvalidOperationException("Landmark layer is already configured.");
		_configured = true;
		_routeLengthMeters = route.DistanceMeters;
		_originElevationMeters = (float)(route.Points?.FirstOrDefault()?.ElevationMeters ?? 0);
		foreach (var landmark in landmarks)
		{
			var routeSample = route.GetSampleAt(landmark.NearestRouteDistanceMeters);
			var groundElevation = routeSample.ElevationMeters ?? _originElevationMeters;
			var localPosition = visualRoute.GetPositionForCoordinate(landmark.Latitude, landmark.Longitude, groundElevation);
			var worldPosition = new Vector3(localPosition.X, localPosition.Y, localPosition.Z);
			var root = new Node3D { Name = SafeNodeName(landmark.Name) };
			root.Position = worldPosition;
			if (!HasNearbyBuildingProxy(visualRoute, localPosition, landmark.Category, buildings))
				AddProxy(root, landmark.Category);
			_proxyMeshCount += root.GetChildCount();
			var label = new Label3D
			{
				Text = landmark.Name,
				FontSize = 26,
				PixelSize = 0.012f,
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				NoDepthTest = true,
				Modulate = Colors.White,
				OutlineSize = 6,
				OutlineModulate = new Color(0.05f, 0.07f, 0.09f, 1),
				Position = Vector3.Up * 5
			};
			root.AddChild(label);
			AddChild(root);
			_entries.Add(new Entry(landmark, root, label, worldPosition));
		}
	}

	private static bool HasNearbyBuildingProxy(VisualRoute visualRoute, System.Numerics.Vector3 landmark,
		string category, IReadOnlyList<RouteBuildingData>? buildings)
	{
		if (buildings is null || category is not ("railway/station" or "education/civic" or "building/civic")) return false;
		foreach (var building in buildings)
		{
			var points = building.Footprint.Take(building.Footprint.Count - 1).ToArray();
			if (points.Length == 0) continue;
			var latitude = points.Average(point => point.Latitude);
			var longitude = points.Average(point => point.Longitude);
			var center = visualRoute.GetPositionForCoordinate(latitude, longitude, building.BaseElevationMeters ?? 0);
			var dx = center.X - landmark.X; var dz = center.Z - landmark.Z;
			if (dx * dx + dz * dz <= 60 * 60) return true;
		}
		return false;
	}

	public void UpdateForRideDistance(double rideDistanceMeters)
	{
		if (!_configured || _routeLengthMeters <= 0) return;
		var wrapped = ((rideDistanceMeters % _routeLengthMeters) + _routeLengthMeters) % _routeLengthMeters;
		foreach (var entry in _entries)
		{
			var delta = Math.Abs(entry.Data.NearestRouteDistanceMeters - wrapped);
			var alongRouteDistance = Math.Min(delta, _routeLengthMeters - delta);
			entry.Root.Visible = alongRouteDistance <= ObjectVisibilityMeters;
			entry.Label.Visible = alongRouteDistance <= LabelVisibilityMeters;
		}
	}

	public LandmarkDebugState GetDebugState(double rideDistanceMeters, Vector3 riderPosition)
	{
		if (_entries.Count == 0 || _routeLengthMeters <= 0) return new(0, null, null, null, null);
		var wrapped = ((rideDistanceMeters % _routeLengthMeters) + _routeLengthMeters) % _routeLengthMeters;
		var nearby = _entries.Select(entry =>
		{
			var delta = Math.Abs(entry.Data.NearestRouteDistanceMeters - wrapped);
			return (Entry: entry, Along: Math.Min(delta, _routeLengthMeters - delta),
				FromRider: riderPosition.DistanceTo(entry.Root.GlobalPosition));
		}).Where(item => item.Along <= NearbyDiagnosticMeters).ToArray();
		var nearest = nearby.OrderBy(item => item.FromRider).FirstOrDefault();
		return new(nearby.Length, nearest.Entry?.Data.Name, nearest.Entry?.Data.NearestRouteDistanceMeters,
			nearest.Entry is null ? null : nearest.Entry.Root.GlobalPosition,
			nearest.Entry is null ? null : nearest.FromRider);
	}

	private static void AddProxy(Node3D root, string category)
	{
		if (category is "park" or "garden")
		{
			var trunk = Material(new Color(0.31f, 0.19f, 0.09f));
			var foliage = Material(new Color(0.12f, 0.42f, 0.19f));
			foreach (var x in new[] { -2.4f, 0f, 2.4f })
			{
				AddMesh(root, new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.25f, Height = 2.4f }, trunk,
					new Vector3(x, 1.2f, 0));
				AddMesh(root, new CylinderMesh { TopRadius = 0, BottomRadius = 1.1f, Height = 2.5f }, foliage,
					new Vector3(x, 3.3f, 0));
			}
		}
		else if (category is "monument" or "historic")
		{
			var stone = Material(new Color(0.68f, 0.66f, 0.59f));
			AddMesh(root, new CylinderMesh { TopRadius = 0.65f, BottomRadius = 0.8f, Height = 0.45f }, stone, new Vector3(0, .22f, 0));
			AddMesh(root, new CylinderMesh { TopRadius = 0.32f, BottomRadius = 0.42f, Height = 3.3f }, stone, new Vector3(0, 2.0f, 0));
			AddMesh(root, new SphereMesh { Radius = .52f, Height = 1.04f }, Material(new Color(.77f, .73f, .63f)), new Vector3(0, 3.9f, 0));
		}
		else if (category == "railway/station")
		{
			AddMesh(root, new BoxMesh { Size = new Vector3(9, 4.5f, 6) }, Material(new Color(.72f, .38f, .17f)), new Vector3(0, 2.25f, 0));
			AddMesh(root, new BoxMesh { Size = new Vector3(11, .35f, 1.2f) }, Material(new Color(.2f, .24f, .29f)), new Vector3(0, .18f, 4));
		}
		else if (category is "education/civic" or "building/civic" or "university")
		{
			AddMesh(root, new BoxMesh { Size = new Vector3(10, 8, 8) }, Material(new Color(.65f, .58f, .43f)), new Vector3(0, 4, 0));
			AddMesh(root, new BoxMesh { Size = new Vector3(10.8f, .5f, 8.8f) }, Material(new Color(.32f, .26f, .2f)), new Vector3(0, 8.25f, 0));
		}
		else
		{
			AddMesh(root, new CylinderMesh { TopRadius = .35f, BottomRadius = .65f, Height = 2.2f },
				Material(new Color(.18f, .42f, .72f)), new Vector3(0, 1.1f, 0));
		}
	}

	private static StandardMaterial3D Material(Color colour) => new() { AlbedoColor = colour, Roughness = 1 };
	private static void AddMesh(Node3D root, Mesh mesh, StandardMaterial3D material, Vector3 position) =>
		root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position });
	private static string SafeNodeName(string name) => string.Concat(name.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
}
