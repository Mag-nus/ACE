using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace NetE2E
{
    public static class BehaveMode
    {
        static void SpinUntil(Stopwatch sw, double ms) { while (sw.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(50); }

        static Client Fresh()
        {
            var c = Env.NewClient(smallRcv: false);
            c.S.TickOutbound();          // the initial TimeSync goes out
            Env.PushTimers(c);           // accessor: no ack / time-sync due unless the experiment says so
            c.Drain();
            Thread.Sleep(10);            // let the 5 ms gate opened by the TimeSync expire
            return c;
        }

        public static void Run(string[] args)
        {
            RunGroups();
            RunSequences();
            RunInvariant();
        }

        // ------------------------------------------------------------------------------------------------
        static void RunGroups()
        {
            U.H("(4a) 'Only one message group's bundle is sent per 5 ms window per session' - observed on the wire (client socket) from the REAL NetworkSession.Update()");
            Console.WriteLine("    No ACE message type uses WeenieQueue; the groups with real message types are DatabaseQueue(5), LoginQueue(4), SecureWeenieQueue(7), UIQueue(9), SmartboxQueue(10).");
            var wo = Stubs.MakeWorldObject();
            GameMessage ui() => new GameMessageSystemChat("hello", ChatMessageType.Broadcast);   // UIQueue (9)
            GameMessage sb() => new GameMessageSound(wo.Guid, Sound.Attack1, 1.0f);              // SmartboxQueue (10)
            GameMessage db() => new GameMessageDDDEndDDD();                                      // DatabaseQueue (5)

            // --- experiment 1: three groups, enqueued Smartbox first, then UI, then Database; Update() every 1 ms; nextSend untouched ---
            {
                var c = Fresh();
                Console.WriteLine();
                Console.WriteLine("  Experiment 1: EnqueueSend(Smartbox msg), EnqueueSend(UI msg), EnqueueSend(Database msg) at t=0 (in that order); Update() called every ~1 ms; nextSend NOT touched.");
                c.Net.EnqueueSend(sb()); c.Net.EnqueueSend(ui()); c.Net.EnqueueSend(db());
                Timeline(c, 30, null);
                Env.Remove(c);
            }
            // --- experiment 2: ack + echo due as well ---
            {
                var c = Fresh();
                Console.WriteLine();
                Console.WriteLine("  Experiment 2: as experiment 1, plus an EchoRequest was received (FlagEcho) and the 2 s ack timer is due (group 0 = InvalidQueue carries ack/echo/time-sync).");
                c.Net.EnqueueSend(sb()); c.Net.EnqueueSend(ui()); c.Net.EnqueueSend(db());
                Acc.FlagEcho(c.Net, 99.5f); Acc.NextAck(c.Net) = default;
                Timeline(c, 30, null);
                Env.Remove(c);
            }
            // --- experiment 3: starvation ---
            {
                var c = Fresh();
                Console.WriteLine();
                Console.WriteLine("  Experiment 3 (starvation): ONE Smartbox message (movement queue) enqueued at t=0; a UI message (chat) is enqueued every 1 ms for 300 ms, then stops. Update() every ~1 ms.");
                c.Net.EnqueueSend(sb());
                var sw = Stopwatch.StartNew(); int call = 0; int uiPackets = 0, uiFrags = 0; double sbAt = -1; int sbCall = -1; double lastUi = 0;
                while (sw.Elapsed.TotalMilliseconds < 400 && sbAt < 0)
                {
                    double t = sw.Elapsed.TotalMilliseconds;
                    if (t < 300) c.Net.EnqueueSend(ui());
                    c.Net.Update();
                    foreach (var d in c.Drain())
                    {
                        if (d.Queues != null && d.Queues.Contains("Smartbox")) { sbAt = t; sbCall = call; }
                        else { uiPackets++; uiFrags += d.Frags; lastUi = t; }
                    }
                    call++;
                    SpinUntil(sw, call * 1.0);
                }
                Console.WriteLine($"    UI packets sent before the Smartbox packet: {uiPackets} (carrying {uiFrags} UI messages), last at t={lastUi:F1} ms");
                Console.WriteLine(sbAt >= 0 ? $"    Smartbox packet sent at t={sbAt:F1} ms (Update call #{sbCall}) - {sbAt:F0} ms after it was enqueued" : "    Smartbox packet NOT sent within 400 ms");
                Env.Remove(c);
            }
            // --- experiment 4: how many groups go out in one Update() when the gate is open ---
            {
                var c = Fresh();
                Console.WriteLine();
                Console.WriteLine("  Experiment 4: gate open (nextSend in the past), three groups queued, ONE Update() call:");
                c.Net.EnqueueSend(sb()); c.Net.EnqueueSend(ui()); c.Net.EnqueueSend(db());
                c.Net.Update();
                var d = c.Drain();
                Console.WriteLine($"    datagrams sent by that single call: {d.Count}  -> {string.Join(" ; ", d.Select(x => x.ToString()))}");
                Env.Remove(c);
            }
        }

        static void Timeline(Client c, int calls, Action<int> perCall)
        {
            var sw = Stopwatch.StartNew();
            for (int call = 0; call < calls; call++)
            {
                perCall?.Invoke(call);
                double t = sw.Elapsed.TotalMilliseconds;
                c.Net.Update();
                var d = c.Drain();
                foreach (var x in d) Console.WriteLine($"    Update call #{call,2} at t={t,6:F2} ms  ->  {x}");
                SpinUntil(sw, (call + 1) * 1.0);
            }
        }

        // ------------------------------------------------------------------------------------------------
        static void Show(Client c, string what, List<Dgram> sent = null)
        {
            sent ??= c.Drain();
            Console.WriteLine($"    {what,-78} wire: {(sent.Count == 0 ? "(nothing)" : string.Join(" ; ", sent.Select(d => $"seq={d.Seq} [{Client.ShortFlags(d.Flags)}]")))}");
            Console.WriteLine($"    {"",-78} PacketSequence.CurrentValue={c.CurSeq}  cachedPackets={c.Keys()}");
        }

        static void Data(Client c) { c.Net.EnqueueSend(new GameMessageSystemChat("data", ChatMessageType.Broadcast)); Acc.NextSend(c.Net) = default; c.Net.Update(); }
        static void AckOnly(Client c) { Acc.NextAck(c.Net) = default; Acc.NextSend(c.Net) = default; c.Net.Update(); }
        static void ClientAck(Client c, uint ack) { var buf = new byte[1024]; int n = c.BuildC2S(InKind.AckClear, ack, buf, 0); c.Deliver(buf, n); }

        static void RunSequences()
        {
            U.H("(4b) Sequence numbering and caching of ack-only packets on the REAL NetworkSession (wire = what the client socket received; key legend E=EncryptedChecksum F=BlobFragments A=AckSequence T=TimeSync e=EchoResponse N=RequestRetransmit)");
            var c = Env.NewClient(smallRcv: false);
            Show(c, "after the handshake ConnectRequest (flags ConnectRequest, sequence 0, never cached)");
            c.S.TickOutbound(); Env.PushTimers(c);
            Show(c, "first tick: TimeSync bundle");
            Data(c); Data(c); Data(c);
            Show(c, "3 data packets (one SystemChat each)");
            AckOnly(c);
            Show(c, "ack-only bundle #1 (2 s timer due, nothing else)");
            var before = c.Cache[c.CurSeq];
            AckOnly(c); AckOnly(c);
            Show(c, "ack-only bundles #2 and #3");
            Console.WriteLine($"      -> cached entry at key {c.CurSeq} is still the original data packet: {ReferenceEquals(before, c.Cache[c.CurSeq])}");

            ClientAck(c, c.CurSeq);
            Show(c, $"client ACKs {c.CurSeq} (= last sequence it received; real inbound path -> AcknowledgeSequence)");
            AckOnly(c);
            Show(c, "ack-only bundle after that");
            Console.WriteLine($"      -> key {c.CurSeq} still the data packet: {ReferenceEquals(before, c.Cache[c.CurSeq])}");

            Console.WriteLine();
            Console.WriteLine("    -- case A: the client acks one PAST the last sequence (client-supplied value, nothing clamps it) --");
            ClientAck(c, c.CurSeq + 1);
            Show(c, $"client ACKs {c.CurSeq + 1} (CurrentValue + 1)");
            AckOnly(c);
            Show(c, "ack-only bundle");
            Console.WriteLine($"      -> the ACK-ONLY packet is now cached under key {c.CurSeq} (flags [{Client.ShortFlags(c.Cache[c.CurSeq].Header.Flags)}]), i.e. BELOW the next data sequence and below an ack-walk's 'lowest' pointer ({c.CurSeq + 1})");
            Data(c);
            Show(c, "next data packet");
            AckOnly(c);
            Show(c, "ack-only bundle");
            ClientAck(c, c.CurSeq);
            Show(c, $"client ACKs {c.CurSeq}");

            Console.WriteLine();
            Console.WriteLine("    -- case B: the 120 s time prune empties the cache (simulated: cached Header.Time moved 200 s back, lastCachedPacketPruneTime reset; both otherwise untouched) --");
            foreach (var p in c.Cache.Values) p.Header.Time = (ushort)(p.Header.Time - 200);
            Acc.LastPrune(c.Net) = default;
            c.Net.Update();
            Show(c, "Update() -> PruneCachedPackets");
            AckOnly(c);
            Show(c, "ack-only bundle");
            Console.WriteLine($"      -> key {c.CurSeq} holds flags [{(c.Cache.TryGetValue(c.CurSeq, out var pp) ? Client.ShortFlags(pp.Header.Flags) : "none")}]: an ack-only packet inserted at the CURRENT sequence after everything below and including it had been pruned");
            Data(c);
            Show(c, "next data packet");

            Console.WriteLine();
            Console.WriteLine("    -- other optional-header bundles --");
            Acc.FlagEcho(c.Net, 12.5f); Acc.NextSend(c.Net) = default; c.Net.Update();
            Show(c, "echo-only bundle (EchoRequest received)");
            Acc.FlagEcho(c.Net, 13.5f); AckOnly(c);
            Show(c, "ack + echo bundle");
            Acc.NextResync(c.Net) = default; Acc.NextSend(c.Net) = default; c.Net.Update(); Acc.NextResync(c.Net) = Env.Far;
            Show(c, "time-sync bundle (20 s timer)");
            Acc.NextResync(c.Net) = default; AckOnly(c); Acc.NextResync(c.Net) = Env.Far;
            Show(c, "ack + time-sync bundle");

            Console.WriteLine();
            Console.WriteLine("    -- server NAK (RequestRetransmit) --");
            {
                // a client packet 3 ahead of the expected sequence makes the real code request a retransmit
                var buf = new byte[1024];
                c.NextSeq += 3;
                int n = c.BuildC2S(InKind.AckEnc, 1, buf, 0);
                c.Deliver(buf, n);
                Acc.NextSend(c.Net) = default; c.Net.Update();
                Show(c, "client packet arrives 3 sequences early -> DoRequestForRetransmission -> Update()");
            }
            Env.Remove(c);
        }

        // ------------------------------------------------------------------------------------------------
        static void RunInvariant()
        {
            U.H("(4b) invariant check: random workload on a REAL session, cachedPackets keys examined after every step");
            foreach (string policy in new[] { "well-behaved client (acks <= last sequence it was sent)", "client may ack CurrentValue+1" })
            {
                var c = Fresh();
                var rnd = new Random(12345);
                int steps = 30000, nonContig = 0, belowLowest = 0, ackOnlyCached = 0, maxNotCur = 0;
                uint lowestWalk = 2; // what an NT.7 "walk from lowestCached" implementation would hold: lowest key not yet acknowledged
                string firstViolation = null;
                for (int i = 0; i < steps; i++)
                {
                    int op = rnd.Next(100);
                    string what;
                    if (op < 55) { Data(c); what = "data"; }
                    else if (op < 75) { AckOnly(c); what = "ack-only"; }
                    else if (op < 80) { Acc.FlagEcho(c.Net, 1f); Acc.NextSend(c.Net) = default; c.Net.Update(); what = "echo"; }
                    else
                    {
                        uint cur = c.CurSeq;
                        uint ack = policy.StartsWith("well") ? (uint)rnd.Next((int)Math.Max(2, cur - 6), (int)cur + 1) : (uint)rnd.Next((int)Math.Max(2, cur - 6), (int)cur + 2);
                        ClientAck(c, ack); what = "client ack " + ack;
                        if (ack > lowestWalk) lowestWalk = ack;
                    }
                    var keys = c.Cache.Keys.OrderBy(x => x).ToArray();
                    bool bad = false;
                    if (keys.Length > 0)
                    {
                        if (keys[^1] - keys[0] + 1 != keys.Length) { nonContig++; bad = true; }
                        if (keys[0] < lowestWalk) { belowLowest++; bad = true; }
                        if (keys[^1] != c.CurSeq) { maxNotCur++; bad = true; }
                    }
                    foreach (var k in keys) if (c.Cache[k].Header.Flags == PacketHeaderFlags.AckSequence) { ackOnlyCached++; bad = true; break; }
                    if (bad && firstViolation == null) firstViolation = $"step {i} after '{what}': CurrentValue={c.CurSeq} lowestWalk={lowestWalk} keys={c.Keys()}";
                    c.Drain();
                }
                Console.WriteLine($"  {policy}: {steps} steps; steps with non-contiguous keys: {nonContig}; steps with a key below the walk's 'lowest' pointer: {belowLowest}; steps where max key != CurrentValue: {maxNotCur}; steps with an ack-only packet in the cache: {ackOnlyCached}");
                if (firstViolation != null) Console.WriteLine($"      first: {firstViolation}");
                Env.Remove(c);
            }
        }
    }
}
