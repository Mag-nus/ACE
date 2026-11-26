using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

/// Inbound GameAction handlers run through the real dispatch: GameActionPacket.HandleGameAction -> InboundMessageManager.HandleGameAction -> [GameAction] handler.
static class Move
{
    static byte[] Action(uint action, Action<BinaryWriter> body)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0xF7B1u); w.Write(5u); w.Write(action);
        body(w);
        while (ms.Length % 4 != 0) w.Write((byte)0);
        return ms.ToArray();
    }
    static void Pos(BinaryWriter w) { w.Write(0xA9B40019u); w.Write(84f); w.Write(7f); w.Write(94f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f); }

    public static void Run()
    {
        InboundMessageManager.Initialize();
        var s = Appraise.Build(40, 20, 12, true);
        var ex = s.Examiner;
        var session = ex.Session;
        var clear = new[] { s.Examiner, s.Target, s.Target2 }.Select(B.BundleClearer).ToArray();
        void Clear() { foreach (var c in clear) c(); }

        // payloads
        var moveFwd = Action(0xF61C, w => { w.Write(0x1u | 0x4u); w.Write(2u); w.Write(0x45000005u); Pos(w); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)3); w.Write((ushort)4); w.Write((byte)1); });
        var moveStop = Action(0xF61C, w => { w.Write(0x1u); w.Write(2u); Pos(w); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)3); w.Write((ushort)4); w.Write((byte)1); });
        var autoPos = Action(0xF753, w => { Pos(w); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)3); w.Write((ushort)4); w.Write((byte)1); });
        var ident = Action(0x00C8, w => w.Write(s.Monster.Guid.Full));
        var qhealth = Action(0x01BF, w => w.Write(s.Monster.Guid.Full));
        var qmanaEq = Action(0x0263, w => w.Write(ex.EquippedObjects.Keys.First().Full));
        var qmanaInv = Action(0x0263, w => w.Write(s.InMainPack.Guid.Full));
        var ping = Action(0x01E9, w => { });
        Console.WriteLine($"payload sizes (fragment body incl. F7B1 header): MoveToState={moveFwd.Length} B, AutonomousPosition={autoPos.Length} B, IdentifyObject={ident.Length} B, QueryHealth={qhealth.Length} B, Ping={ping.Length} B");

        Program.Measure(() => new object(), 2000, 500);   // throwaway: lets the harness itself tier up
        Console.WriteLine();
        Console.WriteLine("=== G. reference units (same run) ===");
        Program.P("  new ClientMessage(byte[]) only", Program.Measure(() => new ClientMessage(autoPos)));
        Program.P("  new GameMessageUpdatePosition(player)   [reference unit]", Program.Measure(() => new GameMessageUpdatePosition(ex)));

        Console.WriteLine();
        Console.WriteLine("=== H. parse only ===");
        Program.P("  AutonomousPosition parse (ClientMessage + hdr + Position + 4 u16 + byte)", Program.Measure(() =>
        {
            var cm = new ClientMessage(autoPos); cm.Payload.ReadUInt32(); cm.Payload.ReadUInt32();
            var p = new Position(cm.Payload); cm.Payload.ReadUInt16(); cm.Payload.ReadUInt16(); cm.Payload.ReadUInt16(); cm.Payload.ReadUInt16(); cm.Payload.ReadByte(); GC.KeepAlive(p);
        }));
        Program.P("  MoveToState parse (ClientMessage + hdr + new MoveToState)", Program.Measure(() =>
        {
            var cm = new ClientMessage(moveFwd); cm.Payload.ReadUInt32(); cm.Payload.ReadUInt32();
            var m = new MoveToState(ex, cm.Payload); GC.KeepAlive(m);
        }));

        Console.WriteLine();
        Console.WriteLine("=== I. ClientMessage + real dispatch + real handler on a real Player (2 other players known and in range) ===");
        void D(string name, byte[] payload)
        {
            Program.Try(name, () =>
            {
                GameActionPacket.HandleGameAction(new ClientMessage(payload), session);
                Clear();
                Program.P("  " + name, Program.Measure(() => { GameActionPacket.HandleGameAction(new ClientMessage(payload), session); Clear(); }));
            });
        }
        D("PingRequest", ping);
        D("AutonomousPosition", autoPos);
        D("MoveToState (run forward)", moveFwd);
        D("MoveToState (stop, hold key = Run)", moveStop);
        D("QueryHealth (monster)", qhealth);
        D("QueryItemMana (equipped item)", qmanaEq);
        D("QueryItemMana (item in main pack)", qmanaInv);
        D("IdentifyObject (monster, same target)", ident);
    }
}
