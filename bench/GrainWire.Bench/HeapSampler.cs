using System.Diagnostics;
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

    /// <summary>Samples the process at <paramref name="baseUrl"/> (for example http://127.0.0.1:5001) until two samples two seconds apart are within 1%, for at most 30 s.</summary>
    /// <exception cref="InvalidOperationException">The process does not answer.</exception>
    public static async Task<HeapSample> StableAsync(string baseUrl, Action<string> log)
    {
        Stopwatch watch = Stopwatch.StartNew();
        HeapSample previous = await ReadAsync(baseUrl);
        while (true)
        {
            await Task.Delay(s_interval);
            HeapSample current = await ReadAsync(baseUrl);
            if (IdleChecks.IsStable(previous.HeapBytes, current.HeapBytes))
            {
                return current;
            }

            if (watch.Elapsed >= s_limit)
            {
                log($"- Note: the heap of the process at {baseUrl} was still moving when it was sampled.");
                return current;
            }

            previous = current;
        }
    }

    private static async Task<HeapSample> ReadAsync(string baseUrl)
    {
        string json;
        try
        {
            json = await s_http.GetStringAsync($"{baseUrl}/stats");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"The server at {baseUrl} is gone.", e);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return new HeapSample(document.RootElement.GetProperty("heapBytes").GetInt64(), document.RootElement.GetProperty("workingSetBytes").GetInt64());
    }
}
