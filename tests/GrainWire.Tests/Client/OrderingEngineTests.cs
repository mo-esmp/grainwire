using GrainWire.Client;
using GrainWire.Client.Protocol;

namespace GrainWire.Tests.Client;

/// <summary>The .NET client's ordering engine. Mirrors clients/ts/test/ordering.test.ts, so both clients are held to the same behaviour.</summary>
public class OrderingEngineTests
{
    private const string Room = "counter/r1";
    private static readonly OrderingOptions s_options = new();

    private static EventEnvelope Event(long seq, string se = "e1", string ri = "i1") =>
        new() { Type = "changed", Room = Room, Incarnation = ri, Epoch = se, Seq = seq };

    private static SnapshotEnvelope Snapshot(long at, string se = "e1", string ri = "i1") =>
        new() { Type = "$snapshot", Room = Room, Incarnation = ri, Epoch = se, At = at, Members = [], You = new Member("p", null, MemberStatus.Online) };

    /// <summary>What the engine asked for, in short: "S41,42,43" is a snapshot at 41, then events 42 and 43.</summary>
    private static string Seqs(IReadOnlyList<OrderingAction> actions) => string.Join(",", actions.Select(action => action switch
    {
        EventAction e => e.Event.Seq.ToString(),
        SnapshotAction s => "S" + s.Snapshot.At,
        _ => "sync",
    }));

    [Fact]
    public void Delivers_events_in_order_after_the_snapshot_and_drops_duplicates()
    {
        OrderingEngine engine = new(s_options, 0);
        Assert.Equal("S0", Seqs(engine.ReceiveSnapshot(Snapshot(0), 0)));
        Assert.Equal("1", Seqs(engine.ReceiveEvent(Event(1), 1)));
        Assert.Equal("", Seqs(engine.ReceiveEvent(Event(1), 2)));
        Assert.Equal("2", Seqs(engine.ReceiveEvent(Event(2), 3)));
        Assert.Equal(new StreamPosition("i1", "e1", 2), engine.Current);
    }

    [Fact]
    public void Holds_events_that_arrive_before_the_snapshot_and_drops_the_ones_the_snapshot_already_covers()
    {
        OrderingEngine engine = new(s_options, 0);
        Assert.Empty(engine.ReceiveEvent(Event(41), 0));
        Assert.Empty(engine.ReceiveEvent(Event(42), 0));
        Assert.Empty(engine.ReceiveEvent(Event(43), 0));
        Assert.Equal("S41,42,43", Seqs(engine.ReceiveSnapshot(Snapshot(41), 1)));
    }

