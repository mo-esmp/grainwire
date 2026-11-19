using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GrainWire;
using GrainWire.Bench;
using GrainWire.Server;

// GrainWire benchmark. See docs/benchmarks.md.
//
//   dotnet run -c Release --project bench/GrainWire.Bench -- run [--rooms 5000] [--members 1000] [--seconds 30] [--out docs/benchmarks-latest.md]
//
// "run" starts three processes of this same program (one Orleans silo and two standalone gateways), drives them
// with real MQTT clients over WebSockets, and prints the results. "silo" and "gateway" are those processes.
//
//   dotnet run -c Release --project bench/GrainWire.Bench -- idle [--connections 3000] [--factor 4] [--rooms-per-connection 1] [--out <file>]
//
// "idle" measures what an idle connection costs the gateways, as the slope between two connection counts, each in
// fresh processes. See docs/specs/2026-11-19-grainwire-on-mqttwire-design.md, section 6.

string mode = args.Length > 0 ? args[0] : "run";
int Arg(string name, int fallback) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
string? Text(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
bool Flag(string name) => Array.IndexOf(args, name) >= 0;

return mode switch
{
    "silo" => await RunSilo(Arg("--http", 0), Arg("--silo-port", 0), Arg("--gateway-port", 0)),
    "gateway" => await RunGateway(Arg("--http", 0), Arg("--gateway-port", 0), Flag("--claims")),
    "run" => await RunBenchmark(Arg("--rooms", 5000), Arg("--members", 1000), Arg("--seconds", 30), Text("--out")),
    "idle" => await RunIdle(Arg("--connections", 3000), Arg("--factor", 4), Arg("--rooms-per-connection", 1), Text("--out")),
    _ => Fail($"Unknown mode '{mode}'. Use: run | idle | silo | gateway."),
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// ---------------------------------------------------------------- the server processes

static WebApplicationBuilder NewBuilder(int http)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
    builder.Logging.SetMinimumLevel(LogLevel.Error);
    builder.WebHost.UseUrls($"http://127.0.0.1:{http}");
    return builder;
}

static void MapStats(WebApplication app)
{
    app.MapGet("/health", () => "ok");
    app.MapGet("/stats", () =>
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return new { heapBytes = GC.GetTotalMemory(forceFullCollection: true), workingSetBytes = Environment.WorkingSet };
    });
}

static async Task<int> RunSilo(int http, int siloPort, int gatewayPort)
{
    WebApplicationBuilder builder = NewBuilder(http);
    builder.UseOrleans(silo =>
    {
        silo.UseLocalhostClustering(siloPort, gatewayPort, null, "gw-bench", "gw-bench");
        silo.AddMemoryGrainStorage("bench");
        silo.AddGrainWire(gw => gw.ConfigureRoom("bench", room => room.StorageProvider = "bench"));
    });
    WebApplication app = builder.Build();
    MapStats(app);
    await app.RunAsync();
    return 0;
}

static async Task<int> RunGateway(int http, int orleansGatewayPort, bool withClaims)
{
    WebApplicationBuilder builder = NewBuilder(http);
    builder.UseOrleansClient(client =>
    {
        client.UseLocalhostClustering(orleansGatewayPort, "gw-bench", "gw-bench");
        client.AddGrainWireClient();
    });
    if (withClaims)
    {
        // Before AddGrainWireGateway, which only adds the development authenticator if none is registered.
        builder.Services.AddSingleton<IConnectionAuthenticator, BenchAuthenticator>();
    }

    builder.Services.AddGrainWireGateway(options => options.RoomCreationsPerMinute = int.MaxValue);
    WebApplication app = builder.Build();
    app.MapGrainWire();
    MapStats(app);
    await app.RunAsync();
    return 0;
}

// ---------------------------------------------------------------- the benchmark

