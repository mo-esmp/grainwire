namespace GrainWire.Bench;

/// <summary>Bytes as a line over the number of connections.</summary>
/// <param name="PerConnection">The growth per added connection: the slope of the line.</param>
/// <param name="Fixed">The line's value at zero connections.</param>
public readonly record struct Fit(double PerConnection, double Fixed);

/// <summary>"Per connection" as the slope between two connection counts, never bytes ÷ connections.</summary>
public static class Slope
{
    /// <summary>The line through two measurements, taken at two different connection counts.</summary>
    /// <exception cref="ArgumentException">Both measurements are at the same count.</exception>
    public static Fit Between(int smallCount, long smallBytes, int largeCount, long largeBytes)
    {
        if (smallCount == largeCount)
        {
            throw new ArgumentException($"Two runs at the same count ({smallCount}) cannot give a slope.", nameof(largeCount));
        }

        if (smallCount > largeCount)
        {
            (smallCount, smallBytes, largeCount, largeBytes) = (largeCount, largeBytes, smallCount, smallBytes);
        }

        double perConnection = (double)(largeBytes - smallBytes) / (largeCount - smallCount);
        return new Fit(perConnection, smallBytes - (perConnection * smallCount));
    }
}
