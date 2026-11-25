using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.Network;

namespace Bench
{
    // Verbatim shape of NetworkSession.Update() + Session.TickOutbound() on the idle path (nothing queued),
    // logging checks removed (they are only reached when something is sent).
    internal sealed class LegacyTickSession
    {
        private const int minimumTimeBetweenBundles = 5, timeBetweenTimeSync = 20000, timeBetweenAck = 2000;
        private readonly object[] currentBundleLocks = new object[(int)GameMessageGroup.QueueMax];
        private readonly LNetworkBundle[] currentBundles = new LNetworkBundle[(int)GameMessageGroup.QueueMax];
        private DateTime nextSend = DateTime.UtcNow;
        public bool sendResync = true;
        private DateTime nextResync = DateTime.UtcNow.AddSeconds(3600);
        private bool sendAck = true;
        private DateTime nextAck = DateTime.UtcNow.AddSeconds(3600);
        private static readonly TimeSpan cachedPacketPruneInterval = TimeSpan.FromSeconds(5);
        private DateTime lastCachedPacketPruneTime = DateTime.UtcNow;
        private readonly ConcurrentQueue<ServerPacket> packetQueue = new();
        private bool isReleased;
        public long TimeoutTick = DateTime.UtcNow.AddSeconds(3600).Ticks;
        public object PendingTermination; public int State = 3; public DateTime logOffRequestTime = DateTime.MinValue;
        public int Sent;

        public LegacyTickSession() { for (int i = 0; i < currentBundles.Length; i++) { currentBundleLocks[i] = new object(); currentBundles[i] = new LNetworkBundle(); } }

        public void TickOutbound()
        {
            if (PendingTermination != null) return;
            if (DateTime.UtcNow.Ticks >= TimeoutTick) { PendingTermination = new object(); return; }
            Update();
            if (logOffRequestTime != DateTime.MinValue && logOffRequestTime.AddSeconds(6) <= DateTime.UtcNow) { }
            if (State == 2) { }
        }

        public void UpdateP4(DateTime now)
        {
            if (isReleased) return;
            if (now - lastCachedPacketPruneTime > cachedPacketPruneInterval) lastCachedPacketPruneTime = now;
            if (now < nextSend && now < nextAck && now < nextResync) { while (packetQueue.TryDequeue(out var p0)) { } return; }
            for (int i = 0; i < currentBundles.Length; i++)
            {
                LNetworkBundle bundleToSend = null;
                var group = (GameMessageGroup)i;
                lock (currentBundleLocks[i])
                {
                    var currentBundle = currentBundles[i];
                    if (group == GameMessageGroup.InvalidQueue)
                    {
                        if (sendResync && !currentBundle.TimeSync && now > nextResync) { currentBundle.TimeSync = true; nextResync = now.AddMilliseconds(timeBetweenTimeSync); }
                        if (sendAck && !currentBundle.SendAck && now > nextAck) { currentBundle.SendAck = true; nextAck = now.AddMilliseconds(timeBetweenAck); }
                        if (currentBundle.NeedsSending && now >= nextSend) { bundleToSend = currentBundle; currentBundles[i] = new LNetworkBundle(); }
                    }
                    else if (currentBundle.NeedsSending && now >= nextSend) { bundleToSend = currentBundle; currentBundles[i] = new LNetworkBundle(); }
                }
                if (bundleToSend != null) { Sent++; nextSend = now.AddMilliseconds(minimumTimeBetweenBundles); }
            }
            while (packetQueue.TryDequeue(out var p)) { }
        }

        public void Update()
        {
            if (isReleased) return;
            if (DateTime.UtcNow - lastCachedPacketPruneTime > cachedPacketPruneInterval) lastCachedPacketPruneTime = DateTime.UtcNow;
            for (int i = 0; i < currentBundles.Length; i++)
            {
                LNetworkBundle bundleToSend = null;
                var group = (GameMessageGroup)i;
                lock (currentBundleLocks[i])
                {
                    var currentBundle = currentBundles[i];
                    if (group == GameMessageGroup.InvalidQueue)
                    {
                        if (sendResync && !currentBundle.TimeSync && DateTime.UtcNow > nextResync) { currentBundle.TimeSync = true; currentBundle.EncryptedChecksum = true; nextResync = DateTime.UtcNow.AddMilliseconds(timeBetweenTimeSync); }
                        if (sendAck && !currentBundle.SendAck && DateTime.UtcNow > nextAck) { currentBundle.SendAck = true; nextAck = DateTime.UtcNow.AddMilliseconds(timeBetweenAck); }
                        if (currentBundle.NeedsSending && DateTime.UtcNow >= nextSend) { bundleToSend = currentBundle; currentBundles[i] = new LNetworkBundle(); }
                    }
                    else if (currentBundle.NeedsSending && DateTime.UtcNow >= nextSend) { bundleToSend = currentBundle; currentBundles[i] = new LNetworkBundle(); }
                }
                if (bundleToSend != null) { Sent++; nextSend = DateTime.UtcNow.AddMilliseconds(minimumTimeBetweenBundles); }
            }
            while (packetQueue.TryDequeue(out var p)) { }
        }
    }