static async Task<int> RunBenchmark(int rooms, int members, int seconds, string? outFile)
{
    int siloHttp = FreePort();
    int orleansGateway = FreePort();
    int[] gatewayHttp = [FreePort(), FreePort()];
    List<Process> processes =
    [
        Spawn($"silo --http {siloHttp} --silo-port {FreePort()} --gateway-port {orleansGateway}"),
    ];
    StringBuilder report = new();
    void Line(string text = "")
    {
        Console.WriteLine(text);
        report.AppendLine(text);
    }

    try
    {
        await WaitForHealth(siloHttp);
        processes.Add(Spawn($"gateway --http {gatewayHttp[0]} --gateway-port {orleansGateway}"));
        processes.Add(Spawn($"gateway --http {gatewayHttp[1]} --gateway-port {orleansGateway}"));
        await Task.WhenAll(WaitForHealth(gatewayHttp[0]), WaitForHealth(gatewayHttp[1]));
        string[] urls = [.. gatewayHttp.Select(p => $"ws://127.0.0.1:{p}/mqtt")];
        int[] stats = [siloHttp, gatewayHttp[0], gatewayHttp[1]];

        Line($"Machine: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB RAM, .NET {Environment.Version}");
        Line("Topology: 1 Orleans silo + 2 standalone gateways + the load generator, all on this machine. Rooms are persistent (Orleans in-memory storage).");
        Line();

        long[] baseline = await Heaps(stats);
        await ManySmallRooms(rooms, seconds, urls, stats, baseline, Line);
        await Task.Delay(3000);
        baseline = await Heaps(stats);
        await BigRoom(members, seconds, urls, stats, baseline, processes[2], Line);
    }
    finally
    {
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already gone.
            }
        }
    }

    if (outFile is not null)
    {
        await File.WriteAllTextAsync(outFile, report.ToString());
    }

    return 0;
}

/// <summary>Workload 2 of the design: many rooms of two players, one command every 5 seconds per room.</summary>
static async Task ManySmallRooms(int rooms, int seconds, string[] urls, int[] stats, long[] baseline, Action<string> line)
{
    line($"## Many small rooms: {rooms:N0} rooms x 2 players, 1 command per room every 5 s, for {seconds} s");
    Stats delivery = new();
    BenchClient[] clients = new BenchClient[rooms * 2];
    string run = Guid.NewGuid().ToString("N")[..8];
    Stopwatch setup = Stopwatch.StartNew();
    await Parallel.ForAsync(0, rooms, new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (i, _) =>
    {
        // The two players of a room sit on different gateways.
        BenchClient first = clients[i * 2] = new BenchClient($"s{run}-{i}-a", delivery);
        BenchClient second = clients[(i * 2) + 1] = new BenchClient($"s{run}-{i}-b", delivery);
        await first.ConnectAsync(urls[0]);
        await second.ConnectAsync(urls[1]);
        await first.JoinAsync($"bench/s{run}-{i}", create: true, snapshotBytes: 256);
        await second.JoinAsync($"bench/s{run}-{i}", create: false, snapshotBytes: 0);
    });
    line($"- Setup: {rooms * 2:N0} connections and {rooms:N0} rooms in {setup.Elapsed.TotalSeconds:F1} s");

    long[] after = await Heaps(stats);
    line($"- Memory: silo {Kb(after[0] - baseline[0], rooms)} KB per room; gateways {Kb(after[1] + after[2] - baseline[1] - baseline[2], rooms * 2)} KB per connection (managed heap after a full GC)");

    Stats requests = new();
    int failed = 0;
    string pad = new('p', 64);
    Stopwatch stop = Stopwatch.StartNew();
    List<Task> tasks = [];
    for (int i = 0; i < rooms; i++)
    {
        int room = i;
        tasks.Add(Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(5000)); // spread the rooms over the 5-second period
            int turn = 0;
            while (stop.Elapsed.TotalSeconds < seconds)
            {
                long started = Stopwatch.GetTimestamp();
                bool ok = await clients[(room * 2) + (turn++ % 2)].SayAsync($"bench/s{run}-{room}", pad);
                if (ok)
                {
                    requests.Record(Stopwatch.GetElapsedTime(started));
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                await Task.Delay(5000);
            }
        }));
    }

    await Task.WhenAll(tasks);
    await Task.Delay(1000);
    (double P50, double P95, double P99, double Max) r = requests.Percentiles();
    (double P50, double P95, double P99, double Max) d = delivery.Percentiles();
    line($"- Commands: {requests.Count:N0} completed ({requests.Count / (double)seconds:F0}/s), {failed} failed");
    line($"- Request to reply: p50 {r.P50:F1} ms, p95 {r.P95:F1} ms, p99 {r.P99:F1} ms, max {r.Max:F0} ms");
    line($"- Command to event at both players: p50 {d.P50:F1} ms, p95 {d.P95:F1} ms, p99 {d.P99:F1} ms, max {d.Max:F0} ms ({delivery.Count:N0} deliveries)");
    line("");

    await Parallel.ForEachAsync(clients, new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (client, _) => await client.DisposeAsync());
}

