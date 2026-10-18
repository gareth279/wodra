using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IndoorCycling.Core.Simulation;

/// <summary>Local-player collectible visuals and swept route-relative collection.</summary>
public partial class CollectibleCatLayer : Node3D
{
	private readonly Dictionary<string, Sprite3D> _sprites = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Vector3> _restPositions = new(StringComparer.Ordinal);
	private IReadOnlyList<CollectiblePlacement> _placements = Array.Empty<CollectiblePlacement>();
	private VisualRoute? _route;
	private double _previousDistance;
	private double _previousLateral;
	private bool _hasPrevious;
	private readonly HashSet<string> _awarded = new(StringComparer.Ordinal);
	private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
	private readonly CollectibleAttemptState _attempt = new();
	private Texture2D? _blue;
	private Texture2D? _gold;
	private double _animationTime;
	public int Score { get; private set; }
	public int Collected { get; private set; }
	public int Available => _placements.Count;
	public CollectibleAttemptState Attempt => _attempt;
	public event Action<CollectiblePlacement>? CollectedPickup;

	public override void _Ready()
	{
		_blue = GD.Load<Texture2D>("res://Data/Cats/blue-cat.png");
		_gold = GD.Load<Texture2D>("res://Data/Cats/gold-cat.png");
	}

	public void Configure(string routeId, VisualRoute route)
	{
		_route = route;
		_placements = CollectibleChallenge.Generate(routeId, route.LengthMeters,
			route.Samples.Select(s => (s.DistanceMeters, s.Forward)).ToArray());
		foreach (var pickup in _placements)
		{
			var pose = route.GetPoseAtDistance(pickup.RouteDistanceMeters);
			var f = new Vector3(pose.Forward.X, pose.Forward.Y, pose.Forward.Z);
			var lateral = Vector3.Up.Cross(f).Normalized();
			var sprite = new Sprite3D
			{
				Name = pickup.Id.Replace(':', '_'),
				Texture = pickup.Kind == CollectibleKind.GoldenCat ? _gold : _blue,
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				PixelSize = 0.0012f,
				Position = new Vector3(pose.Position.X, pose.Position.Y + 1.25f, pose.Position.Z) + lateral * (float)pickup.LateralMeters,
				Visible = false,
				AlphaCut = SpriteBase3D.AlphaCutMode.Discard
			};
			AddChild(sprite);
			_sprites.Add(pickup.Id, sprite);
			_restPositions.Add(pickup.Id, sprite.Position);
		}
	}

	public override void _Process(double delta)
	{
		_animationTime += delta;
		foreach (var pair in _sprites)
			if (pair.Value.Visible && _restPositions.TryGetValue(pair.Key, out var rest))
				pair.Value.Position = rest + Vector3.Up * (float)(Math.Sin(_animationTime * 2 + pair.Key.GetHashCode(StringComparison.Ordinal)) * 0.04);
	}

	public void SuppressNextSweep() => _hasPrevious = false;

	public void UpdateRide(double distance, double lateral, bool enabled, bool looping = true)
	{
		if (_route is null) return;
		Visible = enabled;
		if (!enabled) { _hasPrevious = false; return; }
		var lap = Math.Floor(distance / _route.LengthMeters);
		foreach (var pickup in _placements)
		{
			var absolute = lap * _route.LengthMeters + pickup.RouteDistanceMeters;
			if (absolute < distance - 5) absolute += _route.LengthMeters;
			var ahead = absolute - distance;
			var visible = ahead > 4 && ahead < 105 && !_awarded.Contains($"{lap}:{pickup.Id}");
			if (_sprites.TryGetValue(pickup.Id, out var sprite)) sprite.Visible = visible;
			var previousLap = (long)Math.Floor(_hasPrevious ? _previousDistance / _route.LengthMeters : distance / _route.LengthMeters);
			var currentLap = (long)Math.Floor(distance / _route.LengthMeters);
			if (_hasPrevious && distance >= _previousDistance && currentLap >= previousLap && currentLap - previousLap <= 1)
			{
				for (var crossedLap = previousLap; crossedLap <= currentLap; crossedLap++)
				{
					if (!looping && crossedLap != 0) continue;
					var key = $"{crossedLap}:{pickup.Id}";
					var target = crossedLap * _route.LengthMeters + pickup.RouteDistanceMeters;
					if (target < _previousDistance || target > distance || !_resolved.Add(key)) continue;
					if (CollectibleChallenge.SweptHit(_previousDistance, distance, _previousLateral, lateral,
						pickup, _route.LengthMeters, looping))
					{
						if (_awarded.Add(key) && _attempt.Award(pickup, (int)crossedLap))
						{ Score = _attempt.Score; Collected = _attempt.PickupsCollected; CollectedPickup?.Invoke(pickup); }
					}
					else GameLogger.Log("collectible_missed", new { pickup.Id, pickup.Kind, pickup.RouteDistanceMeters,
						pickup.LateralMeters, riderLateralMeters = _previousLateral, endLateralMeters = lateral, lap = crossedLap });
				}
			}
		}
		_previousDistance = distance;
		_previousLateral = lateral;
		_hasPrevious = true;
	}

}
