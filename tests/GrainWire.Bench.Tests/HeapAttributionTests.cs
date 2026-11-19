namespace GrainWire.Bench.Tests;

public class HeapAttributionTests
{
    [Theory]
    [InlineData("MQTTnet.Server.Internal.MqttSession", HeapAttribution.MqttNet)]
    [InlineData("MQTTnet.AspNetCore.MqttConnectionContext", HeapAttribution.MqttNet)]
    [InlineData("System.IO.Pipelines.Pipe", HeapAttribution.ConnectionsLayer)]
    [InlineData("Microsoft.AspNetCore.Http.Connections.Internal.HttpConnectionContext", HeapAttribution.ConnectionsLayer)]
    [InlineData("Microsoft.AspNetCore.Connections.ConnectionItems", HeapAttribution.ConnectionsLayer)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.Http1Connection", HeapAttribution.AspNetCore)]
    [InlineData("System.Net.WebSockets.ManagedWebSocket", HeapAttribution.AspNetCore)]
    [InlineData("System.Net.Sockets.SocketAsyncEventArgs", HeapAttribution.AspNetCore)]
    [InlineData("GrainWire.Server.Internal.GatewayConnection", HeapAttribution.GrainWire)]
    [InlineData("Orleans.Runtime.Messaging.Connection", HeapAttribution.Orleans)]
    [InlineData("System.String", HeapAttribution.Other)]
    [InlineData("System.Byte[] (Bytes > 10K)", HeapAttribution.Other)]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary<System.String,GrainWire.Server.Internal.GatewayConnection>", HeapAttribution.Other)]
    public void A_type_goes_to_the_bucket_of_its_own_namespace(string typeName, string bucket)
    {
        Assert.Equal(bucket, HeapAttribution.BucketOf(typeName));
    }

    [Fact]
    public void A_report_is_summed_per_bucket()
    {
        // The sample is a real "dotnet-gcdump report" of a gateway: its header, its first 40 type lines and a few lines
        // from further down. The first column is the size of one object, so a line counts bytes × count (exact for
        // fixed-size classes, approximate for strings and arrays).
        // Counted by hand: MQTTnet is 192 × 1 + 152 × 100; "Other" is every line whose name starts with no listed namespace.
        IReadOnlyDictionary<string, long> buckets = HeapAttribution.Parse(File.ReadAllText("gcdump-report-sample.txt"));
        Assert.Equal(15_392, buckets[HeapAttribution.MqttNet]);
        Assert.Equal(4_197_008, buckets[HeapAttribution.Other]);
        Assert.Equal(6, buckets.Count);   // every bucket is present, with 0 if nothing fell into it
    }

    [Fact]
    public void The_total_is_the_dumps_own_heap_bytes_not_the_sum_of_the_lines()
    {
        // For strings and arrays the report shows the size of one object, not the average, so bytes × count does not add
        // up to the heap. The header line does.
        Assert.Equal(9_354_919, HeapAttribution.TotalOf(File.ReadAllText("gcdump-report-sample.txt")));
    }

    [Fact]
    public void A_report_without_its_header_has_no_total()
    {
        Assert.Null(HeapAttribution.TotalOf("            288       404  System.IO.Pipelines.Pipe  [System.IO.Pipelines.dll]\n"));
    }

    [Fact]
    public void Lines_that_are_not_types_are_skipped()
    {
        IReadOnlyDictionary<string, long> buckets = HeapAttribution.Parse("     1,603,588  GC Heap bytes\n\n  Object Bytes     Count  Type\n");
        Assert.All(buckets.Values, bytes => Assert.Equal(0, bytes));
    }
}