/// <summary>Workload 1 of the design: one room of 1,000 members on two gateways, 5 events/s, 512-byte events, 50 KB snapshots; then a gateway is killed.</summary>
static async Task BigRoom(int members, int seconds, string[] urls, int[] stats, long[] baseline, Process secondGateway, Action<string> line)
{
    line($"## Big room: {members:N0} members on 2 gateways, 5 events/s, 512-byte events, 50 KB snapshots, for {seconds} s");
    Stats delivery = new();
    string room = $"bench/big-{Guid.NewGuid().ToString("N")[..8]}";
    BenchClient[] clients = new BenchClient[members];
    Stopwatch setup = Stopwatch.StartNew();
    clients[0] = new BenchClient("big-0", delivery);
    await clients[0].ConnectAsync(urls[0]);
    await clients[0].JoinAsync(room, create: true, snapshotBytes: 50_000);
    await Parallel.ForAsync(1, members, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (i, _) =>
    {
        clients[i] = new BenchClient($"big-{i}", delivery);
        await clients[i].ConnectAsync(urls[i % 2]);
        await clients[i].JoinAsync(room, create: false, snapshotBytes: 0);
    });
    TimeSpan joined = setup.Elapsed;
    TimeSpan drained = await BenchClient.WaitForQuietAsync();
    line($"- Setup: {members:N0} members joined in {joined.TotalSeconds:F1} s. Every join sends a 50 KB snapshot and tells every earlier member, about {(long)members * members / 2:N0} presence messages in total; the last of them arrived {drained.TotalSeconds:F1} s later");

    long[] after = await Heaps(stats);
    line($"- Memory: silo {(after[0] - baseline[0]) / 1024.0 / 1024.0:F1} MB for the room; gateways {Kb(after[1] + after[2] - baseline[1] - baseline[2], members)} KB per connection");

    int joinNoise = delivery.Count; // none expected: presence events are not "said"
    Stats requests = new();
    int failed = 0;
    string pad = new('p', 440);   // makes the event about 512 bytes on the wire
    Stopwatch stop = Stopwatch.StartNew();
    int sent = 0;
    while (stop.Elapsed.TotalSeconds < seconds)
    {
        BenchClient sender = clients[sent++ % members];
        long started = Stopwatch.GetTimestamp();
        _ = sender.SayAsync(room, pad).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result)
            {
                requests.Record(Stopwatch.GetElapsedTime(started));
            }
            else
            {
                Interlocked.Increment(ref failed);
            }
        }, TaskScheduler.Default);
        await Task.Delay(200);
    }

    await Task.Delay(2000);
    (double P50, double P95, double P99, double Max) r = requests.Percentiles();
    (double P50, double P95, double P99, double Max) d = delivery.Percentiles();
    long expected = (long)sent * members;
    line($"- Events: {sent:N0} commands ({failed} failed), {delivery.Count - joinNoise:N0} of {expected:N0} deliveries arrived ({100.0 * (delivery.Count - joinNoise) / expected:F2}%)");
    line($"- Request to reply: p50 {r.P50:F1} ms, p95 {r.P95:F1} ms, p99 {r.P99:F1} ms");
    line($"- Command to event at every member: p50 {d.P50:F1} ms, p95 {d.P95:F1} ms, p99 {d.P99:F1} ms, max {d.Max:F0} ms");

    // Kill the second gateway outright. Its members reconnect to the first one and join again.
    BenchClient[] orphans = [.. Enumerable.Range(0, members).Where(i => i % 2 == 1).Select(i => clients[i])];
    int recovered = 0;
    TaskCompletionSource allBack = new(TaskCreationOptions.RunContinuationsAsynchronously);
    foreach (BenchClient? client in orphans)
    {
        int once = 0;
        client.Disconnected = async () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 1)
            {
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await client.ConnectAsync(urls[0]);
                    await client.JoinAsync(room, create: false, snapshotBytes: 0);
                    break;
                }
                catch when (attempt < 20)
                {
                    await Task.Delay(250);
                }
            }

            if (Interlocked.Increment(ref recovered) == orphans.Length)
            {
                allBack.TrySetResult();
            }
        };
    }

    Stopwatch resync = Stopwatch.StartNew();
    secondGateway.Kill(entireProcessTree: true);
    bool finished = await Task.WhenAny(allBack.Task, Task.Delay(TimeSpan.FromSeconds(120))) == allBack.Task;
    line(finished
        ? $"- Gateway killed: all {orphans.Length:N0} of its members were back on the other gateway with a fresh snapshot after {resync.Elapsed.TotalSeconds:F1} s"
        : $"- Gateway killed: only {recovered:N0} of {orphans.Length:N0} members were back after 120 s");

    // The room still works for everyone.
    int before = delivery.Count;
    await clients[0].SayAsync(room, pad);
    await Task.Delay(2000);
    line($"- After recovery one more event reached {delivery.Count - before:N0} of {members:N0} members");
    line("");

    await Parallel.ForEachAsync(clients, new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (client, _) => await client.DisposeAsync());
}

