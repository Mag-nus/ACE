using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using ACE.Common.Cryptography;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;

namespace Bench
{
    public sealed class TestMessage : GameMessage
    {
        public TestMessage(int totalLen, GameMessageGroup g, byte fill) : base(GameMessageOpcode.UpdatePosition, g)
        {
            var b = new byte[totalLen - 4];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)(fill + i);
            Writer.Write(b);
        }
    }

    // ===================== LEGACY: verbatim copies of NetworkSession.SendBundle/FlushPackets/SendPacket(Raw),
    // MessageFragment and NetworkBundle (logging removed, socket replaced by a sink) =====================
    internal class LMessageFragment
    {
        public GameMessage Message { get; private set; }
        public uint Sequence { get; set; }
        public ushort Index { get; set; }
        public ushort Count { get; set; }
        public int DataLength => (int)Message.Data.Length;
        public int DataRemaining { get; private set; }
        public int NextSize { get { var d = DataRemaining; if (d > PacketFragment.MaxFragmentDataSize) d = PacketFragment.MaxFragmentDataSize; return PacketFragmentHeader.HeaderSize + d; } }
        public int TailSize => PacketFragmentHeader.HeaderSize + (DataLength % PacketFragment.MaxFragmentDataSize);
        public bool TailSent { get; private set; }
        public LMessageFragment(GameMessage message, uint sequence)
        {
            Message = message; DataRemaining = DataLength; Sequence = sequence;
            Count = (ushort)(Math.Ceiling((double)DataLength / PacketFragment.MaxFragmentDataSize));
            Index = 0; if (Count == 1) TailSent = true;
        }
        public ServerPacketFragment GetTailFragment() { var index = (ushort)(Count - 1); TailSent = true; return CreateServerFragment(index); }
        public ServerPacketFragment GetNextFragment() => CreateServerFragment(Index++);
        private ServerPacketFragment CreateServerFragment(ushort index)
        {
            if (index >= Count) throw new ArgumentOutOfRangeException();
            var position = index * PacketFragment.MaxFragmentDataSize;
            if (position > DataLength) throw new ArgumentOutOfRangeException();
            if (DataRemaining <= 0) throw new InvalidOperationException();
            var dataToSend = DataLength - position;
            if (dataToSend > PacketFragment.MaxFragmentDataSize) dataToSend = PacketFragment.MaxFragmentDataSize;
            if (DataRemaining < dataToSend) throw new InvalidOperationException();
            byte[] data = new byte[dataToSend];
            Buffer.BlockCopy(Message.Data.GetBuffer(), position, data, 0, dataToSend);
            ServerPacketFragment fragment = new ServerPacketFragment(data);
            fragment.Header.Sequence = Sequence; fragment.Header.Id = 0x80000000; fragment.Header.Count = Count;
            fragment.Header.Index = index; fragment.Header.Queue = (ushort)Message.Group;
            DataRemaining -= dataToSend;
            return fragment;
        }
    }

    internal class LNetworkBundle
    {
        private bool propChanged;
        public bool NeedsSending => propChanged || messages.Count > 0;
        public bool HasMoreMessages => messages.Count > 0;
        private Queue<GameMessage> messages = new Queue<GameMessage>();
        private float clientTime = -1f;
        public float ClientTime { get => clientTime; set { clientTime = value; propChanged = true; } }
        private bool timeSync;
        public bool TimeSync { get => timeSync; set { timeSync = value; propChanged = true; } }
        private bool ackSeq;
        public bool SendAck { get => ackSeq; set { ackSeq = value; propChanged = true; } }
        public bool EncryptedChecksum { get; set; }
        public int CurrentSize { get; private set; }
        public void Enqueue(GameMessage message) { CurrentSize += (int)message.Data.Length; messages.Enqueue(message); }
        public GameMessage Dequeue() => messages.Dequeue();
        // added for the double-buffer prototype
        public void Reset() { propChanged = false; messages.Clear(); clientTime = -1f; timeSync = false; ackSeq = false; EncryptedChecksum = false; CurrentSize = 0; }
    }

    public interface ISink { void Send(byte[] buf, int size); }
    public sealed class NullSink : ISink { public uint Acc; public void Send(byte[] b, int s) { Acc += b[s - 1]; } }
    public sealed class CollectSink : ISink { public List<byte[]> Out = new(); public void Send(byte[] b, int s) { Out.Add(b.AsSpan(0, s).ToArray()); } }

    internal sealed class LegacySession
    {
        public readonly ConcurrentQueue<ServerPacket> packetQueue = new();
        public readonly ConcurrentDictionary<uint, ServerPacket> cachedPackets = new();
        public ISAAC IssacServer = new ISAAC(BitConverter.GetBytes(0xCAFEu));
        public uint FragmentSequence = 1;
        public uint PacketSequence = 1;
        public uint lastReceivedPacketSequence = 100;
        public ushort ServerId = 0xB;
        public double PortalYearTicks = 1234567.0;
        public ISink Sink;

        public void SendBundle(LNetworkBundle bundle, GameMessageGroup group)
        {
            bool writeOptionalHeaders = true;
            List<LMessageFragment> fragments = new List<LMessageFragment>();
            while (bundle.HasMoreMessages)
            {
                var message = bundle.Dequeue();
                var fragment = new LMessageFragment(message, FragmentSequence++);
                fragments.Add(fragment);
            }
            while (fragments.Count > 0 || writeOptionalHeaders)
            {
                ServerPacket packet = new ServerPacket();
                PacketHeader packetHeader = packet.Header;
                if (fragments.Count > 0) packetHeader.Flags |= PacketHeaderFlags.BlobFragments;
                if (bundle.EncryptedChecksum) packetHeader.Flags |= PacketHeaderFlags.EncryptedChecksum;
                int availableSpace = ServerPacket.MaxPacketSize;
                var firstMessage = fragments.FirstOrDefault();
                if (firstMessage != null)
                {
                    if (firstMessage.DataRemaining >= availableSpace)
                    {
                        ServerPacketFragment spf = firstMessage.GetNextFragment();
                        packet.Fragments.Add(spf);
                        availableSpace -= spf.Length;
                        if (firstMessage.DataRemaining <= 0) fragments.Remove(firstMessage);
                    }
                    else
                    {
                        if (writeOptionalHeaders)
                        {
                            writeOptionalHeaders = false;
                            WriteOptionalHeaders(bundle, packet);
                            if (packet.Data != null) availableSpace -= (int)packet.Data.Length;
                        }
                        List<LMessageFragment> removeList = new List<LMessageFragment>();
                        foreach (LMessageFragment fragment in fragments)
                        {
                            bool fragmentSkipped = false;
                            if (!fragment.TailSent && availableSpace >= fragment.TailSize)
                            {
                                ServerPacketFragment spf = fragment.GetTailFragment();
                                packet.Fragments.Add(spf);
                                availableSpace -= spf.Length;
                            }
                            else if (availableSpace >= fragment.NextSize)
                            {
                                ServerPacketFragment spf = fragment.GetNextFragment();
                                packet.Fragments.Add(spf);
                                availableSpace -= spf.Length;
                            }
                            else fragmentSkipped = true;
                            if (fragment.DataRemaining <= 0) removeList.Add(fragment);
                            if (fragmentSkipped && group == GameMessageGroup.UIQueue) break;
                        }
                        fragments.RemoveAll(x => removeList.Contains(x));
                    }
                }
                else
                {
                    if (writeOptionalHeaders)
                    {
                        writeOptionalHeaders = false;
                        WriteOptionalHeaders(bundle, packet);
                        if (packet.Data != null) availableSpace -= (int)packet.Data.Length;
                    }
                }
                EnqueueSend(packet);
            }
        }

        private void WriteOptionalHeaders(LNetworkBundle bundle, ServerPacket packet)
        {
            PacketHeader packetHeader = packet.Header;
            if (bundle.SendAck) { packetHeader.Flags |= PacketHeaderFlags.AckSequence; packet.InitializeDataWriter(); packet.DataWriter.Write(lastReceivedPacketSequence); }
            if (bundle.TimeSync) { packetHeader.Flags |= PacketHeaderFlags.TimeSync; packet.InitializeDataWriter(); packet.DataWriter.Write(PortalYearTicks); }
            if (bundle.ClientTime != -1f) { packetHeader.Flags |= PacketHeaderFlags.EchoResponse; packet.InitializeDataWriter(); packet.DataWriter.Write(bundle.ClientTime); packet.DataWriter.Write((float)PortalYearTicks - bundle.ClientTime); }
        }

        public void EnqueueSend(params ServerPacket[] packets)
        {
            foreach (var packet in packets) packetQueue.Enqueue(packet);
        }

        public void FlushPackets()
        {
            while (packetQueue.TryDequeue(out var packet))
            {
                bool isNak = packet.Header.Flags.HasFlag(PacketHeaderFlags.RequestRetransmit);
                if (packet.Header.Flags == PacketHeaderFlags.AckSequence || isNak) packet.Header.Sequence = PacketSequence;
                else packet.Header.Sequence = ++PacketSequence;
                packet.Header.Id = ServerId;
                packet.Header.Iteration = 0x01;
                packet.Header.Time = (ushort)PortalYearTicks;
                if (packet.Header.Sequence >= 2u && !isNak) cachedPackets.TryAdd(packet.Header.Sequence, packet);
                SendPacket(packet);
            }
        }

        private void SendPacket(ServerPacket packet)
        {
            if (packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum)) packet.IssacXor = IssacServer.Next();
            SendPacketRaw(packet);
        }

        public void SendPacketRaw(ServerPacket packet)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)(PacketHeader.HeaderSize + (packet.Data?.Length ?? 0) + (packet.Fragments.Count * PacketFragment.MaxFragementSize)));
            try
            {
                packet.CreateReadyToSendPacket(buffer, out var size);
                Sink.Send(buffer, size);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public bool Retransmit(uint sequence)
        {
            if (cachedPackets.TryGetValue(sequence, out var cachedPacket))
            {
                if (!cachedPacket.Header.HasFlag(PacketHeaderFlags.Retransmission)) cachedPacket.Header.Flags |= PacketHeaderFlags.Retransmission;
                SendPacketRaw(cachedPacket);
                return true;
            }
            return false;
        }

        public void AckLegacy(uint sequence)
        {
            var removalList = cachedPackets.Keys.Where(x => x < sequence);
            foreach (var key in removalList) cachedPackets.TryRemove(key, out _);
        }
    }

    // ===================== DIRECT prototype: serialise each packet once into an exact-size wire array =====================
    public sealed class OutPacket
    {
        public byte[] Wire;            // full datagram, header written at flush
        public uint PayloadHash;       // Hash32(optional block) + sum(fragment header hash + fragment data hash)
        public uint XoredPayload;      // PayloadHash ^ issacXor, fixed at first send (needed to re-checksum a retransmit)
        public PacketHeaderFlags Flags;
        public uint Sequence;
        public ushort Time;
    }

    internal struct Cursor
    {
        public GameMessage Msg; public uint Seq; public ushort Count; public ushort Index; public bool TailSent; public int Remaining; public int DataLength;
        public readonly int NextSize { get { var d = Remaining; if (d > 448) d = 448; return 16 + d; } }
        public readonly int TailSize => 16 + (DataLength % 448);
    }

    internal sealed class DirectSession
    {
        public readonly ConcurrentQueue<OutPacket> packetQueue = new();
        public readonly ConcurrentDictionary<uint, OutPacket> cachedPackets = new();
        public ISAAC IssacServer = new ISAAC(BitConverter.GetBytes(0xCAFEu));
        public uint FragmentSequence = 1;
        public uint PacketSequence = 1;
        public uint lastReceivedPacketSequence = 100;
        public ushort ServerId = 0xB;
        public double PortalYearTicks = 1234567.0;
        public ISink Sink;
        private readonly List<Cursor> cursors = new();     // touched only inside SendBundle; see notes on concurrency
        [ThreadStatic] private static byte[] scratch;

        public void SendBundle(LNetworkBundle bundle, GameMessageGroup group)
        {
            var buf = scratch ??= new byte[2048];
            bool writeOptionalHeaders = true;
            var list = cursors;
            list.Clear();
            while (bundle.HasMoreMessages)
            {
                var m = bundle.Dequeue();
                int len = (int)m.Data.Length;
                var c = new Cursor { Msg = m, Seq = FragmentSequence++, DataLength = len, Remaining = len, Count = (ushort)((len + 447) / 448) };
                c.TailSent = c.Count == 1;
                list.Add(c);
            }
            while (list.Count > 0 || writeOptionalHeaders)
            {
                PacketHeaderFlags flags = 0;
                if (list.Count > 0) flags |= PacketHeaderFlags.BlobFragments;
                if (bundle.EncryptedChecksum) flags |= PacketHeaderFlags.EncryptedChecksum;
                int avail = ServerPacket.MaxPacketSize;
                int pos = PacketHeader.HeaderSize;
                uint hash = 0;
                if (list.Count > 0)
                {
                    var span = CollectionsMarshal.AsSpan(list);
                    ref var first = ref span[0];
                    if (first.Remaining >= avail)
                    {
                        int n = WriteFragment(buf, ref pos, ref first, first.Index++, ref hash);
                        avail -= n;
                        if (first.Remaining <= 0) list.RemoveAt(0);
                    }
                    else
                    {
                        if (writeOptionalHeaders)
                        {
                            writeOptionalHeaders = false;
                            int start = pos;
                            flags |= WriteOptionalHeaders(bundle, buf, ref pos);
                            if (pos > start) { hash += Hash32.Calculate(buf.AsSpan(start), pos - start); avail -= pos - start; }
                        }
                        int write = 0;
                        for (int i = 0; i < span.Length; i++)
                        {
                            ref var f = ref span[i];
                            bool skipped = false;
                            if (!f.TailSent && avail >= f.TailSize) { f.TailSent = true; avail -= WriteFragment(buf, ref pos, ref f, (ushort)(f.Count - 1), ref hash); }
                            else if (avail >= f.NextSize) { avail -= WriteFragment(buf, ref pos, ref f, f.Index++, ref hash); }
                            else skipped = true;
                            bool done = f.Remaining <= 0;
                            if (!done) span[write++] = f; // compact in place, preserving order
                            if (skipped && group == GameMessageGroup.UIQueue)
                            {
                                for (int k = i + 1; k < span.Length; k++) span[write++] = span[k];
                                break;
                            }
                        }
                        list.RemoveRange(write, list.Count - write);
                    }
                }
                else if (writeOptionalHeaders)
                {
                    writeOptionalHeaders = false;
                    int start = pos;
                    flags |= WriteOptionalHeaders(bundle, buf, ref pos);
                    if (pos > start) hash += Hash32.Calculate(buf.AsSpan(start), pos - start);
                }
                var op = new OutPacket { Wire = buf.AsSpan(0, pos).ToArray(), PayloadHash = hash, Flags = flags };
                packetQueue.Enqueue(op);
            }
        }

        private static int WriteFragment(byte[] buf, ref int pos, ref Cursor c, ushort index, ref uint hash)
        {
            int position = index * 448;
            int n = c.DataLength - position; if (n > 448) n = 448;
            var h = buf.AsSpan(pos);
            ushort size = (ushort)(16 + n);
            BinaryPrimitives.WriteUInt32LittleEndian(h, c.Seq);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), 0x80000000);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(8), c.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(10), size);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(12), index);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(14), (ushort)c.Msg.Group);
            var src = new ReadOnlySpan<byte>(c.Msg.Data.GetBuffer(), position, n);
            var dst = h.Slice(16, n);
            src.CopyTo(dst);
            hash += unchecked((16u << 16) + c.Seq + 0x80000000u + (uint)(c.Count | (size << 16)) + (uint)(index | ((ushort)c.Msg.Group << 16)))
                  + Hash32.Calculate(dst, n);
            c.Remaining -= n;
            pos += size;
            return size;
        }

        private PacketHeaderFlags WriteOptionalHeaders(LNetworkBundle bundle, byte[] buf, ref int pos)
        {
            PacketHeaderFlags f = 0;
            if (bundle.SendAck) { f |= PacketHeaderFlags.AckSequence; BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), lastReceivedPacketSequence); pos += 4; }
            if (bundle.TimeSync) { f |= PacketHeaderFlags.TimeSync; BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(pos), PortalYearTicks); pos += 8; }
            if (bundle.ClientTime != -1f) { f |= PacketHeaderFlags.EchoResponse; BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos), bundle.ClientTime); BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 4), (float)PortalYearTicks - bundle.ClientTime); pos += 8; }
            return f;
        }

        public void FlushPackets()
        {
            while (packetQueue.TryDequeue(out var p))
            {
                bool isNak = (p.Flags & PacketHeaderFlags.RequestRetransmit) != 0;
                p.Sequence = (p.Flags == PacketHeaderFlags.AckSequence || isNak) ? PacketSequence : ++PacketSequence;
                p.Time = (ushort)PortalYearTicks;
                uint xor = (p.Flags & PacketHeaderFlags.EncryptedChecksum) != 0 ? IssacServer.Next() : 0u;
                p.XoredPayload = p.PayloadHash ^ xor;
                WriteHeader(p, p.Flags);
                Sink.Send(p.Wire, p.Wire.Length);
                if (p.Sequence >= 2u && !isNak) cachedPackets.TryAdd(p.Sequence, p); // after the first send: no concurrent mutation possible
            }
        }

        private void WriteHeader(OutPacket p, PacketHeaderFlags flags) => WriteHeader(p.Wire, p, flags);

        private void WriteHeader(Span<byte> w, OutPacket p, PacketHeaderFlags flags)
        {
            ushort size = (ushort)(p.Wire.Length - 20);
            const ushort iteration = 1;
            uint headerHash = unchecked((20u << 16) + p.Sequence + (uint)flags + 0xBADD70DDu + (uint)(ServerId | (p.Time << 16)) + (uint)(size | (iteration << 16)));
            BinaryPrimitives.WriteUInt32LittleEndian(w, p.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(4), (uint)flags);
            BinaryPrimitives.WriteUInt32LittleEndian(w.Slice(8), unchecked(headerHash + p.XoredPayload));
            BinaryPrimitives.WriteUInt16LittleEndian(w.Slice(12), ServerId);
            BinaryPrimitives.WriteUInt16LittleEndian(w.Slice(14), p.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(w.Slice(16), size);
            BinaryPrimitives.WriteUInt16LittleEndian(w.Slice(18), iteration);
        }

        // Retransmit never mutates the cached bytes: patch a stack copy.
        public bool Retransmit(uint sequence)
        {
            if (!cachedPackets.TryGetValue(sequence, out var p)) return false;
            Span<byte> copy = stackalloc byte[p.Wire.Length]; // bounded: <= 484 + overflow from N.0c; Wire length is server-produced, never client-controlled
            p.Wire.CopyTo(copy);
            WriteHeader(copy, p, p.Flags | PacketHeaderFlags.Retransmission);
            var tmp = ArrayPool<byte>.Shared.Rent(copy.Length); copy.CopyTo(tmp); Sink.Send(tmp, copy.Length); ArrayPool<byte>.Shared.Return(tmp);
            return true;
        }
    }

    public static class OutboundBench
    {
        static GameMessage[] Msgs(params (int len, GameMessageGroup g)[] spec)
        {
            var r = new GameMessage[spec.Length];
            for (int i = 0; i < spec.Length; i++) r[i] = new TestMessage(spec[i].len, spec[i].g, (byte)(i * 7));
            return r;
        }

        record Scenario(string Name, GameMessage[] Messages, bool Ack, bool Echo, bool TimeSync, GameMessageGroup Group);

        public static void Run()
        {
            var S = GameMessageGroup.SmartboxQueue; var U = GameMessageGroup.UIQueue;
            var scenarios = new[]
            {
                new Scenario("1 x 60B msg", Msgs((60, S)), false, false, false, S),
                new Scenario("6 x 40-80B msgs", Msgs((60, S), (44, S), (80, S), (52, S), (72, S), (40, S)), false, false, false, S),
                new Scenario("ack+echo only, no msgs", Msgs(), true, true, false, GameMessageGroup.InvalidQueue),
                new Scenario("ack+echo + 3 small msgs", Msgs((60, S), (48, S), (64, S)), true, true, false, S),
                new Scenario("1500B CreateObject + 3 small (UI)", Msgs((1500, U), (60, U), (90, U), (40, U)), false, false, false, U),
                new Scenario("exact 896B (N.0c boundary) + 1 small", Msgs((896, U), (40, U)), false, false, false, U),
            };

            // ---- parity: identical datagrams, and identical retransmit datagrams ----
            foreach (var sc in scenarios)
            {
                var ls = new LegacySession(); var ds = new DirectSession();
                var lc = new CollectSink(); var dc = new CollectSink(); ls.Sink = lc; ds.Sink = dc;
                for (int it = 0; it < 50; it++)
                {
                    ls.SendBundle(MakeBundle(sc), sc.Group); ls.FlushPackets();
                    ds.SendBundle(MakeBundle(sc), sc.Group); ds.FlushPackets();
                }
                for (uint seq = 2; seq < 40; seq += 3) { ls.Retransmit(seq); ds.Retransmit(seq); }
                if (lc.Out.Count != dc.Out.Count) throw new Exception($"parity count {sc.Name} {lc.Out.Count} vs {dc.Out.Count}");
                for (int i = 0; i < lc.Out.Count; i++)
                    if (!lc.Out[i].AsSpan().SequenceEqual(dc.Out[i])) throw new Exception($"parity bytes {sc.Name} datagram {i}");
                // verify with the server's own inbound parser that a datagram round-trips its CRC
                Console.WriteLine($"parity OK: {sc.Name,-40} {lc.Out.Count / 50.0:F1} datagrams/flush, sizes {string.Join(",", lc.Out.Take(4).Select(b => b.Length))}");
            }

            H.Header("OUTBOUND per flush: SendBundle + FlushPackets + serialise (socket replaced by sink), cache trimmed every 32 pkts");
            foreach (var sc in scenarios)
            {
                {
                    var ls = new LegacySession { Sink = new NullSink() };
                    H.Measure($"{sc.Name,-36} LEGACY (new NetworkBundle per flush)", 200_000, n =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            var b = new LNetworkBundle(); Fill(b, sc);
                            ls.SendBundle(b, sc.Group); ls.FlushPackets();
                            if ((i & 31) == 0) TrimLegacy(ls);
                        }
                    });
                }
                {
                    var ds = new DirectSession { Sink = new NullSink() };
                    var spare = new LNetworkBundle();
                    H.Measure($"{sc.Name,-36} DIRECT (+ reused bundle)", 200_000, n =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            var b = spare; b.Reset(); Fill(b, sc);
                            ds.SendBundle(b, sc.Group); ds.FlushPackets();
                            if ((i & 31) == 0) TrimDirect(ds);
                        }
                    });
                }
            }

            H.Header("Retained bytes per cached packet (what cachedPackets keeps alive)");
            foreach (var sc in scenarios.Take(5))
            {
                long l = Retained(() => { var ls = new LegacySession { Sink = new NullSink() }; for (int i = 0; i < 2000; i++) { ls.SendBundle(MakeBundle(sc), sc.Group); ls.FlushPackets(); } return (ls, ls.cachedPackets.Count); });
                long d = Retained(() => { var ds = new DirectSession { Sink = new NullSink() }; for (int i = 0; i < 2000; i++) { ds.SendBundle(MakeBundle(sc), sc.Group); ds.FlushPackets(); } return (ds, ds.cachedPackets.Count); });
                Console.WriteLine($"{sc.Name,-40} legacy {l,6} B/cached pkt   direct {d,6} B/cached pkt");
            }
        }

        static long Retained(Func<(object keep, int count)> f)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(true);
            var (keep, count) = f();
            long after = GC.GetTotalMemory(true);
            GC.KeepAlive(keep);
            return (after - before) / count;
        }

        static LNetworkBundle MakeBundle(Scenario sc) { var b = new LNetworkBundle(); Fill(b, sc); return b; }
        static void Fill(LNetworkBundle b, Scenario sc)
        {
            b.EncryptedChecksum = true;
            foreach (var m in sc.Messages) b.Enqueue(m);
            if (sc.Ack) b.SendAck = true;
            if (sc.Echo) b.ClientTime = 1234.5f;
            if (sc.TimeSync) b.TimeSync = true;
        }
        static void TrimLegacy(LegacySession s) { uint lo = s.PacketSequence - 8; foreach (var kv in s.cachedPackets) if (kv.Key < lo) s.cachedPackets.TryRemove(kv.Key, out _); }
        static void TrimDirect(DirectSession s) { uint lo = s.PacketSequence - 8; foreach (var kv in s.cachedPackets) if (kv.Key < lo) s.cachedPackets.TryRemove(kv.Key, out _); }
    }
}
