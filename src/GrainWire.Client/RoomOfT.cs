using System.Collections.Immutable;
using System.Text.Json;
using GrainWire.Client.Internal;
using GrainWire.Client.Protocol;

namespace GrainWire.Client;

/// <summary>A room this client has joined, with its view read as <typeparamref name="TView"/>.</summary>
/// <typeparam name="TView">The type the server's snapshot (<c>GetSnapshot</c>) is deserialized to.</typeparam>
public sealed class Room<TView> : Room
{
    private ImmutableArray<Action<TView?, SnapshotInfo>> _snapshotHandlers = [];
    private Lazy<TView?>? _view;

    internal Room(GrainWireClient client, string type, string id, byte[]? joinData)
        : base(client, type, id, joinData)
    {
    }

    /// <summary>
    /// The latest snapshot of the room as this player may see it. Events are not applied to it: that is the app's job.
    /// Null (the default of <typeparamref name="TView"/>) before the first snapshot, and when the room's
    /// <c>GetSnapshot</c> returns nothing.
    /// </summary>
    /// <exception cref="JsonException">The snapshot cannot be read as <typeparamref name="TView"/>.</exception>
    public TView? View
    {
        get
        {
            Lazy<TView?>? view;
            lock (Client.Gate)
            {
                view = _view;
            }

            return view is null ? default : view.Value;
        }
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with every snapshot: after joining, after a reconnect, and whenever the room had
    /// to catch up. If the room already has a snapshot, the handler is called with it right away. Treat a snapshot as
    /// "this is the state now": it carries the effects of events that may never have been delivered. The view is null
    /// (the default of <typeparamref name="TView"/>) when the room's <c>GetSnapshot</c> returns nothing.
    /// </summary>
    /// <remarks>If the snapshot cannot be read as <typeparamref name="TView"/>, the handler is not called and the error goes to <see cref="GrainWireClientOptions.OnHandlerError"/>.</remarks>
    public IDisposable OnSnapshot(Action<TView?, SnapshotInfo> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Client.Gate)
        {
            _snapshotHandlers = _snapshotHandlers.Add(handler);
            if (_view is { } view && YouLocked is { } you)
            {
                SnapshotInfo info = new(MembersLocked, you);
                Client.Callbacks.Enqueue(() => handler(view.Value, info));
            }
        }

        Client.Callbacks.Drain();
        return new Subscription(() =>
        {
            lock (Client.Gate)
            {
                _snapshotHandlers = _snapshotHandlers.Remove(handler);
            }
        });
    }

    private protected override void SnapshotAppliedLocked(SnapshotEnvelope snapshot, SnapshotInfo info)
    {
        // The view is read when someone asks for it, outside the client's lock, so a view that does not fit TView fails
        // in the app's own code and not in the middle of the connection's message handling.
        JsonElement data = snapshot.Data;
        JsonSerializerOptions json = Client.Options.SerializerOptions!;
        Lazy<TView?> view = new(() => Payloads.Deserialize<TView>(data, json));
        _view = view;
        foreach (Action<TView?, SnapshotInfo> handler in _snapshotHandlers)
        {
            Client.Callbacks.Enqueue(() => handler(view.Value, info));
        }
    }
}
