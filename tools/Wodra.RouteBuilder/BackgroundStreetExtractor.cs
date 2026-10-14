using System.Globalization;
using OsmSharp;
using OsmSharp.Streams;

namespace Wodra.RouteBuilder;

public sealed record BackgroundStreetPoint(double Latitude, double Longitude, double ElevationMeters);
public sealed record BackgroundStreet(long OsmId, string? Name, string? Ref, string Highway, bool Oneway,
    double WidthMeters, string WidthSource, IReadOnlyList<BackgroundStreetPoint> Points,
    double DistanceFromRouteMeters, double NearestRouteDistanceMeters)
{
    public int OnewayDirection { get; init; } = 1;
}
public sealed record BackgroundStreetExtractionResult(int WaysConsidered, int WaysIncluded,
    IReadOnlyDictionary<string, int> HighwayClassCounts, IReadOnlyDictionary<string, int> WidthSourceCounts,
    int NamedStreetCount, double TotalLengthMeters, double SuppressedOverlapMeters,
    int RuntimeSampleCount, IReadOnlyList<BackgroundStreet> Streets, double GenerationSeconds = 0,
    int ElevationSkippedStreetCount = 0, int ElevationSkippedPointCount = 0);

/// <summary>Extracts real OSM highway ways as visual-only roads, clipping samples over the ride corridor.</summary>
public static class BackgroundStreetExtractor
{
    private static readonly HashSet<string> Included = new(StringComparer.OrdinalIgnoreCase)
        { "motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential", "service", "living_street", "pedestrian", "footway", "path", "cycleway" };
    private static readonly HashSet<string> ExcludedService = new(StringComparer.OrdinalIgnoreCase)
        { "driveway", "parking_aisle", "emergency_access", "alley", "drive-through" };

    public static bool IsEligible(IReadOnlyDictionary<string, string> tags)
    {
        if (!tags.TryGetValue("highway", out var highway) || !Included.Contains(highway)) return false;
        if (tags.GetValueOrDefault("area") == "yes" || tags.GetValueOrDefault("access") is "private" or "no"
            || tags.GetValueOrDefault("vehicle") is "private" or "no" || tags.GetValueOrDefault("motor_vehicle") is "private" or "no") return false;
        return !(highway.Equals("service", StringComparison.OrdinalIgnoreCase)
            && ExcludedService.Contains(tags.GetValueOrDefault("service") ?? ""));
    }
    public static bool IsWithinCorridor(double distanceMeters, double radiusMeters) =>
        double.IsFinite(distanceMeters) && double.IsFinite(radiusMeters) && distanceMeters >= 0 && radiusMeters > 0 && distanceMeters <= radiusMeters;
    public static bool KeepCorridorSegment(double midpointDistanceMeters, double radiusMeters) =>
        IsWithinCorridor(midpointDistanceMeters, radiusMeters);
    public static bool SuppressRouteOverlap(double distanceMeters, double overlapMeters) =>
        double.IsFinite(distanceMeters) && double.IsFinite(overlapMeters) && distanceMeters >= 0 && overlapMeters >= 0 && distanceMeters <= overlapMeters;
    public static bool DirectionsFollowSameRoad((double X,double Y) street,(double X,double Y) route)
    {
        var a=Math.Sqrt(street.X*street.X+street.Y*street.Y);var b=Math.Sqrt(route.X*route.X+route.Y*route.Y);
        return a>1e-9&&b>1e-9&&Math.Abs((street.X*route.X+street.Y*route.Y)/(a*b))>=.9;
    }

