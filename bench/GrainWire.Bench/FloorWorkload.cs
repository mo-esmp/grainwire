using System.Diagnostics;
using System.Net.WebSockets;

namespace GrainWire.Bench;

/// <summary>The floor process in each state of its workload.</summary>
public sealed record FloorStates(int Connections, HeapSample Baseline, HeapSample Connected, HeapSample AfterChurn, HeapSample ConnectedAgain);

/// <summary>Idle WebSocket connections against the floor: connected, all gone, and connected again.</summary>
public static class FloorWorkload
{
    private const int Parallelism = 64;
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_churnWait = TimeSpan.FromSeconds(5);

    /// <param name="url">The endpoint, <c>ws://</c> or <c>wss://</c>; the stats are read with the matching http scheme.</param>
    public static async Task<FloorStates> RunAsync(int connections, string url, int statsPort, Action<string> log)
    {
        Uri endpoint = new(url);
        string stats = $"{(endpoint.Scheme == "wss" ? "https" : "http")}://127.0.0.1:{statsPort}";
        ClientWebSocket?[] sockets = new ClientWebSocket?[connections];
        try
        {
            HeapSample baseline = await HeapSampler.StableAsync(stats, log);

            Stopwatch watch = Stopwatch.StartNew();
            IdleChecks.RequireAll("connections", await ConnectAllAsync(), connections);
            log($"- {connections:N0} connections in {watch.Elapsed.TotalSeconds:F1} s");
            await Task.Delay(s_settle);
            HeapSample connected = await HeapSampler.StableAsync(stats, log);

            await CloseAllAsync(sockets);
            await Task.Delay(s_churnWait);
            HeapSample afterChurn = await HeapSampler.StableAsync(stats, log);

            IdleChecks.RequireAll("connections", await ConnectAllAsync(), connections);
            await Task.Delay(s_settle);
            HeapSample connectedAgain = await HeapSampler.StableAsync(stats, log);

            return new FloorStates(connections, baseline, connected, afterChurn, connectedAgain);
        }
        finally
        {
            foreach (ClientWebSocket? socket in sockets)
            {
                socket?.Dispose();
            }
        }

        async Task<int> ConnectAllAsync()
        {
            int count = 0;
            await Parallel.ForAsync(0, connections, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (i, cancellationToken) =>
            {
                ClientWebSocket socket = sockets[i] = new ClientWebSocket();
                socket.Options.AddSubProtocol("mqtt");
                socket.Options.KeepAliveInterval = TimeSpan.Zero;
                if (endpoint.Scheme == "wss")
                {
                    socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }

                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    Interlocked.Increment(ref count);
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException)
                {
                    // Counted by RequireAll.
                }
            });
            return count;
        }
    }

    private static async Task CloseAllAsync(ClientWebSocket?[] sockets)
    {
        await Parallel.ForAsync(0, sockets.Length, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (i, cancellationToken) =>
        {
            ClientWebSocket? socket = sockets[i];
            if (socket is null)
            {
                return;
            }

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                }
            }
            catch (WebSocketException)
            {
                // The server is gone; the next sample says so.
            }
            finally
            {
                socket.Dispose();
                sockets[i] = null;
            }
        });
    }
}
