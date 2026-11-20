using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GrainWire.Bench.Tests;

public class HeapSamplerTests
{
    [Fact]
    public async Task A_server_that_never_answers_fails_within_the_limit_not_the_http_timeout()
    {
        await using StatsServer server = new(answered: 0);
        Stopwatch watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<InvalidOperationException>(() => HeapSampler.StableAsync(server.BaseUrl, _ => { }, TimeSpan.FromSeconds(1)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_server_that_hangs_after_one_answer_gives_that_answer_with_a_note_within_the_limit()
    {
        await using StatsServer server = new(answered: 1);
        var notes = new List<string>();
        Stopwatch watch = Stopwatch.StartNew();

        HeapSample sample = await HeapSampler.StableAsync(server.BaseUrl, notes.Add, TimeSpan.FromSeconds(3));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(new HeapSample(1_000, 2_000), sample);
        Assert.Contains(notes, note => note.Contains("still moving", StringComparison.Ordinal));
    }

    /// <summary>Answers the first <c>answered</c> requests with a fixed sample, then accepts connections and never answers.</summary>
    private sealed class StatsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = [];
        private readonly Task _loop;

        public StatsServer(int answered)
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = RunAsync(answered);
        }

        public string BaseUrl { get; }

        private async Task RunAsync(int answered)
        {
            try
            {
                for (int i = 0; ; i++)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    if (i >= answered)
                    {
                        _held.Add(client);
                        continue;
                    }

                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[4096];
                        var request = new StringBuilder();
                        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            int read = await stream.ReadAsync(buffer);
                            if (read == 0)
                            {
                                break;
                            }

                            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }

                        const string body = """{"heapBytes":1000,"workingSetBytes":2000}""";
                        string response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                    }
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // the listener was stopped
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _loop;
            foreach (TcpClient client in _held)
            {
                client.Dispose();
            }
        }
    }
}