    public static (double WidthMeters, string Source) ParseWidth(IReadOnlyDictionary<string, string> tags, string highway)
    {
        if (tags.TryGetValue("width", out var widthText))
        {
            var text = widthText.Trim().ToLowerInvariant();
            var feet = text.EndsWith("ft") || text.EndsWith('\'');
            text = text.Replace("meters", "").Replace("meter", "").Replace("metres", "").Replace("metre", "")
                .Replace("m", "").Replace("feet", "").Replace("foot", "").Replace("ft", "").Replace("'", "").Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed is >= 2 and <= 40)
                return (feet ? parsed * .3048 : parsed, "width");
        }
        if (tags.TryGetValue("lanes", out var lanesText)
            && double.TryParse(lanesText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lanes)
            && double.IsFinite(lanes) && lanes is >= 1 and <= 8) return (lanes * 3.2, "lanes");
        var fallback = highway.ToLowerInvariant() switch
        {
            "motorway" or "trunk" or "primary" => 9,
            "secondary" or "tertiary" => 7.5,
            "service" => 4.5,
            "pedestrian" => 3,
            "cycleway" => 2.5,
            "footway" or "path" => 2,
            "living_street" or "residential" or "unclassified" => 6,
            _ => 6
        };
        return (fallback, "class");
    }

    public static BackgroundStreetExtractionResult Extract(string pbfPath, IReadOnlyList<RoutePointOutput> route,
        BackgroundStreetBuildOptions options)
    {
        if (route.Count < 2) throw new ArgumentException("A route needs at least two points.", nameof(route));
        options.Validate();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var lat0 = route.Average(p => p.Latitude); var lon0 = route.Average(p => p.Longitude);
        var minLat=route.Min(p=>p.Latitude);var maxLat=route.Max(p=>p.Latitude);var minLon=route.Min(p=>p.Longitude);var maxLon=route.Max(p=>p.Longitude);
        var latMargin = (options.SearchRadiusMeters + 100) / 111_320d + .0001;
        var lonMargin = latMargin / Math.Max(.01, Math.Cos(lat0 * Math.PI / 180));
        var coordinates = new Dictionary<long, GeoPoint>();
        var candidates = new List<(long Id, long[] Nodes, Dictionary<string,string> Tags)>();
        using (var input = File.OpenRead(pbfPath))
        foreach (var element in new PBFOsmStreamSource(input))
        {
            if (element is Node node && node.Id is long id && node.Latitude is double lat && node.Longitude is double lon)
            {
                if (lat >= minLat-latMargin && lat <= maxLat+latMargin && lon >= minLon-lonMargin && lon <= maxLon+lonMargin)
                    coordinates[id] = new GeoPoint(lat, lon);
            }
            else if (element is Way way && way.Id is long wayId && way.Nodes is { Length: >= 2 } nodes && way.Tags is not null)
            {
                var tags = OsmPbfGeometry.ToTags(way.Tags);
                if (IsEligible(tags) && nodes.Any(coordinates.ContainsKey)) candidates.Add((wayId, nodes, tags));
            }
        }
        var routeXY = route.Select(p => XY(p.Latitude, p.Longitude, lat0, lon0)).ToArray();
        var selected = new List<BackgroundStreet>(); var classCounts = new SortedDictionary<string,int>(StringComparer.Ordinal);
        var widthCounts = new SortedDictionary<string,int>(StringComparer.Ordinal); var includedWays=new HashSet<long>();var namedWays=new HashSet<long>();
        var waysConsidered=0; double suppressed = 0;
        foreach (var candidate in candidates.OrderBy(c => c.Id))
        {
            if (candidate.Nodes.Any(id => !coordinates.ContainsKey(id))) continue;
            var raw = candidate.Nodes.Select(id => coordinates[id]).ToArray();
            if (PolylineLength(raw,lat0,lon0)<25
                && string.IsNullOrWhiteSpace(candidate.Tags.GetValueOrDefault("name"))
                && string.IsNullOrWhiteSpace(candidate.Tags.GetValueOrDefault("ref"))) continue;
            var distance = PolylineDistance(raw, routeXY, lat0, lon0);
            if (!IsWithinCorridor(distance.Distance,options.SearchRadiusMeters)) continue;
            waysConsidered++;
            var densified = Densify(raw, options.SampleSpacingMeters, lat0, lon0);
            var parts = new List<List<GeoPoint>>(); var current = new List<GeoPoint>();
            for (var i=0;i<densified.Count-1;i++)
            {
                var a=densified[i]; var b=densified[i+1]; var midpoint=new GeoPoint((a.Latitude+b.Latitude)/2,(a.Longitude+b.Longitude)/2);
                var near = Nearest(XY(midpoint.Latitude, midpoint.Longitude, lat0, lon0), routeXY);
                var segmentLength = Distance(a,b,lat0,lon0);
                // A way is selected by its closest point to the route, but its full
                // OSM geometry can extend kilometres beyond the scenery radius.
                // Keep only densified pieces in the configured corridor so scenery
                // elevation sampling remains within the requested coverage area.
                if (!KeepCorridorSegment(near.Distance, options.SearchRadiusMeters))
                { if (current.Count >= 2) parts.Add(current); current=[]; continue; }
                // Only remove mapped road that actually follows the selected ride road.
                // The old distance-only cutoff also erased the middle of crossing and
                // connecting streets, leaving gaps at exactly the intersections we need.
                var segmentDirection = XY(b.Latitude,b.Longitude,lat0,lon0);
                var previousDirection = XY(a.Latitude,a.Longitude,lat0,lon0);
                if (SuppressRouteOverlap(near.Distance,options.RouteOverlapSuppressionMeters)
                    && IsRouteAligned(previousDirection, segmentDirection, routeXY))
                { suppressed += segmentLength; if (current.Count >= 2) parts.Add(current); current=[]; continue; }
                if (current.Count == 0) current.Add(a);
                else if (current[^1] != a) current.Add(a);
                current.Add(b);
            }
            if (current.Count>=2) parts.Add(current);
            var width=ParseWidth(candidate.Tags,candidate.Tags["highway"]);
            foreach (var part in parts)
            {
                var points=part.Select(p=>new BackgroundStreetPoint(p.Latitude,p.Longitude,0)).ToArray();
                selected.Add(new BackgroundStreet(candidate.Id,candidate.Tags.GetValueOrDefault("name"),candidate.Tags.GetValueOrDefault("ref"),
                    candidate.Tags["highway"], IsOneway(candidate.Tags), width.WidthMeters,width.Source, points,distance.Distance,
                    distance.Chainage) { OnewayDirection = OnewayDirection(candidate.Tags) });
            }
            if (parts.Count>0) { includedWays.Add(candidate.Id);if(!string.IsNullOrWhiteSpace(candidate.Tags.GetValueOrDefault("name")))namedWays.Add(candidate.Id);
                classCounts[candidate.Tags["highway"]]=classCounts.GetValueOrDefault(candidate.Tags["highway"])+1;
                widthCounts[width.Source]=widthCounts.GetValueOrDefault(width.Source)+1; }
        }
        timer.Stop();
        var length=selected.Sum(s=>PolylineLength(s.Points.Select(p=>new GeoPoint(p.Latitude,p.Longitude)).ToArray(),lat0,lon0));
        return new BackgroundStreetExtractionResult(waysConsidered,includedWays.Count,classCounts,widthCounts,
            namedWays.Count,length,suppressed,selected.Sum(s=>s.Points.Count),selected,timer.Elapsed.TotalSeconds);
    }

    public static bool IsOneway(IReadOnlyDictionary<string,string> tags) => tags.GetValueOrDefault("oneway") is "yes" or "1" or "true" or "-1";
    public static int OnewayDirection(IReadOnlyDictionary<string, string> tags) =>
        tags.GetValueOrDefault("oneway") == "-1" ? -1 : 1;
    public static HashSet<long> FindReverseOnewayWays(string pbfPath, IReadOnlySet<long> wantedWayIds)
    {
        var result = new HashSet<long>();
        using var input = File.OpenRead(pbfPath);
        foreach (var element in new PBFOsmStreamSource(input))
        {
            if (element is Way way && way.Id is long id && wantedWayIds.Contains(id) && way.Tags is not null
                && OnewayDirection(OsmPbfGeometry.ToTags(way.Tags)) < 0)
                result.Add(id);
        }
        return result;
    }
    private static bool IsRouteAligned((double X,double Y) a, (double X,double Y) b,
        (double X,double Y)[] route)
    {
        var dx=b.X-a.X;var dy=b.Y-a.Y;var length=Math.Sqrt(dx*dx+dy*dy);
        if(length<1e-6)return false;
        var midpoint=((a.X+b.X)*.5,(a.Y+b.Y)*.5);
        var (_,chainage)=Nearest(midpoint,route);
        var routeLength=0d;for(var i=0;i<route.Length-1;i++)routeLength+=Math.Sqrt(Math.Pow(route[i+1].X-route[i].X,2)+Math.Pow(route[i+1].Y-route[i].Y,2));
        var before=PointAtChainage(route,Math.Max(0,chainage-5));var after=PointAtChainage(route,Math.Min(routeLength,chainage+5));
        var rx=after.X-before.X;var ry=after.Y-before.Y;var routeMagnitude=Math.Sqrt(rx*rx+ry*ry);
        return routeMagnitude>1e-6&&DirectionsFollowSameRoad((dx,dy),(rx,ry));
    }
    private static (double X,double Y) PointAtChainage((double X,double Y)[] line,double distance)
    {
        for(var i=0;i<line.Length-1;i++){var a=line[i];var b=line[i+1];var dx=b.X-a.X;var dy=b.Y-a.Y;var length=Math.Sqrt(dx*dx+dy*dy);if(distance<=length||i==line.Length-2){var t=length<1e-9?0:Math.Clamp(distance/length,0,1);return(a.X+dx*t,a.Y+dy*t);}distance-=length;}return line[^1];
    }
    public static IReadOnlyList<GeoPoint> Densify(IReadOnlyList<GeoPoint> points, double spacingMeters, double lat0, double lon0)
    {
        var result=new List<GeoPoint>(); if(points.Count==0)return result; result.Add(points[0]);
        for(var i=0;i<points.Count-1;i++) { var a=XY(points[i].Latitude,points[i].Longitude,lat0,lon0); var b=XY(points[i+1].Latitude,points[i+1].Longitude,lat0,lon0);
            var n=Math.Max(1,(int)Math.Ceiling(Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y))/spacingMeters)); for(var j=1;j<=n;j++){var t=(double)j/n; result.Add(new GeoPoint(points[i].Latitude+(points[i+1].Latitude-points[i].Latitude)*t,points[i].Longitude+(points[i+1].Longitude-points[i].Longitude)*t));}}
        return result;
    }
    private static (double Distance,double Chainage) PolylineDistance(IReadOnlyList<GeoPoint> line, (double X,double Y)[] route,double lat0,double lon0)
    { var best=(Distance:double.PositiveInfinity,Chainage:0d); double chain=0; for(int i=0;i<line.Count-1;i++){var a=XY(line[i].Latitude,line[i].Longitude,lat0,lon0);var b=XY(line[i+1].Latitude,line[i+1].Longitude,lat0,lon0);var da=Nearest(a,route); var db=Nearest(b,route); if(da.Distance<best.Distance)best=(da.Distance,da.Chainage);if(db.Distance<best.Distance)best=(db.Distance,db.Chainage); chain+=Distance(line[i],line[i+1],lat0,lon0);}return best; }
    private static (double Distance,double Chainage) Nearest((double X,double Y) p,(double X,double Y)[] line)
    { var best=(Distance:double.PositiveInfinity,Chainage:0d);double chain=0;for(int i=0;i<line.Length-1;i++){var a=line[i];var b=line[i+1];var dx=b.X-a.X;var dy=b.Y-a.Y;var l2=dx*dx+dy*dy;var t=l2<1e-9?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/l2,0,1);var px=p.X-(a.X+t*dx);var py=p.Y-(a.Y+t*dy);var d=Math.Sqrt(px*px+py*py);var len=Math.Sqrt(l2);if(d<best.Distance)best=(d,chain+t*len);chain+=len;}return best; }
    private static (double X,double Y) XY(double lat,double lon,double lat0,double lon0){const double r=6371000,rad=Math.PI/180;return((lon-lon0)*rad*r*Math.Cos(lat0*rad),-(lat-lat0)*rad*r);}
    private static double Distance(GeoPoint a,GeoPoint b,double lat0,double lon0){var x=XY(a.Latitude,a.Longitude,lat0,lon0);var y=XY(b.Latitude,b.Longitude,lat0,lon0);var dx=x.X-y.X;var dy=x.Y-y.Y;return Math.Sqrt(dx*dx+dy*dy);}
    private static double PolylineLength(IReadOnlyList<GeoPoint> p,double lat0,double lon0){double sum=0;for(int i=1;i<p.Count;i++)sum+=Distance(p[i-1],p[i],lat0,lon0);return sum;}
}
