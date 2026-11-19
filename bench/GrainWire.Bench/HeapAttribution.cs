using System.Globalization;
using System.Text.RegularExpressions;

namespace GrainWire.Bench;

/// <summary>Which library owns the bytes of a heap dump, by the namespace each type is declared in.</summary>
public static partial class HeapAttribution
{
    public const string MqttNet = "MQTTnet";
    public const string ConnectionsLayer = "ASP.NET Core Connections layer and pipes";
    public const string AspNetCore = "Kestrel, ASP.NET Core, WebSocket, sockets";
    public const string GrainWire = "GrainWire";
    public const string Orleans = "Orleans";
    public const string Other = "Other (strings, arrays, runtime, tasks)";

    /// <summary>The buckets in the order a report lists them.</summary>
    public static readonly string[] Buckets = [MqttNet, ConnectionsLayer, AspNetCore, GrainWire, Orleans, Other];

    // In this order: the Connections layer is part of Microsoft.AspNetCore, so it is tested first.
    private static readonly (string Prefix, string Bucket)[] s_prefixes =
    [
        ("MQTTnet.", MqttNet),
        ("System.IO.Pipelines.", ConnectionsLayer),
        ("Microsoft.AspNetCore.Connections.", ConnectionsLayer),
        ("Microsoft.AspNetCore.Http.Connections.", ConnectionsLayer),
        ("Microsoft.AspNetCore.", AspNetCore),
        ("Microsoft.Extensions.", AspNetCore),
        ("System.Net.", AspNetCore),
        ("GrainWire.", GrainWire),
        ("Orleans.", Orleans),
    ];

    /// <summary>
    /// Decides by the start of the type name only, so a generic collection of GrainWire objects counts as the collection
    /// ("Other") and not as GrainWire.
    /// </summary>
    public static string BucketOf(string typeName)
    {
        foreach ((string prefix, string bucket) in s_prefixes)
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bucket;
            }
        }

        return Other;
    }

    /// <summary>Bytes per bucket, from the text that "dotnet-gcdump report" prints.</summary>
    /// <remarks>
    /// A type line is "average bytes of one object, count, type name, [module]"; the line stands for bytes × count. Every
    /// bucket is present, with 0 if nothing fell into it.
    /// </remarks>
    public static IReadOnlyDictionary<string, long> Parse(string report)
    {
        Dictionary<string, long> buckets = Buckets.ToDictionary(bucket => bucket, _ => 0L);
        foreach (string line in report.Split('\n'))
        {
            Match match = TypeLine().Match(line);
            if (match.Success)
            {
                long bytes = long.Parse(match.Groups["bytes"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                long count = long.Parse(match.Groups["count"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                buckets[BucketOf(match.Groups["type"].Value)] += bytes * count;
            }
        }

        return buckets;
    }

    [GeneratedRegex(@"^\s*(?<bytes>\d[\d,]*)\s+(?<count>\d[\d,]*)\s+(?<type>\S.*?)(?:\s+\[[^\]]*\])?\s*$")]
    private static partial Regex TypeLine();
}
