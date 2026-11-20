using System.Globalization;

namespace GrainWire.Bench;

/// <summary>The conditions under which the idle workload's figures mean something.</summary>
public static class IdleChecks
{
    /// <summary>Stops the run unless every one of <paramref name="expected"/> succeeded: figures for a partial population are never reported.</summary>
    /// <exception cref="InvalidOperationException">Fewer than expected.</exception>
    public static void RequireAll(string what, int actual, int expected)
    {
        if (actual < expected)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Only {actual:N0} of {expected:N0} {what}. Lower --connections, or look at the server's output."));
        }
    }

    /// <summary>Stops the run if a command of the activity phase failed, because then the connections were not active.</summary>
    /// <exception cref="InvalidOperationException">At least one command failed.</exception>
    public static void RequireNoFailures(int failed)
    {
        if (failed > 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{failed:N0} commands failed in the activity phase; the connections were not active."));
        }
    }

    /// <summary>
    /// Stops the run unless the activity phase's deliveries arrived, and returns how many never did. The gateway publishes
    /// at QoS 0 and skips a batch that comes more than 250 ms late (docs/guarantees.md: not every event is guaranteed), so a
    /// small loss is tolerated and reported: at most 0.1% of all deliveries, and at most a tenth of any one client's. Each
    /// client is checked on its own, so a total that adds up cannot hide a client that received nothing.
    /// </summary>
    /// <returns>The deliveries that never arrived.</returns>
    /// <exception cref="InvalidOperationException">More was lost than is tolerated.</exception>
    public static long RequireDeliveries(IReadOnlyList<long> receivedPerClient, long expectedEach)
    {
        // A client's surplus does not make up for another's shortfall.
        long[] missingPerClient = [.. receivedPerClient.Select(received => Math.Max(0, expectedEach - received))];
        long missing = missingPerClient.Sum();
        long expected = expectedEach * receivedPerClient.Count;
        long worst = missingPerClient.DefaultIfEmpty(0).Max();
        long perClientTolerated = expectedEach / 10;
        if (missing * 1_000 > expected || worst > perClientTolerated)
        {
            int shortClients = missingPerClient.Count(m => m > 0);
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{missing:N0} of {expected:N0} deliveries never arrived in the activity phase; {shortClients:N0} of {receivedPerClient.Count:N0} clients are short, the worst by {worst:N0} of {expectedEach:N0}. At most 0.1% in all and {perClientTolerated:N0} per client are tolerated: the fan-out was not exercised."));
        }

        return missing;
    }

    /// <summary>Two heap samples are stable when they differ by less than 1% of the larger.</summary>
    public static bool IsStable(long first, long second) =>
        Math.Abs(first - second) <= Math.Max(first, second) / 100.0;
}
