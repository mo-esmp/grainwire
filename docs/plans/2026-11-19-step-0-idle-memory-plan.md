# Step 0: Idle-Memory Measurement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure what an idle connection costs in today's gateway, how that splits between MQTTnet, ASP.NET Core and GrainWire, and what the floor is, and report it to the owner before anything else is built.

**Architecture:** Three additions to the existing benchmark program (`bench/GrainWire.Bench`), which already starts a silo and two gateways as child processes and reads their heaps over `/stats`: an `idle` workload that walks today's gateway through four states at two connection counts; a `floor` server (a bare WebSocket endpoint) with its own load generator; and an attribution of two heap dumps by namespace. The pure parts (slope, checks, attribution) are unit-tested in a new small test project; the workloads are verified by running them.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core WebSockets, MQTTnet 5 client (load generator, already referenced), xunit, `dotnet-gcdump`.

**Spec:** [docs/specs/2026-11-19-grainwire-on-mqttwire-design.md](https://github.com/mo-esmp/mqttwire/blob/main/docs/specs/2026-11-19-grainwire-on-mqttwire-design.md) in the mqttwire repository, section 6 ("Step 0"), and section 7 row 0. The floor is also section 6.14 of [the MqttWire design](../specs/2026-11-19-mqttwire-design.md).

This is the first of several plans. The others (MqttWire's codec, client and server; GrainWire's client and gateway; the final measurement) are written one at a time, **after the owner has seen this step's report**, because the report can change them.

## Global Constraints

- .NET 10, C# 14, warnings are errors, nullable enabled (`Directory.Build.props`). Package versions are central (`Directory.Packages.props`); a new project references packages without a version.
- **No file under `src/` changes.** This step measures today's code. The MQTTnet-based gateway must be exactly what it is.
- "Per connection" is **a slope between two connection counts**: (bytes at the larger count − bytes at the smaller) ÷ (the difference in connections). Never "bytes ÷ connections".
- The figure that decides is **live managed bytes after a full collection** (`GC.GetTotalMemory(forceFullCollection: true)`, as `/stats` already returns). Working set is reported for information only.
- Each connection count runs in **fresh server processes**.
- Connections carry **three claims** each.
- The floor endpoint: sub-protocol `mqtt`, WebSocket keep-alive off, waiting in a **zero-byte read**.
- Defaults: 3,000 and 12,000 connections (factor 4). Windows gives one address about 16,000 outbound ports, and the load generator runs on the same machine; the counts are options, not constants.
- Commit messages follow the repository's style: one sentence, capitalised, ending with a full stop (`git log --oneline`).
- Work on a new branch `bench/step-0-idle-memory`, created from `main`.

## Review Focus

Conditions the spec implies that the unit tests of the pure parts do not reach. Each has a check in the task that owns the code.

1. **Not every connection connects** (outbound ports run out, a gateway is slow). Expected: the run stops with a message that names how many connected and tells the reader to lower `--connections`; it never reports figures for a partial population. Task 2, `IdleChecks.RequireAll`.
2. **The heap is sampled while work is still going on** (presence messages draining, lease renewal). Expected: a sample is taken only when two samples two seconds apart differ by less than 1%, or after 30 s with a visible note. Task 2, `IdleChecks.IsStable`.
3. **Commands in the activity phase are refused** (rate limit) so "active" was not active. Expected: the run counts failed commands and stops if there is one. Task 2.
4. **A server process dies during the run.** Expected: the run names the process and exits with a non-zero code; child processes are killed in every case. Task 2.
5. **`dotnet-gcdump` is not installed or fails.** Expected: the measurement completes and the report says "no attribution", with the reason. Task 4.

---

### Task 1: The test project and the slope

**Files:**
- Create: `tests/GrainWire.Bench.Tests/GrainWire.Bench.Tests.csproj`
- Create: `tests/GrainWire.Bench.Tests/SlopeTests.cs`
- Create: `bench/GrainWire.Bench/Slope.cs`
- Modify: `GrainWire.slnx` (add the test project under `/tests/`)

**Interfaces:**
- Produces:
  ```csharp
  namespace GrainWire.Bench;
  /// <summary>Bytes as a line over the number of connections.</summary>
  public readonly record struct Fit(double PerConnection, double Fixed);
  public static class Slope
  {
      public static Fit Between(int smallCount, long smallBytes, int largeCount, long largeBytes);
  }
  ```

- [ ] **Step 1: Create the test project**

`tests/GrainWire.Bench.Tests/GrainWire.Bench.Tests.csproj`: `Microsoft.NET.Sdk`, `RootNamespace` `GrainWire.Bench.Tests`, `NoWarn` `$(NoWarn);CS1591`, packages `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, a `ProjectReference` to `../../bench/GrainWire.Bench/GrainWire.Bench.csproj`, and `<Using Include="Xunit" />`. It must **not** reference `GrainWire.Tests` or the test cluster: the benchmark's `BenchRoom` grain must not appear in the main tests' silos. Add `<Project Path="tests/GrainWire.Bench.Tests/GrainWire.Bench.Tests.csproj" />` to the `/tests/` folder of `GrainWire.slnx`.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace GrainWire.Bench.Tests;

public class SlopeTests
{
    [Fact]
    public void The_slope_is_the_growth_per_added_connection()
    {
        Fit fit = Slope.Between(3_000, 130_000_000, 12_000, 400_000_000);
        Assert.Equal(30_000, fit.PerConnection, precision: 6);
        Assert.Equal(40_000_000, fit.Fixed, precision: 3);
    }

    [Fact]
    public void A_cost_that_does_not_grow_gives_a_slope_of_zero()
    {
        Fit fit = Slope.Between(3_000, 50_000_000, 12_000, 50_000_000);
        Assert.Equal(0, fit.PerConnection, precision: 6);
        Assert.Equal(50_000_000, fit.Fixed, precision: 3);
    }

    [Fact]
    public void The_order_of_the_two_points_does_not_matter()
    {
        Assert.Equal(Slope.Between(3_000, 130_000_000, 12_000, 400_000_000), Slope.Between(12_000, 400_000_000, 3_000, 130_000_000));
    }

    [Fact]
    public void Two_runs_at_the_same_count_cannot_give_a_slope()
    {
        Assert.Throws<ArgumentException>(() => Slope.Between(3_000, 1, 3_000, 2));
    }
}
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test tests/GrainWire.Bench.Tests`
Expected: the build fails, `Slope` and `Fit` do not exist.

- [ ] **Step 4: Implement `Slope.Between` and `Fit` in `bench/GrainWire.Bench/Slope.cs`**

`Fixed` is the line's value at zero connections.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/GrainWire.Bench.Tests`
Expected: 4 passed.

- [ ] **Step 6: Commit**

```bash
git add GrainWire.slnx tests/GrainWire.Bench.Tests bench/GrainWire.Bench/Slope.cs
git commit -m "Add the slope between two connection counts to the benchmark."
```

---

### Task 2: The `idle` workload on today's gateway

**Files:**
- Create: `bench/GrainWire.Bench/IdleChecks.cs`
- Create: `bench/GrainWire.Bench/HeapSampler.cs`
- Create: `bench/GrainWire.Bench/IdleWorkload.cs`
- Create: `bench/GrainWire.Bench/BenchAuthenticator.cs`
- Create: `tests/GrainWire.Bench.Tests/IdleChecksTests.cs`
- Modify: `bench/GrainWire.Bench/Program.cs` (the mode switch at line 22; `RunGateway`; `Heaps`; a new `RunIdle`)

**Interfaces:**
- Consumes: `Slope.Between`, `Fit` (Task 1). `BenchClient` and `Stats` as they are.
- Produces:
  ```csharp
  namespace GrainWire.Bench;

  public readonly record struct HeapSample(long HeapBytes, long WorkingSetBytes)
  {
      public static HeapSample operator +(HeapSample a, HeapSample b);
  }

  /// <summary>The two gateways together, in each state of the workload.</summary>
  public sealed record IdleStates(
      int Connections,
      HeapSample Baseline,        // gateways up, nobody connected
      HeapSample Connected,       // state 1
      HeapSample Joined,          // state 2
      HeapSample ActiveThenIdle,  // state 3
      HeapSample AfterChurn,      // state 4: everyone gone
      HeapSample JoinedAgain,     // state 2 again, after everyone came back
      HeapSample SiloJoined);     // the silo in state 2, for information

  public static class IdleChecks
  {
      /// <exception cref="InvalidOperationException">Fewer than expected.</exception>
      public static void RequireAll(string what, int actual, int expected);
      /// <exception cref="InvalidOperationException">At least one command failed.</exception>
      public static void RequireNoFailures(int failed);
      public static bool IsStable(long first, long second);
  }

  public static class HeapSampler
  {
      /// <summary>Samples the process at <paramref name="baseUrl"/> (for example http://127.0.0.1:5001) until two samples two seconds apart are within 1%, for at most 30 s.</summary>
      /// <exception cref="InvalidOperationException">The process does not answer.</exception>
      public static Task<HeapSample> StableAsync(string baseUrl, Action<string> log);
  }

  public static class IdleWorkload
  {
      public static Task<IdleStates> RunAsync(IdleSetup setup, Action<string> log);
  }

  /// <param name="AtJoined">Called in state 2, after its sample; Task 4 takes its heap dump here.</param>
  public sealed record IdleSetup(
      int Connections,
      int RoomsPerConnection,
      string[] GatewayUrls,
      int[] GatewayStatsPorts,
      int SiloStatsPort,
      Func<Task>? AtJoined);
  ```
- CLI produced: `idle [--connections 3000] [--factor 4] [--rooms-per-connection 1] [--out <file>]`.

- [ ] **Step 1: Write the failing tests for the checks**

```csharp
namespace GrainWire.Bench.Tests;

public class IdleChecksTests
{
    [Fact]
    public void A_full_population_passes()
    {
        IdleChecks.RequireAll("connections", 3_000, 3_000);
    }

    [Fact]
    public void A_partial_population_stops_the_run_and_says_what_to_do()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireAll("connections", 2_871, 3_000));
        Assert.Contains("2,871 of 3,000 connections", error.Message);
        Assert.Contains("--connections", error.Message);
    }

    [Fact]
    public void No_failed_command_passes_and_one_stops_the_run()
    {
        IdleChecks.RequireNoFailures(0);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireNoFailures(12));
        Assert.Equal("12 commands failed in the activity phase; the connections were not active.", error.Message);
    }

    [Theory]
    [InlineData(100_000_000, 100_900_000, true)]    // 0.9% apart
    [InlineData(100_000_000, 99_100_000, true)]
    [InlineData(100_000_000, 101_100_000, false)]   // 1.1% apart
    [InlineData(0, 0, true)]
    public void A_heap_is_stable_when_two_samples_are_within_one_percent(long first, long second, bool stable)
    {
        Assert.Equal(stable, IdleChecks.IsStable(first, second));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/GrainWire.Bench.Tests`
Expected: the build fails, `IdleChecks` does not exist.

- [ ] **Step 3: Implement `IdleChecks` in `bench/GrainWire.Bench/IdleChecks.cs`**

The message of `RequireAll` is `"Only {actual:N0} of {expected:N0} {what}. Lower --connections, or look at the server's output."`, and that of `RequireNoFailures` is `"{failed:N0} commands failed in the activity phase; the connections were not active."`, both formatted with the invariant culture. `IsStable` compares the difference with 1% of the larger value.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/GrainWire.Bench.Tests`
Expected: 11 passed (4 from Task 1; here 3 facts and a theory of 4 cases).

- [ ] **Step 5: Three claims per connection**

`bench/GrainWire.Bench/BenchAuthenticator.cs`: `internal sealed class BenchAuthenticator : IConnectionAuthenticator`. `AuthenticateAsync` returns `AuthenticationResult.Success(new PlayerId(context.UserName!), claims)` where `claims` is a new dictionary per connection with exactly these three entries: `"tenant" = "bench"`, `"tier" = "standard"`, `"region" = "eu-west"`. In `Program.cs`, `RunGateway` gets a parameter `bool withClaims` and, when it is true, registers `builder.Services.AddSingleton<IConnectionAuthenticator, BenchAuthenticator>()` **before** `AddGrainWireGateway` (which only adds the development authenticator if none is registered). The `gateway` mode reads it from a `--claims` flag; add a helper `bool Flag(string name) => Array.IndexOf(args, name) >= 0;` beside `Arg` and `Text`. The existing `run` mode keeps spawning gateways without `--claims`, so the published benchmark does not change.

- [ ] **Step 6: Samples that carry the working set, and only when the heap is quiet**

Implement `HeapSampler.StableAsync` in `bench/GrainWire.Bench/HeapSampler.cs`. It reads `heapBytes` and `workingSetBytes` from `{baseUrl}/stats`, uses `IdleChecks.IsStable` on the heap, and when it gives up after 30 s it logs `"- Note: the heap of the process at {baseUrl} was still moving when it was sampled."` and returns the last sample. If `/stats` cannot be reached it throws `InvalidOperationException($"The server at {baseUrl} is gone.")` (Review Focus 4). Its `HttpClient` accepts any server certificate, because Task 3 samples a loopback server over `https`. The existing `Heaps` in `Program.cs` stays as it is for the `run` mode.

- [ ] **Step 7: Implement `IdleWorkload.RunAsync` in `bench/GrainWire.Bench/IdleWorkload.cs`**

The states, in order. "Sample" means `HeapSampler.StableAsync` of both gateways (`http://127.0.0.1:{port}`), added together.

| State | What happens before the sample |
|---|---|
| Baseline | Nothing. |
| 1 Connected | `Connections` `BenchClient`s connect (which also subscribes), even indexes to `GatewayUrls[0]`, odd to `GatewayUrls[1]`, with `Parallel.ForAsync` at 64, as `ManySmallRooms` does. Then `IdleChecks.RequireAll("connections", connected, Connections)`. |
| 2 Joined | Clients `2i` and `2i+1` are a pair. For `r` in `0..RoomsPerConnection-1`, the first creates `bench/i{run}-{i}-{r}` with `snapshotBytes: 256` and the second joins it. `RequireAll("joins", ...)`. `BenchClient.WaitForQuietAsync()`. Sample the gateways, and the silo into `SiloJoined`. Then `await setup.AtJoined()` if it is not null. |
| 3 Active, then idle | Every client sends 20 `say` commands to its first room without waiting between them (pad of 64 characters), then all replies are awaited. 20 is inside the gateway's default burst of 40. Then `IdleChecks.RequireNoFailures(failed)` (Review Focus 3). `WaitForQuietAsync()`. |
| 4 After churn | Every client is disposed. Wait 25 s: the gateway drops its empty per-room tables when it renews its leases, every 10 s. |
| Joined again | New `BenchClient`s with the same player names connect and join the same rooms with `create: false`. `RequireAll` for both. `WaitForQuietAsync()`. |

Player names are `i{run}-{index}`, where `run` is eight characters of a new GUID, as the existing workloads do. All clients are disposed in a `finally`.

- [ ] **Step 8: The `idle` mode in `Program.cs`**

Add `"idle" => await RunIdle(Arg("--connections", 3000), Arg("--factor", 4), Arg("--rooms-per-connection", 1), Text("--out"))` to the switch, and `idle` to the "Unknown mode" text and to the comment at the top of the file.

`RunIdle` runs the workload twice, at `connections` and at `connections * factor`, each time in fresh processes (one silo, two gateways with `--claims`), started and killed exactly as `RunBenchmark` does, in a `try`/`finally` that kills them in every case. It then prints, and writes to `--out` if given:

```text
Machine: <as RunBenchmark prints it>
Idle connections on today's gateway (MQTTnet 5.2.0.1603): <small> and <large> connections, <rooms> room(s) of two per pair, three claims each.
Per connection is the slope between the two counts. "Fixed" is the line's value at no connections.

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| 1. Connected and subscribed | 12,345 | 41.2 | 23,456 |
| 2. Joined | ... | ... | ... |
| 3. Active, then idle | ... | ... | ... |
| 2 again, after churn | ... | ... | ... |

- Kept after activity, per connection (state 3 − state 2): <n> bytes
- After churn (state 4), live managed bytes above the baseline: <small run> MB and <large run> MB
- Silo, per room in state 2: <n> bytes
```

Bytes are whole numbers with thousands separators, megabytes with one decimal, invariant culture. Every per-connection figure comes from `Slope.Between` over the two runs; the working-set column uses the same slope on `WorkingSetBytes`. "After churn" is not a slope: it is `AfterChurn.HeapBytes − Baseline.HeapBytes` for each run.

- [ ] **Step 9: Run it small, to see that it works**

Run: `dotnet run -c Release --project bench/GrainWire.Bench -- idle --connections 200 --factor 2`
Expected: it ends within about three minutes with exit code 0 and prints the table with four rows of numbers. No `Note:` line about a moving heap; if there is one, the wait before that sample is too short and is fixed before going on. No `dotnet` child process is left running afterwards (`tasklist | findstr GrainWire.Bench` shows nothing).

- [ ] **Step 10: See what happens when a server dies (Review Focus 4)**

Start the same small run again and, while it prints nothing (it is connecting or waiting), end one of the two `gateway` child processes from another terminal (`tasklist /v | findstr GrainWire.Bench`, then `taskkill /pid <pid> /f`).
Expected: the run ends with a non-zero exit code and a message that names the server that is gone or the connections that failed; no figures are printed; no child process is left running.

- [ ] **Step 11: Build everything and run the tests**

Run: `dotnet build -c Release` then `dotnet test tests/GrainWire.Bench.Tests`
Expected: no warnings, 11 passed. (The `run` mode is not run here; its code was not changed.)

- [ ] **Step 12: Commit**

```bash
git add bench/GrainWire.Bench tests/GrainWire.Bench.Tests
git commit -m "Add an idle-connection workload to the benchmark."
```

---

### Task 3: The floor

**Files:**
- Create: `bench/GrainWire.Bench/FloorServer.cs`
- Create: `bench/GrainWire.Bench/FloorWorkload.cs`
- Modify: `bench/GrainWire.Bench/Program.cs` (two modes)

**Interfaces:**
- Consumes: `HeapSample`, `IdleChecks`, `HeapSampler.StableAsync`, `Slope`, `Fit` (Tasks 1 and 2); `NewBuilder`, `MapStats`, `Spawn`, `WaitForHealth`, `FreePort` in `Program.cs`.
- Produces:
  ```csharp
  namespace GrainWire.Bench;

  public static class FloorServer
  {
      /// <summary>Maps the bare endpoint at /mqtt. The process is the "floor" mode.</summary>
      public static void Map(WebApplication app);
  }

  public sealed record FloorStates(int Connections, HeapSample Baseline, HeapSample Connected, HeapSample AfterChurn, HeapSample ConnectedAgain);

  public static class FloorWorkload
  {
      public static Task<FloorStates> RunAsync(int connections, string url, int statsPort, Action<string> log);
  }
  ```
- CLI produced: `floor --http <port> [--tls]` (the server process) and `floor-run [--connections 3000] [--factor 4] [--tls] [--out <file>]`.

- [ ] **Step 1: The endpoint, `FloorServer.Map`**

This is the thing being measured, so its shape is fixed:

```csharp
public static void Map(WebApplication app)
{
    app.UseWebSockets();
    app.Map("/mqtt", async (HttpContext context) =>
    {
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains("mqtt"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = "mqtt",
            KeepAliveInterval = TimeSpan.Zero,   // no ping timer
        });
        await WaitUntilClosed(socket, context.RequestAborted);
    });
}
```

`WaitUntilClosed(WebSocket socket, CancellationToken cancellationToken)`: a loop that awaits `socket.ReceiveAsync(Memory<byte>.Empty, cancellationToken)` (**the zero-byte read: an idle connection is parked here and holds no buffer of ours**); when that returns, it rents 256 bytes from `ArrayPool<byte>.Shared`, receives into them, returns them, and leaves the loop when the message type is `Close` (answering with `CloseOutputAsync`). A `WebSocketException` or `OperationCanceledException` ends it quietly. No buffer is referenced across the zero-byte read.

- [ ] **Step 2: The `floor` server mode**

`"floor" => await RunFloor(Arg("--http", 0), Flag("--tls"))`. `RunFloor` uses `NewBuilder`, `FloorServer.Map`, `MapStats`. With `--tls`, Kestrel listens with `https` on the same port, using a self-signed certificate made at start-up (`CertificateRequest` with an RSA key of 2,048 bits, subject `CN=localhost`; on Windows the certificate is exported to PKCS#12 and loaded again with `X509CertificateLoader.LoadPkcs12`, because `SslStream` cannot use an ephemeral key there). `/health` and `/stats` are then served over `https` as well, so `WaitForHealth` gets an optional parameter `string scheme = "http"` and an `HttpClient` whose handler accepts any server certificate; the sampler already takes a base URL.

- [ ] **Step 3: The load generator, `FloorWorkload.RunAsync`**

Each connection is a `ClientWebSocket` with `Options.AddSubProtocol("mqtt")`, `Options.KeepAliveInterval = TimeSpan.Zero`, and, for a `wss` URL, `Options.RemoteCertificateValidationCallback` accepting any certificate. They connect with `Parallel.ForAsync` at 64.

| State | What happens before the sample |
|---|---|
| Baseline | Nothing. |
| Connected | All connect. `IdleChecks.RequireAll("connections", connected, connections)`. Wait 2 s. |
| After churn | All are closed with `CloseAsync(NormalClosure)` and disposed. Wait 5 s. |
| Connected again | All connect again. `RequireAll`. Wait 2 s. |

Samples use `HeapSampler.StableAsync`. All sockets are disposed in a `finally`.

- [ ] **Step 4: The `floor-run` mode**

`"floor-run" => await RunFloorBenchmark(Arg("--connections", 3000), Arg("--factor", 4), Flag("--tls"), Text("--out"))`. It runs the workload at the two counts, each against a fresh `floor` process, kills the process in a `finally`, and prints:

```text
The floor: a bare ASP.NET Core WebSocket endpoint (sub-protocol mqtt, keep-alive off, zero-byte read)<, with TLS in Kestrel>. <small> and <large> idle connections.

| State | Live managed bytes per connection | Fixed (MB) | Working set per connection |
|---|---|---|---|
| Connected | ... | ... | ... |
| Connected again, after churn | ... | ... | ... |

- After churn, live managed bytes above the baseline: <small run> MB and <large run> MB
```

- [ ] **Step 5: Run it small, without and with TLS**

Run: `dotnet run -c Release --project bench/GrainWire.Bench -- floor-run --connections 500 --factor 2`
Then: `dotnet run -c Release --project bench/GrainWire.Bench -- floor-run --connections 500 --factor 2 --tls`
Expected: each ends with exit code 0 and a table of two rows. The per-connection figure with TLS is larger than without. "After churn" is a few megabytes at most in both runs; a value that grows with the count means connections are not being released, which is a fault in this code and is fixed before going on.

- [ ] **Step 6: Check by hand that a wrong client is refused**

Run the server alone: `dotnet run -c Release --project bench/GrainWire.Bench -- floor --http 5599`, and in a second terminal: `curl -i http://127.0.0.1:5599/mqtt`
Expected: `HTTP/1.1 400 Bad Request`. Stop the server.

- [ ] **Step 7: Commit**

```bash
git add bench/GrainWire.Bench
git commit -m "Measure the floor: idle connections on a bare WebSocket endpoint."
```

---

### Task 4: Who owns the bytes

**Files:**
- Create: `bench/GrainWire.Bench/HeapAttribution.cs`
- Create: `tests/GrainWire.Bench.Tests/HeapAttributionTests.cs`
- Create: `tests/GrainWire.Bench.Tests/gcdump-report-sample.txt` (copied to the output directory)
- Modify: `bench/GrainWire.Bench/Program.cs` (`RunIdle`: a `--gcdump <directory>` option)

**Interfaces:**
- Consumes: `IdleSetup.AtJoined`, `Slope.Between` (Tasks 1 and 2).
- Produces:
  ```csharp
  namespace GrainWire.Bench;

  public static class HeapAttribution
  {
      public const string MqttNet = "MQTTnet";
      public const string ConnectionsLayer = "ASP.NET Core Connections layer and pipes";
      public const string AspNetCore = "Kestrel, ASP.NET Core, WebSocket, sockets";
      public const string GrainWire = "GrainWire";
      public const string Orleans = "Orleans";
      public const string Other = "Other (strings, arrays, runtime, tasks)";

      public static string BucketOf(string typeName);

      /// <summary>Bytes per bucket, from the text that "dotnet-gcdump report" prints.</summary>
      public static IReadOnlyDictionary<string, long> Parse(string report);
  }
  ```

- [ ] **Step 1: Ask the owner before installing the tool**

`dotnet-gcdump` is not installed on this machine (`dotnet tool list -g` shows only `dotnet-ef`). Installing it downloads a Microsoft tool from NuGet: `dotnet tool install --global dotnet-gcdump`. **Ask the owner first.** If the owner says no, skip steps 2 and 7 to 9, do steps 3 to 6 with the fixture described in step 2's fallback, and the report of Task 5 says "no attribution: the owner chose not to install dotnet-gcdump".

- [ ] **Step 2: Get a real report to write the parser against**

Start a gateway set by hand (`idle --connections 200 --factor 2` in one terminal is enough to have processes to look at; take the process id of one `gateway` from `tasklist /v`), then:

```bash
dotnet-gcdump collect -p <pid> -o scratch.gcdump
dotnet-gcdump report scratch.gcdump > tests/GrainWire.Bench.Tests/gcdump-report-sample.txt
```

Keep the header lines and the first 40 type lines of that file, and delete `scratch.gcdump`. The report is expected to look like this, one type per line, bytes first, then the count, then the type name with its module in brackets:

```text
     1,603,588  GC Heap bytes
        32,049  GC Heap objects

  Object Bytes     Count  Type
       464,292       164  System.Byte[] (Bytes > 10K)  [System.Private.CoreLib.dll]
       120,480     2,510  MQTTnet.Server.Internal.MqttSession  [MQTTnet.Server.dll]
```

**If the real file looks different, the real file wins:** the tests of step 3 are written against what it actually contains, and the parser reads that format. Fallback without the tool: use the six lines above as the fixture.

Add to the test project file: `<None Include="gcdump-report-sample.txt" CopyToOutputDirectory="PreserveNewest" />`.

- [ ] **Step 3: Write the failing tests**

```csharp
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
        IReadOnlyDictionary<string, long> buckets = HeapAttribution.Parse(File.ReadAllText("gcdump-report-sample.txt"));
        // These two numbers are right for the six-line fallback fixture. With a real sample file, replace them
        // with what its MQTTnet lines and its "Other" lines add up to, counted by hand.
        Assert.Equal(120_480, buckets[HeapAttribution.MqttNet]);
        Assert.Equal(464_292, buckets[HeapAttribution.Other]);
        Assert.Equal(6, buckets.Count);   // every bucket is present, with 0 if nothing fell into it
    }

    [Fact]
    public void Lines_that_are_not_types_are_skipped()
    {
        IReadOnlyDictionary<string, long> buckets = HeapAttribution.Parse("     1,603,588  GC Heap bytes\n\n  Object Bytes     Count  Type\n");
        Assert.All(buckets.Values, bytes => Assert.Equal(0, bytes));
    }
}
```

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test tests/GrainWire.Bench.Tests`
Expected: the build fails, `HeapAttribution` does not exist.

- [ ] **Step 5: Implement `HeapAttribution` in `bench/GrainWire.Bench/HeapAttribution.cs`**

`BucketOf` decides by the start of the type name only, in this order, so that a generic collection of GrainWire objects counts as the collection ("Other") and not as GrainWire:

| The type name starts with | Bucket |
|---|---|
| `MQTTnet.` | `MqttNet` |
| `System.IO.Pipelines.`, `Microsoft.AspNetCore.Connections.`, `Microsoft.AspNetCore.Http.Connections.` | `ConnectionsLayer` |
| `Microsoft.AspNetCore.`, `Microsoft.Extensions.`, `System.Net.` | `AspNetCore` |
| `GrainWire.` | `GrainWire` |
| `Orleans.` | `Orleans` |
| anything else | `Other` |

`Parse` reads a line as a type line when it begins with a number, a count and a name; numbers have thousands separators; the module in square brackets at the end is not part of the name.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/GrainWire.Bench.Tests`
Expected: 26 passed (11 from before; here a theory of 13 cases and 2 facts).

- [ ] **Step 7: Take the dumps during the idle run**

`RunIdle` gets `Text("--gcdump")`, a directory. When it is given, `IdleSetup.AtJoined` for each of the two runs does, for the **first gateway** process:

```text
dotnet-gcdump collect -p <pid> -o <directory>/gateway-<connections>.gcdump
dotnet-gcdump report <directory>/gateway-<connections>.gcdump     (standard output saved to <directory>/gateway-<connections>.txt)
```

Each with a time limit of three minutes. If either cannot be started, exits with a non-zero code or times out, the run **continues**, and the report gets the line `- No attribution: <what failed>.` (Review Focus 5). The state-2 sample is taken before the dump, so the dump cannot disturb it; state 3's stable sample absorbs what the dump left behind.

- [ ] **Step 8: Print the attribution**

After the states table, when both text files exist, `RunIdle` prints:

```text
Who owns the bytes in state 2, per connection on one gateway (slope between the two heap dumps):

| Owner | Bytes per connection |
|---|---|
| MQTTnet | ... |
| ASP.NET Core Connections layer and pipes | ... |
| Kestrel, ASP.NET Core, WebSocket, sockets | ... |
| GrainWire | ... |
| Orleans | ... |
| Other (strings, arrays, runtime, tasks) | ... |

A type is counted under its own namespace, so the strings, arrays and tasks that belong to a library's objects are under "Other". Compare with the floor to see how much of "Other" comes with any WebSocket.
```

Each figure is `Slope.Between(smallOnThisGateway, bytes, largeOnThisGateway, bytes).PerConnection`, where the connections on the first gateway are half of the total.

- [ ] **Step 9: Run it small**

Run: `dotnet run -c Release --project bench/GrainWire.Bench -- idle --connections 200 --factor 2 --gcdump artifacts/step-0`
Expected: exit code 0, both tables printed, `artifacts/step-0/` holds two `.gcdump` and two `.txt` files. Then run it once with `dotnet-gcdump` taken off the `PATH` (rename the tool's shim for the moment, or run from a shell with a reduced `PATH`): exit code 0, the states table printed, and the `- No attribution:` line in place of the second table.

- [ ] **Step 10: Commit**

```bash
git add bench/GrainWire.Bench tests/GrainWire.Bench.Tests
git commit -m "Attribute a gateway's heap to its owners in the idle benchmark."
```

(`artifacts/` is ignored by git; check with `git status` that nothing from it is staged.)

---

### Task 5: Measure, and report to the owner

**Files:**
- Create: `docs/step-0-idle-memory.md`

**Interfaces:**
- Consumes: the three modes of Tasks 2 to 4.
- Produces: the report that decides whether the rest of the work starts as designed.

- [ ] **Step 1: A quiet machine**

Close what can be closed. The load generator and the servers share this machine, and the working-set figures are sensitive to it. Build once: `dotnet build -c Release`.

- [ ] **Step 2: Today's gateway, one room per pair**

Run: `dotnet run -c Release --no-build --project bench/GrainWire.Bench -- idle --gcdump artifacts/step-0 --out artifacts/step-0/idle.md`
Expected: exit code 0, no `Note:` line. If connections fail (Review Focus 1), rerun with `--connections 2000`; the report states the counts that were used.

- [ ] **Step 3: Today's gateway, five rooms per connection**

Run: `dotnet run -c Release --no-build --project bench/GrainWire.Bench -- idle --rooms-per-connection 5 --out artifacts/step-0/idle-5-rooms.md`

- [ ] **Step 4: The floor, without and with TLS**

Run: `dotnet run -c Release --no-build --project bench/GrainWire.Bench -- floor-run --out artifacts/step-0/floor.md`
Run: `dotnet run -c Release --no-build --project bench/GrainWire.Bench -- floor-run --tls --out artifacts/step-0/floor-tls.md`

- [ ] **Step 5: Do it all once more**

Repeat steps 2 and 4 (without TLS) with `--out` names ending in `-2.md`. One run is an anecdote; if the per-connection figures of the two runs differ by more than 10%, run a third time and report all three.

- [ ] **Step 6: Write `docs/step-0-idle-memory.md`**

In this order, in plain language, with the tables copied from the outputs:

1. **The result in three lines:** what an idle, joined connection costs today (state 2, live managed bytes per connection); what the floor is; the floor as a percentage of today's figure.
2. **The 70% rule, applied.** The design says: if the floor alone is more than about 70% of today's figure, the approach cannot deliver much and the owner decides whether to continue. State on which side of that the measurement falls.
3. **The targets.** The design proposes floor + 1 KB for MqttWire alone and floor + 2 KB for a joined connection in GrainWire. Give both as numbers, and say whether the attribution makes them look realistic: what GrainWire's own objects cost per connection today, and what MQTTnet and the Connections layer cost, which is what goes away.
4. **Today's gateway:** the states table, "kept after activity", "after churn", the five-rooms run.
5. **Who owns the bytes:** the attribution table and its caveat.
6. **The floor:** both tables.
7. **How it was measured:** the machine line, the counts, that per connection is a slope, the commands that were run, how many runs and how far they agreed.
8. **What this does not show:** real networks; a load balancer in front; more than one silo; that working set is not the deciding figure.
9. **One sentence on the zero-byte read:** whether the floor's figure is consistent with an idle connection holding no buffer (a buffer of 4 KB per connection would show up as about 4,000 bytes in the slope).

- [ ] **Step 7: Commit**

```bash
git add docs/step-0-idle-memory.md
git commit -m "Report what an idle connection costs today and what the floor is."
```

- [ ] **Step 8: Stop and report to the owner**

Give the owner the three lines of section 1 and the link to the report. **Nothing else is built until the owner has answered**: the design makes this report the gate for the rest of the work, and the next plan is written from it.