    // Proposal: one clock read per DoSessionWork, a dirty-group bitmask set under the group lock by
    // EnqueueSend, and timer checks without locks. Same observable behaviour (see report).
    internal sealed class FastTickSession
    {
        private const int minimumTimeBetweenBundles = 5, timeBetweenTimeSync = 20000, timeBetweenAck = 2000;
        private readonly object[] locks = new object[(int)GameMessageGroup.QueueMax];
        private readonly LNetworkBundle[] bundles = new LNetworkBundle[(int)GameMessageGroup.QueueMax];
        private int dirtyMask;                    // bit i set (under locks[i]) when bundles[i] has content
        private long nextSendTicks, nextResyncTicks, nextAckTicks, nextPruneTicks;
        public bool sendResync = true; private bool sendAck = true;
        private readonly ConcurrentQueue<ServerPacket> packetQueue = new();
        public long TimeoutTick; public object PendingTermination; public int State = 3; public long logOffRequestTicks = 0;
        public int Sent;

        public FastTickSession()
        {
            for (int i = 0; i < bundles.Length; i++) { locks[i] = new object(); bundles[i] = new LNetworkBundle(); }
            long now = DateTime.UtcNow.Ticks;
            nextSendTicks = now; nextResyncTicks = now + TimeSpan.TicksPerHour; nextAckTicks = now + TimeSpan.TicksPerHour; nextPruneTicks = now + TimeSpan.TicksPerHour; TimeoutTick = now + TimeSpan.TicksPerHour;
        }

        public void Enqueue(int group, ACE.Server.Network.GameMessages.GameMessage m)
        {
            lock (locks[group]) { bundles[group].Enqueue(m); Interlocked.Or(ref dirtyMask, 1 << group); }
        }

        public void TickOutbound(long nowTicks)
        {
            if (PendingTermination != null) return;
            if (nowTicks >= TimeoutTick) { PendingTermination = new object(); return; }
            Update(nowTicks);
            if (logOffRequestTicks != 0 && logOffRequestTicks <= nowTicks) { }
        }

        public void Update(long now)
        {
            if (now > nextPruneTicks) nextPruneTicks = now + 5 * TimeSpan.TicksPerSecond;
            if (sendResync && now > nextResyncTicks) { lock (locks[0]) { if (!bundles[0].TimeSync) { bundles[0].TimeSync = true; bundles[0].EncryptedChecksum = true; Interlocked.Or(ref dirtyMask, 1); nextResyncTicks = now + timeBetweenTimeSync * TimeSpan.TicksPerMillisecond; } } }
            if (sendAck && now > nextAckTicks) { lock (locks[0]) { if (!bundles[0].SendAck) { bundles[0].SendAck = true; Interlocked.Or(ref dirtyMask, 1); nextAckTicks = now + timeBetweenAck * TimeSpan.TicksPerMillisecond; } } }
            int mask = Volatile.Read(ref dirtyMask);
            if (mask != 0 && now >= nextSendTicks)
            {
                for (int i = 0; i < bundles.Length; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    LNetworkBundle toSend = null;
                    lock (locks[i])
                    {
                        if (bundles[i].NeedsSending && now >= nextSendTicks) { toSend = bundles[i]; bundles[i] = new LNetworkBundle(); Interlocked.And(ref dirtyMask, ~(1 << i)); }
                    }
                    if (toSend != null) { Sent++; nextSendTicks = now + minimumTimeBetweenBundles * TimeSpan.TicksPerMillisecond; }
                }
            }
            if (!packetQueue.IsEmpty) while (packetQueue.TryDequeue(out var p)) { }
        }
    }

