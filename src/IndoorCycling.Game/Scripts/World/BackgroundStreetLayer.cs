using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using NVector3 = System.Numerics.Vector3;

/// <summary>Mapped OSM streets, batched by the route chunk nearest each road segment.</summary>
public partial class BackgroundStreetLayer : Node3D
{
    private const double ChunkMeters=400, VisibleRadiusMeters=1000;
    private sealed record Chunk(double Center,IReadOnlyList<MeshInstance3D> Meshes);
    private readonly List<Chunk> _chunks=[];
    private double _routeLength;
    public int LoadedStreetCount { get; private set; }
    public int VisibleChunkCount { get; private set; }
    public int MeshChunkCount=>_chunks.Sum(c=>c.Meshes.Count);
    public int TriangleCount { get; private set; }
    public int RouteConnectionsRestored { get; private set; }
    public long BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute route,RouteBackgroundStreetData data)
    {
        var timer=Stopwatch.StartNew();_routeLength=route.LengthMeters;LoadedStreetCount=data.Streets.Count;
        var surfaces=new Dictionary<(int Chunk,Color Color),SurfaceTool>();
        var triangleCounts=new Dictionary<(int Chunk,Color Color),int>();
        SurfaceTool SurfaceFor(int index,Color color)
        {
            var key=(index,color);
            if(surfaces.TryGetValue(key,out var existing))return existing;
            var created=new SurfaceTool();created.Begin(Mesh.PrimitiveType.Triangles);surfaces.Add(key,created);triangleCounts[key]=0;return created;
        }
        int ChunkAt(double distance)=>BackgroundStreetGeometry.RouteChunkIndex(distance,_routeLength,ChunkMeters);

        foreach(var street in data.Streets)
        {
            var original=BackgroundStreetGeometry.ConvertPoints(route,street.Points);
            var connected=BackgroundStreetGeometry.ConnectRouteEnds(original,route.Samples);
            var quads=BackgroundStreetGeometry.BuildRibbon(connected.Points,(float)street.WidthMeters);
            var offset=new NVector3(0,.004f+(street.OsmId%7)*.0004f,0);
            var color=ClassMaterial(street.Highway);
            foreach(var q in quads)
            {
                // A short OSM way can duplicate part of the selected cycling route even
                // when its bundle-level overlap filter misses the local alignment. Do not
                // let its differently classified asphalt or end cap cover route markings.
                if(BackgroundStreetGeometry.OverlapsRoute(q,route.Samples,(float)street.WidthMeters))continue;
                var center=(q.StartLeft+q.EndLeft+q.StartRight+q.EndRight)*.25f;
                var chunk=ChunkAt(BackgroundStreetGeometry.NearestRouteDistanceMeters(center,route.Samples));
                var a=ProceduralRoad.ToGodot(q.StartLeft+offset);var b=ProceduralRoad.ToGodot(q.EndLeft+offset);
                var c=ProceduralRoad.ToGodot(q.StartRight+offset);var d=ProceduralRoad.ToGodot(q.EndRight+offset);
                if((b-a).Cross(c-a).LengthSquared()<1e-8f||(c-b).Cross(d-b).LengthSquared()<1e-8f)continue;
                var surface=SurfaceFor(chunk,color);
                surface.AddVertex(a);surface.AddVertex(c);surface.AddVertex(b);surface.AddVertex(c);surface.AddVertex(d);surface.AddVertex(b);
                triangleCounts[(chunk,color)]+=2;
            }
            foreach(var connection in connected.Connections)
            {
                RouteConnectionsRestored++;
                var chunk=ChunkAt(connection.RouteDistanceMeters);
                // The route ribbon already supplies the main-road surface. Use its exact
                // asphalt tone for the small fan so the connection cannot read as a patch.
                var surface=SurfaceFor(chunk,WodraWorldStyle.Asphalt);
                var center=ProceduralRoad.ToGodot(connection.Position+new NVector3(0,.001f,0));
                var radius=(float)street.WidthMeters*.5f+.2f;const int sides=16;
                for(var side=0;side<sides;side++)
                {
                    var a=side*Mathf.Tau/sides;var b=(side+1)*Mathf.Tau/sides;
                    var pa=center+new Vector3(Mathf.Cos(a)*radius,0,Mathf.Sin(a)*radius);
                    var pb=center+new Vector3(Mathf.Cos(b)*radius,0,Mathf.Sin(b)*radius);
                    surface.AddVertex(center);surface.AddVertex(pb);surface.AddVertex(pa);
                }
                triangleCounts[(chunk,color)]+=sides;
            }
        }

        foreach(var group in surfaces.GroupBy(pair=>pair.Key.Chunk).OrderBy(group=>group.Key))
        {
            var nodes=new List<MeshInstance3D>();
            foreach(var pair in group.OrderBy(pair=>pair.Key.Color.R).ThenBy(pair=>pair.Key.Color.G).ThenBy(pair=>pair.Key.Color.B))
            {
                pair.Value.GenerateNormals();
                var instance=new MeshInstance3D{Name="BackgroundStreetMesh",Mesh=pair.Value.Commit(),
                    MaterialOverride=new StandardMaterial3D{AlbedoColor=pair.Key.Color,Roughness=1}};
                AddChild(instance);nodes.Add(instance);TriangleCount+=triangleCounts[pair.Key];
            }
            var center=Math.Min(_routeLength-.01,group.Key*ChunkMeters+ChunkMeters/2);
            _chunks.Add(new Chunk(center,nodes));
        }
        timer.Stop();BuildMilliseconds=timer.ElapsedMilliseconds;
    }

    public void UpdateForRideDistance(double distance)
    {
        if(_routeLength<=0)return;
        var wrapped=((distance%_routeLength)+_routeLength)%_routeLength;VisibleChunkCount=0;
        foreach(var chunk in _chunks)
        {
            var delta=Math.Abs(wrapped-chunk.Center);delta=Math.Min(delta,_routeLength-delta);
            var visible=delta<=VisibleRadiusMeters;if(visible)VisibleChunkCount++;
            foreach(var mesh in chunk.Meshes)mesh.Visible=visible;
        }
    }

    private static Color ClassMaterial(string highway)=>highway switch
    {
        "motorway"=>new Color(.20f,.21f,.23f),"trunk"=>new Color(.24f,.25f,.27f),"primary"=>new Color(.28f,.29f,.31f),"secondary"=>new Color(.31f,.32f,.34f),
        "footway" or "path"=>new Color(.46f,.47f,.43f),"pedestrian"=>new Color(.43f,.44f,.41f),"cycleway"=>new Color(.33f,.35f,.34f),_=>new Color(.35f,.36f,.38f)
    };
}
