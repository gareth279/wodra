using System;
using System.Linq;
using Godot;
using NVector2 = System.Numerics.Vector2;

/// <summary>Small cached route/elevation overview driven by the active ride distance.</summary>
public partial class RouteGradientInset : Control
{
	public static string FormatRouteHeader(RouteDefinition route, double rideDistanceMeters, int lap)
	{
		var distance = $"{Math.Max(0, rideDistanceMeters) / 1000:0.00} km";
		return route.RouteType == "Point-to-Point" ? $"ROUTE · {distance}" : $"ROUTE · LAP {Math.Max(1, lap)} · {distance}";
	}
	private RideSessionNode? _session;
	private RideWorldView? _world;
	private RouteOverviewGeometry? _overview;
	private RouteDefinition? _route;
	private Vector2[] _mapLine = [];
	private Vector2[] _profileLine = [];
	private Vector2 _profileOrigin;
	private Vector2 _profileSize;
	private double _lastDistance = double.NaN;
	private bool _hasRide;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		_session = GetNodeOrNull<RideSessionNode>("../../RideSessionNode");
		_world = GetNodeOrNull<RideWorldView>("../../RideWorldView");
		SetProcess(true);
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized && IsInsideTree()) RebuildLayout();
	}

	public override void _Process(double delta)
	{
		var active = _session?.IsRideActive == true;
		var visiblePreference = GameBootstrap.Settings?.Display.RouteGradientInsetVisible ?? true;
		var visible = active && _session?.IsWorkoutSession != true && visiblePreference;
		if (Visible != visible) Visible = visible;
		if (!visible || _session is null || _world?.RouteGeometry is null || _session.CurrentRoute is null) return;
		if (!ReferenceEquals(_route, _session.CurrentRoute) || _overview is null)
		{
			_route = _session.CurrentRoute;
			_overview = new RouteOverviewGeometry(_route, _world.RouteGeometry);
			RebuildLayout();
			_lastDistance = double.NaN;
		}
		var distance = _session.CurrentRideState.DistanceMeters;
		if (!_hasRide || Math.Abs(distance - _lastDistance) >= 0.35)
		{
			_hasRide = true;
			_lastDistance = distance;
			QueueRedraw();
		}
	}

	private void RebuildLayout()
	{
		if (_overview is null || Size.X <= 0 || Size.Y <= 0) return;
		const float mapHeight = 92;
		var mapSize = new NVector2(Size.X, mapHeight);
		var mapPoints = _overview.BuildMapPolyline(mapSize);
		var mapStride = Math.Max(1, (int)Math.Ceiling(mapPoints.Length / 700d));
		_mapLine = mapPoints.Where((_, index) => index % mapStride == 0 || index == mapPoints.Length - 1)
			.Select(p => new Vector2(p.X, p.Y + 24)).ToArray();
		_profileOrigin = new Vector2(42, 158);
		_profileSize = new Vector2(Math.Max(1, Size.X - 54), Math.Max(1, Size.Y - 184));
		var profile = _overview.BuildProfilePolyline(new NVector2(_profileOrigin.X, _profileOrigin.Y), new NVector2(_profileSize.X, _profileSize.Y));
		// Cap draw vertices while preserving source-route shape and the exact live cursor sample.
		var stride = Math.Max(1, (int)Math.Ceiling(profile.Length / 240d));
		_profileLine = profile.Where((_, index) => index % stride == 0 || index == profile.Length - 1)
			.Select(p => new Vector2(p.X, p.Y)).ToArray();
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_overview is null || _session?.CurrentRoute is null) return;
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.035f, 0.045f, 0.052f, 0.88f), true);
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(.68f, .51f, .31f, .72f), false, 1);
		var rideState = _session?.CurrentRideState;
		var currentPose = rideState is null ? default : _overview.GetMapPose(rideState.DistanceMeters, new NVector2(Size.X, 92));
		DrawString(GetThemeDefaultFont(), new Vector2(12, 18), rideState is null ? "ROUTE" : FormatRouteHeader(_session.CurrentRoute, rideState.DistanceMeters, currentPose.Lap), HorizontalAlignment.Left, Size.X - 64, 11, new Color(.88f, .82f, .69f));
		DrawString(GetThemeDefaultFont(), new Vector2(Size.X - 12, 18), "N ↑", HorizontalAlignment.Right, -1, 10, new Color(.75f, .82f, .76f));
		if (_mapLine.Length >= 2) DrawPolyline(_mapLine, new Color(.58f, .65f, .57f), 2.2f, true);
		if (_world is not null)
		{
			foreach (var pacerState in _world.PacerRiderStates)
			{
				var presetId = pacerState.RiderId["ai-pacer-".Length..];
				var selected = _world.SelectedPacerPreset?.Id == presetId;
				var pacerPose = _overview.GetMapPose(pacerState.RouteDistanceMeters, new NVector2(Size.X, 92));
				var pacerMarker = new Vector2(pacerPose.Position.X, pacerPose.Position.Y + 24);
				var pacerColor = presetId switch
				{
					"easy" => new Color(.38f, .77f, 1f),
					"strong" => new Color(1f, .43f, .39f),
					_ => new Color(1f, .82f, .27f)
				};
				if (selected) DrawCircle(pacerMarker, 8, Colors.White);
				DrawCircle(pacerMarker, selected ? 6 : 5, pacerColor);
				DrawCircle(pacerMarker, selected ? 6 : 5, new Color(.1f, .1f, .09f), false, 1.3f, true);
			}
		}
		DrawString(GetThemeDefaultFont(), new Vector2(12, 141), "ELEVATION · M", HorizontalAlignment.Left, -1, 9, new Color(.75f, .78f, .74f));
		DrawString(GetThemeDefaultFont(), new Vector2(12, 154), $"{_overview.MaximumElevationMeters:0}", HorizontalAlignment.Left, -1, 9, new Color(.75f, .78f, .74f));
		DrawString(GetThemeDefaultFont(), new Vector2(12, _profileOrigin.Y + _profileSize.Y), $"{_overview.MinimumElevationMeters:0}", HorizontalAlignment.Left, -1, 9, new Color(.75f, .78f, .74f));
		var profileTop = new Vector2(_profileOrigin.X, _profileOrigin.Y);
		DrawLine(profileTop, new Vector2(_profileOrigin.X + _profileSize.X, _profileOrigin.Y), new Color(.42f, .46f, .43f, .6f), 1);
		if (_profileLine.Length >= 2) DrawPolyline(_profileLine, new Color(.92f, .68f, .34f), 2, true);
		DrawString(GetThemeDefaultFont(), new Vector2(_profileOrigin.X, _profileOrigin.Y + _profileSize.Y + 12), "0 km", HorizontalAlignment.Left, -1, 9, new Color(.75f, .78f, .74f));
		DrawString(GetThemeDefaultFont(), new Vector2(_profileOrigin.X + _profileSize.X, _profileOrigin.Y + _profileSize.Y + 12), $"{_overview.LengthMeters / 1000:0.#} km", HorizontalAlignment.Right, -1, 9, new Color(.75f, .78f, .74f));
		if (_session is null) return;
		var state = _session.CurrentRideState;
		var pose = _overview.GetMapPose(state.DistanceMeters, new NVector2(Size.X, 92));
		var marker = new Vector2(pose.Position.X, pose.Position.Y + 24);
		var direction = new Vector2(pose.Direction.X, pose.Direction.Y);
		DrawLine(marker - direction * 8, marker + direction * 8, new Color(.12f, .13f, .13f), 7, true);
		DrawLine(marker - direction * 8, marker + direction * 8, new Color(.35f, .93f, .79f), 4, true);
		var cross = new Vector2(-direction.Y, direction.X);
		var tip = marker + direction * 7;
		DrawColoredPolygon(new[] { tip, marker - direction * 5 + cross * 4, marker - direction * 5 - cross * 4 }, new Color(.35f, .93f, .79f));
		var cursorN = _overview.GetProfilePosition(state.DistanceMeters, new NVector2(_profileOrigin.X, _profileOrigin.Y), new NVector2(_profileSize.X, _profileSize.Y));
		var cursor = new Vector2(cursorN.X, cursorN.Y);
		DrawLine(new Vector2(cursor.X, _profileOrigin.Y), new Vector2(cursor.X, _profileOrigin.Y + _profileSize.Y), new Color(.98f, .83f, .51f, .75f), 1.2f);
		DrawCircle(cursor, 3.5f, new Color(.35f, .93f, .79f));
		var altitude = _session.CurrentRoute.GetSampleAt(state.DistanceMeters).ElevationMeters ?? 0;
		var grade = state.CurrentGradientPercent;
		DrawString(GetThemeDefaultFont(), new Vector2(12, 124), $"NOW {altitude:0} m · GRADE {grade:+0.0;-0.0;0.0}%", HorizontalAlignment.Left, Size.X - 24, 10, new Color(.94f, .93f, .88f));
	}
}