// ---------------------------------------------------------------- idle connections (step 0)

static async Task<int> RunIdle(int connections, int factor, int roomsPerConnection, string? outFile)
{
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    StringBuilder report = new();
    void Line(string text = "")
    {
        Console.WriteLine(text);
        report.AppendLine(text);
    }

    Line(Machine());
    IdleStates small, large;
    try
    {
        small = await IdleRun(connections, roomsPerConnection, Line);
        large = await IdleRun(connections * factor, roomsPerConnection, Line);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"The idle run stopped: {e.Message}");
        return 1;
    }

    Fit Heap(Func<IdleStates, HeapSample> state) => Slope.Between(small.Connections, state(small).HeapBytes, large.Connections, state(large).HeapBytes);
    Fit WorkingSet(Func<IdleStates, HeapSample> state) => Slope.Between(small.Connections, state(small).WorkingSetBytes, large.Connections, state(large).WorkingSetBytes);
    void Row(string name, Func<IdleStates, HeapSample> state) =>
        Line($"| {name} | {Heap(state).PerConnection:N0} | {Mb(Heap(state).Fixed)} | {WorkingSet(state).PerConnection:N0} |");

    int RoomsOf(IdleStates states) => states.Connections / 2 * roomsPerConnection;
    Fit silo = Slope.Between(RoomsOf(small), small.SiloJoined.HeapBytes, RoomsOf(large), large.SiloJoined.HeapBytes);

    Line();
    Line($"Idle connections on today's gateway (MQTTnet {MqttNetVersion()}): {small.Connections:N0} and {large.Connections:N0} connections, {roomsPerConnection} room(s) of two per pair, three claims each.");
    Line("Per connection is the slope between the two counts. \"Fixed\" is the line's value at no connections.");
    Line();
    Line("| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |");
    Line("|---|---|---|---|");
    Row("1. Connected and subscribed", s => s.Connected);
    Row("2. Joined", s => s.Joined);
    Row("3. Active, then idle", s => s.ActiveThenIdle);
    Row("2 again, after churn", s => s.JoinedAgain);
    Line();
    Line($"- Kept after activity, per connection (state 3 − state 2): {Heap(s => s.ActiveThenIdle).PerConnection - Heap(s => s.Joined).PerConnection:N0} bytes");
    Line($"- After churn (state 4), live managed bytes above the baseline: {Mb(small.AfterChurn.HeapBytes - small.Baseline.HeapBytes)} MB and {Mb(large.AfterChurn.HeapBytes - large.Baseline.HeapBytes)} MB");
    Line($"- Silo, per room in state 2: {silo.PerConnection:N0} bytes");

    if (outFile is not null)
    {
        await File.WriteAllTextAsync(outFile, report.ToString());
    }

    return 0;
}

