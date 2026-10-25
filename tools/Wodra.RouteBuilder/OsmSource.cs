using OsmSharp;
using OsmSharp.Streams;

namespace Wodra.RouteBuilder;

/// <summary>Opens local OSM PBF extracts and small XML extracts with the same streaming interface.</summary>
internal static class OsmSource
{
    public static IEnumerable<OsmGeo> Open(Stream stream, string sourcePath) =>
        Path.GetExtension(sourcePath).Equals(".osm", StringComparison.OrdinalIgnoreCase)
            ? new XmlOsmStreamSource(stream)
            : new PBFOsmStreamSource(stream);
}
