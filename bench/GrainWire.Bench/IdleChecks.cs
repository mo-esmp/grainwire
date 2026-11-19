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

    /// <summary>Two heap samples are stable when they differ by less than 1% of the larger.</summary>
    public static bool IsStable(long first, long second) =>
        Math.Abs(first - second) <= Math.Max(first, second) / 100.0;
}