    [Fact]
    public void Puts_reordered_events_back_in_order_without_asking_the_server()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(0), 0);
        Assert.Empty(engine.ReceiveEvent(Event(2), 10));
        Assert.Empty(engine.ReceiveEvent(Event(3), 20));
        Assert.Empty(engine.Tick(100)); // still within the gap wait
        Assert.Equal("1,2,3", Seqs(engine.ReceiveEvent(Event(1), 120)));
        Assert.Empty(engine.Tick(1_000));
    }

    [Fact]
    public void Asks_for_a_sync_when_a_gap_does_not_close_and_not_more_often_than_the_retry_interval()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(0), 0);
        engine.ReceiveEvent(Event(2), 10);
        Assert.Empty(engine.Tick(200));
        Assert.Equal("sync", Seqs(engine.Tick(260)));
        Assert.Empty(engine.Tick(300));
        Assert.Equal("sync", Seqs(engine.Tick(2_300)));

        // The snapshot that answers the sync covers the hole.
        Assert.Equal("S2", Seqs(engine.ReceiveSnapshot(Snapshot(2), 2_400)));
        Assert.Empty(engine.Tick(10_000));
        Assert.Equal("3", Seqs(engine.ReceiveEvent(Event(3), 10_001)));
    }

    [Fact]
    public void Checks_a_silent_room_against_the_server()
    {
        // A lost final event: no gap would ever reveal it.
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(41), 0);
        Assert.Empty(engine.Tick(14_999));
        Assert.Equal("sync", Seqs(engine.Tick(15_000)));
        engine.ConfirmInSync(15_050);
        Assert.Empty(engine.Tick(20_000));
        Assert.Equal("sync", Seqs(engine.Tick(30_050)));
    }

    [Fact]
    public void Asks_for_a_sync_when_the_first_snapshot_never_arrives()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ExpectBaseline(0);
        Assert.Empty(engine.Tick(100));
        Assert.Equal("sync", Seqs(engine.Tick(300)));
        Assert.False(engine.Ready);
    }

    [Fact]
    public void Switches_to_a_new_stream_epoch_only_through_a_snapshot()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(5), 0);
        Assert.Empty(engine.ReceiveEvent(Event(1, "e2"), 10));
        Assert.Empty(engine.ReceiveEvent(Event(2, "e2"), 11));
        Assert.Equal("sync", Seqs(engine.Tick(300)));
        Assert.Equal("S0,1,2", Seqs(engine.ReceiveSnapshot(Snapshot(0, "e2"), 320)));

        // A late event of the old epoch is no longer deliverable.
        Assert.Empty(engine.ReceiveEvent(Event(6, "e1"), 330));
        Assert.Equal(new StreamPosition("i1", "e2", 2), engine.Current);
    }

    [Fact]
    public void Switches_to_a_new_room_incarnation_only_through_a_snapshot()
    {
        // The room was closed and created again under the same id: positions start over, like after a restart.
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(5), 0);
        Assert.Empty(engine.ReceiveEvent(Event(1, "e9", "i2"), 10));
        Assert.Equal("sync", Seqs(engine.Tick(300)));
        Assert.Equal("S0,1", Seqs(engine.ReceiveSnapshot(Snapshot(0, "e9", "i2"), 320)));
        Assert.Empty(engine.ReceiveEvent(Event(6), 330));
        Assert.Equal(new StreamPosition("i2", "e9", 1), engine.Current);
    }

    [Fact]
    public void Ignores_an_older_snapshot_of_the_stream_it_is_already_following()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(0), 0);
        engine.ReceiveEvent(Event(1), 1);
        engine.ReceiveEvent(Event(2), 2);
        Assert.Empty(engine.ReceiveSnapshot(Snapshot(1), 3));
        Assert.Equal(2, engine.Current.Seq);
        Assert.Equal("S2", Seqs(engine.ReceiveSnapshot(Snapshot(2), 4)));
    }

    [Theory]
    [InlineData("e2", "i1")] // the room restarted: a new stream epoch
    [InlineData("e9", "i2")] // the room was closed and created again: a new incarnation
    public void Refuses_a_late_snapshot_of_a_stream_it_has_left(string se, string ri)
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(0), 0);
        Assert.Equal("1", Seqs(engine.ReceiveEvent(Event(1), 1)));
        Assert.Equal("S0", Seqs(engine.ReceiveSnapshot(Snapshot(0, se, ri), 10)));
        Assert.Equal("1", Seqs(engine.ReceiveEvent(Event(1, se, ri), 11)));

        // A snapshot of the old stream that was still on its way does not bring that stream back,
        Assert.Empty(engine.ReceiveSnapshot(Snapshot(0), 20));

        // so a late copy of an event the app already has cannot be delivered a second time.
        Assert.Empty(engine.ReceiveEvent(Event(1), 21));
        Assert.Equal(new StreamPosition(ri, se, 1), engine.Current);
        Assert.Equal("2", Seqs(engine.ReceiveEvent(Event(2, se, ri), 22)));
    }

    [Fact]
    public void Returns_to_a_stream_it_left_when_the_server_answers_a_sync_with_a_snapshot_of_it()
    {
        // The client followed e2 and then took a late snapshot of e1, an epoch it had never seen, for a restart.
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(4, "e2"), 0);
        Assert.Equal("S7", Seqs(engine.ReceiveSnapshot(Snapshot(7, "e1"), 10)));

        // An event of e2 shows that something is wrong. The server answers the sync with e2's snapshot and "resynced".
        Assert.Empty(engine.ReceiveEvent(Event(6, "e2"), 20));
        Assert.Equal("sync", Seqs(engine.Tick(300)));
        long asked = engine.SnapshotsReceived;
        Assert.Empty(engine.ReceiveSnapshot(Snapshot(5, "e2"), 320));
        Assert.Equal("S5,6", Seqs(engine.ConfirmResynced(asked, now: 321)));
        Assert.Equal(new StreamPosition("i1", "e2", 6), engine.Current);

        // Now e1 is the stream that was left.
        Assert.Empty(engine.ReceiveSnapshot(Snapshot(7, "e1"), 330));
        Assert.Empty(engine.Tick(5_000));
    }

    [Fact]
    public void Stays_away_from_a_stream_it_left_when_the_sync_was_answered_with_another_snapshot()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(0), 0);
        engine.ReceiveSnapshot(Snapshot(0, "e2"), 10);

        // Refused before the sync was sent: it cannot be the answer, even when no time passed in between.
        Assert.Empty(engine.ReceiveSnapshot(Snapshot(0), 20));
        Assert.Empty(engine.ConfirmResynced(engine.SnapshotsReceived, now: 20));

        // The answer was a snapshot of the stream the client follows; a late one of the old stream came right after it.
        long asked = engine.SnapshotsReceived;
        Assert.Equal("S3", Seqs(engine.ReceiveSnapshot(Snapshot(3, "e2"), 220)));
        Assert.Empty(engine.ReceiveSnapshot(Snapshot(0), 225));
        Assert.Empty(engine.ConfirmResynced(asked, now: 230));
        Assert.Equal(new StreamPosition("i1", "e2", 3), engine.Current);
    }

    [Fact]
    public void Forgets_held_events_of_an_older_stream_when_the_server_says_the_client_is_in_sync()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(3, "e2"), 0);
        engine.ReceiveEvent(Event(9, "e1"), 10); // a late event of the epoch before
        Assert.Equal("sync", Seqs(engine.Tick(300)));
        engine.ConfirmInSync(320);

        // The stale event is gone, so the client does not keep asking.
        Assert.Empty(engine.Tick(5_000));
        Assert.Empty(engine.Tick(10_000));
    }

    [Fact]
    public void Stops_asking_after_a_failover_however_often_the_server_answers_in_sync()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(5, "e1"), 0);
        engine.ReceiveSnapshot(Snapshot(2, "e2"), 10); // the room restarted
        engine.ReceiveEvent(Event(6, "e1"), 20); // a late event of the old epoch

        // One minute of ticks, every sync answered with "in-sync".
        int syncs = 0;
        for (long now = 100; now <= 60_000; now += 100)
        {
            if (engine.Tick(now).Count > 0)
            {
                syncs++;
                engine.ConfirmInSync(now + 10);
            }
        }

        // One sync to find out about the held event, and then only the regular check of a silent room (every 15 s).
        Assert.InRange(syncs, 1, 4);
        Assert.Equal(new StreamPosition("i1", "e2", 2), engine.Current);
    }

    [Fact]
    public void Keeps_later_events_of_the_current_stream_when_the_server_says_the_client_is_in_sync()
    {
        OrderingEngine engine = new(s_options, 0);
        engine.ReceiveSnapshot(Snapshot(3), 0);
        engine.ReceiveEvent(Event(5), 10); // sent after the server answered the sync
        engine.ConfirmInSync(20);
        Assert.Equal("4,5", Seqs(engine.ReceiveEvent(Event(4), 30)));
        Assert.Empty(engine.Tick(5_000));
    }

    [Fact]
    public void Holds_no_more_events_than_its_limit()
    {
        OrderingEngine engine = new(s_options with { MaxBuffered = 3 }, 0);
        engine.ReceiveSnapshot(Snapshot(0), 0);
        for (int seq = 2; seq <= 6; seq++)
        {
            engine.ReceiveEvent(Event(seq), seq);
        }

        // 2 and 3 were pushed out, so the missing event alone cannot close the gap any more: a snapshot has to.
        Assert.Equal("1", Seqs(engine.ReceiveEvent(Event(1), 10)));
        Assert.Equal("sync", Seqs(engine.Tick(300)));
        Assert.Equal("S3,4,5,6", Seqs(engine.ReceiveSnapshot(Snapshot(3), 320)));
    }

    // ---------------------------------------------------------------- randomized network behaviour

    /// <summary>
    /// The same property the TypeScript client checks with fast-check, over seeded random runs: whatever the network
    /// drops, duplicates or delays (by seconds, too), events are never delivered twice or out of order, and the client
    /// ends where the room is.
    /// </summary>
    [Fact]
    public void Never_delivers_an_event_twice_or_out_of_order_and_always_converges()
    {
        for (int seed = 1; seed <= 300; seed++)
        {
            Random random = new(seed);
            List<Step> steps = [];
            int count = random.Next(1, 81);
            for (int i = 0; i < count; i++)
            {
                // Op 0 = the room restarts, otherwise an event. One step in four is late by up to six seconds.
                steps.Add(new Step(random.Next(0, 15), random.Next(0, 4) == 0, random.Next(0, 2) == 0, random.Next(0, 401), random.Next(0, 4) == 0 ? random.Next(1, 6_001) : 0));
            }

            Simulation result = Simulation.Run(steps);

            // Between two baselines, events are consecutive, starting right after the baseline.
            for (int b = 0; b < result.Baselines.Count; b++)
            {
                (string epoch, long at, int from) = result.Baselines[b];
                int to = b + 1 < result.Baselines.Count ? result.Baselines[b + 1].Index : result.Delivered.Count;
                long expected = at + 1;
                for (int i = from; i < to; i++)
                {
                    Assert.True(result.Delivered[i] == (epoch, expected), $"Seed {seed}: delivery {i} was {result.Delivered[i]}, expected ({epoch}, {expected}).");
                    expected++;
                }
            }

            // Across baselines too: no event reaches the app a second time.
            Assert.True(result.Delivered.Distinct().Count() == result.Delivered.Count, $"Seed {seed}: an event was delivered twice.");

            // Nothing is delivered before the first baseline.
            if (result.Baselines.Count > 0)
            {
                Assert.True(result.Baselines[0].Index == 0, $"Seed {seed}: events were delivered before the first snapshot.");
            }
            else
            {
                Assert.True(result.Delivered.Count == 0, $"Seed {seed}: events were delivered without a snapshot.");
            }

            // Eventual convergence: the client ends where the room is.
            Assert.True(result.FinalClient == result.FinalServer, $"Seed {seed}: the client ended at {result.FinalClient}, the room at {result.FinalServer}.");
        }
    }

    /// <param name="Op">0 = the room restarts, otherwise an event.</param>
    /// <param name="Drop">The packet is lost.</param>
    /// <param name="Duplicate">A copy of the packet arrives after it.</param>
    /// <param name="Delay">How long the packet takes, in milliseconds.</param>
    /// <param name="Late">How much longer than a few milliseconds the copy takes. Mostly 0.</param>
    private sealed record Step(int Op, bool Drop, bool Duplicate, int Delay, int Late);

    /// <summary>
    /// A room that produces events (and sometimes restarts into a new epoch) and a network that may drop, duplicate and
    /// delay anything. Sync requests are answered with a snapshot and then "resynced", which travel the same network.
    /// </summary>
    private sealed class Simulation
    {
        private readonly OrderingEngine _engine = new(s_options, 0);
        private readonly List<(long At, long Order, Func<long, IReadOnlyList<OrderingAction>> Arrive)> _inFlight = [];
        private Step _step = new(1, false, false, 0, 0);
        private long _now;
        private long _sent;
        private int _syncs;
        private int _epoch = 1;
        private long _position;

        public List<(string Epoch, long Seq)> Delivered { get; } = [];

        public List<(string Epoch, long At, int Index)> Baselines { get; } = [];

        public (string? Epoch, long Seq) FinalClient => (_engine.Current.Epoch, _engine.Current.Seq);

        public (string? Epoch, long Seq) FinalServer => ($"e{_epoch}", _position);

        public static Simulation Run(List<Step> steps)
        {
            Simulation simulation = new();

            // The join: its snapshot may be lost like anything else.
            simulation._engine.ExpectBaseline(0);
            simulation.Transmit(Snapshot(0), steps[0], 5);

            foreach (Step step in steps)
            {
                simulation._step = step;
                if (step.Op == 0)
                {
                    // The room restarts: new epoch, positions start over. Clients learn of it through a later event or sync.
                    simulation._epoch++;
                    simulation._position = 0;
                }
                else
                {
                    simulation._position++;
                    simulation.Transmit(Event(simulation._position, $"e{simulation._epoch}"), step, step.Delay);
                }

                simulation.Advance(simulation._now + 20, reliable: false);
            }

            // The network calms down; give the engine time to notice anything it missed (idle sync included).
            simulation.Advance(simulation._now + 40_000, reliable: true);
            return simulation;
        }

        private void Transmit(InboxEnvelope packet, Step step, int delay)
        {
            if (step.Drop)
            {
                return;
            }

            Send(delay, Arrival(packet));
            if (step.Duplicate)
            {
                Send(delay + 7 + step.Late, Arrival(packet));
            }
        }

        private Func<long, IReadOnlyList<OrderingAction>> Arrival(InboxEnvelope packet) => now =>
            packet is EventEnvelope @event ? _engine.ReceiveEvent(@event, now) : _engine.ReceiveSnapshot((SnapshotEnvelope)packet, now);

        private void Send(long delay, Func<long, IReadOnlyList<OrderingAction>> arrive) => _inFlight.Add((_now + delay, _sent++, arrive));

        /// <summary>
        /// The server answers a sync with a snapshot and then "resynced". Until the network calms down the answer may take
        /// seconds, or a copy of its snapshot may arrive seconds later. Like the client, the simulation waits for one
        /// answer at a time, and not for longer than five seconds.
        /// </summary>
        private void AnswerSync(bool reliable)
        {
            int id = ++_syncs;
            long askedAt = _now;
            long snapshotsBefore = _engine.SnapshotsReceived;
            SnapshotEnvelope answer = Snapshot(_position, $"e{_epoch}");
            long delay = reliable ? 1 : _step.Duplicate ? 30 : 30 + _step.Late;
            Send(delay, Arrival(answer));
            Send(delay, now => id == _syncs && now - askedAt < 5_000 ? _engine.ConfirmResynced(snapshotsBefore, now) : []);
            if (!reliable && _step.Duplicate)
            {
                Send(delay + 7 + _step.Late, Arrival(answer));
            }
        }

        private void Apply(IReadOnlyList<OrderingAction> actions, bool reliable)
        {
            foreach (OrderingAction action in actions)
            {
                switch (action)
                {
                    case EventAction e:
                        Delivered.Add((e.Event.Epoch, e.Event.Seq));
                        break;
                    case SnapshotAction s:
                        Baselines.Add((s.Snapshot.Epoch, s.Snapshot.At, Delivered.Count));
                        break;
                    default:
                        AnswerSync(reliable);
                        break;
                }
            }
        }

        private void Advance(long to, bool reliable)
        {
            while (_now < to)
            {
                _now += 10;
                while (true)
                {
                    // Earliest arrival first; packets that arrive together keep the order they were sent in.
                    int next = -1;
                    for (int i = 0; i < _inFlight.Count; i++)
                    {
                        if (_inFlight[i].At <= _now && (next < 0 || (_inFlight[i].At, _inFlight[i].Order).CompareTo((_inFlight[next].At, _inFlight[next].Order)) < 0))
                        {
                            next = i;
                        }
                    }

                    if (next < 0)
                    {
                        break;
                    }

                    Func<long, IReadOnlyList<OrderingAction>> arrive = _inFlight[next].Arrive;
                    _inFlight.RemoveAt(next);
                    Apply(arrive(_now), reliable);
                }

                Apply(_engine.Tick(_now), reliable);
            }
        }
    }
}
