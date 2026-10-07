using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using NVector3 = System.Numerics.Vector3;

/// <summary>Static visual-only OSM road ribbons, batched by 400 m ride-route chunk.</summary>
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
    public long BuildMilliseconds { get; private set; }

    public void Configure(VisualRoute route,RouteBackgroundStreetData data)
    {
        var timer=Stopwatch.StartNew();_routeLength=route.LengthMeters;LoadedStreetCount=data.Streets.Count;
        var bucket=new Dictionary<int,List<RouteBackgroundStreet>>();
        foreach(var street in data.Streets){var index=Math.Clamp((int)(street.NearestRouteDistanceMeters/ChunkMeters),0,Math.Max(0,(int)Math.Ceiling(_routeLength/ChunkMeters)-1));if(!bucket.TryGetValue(index,out var list))bucket[index]=list=[];list.Add(street);}
        foreach(var (index,streets) in bucket.OrderBy(p=>p.Key))
        {
            var nodes=new List<MeshInstance3D>();
            foreach(var highwayGroup in streets.GroupBy(s=>ClassMaterial(s.Highway)))
            {
                var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);var emitted=0;
                foreach(var street in highwayGroup)
                {
                    var positions=BackgroundStreetGeometry.ConvertPoints(route,street.Points);
                    var quads=BackgroundStreetGeometry.BuildRibbon(positions,(float)street.WidthMeters);
                    var offset=new NVector3(0,.004f+(street.OsmId%7)*.0004f,0);
                    foreach(var q in quads)
                    {
                        var a=ProceduralRoad.ToGodot(q.StartLeft+offset);var b=ProceduralRoad.ToGodot(q.EndLeft+offset);var c=ProceduralRoad.ToGodot(q.StartRight+offset);var d=ProceduralRoad.ToGodot(q.EndRight+offset);
                        if((b-a).Cross(c-a).LengthSquared()<1e-8f||(c-b).Cross(d-b).LengthSquared()<1e-8f)continue;
                        st.AddVertex(a);st.AddVertex(c);st.AddVertex(b);st.AddVertex(c);st.AddVertex(d);st.AddVertex(b);emitted++;
                    }
                }
                if(emitted==0)continue;st.GenerateNormals();var material=new StandardMaterial3D{AlbedoColor=highwayGroup.Key,Roughness=1};var mesh=st.Commit();
                TriangleCount+=emitted*2;var instance=new MeshInstance3D{Name="BackgroundStreetMesh",Mesh=mesh,MaterialOverride=material};AddChild(instance);nodes.Add(instance);
            }
            _chunks.Add(new Chunk(Math.Min(_routeLength-.01,index*ChunkMeters+ChunkMeters/2),nodes));
        }
        timer.Stop();BuildMilliseconds=timer.ElapsedMilliseconds;
    }
    public void UpdateForRideDistance(double distance)
    {
        if(_routeLength<=0)return;var wrapped=((distance%_routeLength)+_routeLength)%_routeLength;
        VisibleChunkCount=0;foreach(var chunk in _chunks){var delta=Math.Abs(wrapped-chunk.Center);delta=Math.Min(delta,_routeLength-delta);var visible=delta<=VisibleRadiusMeters;if(visible)VisibleChunkCount++;foreach(var mesh in chunk.Meshes)mesh.Visible=visible;}
    }
    private static Color ClassMaterial(string highway)=>highway switch
    {"motorway"=>new Color(.20f,.21f,.23f),"trunk"=>new Color(.24f,.25f,.27f),"primary"=>new Color(.28f,.29f,.31f),"secondary"=>new Color(.31f,.32f,.34f),_=>new Color(.35f,.36f,.38f)};
}
