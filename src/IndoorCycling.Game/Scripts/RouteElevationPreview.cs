using Godot;
using System;
using System.Linq;

public partial class RouteElevationPreview : Control
{
	public RouteDefinition? Route { get; set; }
	public bool ShowRoute { get; set; }
	public override void _Draw()
	{
		var size = Size;
		DrawRect(new Rect2(Vector2.Zero, size), new Color(.06f,.085f,.09f,.88f));
		if (Route?.Points is not { Count: > 1 } points) { DrawString(ThemeDB.FallbackFont, new Vector2(10, 22), "Route preview unavailable", HorizontalAlignment.Left, -1, 14, Colors.LightGray); return; }
		var values = ShowRoute ? points.Select(p => (X: p.Longitude, Y: p.Latitude)).ToArray() : points.Select(p => (X: p.DistanceMeters, Y: p.ElevationMeters)).ToArray();
		var minX = values.Min(p => p.X); var maxX = values.Max(p => p.X); var minY = values.Min(p => p.Y); var maxY = values.Max(p => p.Y);
		var pad = 12f; var spanX = Math.Max(1e-9, maxX-minX); var spanY = Math.Max(1e-9, maxY-minY);
		var line = new Vector2[values.Length];
		for (var i=0;i<values.Length;i++) line[i] = new Vector2(pad+(float)((values[i].X-minX)/spanX)*(size.X-2*pad), size.Y-pad-(float)((values[i].Y-minY)/spanY)*(size.Y-2*pad));
		DrawPolyline(line, ShowRoute ? new Color(.95f,.72f,.32f) : new Color(.53f,.78f,.49f), ShowRoute ? 2.5f : 2f, true);
		DrawString(ThemeDB.FallbackFont, new Vector2(10, 18), ShowRoute ? "Route overview" : "Elevation profile", HorizontalAlignment.Left, -1, 13, new Color(.86f,.86f,.8f));
	}
}