    public static class TickBench
    {
        public static void Run()
        {
            H.Header("CLOCK sources (per call)");
            H.Measure("DateTime.UtcNow", 10_000_000, n => { long s = 0; for (int i = 0; i < n; i++) s += DateTime.UtcNow.Ticks; GC.KeepAlive(s); });
            H.Measure("Stopwatch.GetTimestamp", 10_000_000, n => { long s = 0; for (int i = 0; i < n; i++) s += Stopwatch.GetTimestamp(); GC.KeepAlive(s); });
            H.Measure("Environment.TickCount64", 10_000_000, n => { long s = 0; for (int i = 0; i < n; i++) s += Environment.TickCount64; GC.KeepAlive(s); });
            var o = new object();
            H.Measure("uncontended lock(obj){}", 10_000_000, n => { for (int i = 0; i < n; i++) lock (o) { } });

            H.Measure("ArrayPool<byte>.Shared Rent(3732)+Return (today, 8-frag bundle)", 5_000_000, n => { for (int i = 0; i < n; i++) System.Buffers.ArrayPool<byte>.Shared.Return(System.Buffers.ArrayPool<byte>.Shared.Rent(3732)); });
            H.Measure("ArrayPool<byte>.Shared Rent(484)+Return (P.5 fix)", 5_000_000, n => { for (int i = 0; i < n; i++) System.Buffers.ArrayPool<byte>.Shared.Return(System.Buffers.ArrayPool<byte>.Shared.Rent(484)); });
            H.Header("IDLE session tick (nothing queued) - per session");
            var legacy = new LegacyTickSession[1000]; var fast = new FastTickSession[1000];
            for (int i = 0; i < 1000; i++) { legacy[i] = new LegacyTickSession(); fast[i] = new FastTickSession(); }
            H.Measure("LEGACY Session.TickOutbound+NetworkSession.Update (1000 sessions)", 2_000_000, n => { for (int i = 0; i < n; i++) legacy[i % 1000].TickOutbound(); });
            H.Measure("P.4 AS WRITTEN: snapshot + early-return if now<nextSend&&now<nextAck&&now<nextResync", 2_000_000, n => { var now = DateTime.UtcNow; for (int i = 0; i < n; i++) { if ((i & 1023) == 0) now = DateTime.UtcNow; var s = legacy[i % 1000]; if (now.Ticks >= s.TimeoutTick) continue; s.UpdateP4(now); } });
            H.Measure("FAST   snapshot clock + dirty mask (1000 sessions)", 2_000_000, n => { long now = DateTime.UtcNow.Ticks; for (int i = 0; i < n; i++) { if ((i & 1023) == 0) now = DateTime.UtcNow.Ticks; fast[i % 1000].TickOutbound(now); } });

            H.Header("DoSessionWork outer loop over sessionMap[1000] - per CALL (body = idle legacy tick)");
            foreach (int active in new[] { 10, 200, 1000 })
            {
                var map = new LegacyTickSession[1000];
                var compact = new LegacyTickSession[active];
                for (int i = 0; i < active; i++) { map[i * (1000 / active)] = legacy[i]; compact[i] = legacy[i]; }
                foreach (int dop in new[] { 1, 2, -1 })
                {
                    var po = new ParallelOptions { MaxDegreeOfParallelism = dop };
                    H.Measure($"Parallel.ForEach(map, dop={dop}) active={active}", active >= 1000 ? 2_000 : 20_000, n => { for (int k = 0; k < n; k++) Parallel.ForEach(map, po, s => s?.TickOutbound()); }, runs: 3, allThreads: true);
                }
                H.Measure($"for loop over compact[{active}] (serial)", active >= 1000 ? 2_000 : 20_000, n => { for (int k = 0; k < n; k++) { var c = compact; for (int i = 0; i < c.Length; i++) c[i].TickOutbound(); } }, runs: 3, allThreads: true);
                H.Measure($"LINQ Where(!Equals(null,k)) over map[1000] (P.0b loop)", 20_000, n => { int cnt = 0; for (int k = 0; k < n; k++) foreach (var s in System.Linq.Enumerable.Where(map, x => !Equals(null, x))) cnt++; GC.KeepAlive(cnt); }, runs: 3);
                H.Measure($"plain for over map[1000] null-skip (P.0b fix)", 20_000, n => { int cnt = 0; for (int k = 0; k < n; k++) { var m = map; for (int i = 0; i < m.Length; i++) if (m[i] != null) cnt++; } GC.KeepAlive(cnt); }, runs: 3);
            }
        }
    }
}
