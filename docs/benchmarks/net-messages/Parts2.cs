using System;
using System.IO;
using ACE.Common.Extensions;
using ACE.Server.Entity.Actions;
using ACE.Server.Network;
using ACE.Server.Network.Structure;

static class Inbound
{
    static void P(string name, (double bytes, double ns) r) => Console.WriteLine($"{name,-66} alloc={r.bytes,7:F0} B  {r.ns,8:F1} ns");
    static Action captured;
    public static void Run() { Run1(); Console.WriteLine("-- round 2 --"); Run1(); }
    static void Run1()
    {
        // MoveToState payload: GameAction header (opcode F7B1, seq, action F61C) + RawMotionState + Position + 4 ushort + byte + align
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0xF7B1u); w.Write(5u); w.Write(0xF61Cu);
        w.Write(0x1u | 0x4u);              // flags: CurrentHoldKey | ForwardCommand, 0 commands
        w.Write(2u);                        // HoldKey.Run
        w.Write(0x45000005u);               // WalkForward
        w.Write(0xA9B40019u); w.Write(84f); w.Write(7f); w.Write(94f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
        w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)3); w.Write((ushort)4); w.Write((byte)1);
        while (ms.Length % 4 != 0) w.Write((byte)0);
        var bytes = ms.ToArray();
        var wo = Stubs.MakeWorldObject();
        var session = Stubs.MakeSession();

        P("new ClientMessage(byte[]) (MemoryStream + BinaryReader + opcode read)", Program.Measure(() => new ClientMessage(bytes)));
        P("closure + delegate + ActionEventDelegate (HandleClientMessage shape)", Program.Measure(() =>
        {
            var m = (ClientMessage)null; var s = session; var info = (object)null; uint op = 1;
            var a = new ActionEventDelegate(() => { GC.KeepAlive(m); GC.KeepAlive(s); GC.KeepAlive(info); GC.KeepAlive(op); });
            GC.KeepAlive(a);
        }));
        P("MoveToState parse (ClientMessage + 2 header reads + new MoveToState)", Program.Measure(() =>
        {
            var cm = new ClientMessage(bytes); cm.Payload.ReadUInt32(); cm.Payload.ReadUInt32();
            var mts = new MoveToState(wo, cm.Payload); GC.KeepAlive(mts);
        }));
        // Talk payload: ReadString16L of 40 chars
        var ms2 = new MemoryStream(); var w2 = new BinaryWriter(ms2);
        w2.Write(0xF7B1u); w2.Write(5u); w2.Write(0x15u);
        ACE.Server.Network.Extensions.WriteString16L(w2, "Anyone selling a Pyreal Mote? PST please");
        var tb = ms2.ToArray();
        P("Talk parse: ClientMessage + ReadString16L(40) [BinaryReader.ReadChars]", Program.Measure(() =>
        {
            var cm = new ClientMessage(tb); cm.Payload.ReadUInt32(); cm.Payload.ReadUInt32(); var s = cm.Payload.ReadString16L(); GC.KeepAlive(s);
        }));
        P("Talk parse with span decode (prototype: cp1252 GetString over GetBuffer)", Program.Measure(() =>
        {
            var data = new MemoryStream(tb, 0, tb.Length, false, true); var r = new BinaryReader(data); r.ReadUInt32();
            r.ReadUInt32(); r.ReadUInt32(); var len = r.ReadUInt16(); var pos = (int)data.Position;
            var s = Cp1252.GetString(data.GetBuffer(), pos, len);
            data.Position = pos + len + ((4 - ((2 + len) & 3)) & 3); GC.KeepAlive(s);
        }));
    }
    static readonly System.Text.Encoding Cp1252 = System.Text.Encoding.GetEncoding(1252);
}
