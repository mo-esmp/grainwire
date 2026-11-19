using System.Buffers;
using System.Net.WebSockets;

namespace GrainWire.Bench;

/// <summary>
/// The floor: a bare ASP.NET Core WebSocket endpoint with sub-protocol <c>mqtt</c>, keep-alive off, that parks every idle
/// connection in a zero-byte read. What an idle connection costs here, any WebSocket server on ASP.NET Core pays.
/// </summary>
public static class FloorServer
{
    private const int BufferBytes = 256;

    /// <summary>Maps the bare endpoint at /mqtt. The process is the "floor" mode.</summary>
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

    /// <summary>
    /// An idle connection waits in a zero-byte read and holds no buffer of ours. Only when something arrives is a buffer
    /// rented, read into and returned; a Close is answered and ends the loop.
    /// </summary>
    private static async Task WaitUntilClosed(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult waited = await socket.ReceiveAsync(Memory<byte>.Empty, cancellationToken);
                if (waited.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                    return;
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
                try
                {
                    ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(0, BufferBytes), cancellationToken);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                        return;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // The client went away.
        }
    }
}
