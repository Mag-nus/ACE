using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using ACE.Common.Cryptography;
using ACE.Server.Network;

namespace Bench
{
    public enum InKind { AckOnlyClear, AckOnlyEncrypted, ActionEncrypted, AckPlusActionEncrypted, EchoTimeSyncAck }

    public static class PacketFactory
    {
        public static readonly byte[] Seed = BitConverter.GetBytes(0x12345678u);

        // Builds n consecutive client->server datagrams using the SERVER's own ServerPacket serialiser
        // (the wire format is symmetric). Encrypted packets consume consecutive ISAAC keys from a stream
        // seeded identically to the CryptoSystem the receiver will use.
        public static (byte[][] bufs, int[] sizes) Build(int n, InKind kind, int payloadLen = 64)
        {
            var isaac = new ISAAC(Seed);
            var bufs = new byte[n][];
            var sizes = new int[n];
            var rnd = new Random(1);
            for (int i = 0; i < n; i++)
            {
                var p = new ServerPacket();
                bool enc = kind != InKind.AckOnlyClear;
                bool ack = kind is InKind.AckOnlyClear or InKind.AckOnlyEncrypted or InKind.AckPlusActionEncrypted or InKind.EchoTimeSyncAck;
                bool frag = kind is InKind.ActionEncrypted or InKind.AckPlusActionEncrypted;
                PacketHeaderFlags f = 0;
                if (enc) f |= PacketHeaderFlags.EncryptedChecksum;
                if (ack) f |= PacketHeaderFlags.AckSequence;
                if (frag) f |= PacketHeaderFlags.BlobFragments;
                if (kind == InKind.EchoTimeSyncAck) f |= PacketHeaderFlags.TimeSync | PacketHeaderFlags.EchoRequest;
                p.Header.Flags = f;
                if (ack || kind == InKind.EchoTimeSyncAck)
                {
                    p.InitializeDataWriter();
                    if (ack) p.DataWriter.Write((uint)(1000 + i));
                    if (kind == InKind.EchoTimeSyncAck) { p.DataWriter.Write(12345.678d + i); p.DataWriter.Write(99.5f + i); }
                }
                if (frag)
                {
                    var payload = new byte[payloadLen];
                    rnd.NextBytes(payload);
                    BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xF7B1); // GameAction
                    var spf = new ServerPacketFragment(payload);
                    spf.Header.Sequence = (uint)(i + 1);
                    spf.Header.Id = 0x80000000;
                    spf.Header.Count = 1;
                    spf.Header.Index = 0;
                    spf.Header.Queue = 0x09;
                    p.Fragments.Add(spf);
                }
                p.Header.Sequence = (uint)(i + 2);
                p.Header.Id = 7;
                p.Header.Time = (ushort)(i * 3);
                p.Header.Iteration = 0x14;
                if (enc) p.IssacXor = isaac.Next();
                var buf = new byte[1024];
                p.CreateReadyToSendPacket(buf, out var size);
                bufs[i] = buf; sizes[i] = size;
            }
            return (bufs, sizes);
        }
    }

    // ---------------- Prototype V2: same public class shapes, no streams, no headerBytes copy ----------------
    public sealed class FragV2 : PacketFragment
    {
        public FragV2(byte[] d) { Data = d; }
    }

    public sealed class ClientPacketV2
    {
        public PacketHeader Header { get; } = new PacketHeader();
        public List<PacketFragment> Fragments { get; } = new List<PacketFragment>();
        public uint AckSequence; public double TimeSynch; public float EchoRequestClientTime;
        public List<uint> RetransmitData; public uint FlowBytes; public ushort FlowInterval;
        private uint headerChecksum, payloadChecksum;

        public bool Unpack(byte[] buffer, int bufferSize)
        {
            if (bufferSize < PacketHeader.HeaderSize) return false;
            Header.Unpack(buffer);
            if (Header.Size > bufferSize - PacketHeader.HeaderSize) return false;
            var payload = new Span<byte>(buffer, PacketHeader.HeaderSize, Header.Size);
            int pos = 0;
            var flags = Header.Flags;
            const PacketHeaderFlags cold = PacketHeaderFlags.LoginRequest | PacketHeaderFlags.WorldLoginRequest | PacketHeaderFlags.ConnectResponse;
            if ((flags & cold) != 0) throw new NotSupportedException("cold path: fall back to legacy parser");
            if ((flags & PacketHeaderFlags.ServerSwitch) != 0) { if (payload.Length < pos + 8) return false; pos += 8; }
            if ((flags & PacketHeaderFlags.RequestRetransmit) != 0)
            {
                if (payload.Length < pos + 4) return false;
                uint c = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
                if (c > (uint)(payload.Length - pos) / 4) return false;
                RetransmitData = new List<uint>((int)c);
                for (int i = 0; i < c; i++) { RetransmitData.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos))); pos += 4; }
            }
            if ((flags & PacketHeaderFlags.RejectRetransmit) != 0)
            {
                if (payload.Length < pos + 4) return false;
                uint c = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
                if (c > (uint)(payload.Length - pos) / 4) return false;
                pos += (int)c * 4;
            }
            if ((flags & PacketHeaderFlags.AckSequence) != 0) { if (payload.Length < pos + 4) return false; AckSequence = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4; }
            if ((flags & PacketHeaderFlags.CICMDCommand) != 0) { if (payload.Length < pos + 8) return false; pos += 8; }
            if ((flags & PacketHeaderFlags.TimeSync) != 0) { if (payload.Length < pos + 8) return false; TimeSynch = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(pos)); pos += 8; }
            if ((flags & PacketHeaderFlags.EchoRequest) != 0) { if (payload.Length < pos + 4) return false; EchoRequestClientTime = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos)); pos += 4; }
            if ((flags & PacketHeaderFlags.Flow) != 0) { if (payload.Length < pos + 6) return false; FlowBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); FlowInterval = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos + 4)); pos += 6; }

            uint pay = Hash32.Calculate(payload, pos); // identical bytes to what headerBytes re-serialised

            if ((flags & PacketHeaderFlags.BlobFragments) != 0)
            {
                while (pos < payload.Length)
                {
                    if (payload.Length - pos < PacketFragmentHeader.HeaderSize) return false;
                    var s = payload.Slice(pos);
                    ushort size = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10));
                    if (size < PacketFragmentHeader.HeaderSize || size > 464 || size > s.Length) return false;
                    var data = s.Slice(16, size - 16).ToArray();
                    var fr = new FragV2(data);
                    var fh = fr.Header;
                    fh.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(s);
                    fh.Id = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4));
                    fh.Count = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8));
                    fh.Size = size;
                    fh.Index = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12));
                    fh.Queue = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14));
                    // header hash = sum of the four LE words + (16 << 16); data hash over the span
                    pay += Hash32.Calculate(s, 16) + Hash32.Calculate(s.Slice(16), size - 16);
                    Fragments.Add(fr);
                    pos += size;
                }
            }
            payloadChecksum = pay;
            headerChecksum = Header.CalculateHash32();
            return true;
        }

        public bool VerifyCRC(CryptoSystem fq)
        {
            if (Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
            {
                var key = (Header.Checksum - headerChecksum) ^ payloadChecksum;
                if (fq.Search(key)) { fq.ConsumeKey(key); return true; }
                return false;
            }
            return headerChecksum + payloadChecksum == Header.Checksum;
        }
    }

    // ---------------- Prototype V3: readonly structs parsed with BinaryPrimitives ----------------
    public readonly struct PacketHeaderV
    {
        public readonly uint Sequence; public readonly PacketHeaderFlags Flags; public readonly uint Checksum;
        public readonly ushort Id, Time, Size, Iteration;
        public PacketHeaderV(ReadOnlySpan<byte> s)
        {
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(s);
            Flags = (PacketHeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4));
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8));
            Id = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12));
            Time = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14));
            Size = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(16));
            Iteration = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(18));
        }
        // Hash32 over the 20 header bytes with Checksum replaced by 0xBADD70DD. Hash32 is a sum of LE words,
        // so no Pack/stackalloc is needed.
        public uint Hash32 => unchecked((20u << 16) + Sequence + (uint)Flags + 0xBADD70DDu + (uint)(Id | (Time << 16)) + (uint)(Size | (Iteration << 16)));
    }

    public readonly struct FragmentHeaderV
    {
        public readonly uint Sequence, Id; public readonly ushort Count, Size, Index, Queue;
        public FragmentHeaderV(ReadOnlySpan<byte> s)
        {
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(s);
            Id = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4));
            Count = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8));
            Size = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10));
            Index = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12));
            Queue = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14));
        }
        public uint Hash32 => unchecked((16u << 16) + Sequence + Id + (uint)(Count | (Size << 16)) + (uint)(Index | (Queue << 16)));
    }

    public struct FragmentV { public FragmentHeaderV Header; public byte[] Data; }

    public sealed class ClientPacketV3
    {
        public PacketHeaderV Header;
        public uint AckSequence; public double TimeSynch; public float EchoRequestClientTime; public uint FlowBytes; public ushort FlowInterval;
        public int FragmentCount; public FragmentV Fragment0; public FragmentV[] More;
        private uint payloadChecksum;

        public bool Unpack(byte[] buffer, int bufferSize)
        {
            if (bufferSize < PacketHeader.HeaderSize) return false;
            Header = new PacketHeaderV(buffer);
            if (Header.Size > bufferSize - PacketHeader.HeaderSize) return false;
            var payload = new Span<byte>(buffer, PacketHeader.HeaderSize, Header.Size);
            int pos = 0;
            var flags = Header.Flags;
            const PacketHeaderFlags notHandled = PacketHeaderFlags.LoginRequest | PacketHeaderFlags.WorldLoginRequest | PacketHeaderFlags.ConnectResponse | PacketHeaderFlags.RequestRetransmit | PacketHeaderFlags.RejectRetransmit | PacketHeaderFlags.ServerSwitch | PacketHeaderFlags.CICMDCommand;
            if ((flags & notHandled) != 0) throw new NotSupportedException();
            if ((flags & PacketHeaderFlags.AckSequence) != 0) { if (payload.Length < pos + 4) return false; AckSequence = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4; }
            if ((flags & PacketHeaderFlags.TimeSync) != 0) { if (payload.Length < pos + 8) return false; TimeSynch = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(pos)); pos += 8; }
            if ((flags & PacketHeaderFlags.EchoRequest) != 0) { if (payload.Length < pos + 4) return false; EchoRequestClientTime = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos)); pos += 4; }
            if ((flags & PacketHeaderFlags.Flow) != 0) { if (payload.Length < pos + 6) return false; FlowBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); FlowInterval = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos + 4)); pos += 6; }
            uint pay = Hash32.Calculate(payload, pos);
            if ((flags & PacketHeaderFlags.BlobFragments) != 0)
            {
                while (pos < payload.Length)
                {
                    if (payload.Length - pos < 16) return false;
                    var s = payload.Slice(pos);
                    var fh = new FragmentHeaderV(s);
                    if (fh.Size < 16 || fh.Size > 464 || fh.Size > s.Length) return false;
                    var dataSpan = s.Slice(16, fh.Size - 16);
                    pay += fh.Hash32 + Hash32.Calculate(dataSpan, dataSpan.Length);
                    var fv = new FragmentV { Header = fh, Data = dataSpan.ToArray() };
                    if (FragmentCount == 0) Fragment0 = fv;
                    else
                    {
                        More ??= new FragmentV[4];
                        if (FragmentCount - 1 == More.Length) Array.Resize(ref More, More.Length * 2);
                        More[FragmentCount - 1] = fv;
                    }
                    FragmentCount++;
                    pos += fh.Size;
                }
            }
            payloadChecksum = pay;
            return true;
        }

        public bool VerifyCRC(CryptoSystem fq)
        {
            uint hc = Header.Hash32;
            if ((Header.Flags & PacketHeaderFlags.EncryptedChecksum) != 0)
            {
                var key = (Header.Checksum - hc) ^ payloadChecksum;
                if (fq.Search(key)) { fq.ConsumeKey(key); return true; }
                return false;
            }
            return hc + payloadChecksum == Header.Checksum;
        }
    }

    public static class InboundBench
    {
        public static void Run()
        {
            H.Header("INBOUND: parse + CRC verify (+ ClientMessage for each fragment), per datagram");
            const int N = 200_000;
            foreach (InKind kind in Enum.GetValues(typeof(InKind)))
            {
                var (bufs, sizes) = PacketFactory.Build(N, kind);

                // correctness parity
                {
                    var cs1 = new CryptoSystem(PacketFactory.Seed); var cs2 = new CryptoSystem(PacketFactory.Seed); var cs3 = new CryptoSystem(PacketFactory.Seed);
                    for (int i = 0; i < 2000; i++)
                    {
                        var a = new ClientPacket(); var b = new ClientPacketV2(); var c = new ClientPacketV3();
                        bool ua = a.Unpack(bufs[i], sizes[i]), ub = b.Unpack(bufs[i], sizes[i]), uc = c.Unpack(bufs[i], sizes[i]);
                        bool va = ua && a.VerifyCRC(cs1), vb = ub && b.VerifyCRC(cs2), vc = uc && c.VerifyCRC(cs3);
                        if (!(ua && ub && uc && va && vb && vc)) throw new Exception($"parity fail {kind} i={i} {ua}{ub}{uc} {va}{vb}{vc}");
                        if (a.Fragments.Count != b.Fragments.Count || a.Fragments.Count != c.FragmentCount) throw new Exception("frag count");
                        if (a.Fragments.Count > 0 && !a.Fragments[0].Data.AsSpan().SequenceEqual(c.Fragment0.Data)) throw new Exception("data");
                        if (a.HeaderOptional.AckSequence != c.AckSequence || a.HeaderOptional.EchoRequestClientTime != c.EchoRequestClientTime || a.HeaderOptional.TimeSynch != c.TimeSynch) throw new Exception("opt");
                    }
                }

                H.Measure($"{kind,-24} current ClientPacket (+ClientMessage)", N, n =>
                {
                    var cs = new CryptoSystem(PacketFactory.Seed);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new ClientPacket();
                        if (!p.Unpack(bufs[i], sizes[i]) || !p.VerifyCRC(cs)) throw new Exception();
                        foreach (var f in p.Fragments) { var m = new ClientMessage(f.Data); }
                        p.ReleaseBuffer();
                    }
                });
                H.Measure($"{kind,-24} V2 same classes, span parse, no streams", N, n =>
                {
                    var cs = new CryptoSystem(PacketFactory.Seed);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new ClientPacketV2();
                        if (!p.Unpack(bufs[i], sizes[i]) || !p.VerifyCRC(cs)) throw new Exception();
                        foreach (var f in p.Fragments) { var m = new ClientMessage(f.Data); }
                    }
                });
                H.Measure($"{kind,-24} V3 readonly-struct headers", N, n =>
                {
                    var cs = new CryptoSystem(PacketFactory.Seed);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new ClientPacketV3();
                        if (!p.Unpack(bufs[i], sizes[i]) || !p.VerifyCRC(cs)) throw new Exception();
                        if (p.FragmentCount > 0) { var m = new ClientMessage(p.Fragment0.Data); }
                    }
                });
                H.Measure($"{kind,-24} (ref) CryptoSystem only, new per run", N, n =>
                {
                    var cs = new CryptoSystem(PacketFactory.Seed);
                    uint s = 0;
                    for (int i = 0; i < n; i++) { s += cs.CurrentKey; cs.ConsumeKey(cs.CurrentKey); }
                });
            }

            H.Header("INBOUND allocation breakdown (single objects)");
            H.Measure("new ClientPacket()", 1_000_000, n => { for (int i = 0; i < n; i++) { var p = new ClientPacket(); } });
            var (b1, s1) = PacketFactory.Build(1, InKind.AckOnlyEncrypted);
            H.Measure("ClientPacket.Unpack ack-only (no verify)", 1_000_000, n => { for (int i = 0; i < n; i++) { var p = new ClientPacket(); p.Unpack(b1[0], s1[0]); } });
            var arr = new byte[64];
            H.Measure("new ClientMessage(byte[64])", 1_000_000, n => { for (int i = 0; i < n; i++) { var m = new ClientMessage(arr); } });
            H.Measure("new MemoryStream(byte[]) + BinaryReader", 1_000_000, n => { for (int i = 0; i < n; i++) { var ms = new System.IO.MemoryStream(arr); var br = new System.IO.BinaryReader(ms); } });
            H.Measure("new MemoryStream() + BinaryWriter + Write(uint)", 1_000_000, n => { for (int i = 0; i < n; i++) { var ms = new System.IO.MemoryStream(); var bw = new System.IO.BinaryWriter(ms); bw.Write(1u); } });
        }
    }
}
