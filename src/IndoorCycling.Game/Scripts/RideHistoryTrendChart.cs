using Godot;
using IndoorCycling.Core.Persistence;
using System.Collections.Generic;
using System.Linq;
using System;

public partial class RideHistoryTrendChart : Control
{
    private IReadOnlyList<RideHistoryBucket> _buckets = [];
    private string _rangeLabel = "";
    private static readonly Color[] Colors = [new("#59c6c5"), new("#f1c75b"), new("#a58be8")];

    public void SetBuckets(IReadOnlyList<RideHistoryBucket> buckets, string rangeLabel)
    {
        _buckets = buckets; _rangeLabel = rangeLabel; QueueRedraw();
    }

    public override void _Draw()
    {
        var font = GetThemeDefaultFont(); if (font is null) return;
        var size = GetThemeDefaultFontSize(); var width = Size.X; var left = 92f; var right = width - 6;
        DrawString(font, new Vector2(2, size), _rangeLabel, HorizontalAlignment.Left, -1, Math.Max(10, size - 4), new Color("#aeb7c1"));
        if (_buckets.Count == 0) { DrawString(font, new Vector2(left, 38), "No rides in this range", HorizontalAlignment.Left, -1, Math.Max(10, size - 4), new Color("#aeb7c1")); return; }
        var rows = new[] { _buckets.Select(b => b.DistanceMeters / 1000).ToArray(), _buckets.Select(b => b.DurationSeconds / 3600).ToArray(), _buckets.Select(b => b.ElevationGainMeters).ToArray() };
        var labels = new[] { "Distance km", "Active hours", "Climb m" };
        var rowHeight = 17f; var count = _buckets.Count; var slot = Mathf.Max(2, (right - left) / count); var barWidth = Mathf.Max(2, slot * .58f);
        for (var row = 0; row < rows.Length; row++)
        {
            var y = 19 + row * rowHeight; DrawString(font, new Vector2(2, y + 9), labels[row], HorizontalAlignment.Left, -1, Math.Max(10, size - 5), new Color("#d2d8df"));
            var max = rows[row].DefaultIfEmpty(0).Max();
            for (var i = 0; i < count; i++)
            {
                var h = max <= 0 ? 1 : Mathf.Max(2, (float)(rows[row][i] / max * 9));
                DrawRect(new Rect2(left + i * slot + (slot - barWidth) / 2, y + 10 - h, barWidth, h), Colors[row]);
            }
        }
        var first = _buckets[0].StartUtc.ToLocalTime(); var last = _buckets[^1].StartUtc.ToLocalTime();
        DrawString(font, new Vector2(left, Size.Y - 1), first.ToString(_rangeLabel.StartsWith("6 months") ? "MMM yy" : "d MMM"), HorizontalAlignment.Left, -1, Math.Max(9, size - 6), new Color("#89939e"));
        DrawString(font, new Vector2(right, Size.Y - 1), last.ToString(_rangeLabel.StartsWith("6 months") ? "MMM yy" : "d MMM"), HorizontalAlignment.Right, -1, Math.Max(9, size - 6), new Color("#89939e"));
    }
}