/// <summary>One run of the idle workload in fresh processes: one silo and two gateways that give every connection three claims.</summary>
static async Task<IdleStates> IdleRun(int connections, int roomsPerConnection, Action<string> log)
{
    log($"Run with {connections:N0} connections:");
    int siloHttp = FreePort();
    int orleansGateway = FreePort();
    int[] gatewayHttp = [FreePort(), FreePort()];
    List<(string Name, Process Process)> processes = [("silo", Spawn($"silo --http {siloHttp} --silo-port {FreePort()} --gateway-port {orleansGateway}"))];
    try
    {
        await WaitForHealth(siloHttp);
        processes.Add(("first gateway", Spawn($"gateway --http {gatewayHttp[0]} --gateway-port {orleansGateway} --claims")));
        processes.Add(("second gateway", Spawn($"gateway --http {gatewayHttp[1]} --gateway-port {orleansGateway} --claims")));
        await Task.WhenAll(WaitForHealth(gatewayHttp[0]), WaitForHealth(gatewayHttp[1]));
        IdleSetup setup = new(connections, roomsPerConnection, [.. gatewayHttp.Select(p => $"ws://127.0.0.1:{p}/mqtt")], gatewayHttp, siloHttp, AtJoined: null);
        return await IdleWorkload.RunAsync(setup, log);
    }
    catch (Exception e) when (processes.FirstOrDefault(p => p.Process.HasExited) is { Process: not null } gone)
    {
        throw new InvalidOperationException($"the {gone.Name} (process {gone.Process.Id}) exited during the run. {e.Message}", e);
    }
    finally
    {
        Kill(processes.Select(p => p.Process));
    }
}

// ---------------------------------------------------------------- helpers

static string Machine() =>
    $"Machine: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB RAM, .NET {Environment.Version}";

static string MqttNetVersion() =>
    typeof(MQTTnet.Server.MqttServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

static string Mb(double bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

static void Kill(IEnumerable<Process> processes)
{
    foreach (Process process in processes)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }
    }
}

static string Kb(long bytes, int count) => (bytes / 1024.0 / Math.Max(1, count)).ToString("F1");

static async Task<long[]> Heaps(int[] ports)
{
    using HttpClient http = new();
    long[] result = new long[ports.Length];
    for (int i = 0; i < ports.Length; i++)
    {
        using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{ports[i]}/stats"));
        result[i] = document.RootElement.GetProperty("heapBytes").GetInt64();
    }

    return result;
}

static Process Spawn(string arguments)
{
    string self = Environment.ProcessPath!;
    bool isHost = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    ProcessStartInfo start = new(self, isHost ? $"\"{Path.Combine(AppContext.BaseDirectory, "GrainWire.Bench.dll")}\" {arguments}" : arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = false,
    };
    return Process.Start(start)!;
}

static async Task WaitForHealth(int port)
{
    using HttpClient http = new();
    DateTime deadline = DateTime.UtcNow.AddSeconds(90);
    while (true)
    {
        try
        {
            if ((await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Not up yet.
        }

        if (DateTime.UtcNow > deadline)
        {
            throw new TimeoutException($"The server on port {port} did not start.");
        }

        await Task.Delay(250);
    }
}

static int FreePort()
{
    TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
