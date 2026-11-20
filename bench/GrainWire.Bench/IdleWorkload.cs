using System.Diagnostics;

namespace GrainWire.Bench;

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

/// <param name="AtJoined">Called in state 2, after its sample; Task 4 takes its heap dump here.</param>
public sealed record IdleSetup(
    int Connections,
    int RoomsPerConnection,
    string[] GatewayUrls,
    int[] GatewayStatsPorts,
    int SiloStatsPort,
    Func<Task>? AtJoined);

/// <summary>
/// Idle connections on today's gateway, in four states: connected and subscribed; joined (rooms of two); active, then
/// idle; and after churn, when everyone has gone and come back. See section 6 of docs/specs/2026-11-19-grainwire-on-mqttwire-design.md.
/// </summary>
public static class IdleWorkload
{
    private const int Parallelism = 64;
    private const int CommandsPerClient = 20;   // inside the gateway's default burst of 40

    // Each "say" goes to both members of its room, so every client receives its own commands and its partner's.
    private const int SaidPerClient = 2 * CommandsPerClient;
    private static readonly TimeSpan s_deliveryLimit = TimeSpan.FromSeconds(60);

    // The gateway drops its empty per-room tables when it renews its leases, every 10 s.
    private static readonly TimeSpan s_churnWait = TimeSpan.FromSeconds(25);

    public static async Task<IdleStates> RunAsync(IdleSetup setup, Action<string> log)
    {
        if (setup.Connections < 2 || setup.Connections % 2 != 0)
        {
            throw new ArgumentException($"The connections are taken in pairs; {setup.Connections} is not an even number of at least 2.", nameof(setup));
        }

        string run = Guid.NewGuid().ToString("N")[..8];
        int pairs = setup.Connections / 2;
        Stats events = new();
        BenchClient?[] clients = new BenchClient?[setup.Connections];
        BenchClient?[] again = new BenchClient?[setup.Connections];
        try
        {
            HeapSample baseline = await SampleGateways();

            // State 1: connected and subscribed.
            Stopwatch watch = Stopwatch.StartNew();
            int connected = await ConnectAllAsync(clients);
            IdleChecks.RequireAll("connections", connected, setup.Connections);
            log($"- {setup.Connections:N0} connections in {watch.Elapsed.TotalSeconds:F1} s");
            HeapSample stateConnected = await SampleGateways();

            // State 2: joined, rooms of two.
            watch.Restart();
            int joined = await JoinAllAsync(clients, create: true);
            IdleChecks.RequireAll("joins", joined, setup.Connections * setup.RoomsPerConnection);
            await BenchClient.WaitForQuietAsync();
            log($"- {pairs * setup.RoomsPerConnection:N0} rooms of two in {watch.Elapsed.TotalSeconds:F1} s");
            HeapSample stateJoined = await SampleGateways();
            HeapSample silo = await HeapSampler.StableAsync($"http://127.0.0.1:{setup.SiloStatsPort}", log);
            if (setup.AtJoined is not null)
            {
                await setup.AtJoined();
            }

            // State 3: active, then idle. Every client sends its commands without waiting between them.
            watch.Restart();
            int failed = 0;
            string pad = new('p', 64);
            await Parallel.ForAsync(0, setup.Connections, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (i, _) =>
            {
                string room = Room(run, i / 2, 0);
                Task<bool>[] says = [.. Enumerable.Range(0, CommandsPerClient).Select(_ => clients[i]!.SayAsync(room, pad))];
                try
                {
                    await Task.WhenAll(says);
                }
                catch
                {
                    // Counted below.
                }

                Interlocked.Add(ref failed, says.Count(say => !say.IsCompletedSuccessfully || !say.Result));
            });
            IdleChecks.RequireNoFailures(failed);
            await BenchClient.WaitForSaidAsync(clients, SaidPerClient, s_deliveryLimit);
            IdleChecks.RequireDeliveries([.. clients.Select(client => client?.SaidReceived ?? 0)], SaidPerClient);
            await BenchClient.WaitForQuietAsync();
            log($"- {setup.Connections * CommandsPerClient:N0} commands and {(long)setup.Connections * SaidPerClient:N0} deliveries in {watch.Elapsed.TotalSeconds:F1} s");
            HeapSample stateActive = await SampleGateways();

            // State 4: everyone gone.
            await DisposeAllAsync(clients);
            await Task.Delay(s_churnWait);
            HeapSample stateChurned = await SampleGateways();

            // State 2 again: everyone back, with the same names, in the same rooms.
            int back = await ConnectAllAsync(again);
            IdleChecks.RequireAll("connections", back, setup.Connections);
            int rejoined = await JoinAllAsync(again, create: false);
            IdleChecks.RequireAll("joins", rejoined, setup.Connections * setup.RoomsPerConnection);
            await BenchClient.WaitForQuietAsync();
            HeapSample stateJoinedAgain = await SampleGateways();

            return new IdleStates(setup.Connections, baseline, stateConnected, stateJoined, stateActive, stateChurned, stateJoinedAgain, silo);
        }
        finally
        {
            await DisposeAllAsync(clients);
            await DisposeAllAsync(again);
        }

        async Task<HeapSample> SampleGateways()
        {
            HeapSample total = default;
            foreach (int port in setup.GatewayStatsPorts)
            {
                total += await HeapSampler.StableAsync($"http://127.0.0.1:{port}", log);
            }

            return total;
        }

        // Even indexes connect to the first gateway and odd to the second, so the two players of a room sit on different gateways.
        async Task<int> ConnectAllAsync(BenchClient?[] into)
        {
            await PortBudget.WaitForAsync(into.Length, log);
            int count = 0;
            await Parallel.ForAsync(0, into.Length, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (i, _) =>
            {
                BenchClient client = into[i] = new BenchClient($"i{run}-{i}", events);
                try
                {
                    await client.ConnectAsync(setup.GatewayUrls[i % 2]);
                    Interlocked.Increment(ref count);
                }
                catch
                {
                    // Counted by RequireAll.
                }
            });
            return count;
        }

        // Clients 2i and 2i+1 are a pair: the first creates each of their rooms (or, coming back, joins it) and the second joins it.
        async Task<int> JoinAllAsync(BenchClient?[] members, bool create)
        {
            int count = 0;
            await Parallel.ForAsync(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (pair, _) =>
            {
                for (int r = 0; r < setup.RoomsPerConnection; r++)
                {
                    string room = Room(run, pair, r);
                    try
                    {
                        await members[2 * pair]!.JoinAsync(room, create, snapshotBytes: create ? 256 : 0);
                        Interlocked.Increment(ref count);
                        await members[(2 * pair) + 1]!.JoinAsync(room, create: false, snapshotBytes: 0);
                        Interlocked.Increment(ref count);
                    }
                    catch
                    {
                        // Counted by RequireAll.
                    }
                }
            });
            return count;
        }
    }

    private static string Room(string run, int pair, int index) => $"bench/i{run}-{pair}-{index}";

    private static async Task DisposeAllAsync(BenchClient?[] clients)
    {
        await Parallel.ForEachAsync(clients, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (client, _) =>
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        });
        Array.Clear(clients);
    }
}
