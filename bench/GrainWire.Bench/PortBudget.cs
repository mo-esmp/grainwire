using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GrainWire.Bench;

/// <summary>
/// The load generator and the servers share this machine, so every connection takes one outbound (ephemeral) port, and a
/// closed connection keeps its port in TIME_WAIT for about two minutes. Windows has 16,384 of them by default: 12,000
/// connections that all leave and come back cannot get ports until the first ones are released. This waits for them.
/// </summary>
public static class PortBudget
{
    /// <summary>Kept free for the servers, the stats requests and everything else on the machine.</summary>
    public const int Margin = 512;

    private static readonly TimeSpan s_limit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_poll = TimeSpan.FromSeconds(5);

    public static bool IsEnough(int rangeSize, int inUse, int needed) => rangeSize - inUse >= needed + Margin;

    /// <summary>
    /// Waits until <paramref name="needed"/> outbound ports are free, for at most five minutes. If they are not, it goes on:
    /// the connections that fail are counted, and the run stops with that count.
    /// </summary>
    public static async Task WaitForAsync(int needed, Action<string> log)
    {
        (int start, int size) = Range();
        if (IsEnough(size, InUse(start, size), needed))
        {
            return;
        }

        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < s_limit)
        {
            await Task.Delay(s_poll);
            if (IsEnough(size, InUse(start, size), needed))
            {
                log($"- Waited {watch.Elapsed.TotalSeconds:F0} s for {needed:N0} free outbound ports (the last connections' ports were in TIME_WAIT)");
                return;
            }
        }

        log($"- Note: after {s_limit.TotalMinutes:F0} minutes only {size - InUse(start, size):N0} outbound ports were free for {needed:N0} connections.");
    }

    /// <summary>
    /// Distinct local ports in the dynamic range held by a connection in any state, TIME_WAIT included. Distinct, because
    /// on loopback the server's side of every connection has the server's own port as its local port.
    /// </summary>
    private static int InUse(int start, int size)
    {
        HashSet<int> ports = [];
        foreach (TcpConnectionInformation connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            int port = connection.LocalEndPoint.Port;
            if (port >= start && port < start + size)
            {
                ports.Add(port);
            }
        }

        return ports.Count;
    }

    /// <summary>Linux says it in /proc; Windows' default (netsh int ipv4 show dynamicport tcp) is assumed otherwise.</summary>
    private static (int Start, int Size) Range()
    {
        const string linux = "/proc/sys/net/ipv4/ip_local_port_range";
        if (File.Exists(linux))
        {
            string[] parts = File.ReadAllText(linux).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int first = int.Parse(parts[0]);
            return (first, int.Parse(parts[1]) - first + 1);
        }

        return (49152, 16384);
    }
}
