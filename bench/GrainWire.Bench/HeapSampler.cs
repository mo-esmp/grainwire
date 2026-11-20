using System.Text.Json;

namespace GrainWire.Bench;

/// <summary>Live managed bytes after a full collection, and the working set, of one process.</summary>
public readonly record struct HeapSample(long HeapBytes, long WorkingSetBytes)
{
    public static HeapSample operator +(HeapSample a, HeapSample b) => new(a.HeapBytes + b.HeapBytes, a.WorkingSetBytes + b.WorkingSetBytes);
}

/// <summary>Reads a server's <c>/stats</c> only once its heap has stopped moving.</summary>
public static class HeapSampler
{
    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(30);

    // Any certificate is accepted: the floor is sampled over https on loopback, with a certificate made at start-up.
    private static readonly HttpClient s_http = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Samples the process at <paramref name="baseUrl"/> (for example http://127.0.0.1:5001) until two samples two seconds
    /// apart are within 1%, for at most <paramref name="limit"/> (30 s by default). The limit covers the requests too: if it
    /// runs out, the last sample read is returned with a note.
    /// </summary>
    /// <exception cref="InvalidOperationException">The process does not answer, or gives no sample within the limit.</exception>
    public static async Task<HeapSample> StableAsync(string baseUrl, Action<string> log, TimeSpan? limit = null)
    {
        using CancellationTokenSource deadline = new(limit ?? s_limit);
        HeapSample previous;
        try
        {
            previous = await ReadAsync(baseUrl, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The server at {baseUrl} did not answer within {(limit ?? s_limit).TotalSeconds:N0} s.");
        }

        while (true)
        {
            HeapSample current;
            try
            {
                await Task.Delay(s_interval, deadline.Token);
                current = await ReadAsync(baseUrl, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                log($"- Note: the heap of the process at {baseUrl} was still moving when it was sampled.");
                return previous;
            }

            if (IdleChecks.IsStable(previous.HeapBytes, current.HeapBytes))
            {
                return current;
            }

            previous = current;
        }
    }

    private static async Task<HeapSample> ReadAsync(string baseUrl, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await s_http.GetStringAsync($"{baseUrl}/stats", cancellationToken);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The server at {baseUrl} is gone.", e);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return new HeapSample(document.RootElement.GetProperty("heapBytes").GetInt64(), document.RootElement.GetProperty("workingSetBytes").GetInt64());
    }
}
