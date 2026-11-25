using System;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Text;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Sequence;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

/// Prototypes. Each one is written as a drop-in for the real code and verified byte-for-byte against it.
static class Proto
{
    // ---- Extensions.cs replacements -------------------------------------------------------------

    // Holder class so the Encoding is resolved on first string write, not at Extensions type-init
    // (CodePagesEncodingProvider must already be registered; ACE registers it at the top of Main).
    static class Cp1252Holder { public static readonly Encoding Value = Encoding.GetEncoding(1252); }

    static readonly byte[] Zeros = new byte[4];

    public static void WriteString16L(BinaryWriter writer, string data)
    {
        data ??= "";
        writer.Write((ushort)data.Length);

        // cp1252 is single-byte: encoded length == data.Length. Bounded stackalloc, pool above that.
        if (data.Length <= 256)
        {
            Span<byte> buf = stackalloc byte[256];
            int n = Cp1252Holder.Value.GetBytes(data, buf);
            writer.Write(buf.Slice(0, n));
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(data.Length);
            int n = Cp1252Holder.Value.GetBytes(data, 0, data.Length, rented, 0);
            writer.Write(rented, 0, n);
            ArrayPool<byte>.Shared.Return(rented);
        }

        int pad = (int)((4 - ((2 + (uint)data.Length) & 3)) & 3);
        if (pad != 0) writer.Write(Zeros, 0, pad);
    }

    // ASCII fast path variant (identical bytes: cp1252 maps 0x00-0x7F 1:1)
    public static void WriteString16L_Ascii(BinaryWriter writer, string data)
    {
        data ??= "";
        writer.Write((ushort)data.Length);
        if (data.Length <= 256)
        {
            Span<byte> buf = stackalloc byte[256];
            int n;
            if (System.Text.Ascii.FromUtf16(data, buf, out n) != OperationStatus.Done)
                n = Cp1252Holder.Value.GetBytes(data, buf);
            writer.Write(buf.Slice(0, n));
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(data.Length);
            int n = Cp1252Holder.Value.GetBytes(data, 0, data.Length, rented, 0);
            writer.Write(rented, 0, n);
            ArrayPool<byte>.Shared.Return(rented);
        }
        int pad = (int)((4 - ((2 + (uint)data.Length) & 3)) & 3);
        if (pad != 0) writer.Write(Zeros, 0, pad);
    }

    public static void WritePackedDword(BinaryWriter writer, uint value)
    {
        if (value <= 32767) writer.Write((ushort)value);
        else writer.Write((value << 16) | ((value >> 16) | 0x8000));
    }

    // ---- Sequence writes without byte[] ---------------------------------------------------------
    // Would live on SequenceManager as WriteCurrentSequence / WriteNextSequence. Falls back to the
    // byte[] API for any ISequence implementation it does not know (mods can SetSequence()).
    public static void WriteCurrent(BinaryWriter w, SequenceManager sm, SequenceType type, uint prop = 0)
    {
        var s = sm.GetSequence(type, prop);
        switch (s)
        {
            case UShortSequence u: w.Write(u.CurrentValue); break;
            case ByteSequence b: w.Write(b.CurrentValue); break;
            case UIntSequence i: w.Write(i.CurrentValue); break;
            default: w.Write(s.CurrentBytes); break;
        }
    }
    public static void WriteNext(BinaryWriter w, SequenceManager sm, SequenceType type, uint prop = 0)
    {
        var s = sm.GetSequence(type, prop);
        switch (s)
        {
            case UShortSequence u: w.Write(u.NextValue); break;
            case ByteSequence b: w.Write(b.NextValue); break;
            case UIntSequence i: w.Write(i.NextValue); break;
            default: w.Write(s.NextBytes); break;
        }
    }

    // ---- Message prototypes ---------------------------------------------------------------------

    public class UpdatePositionLean : GameMessage
    {
        public UpdatePositionLean(WorldObject wo, bool adminMove = false)
            : base(GameMessageOpcode.UpdatePosition, GameMessageGroup.SmartboxQueue, 68)
        {
            var loc = wo.Location;
            var rot = loc.Rotation;
            var velocity = wo.PhysicsObj != null ? wo.PhysicsObj.Velocity : Vector3.Zero;
            var placement = wo.Placement;

            var flags = PositionFlags.None;
            if (velocity != Vector3.Zero) flags |= PositionFlags.HasVelocity;
            if (placement != null) flags |= PositionFlags.HasPlacementID;
            if (wo.PhysicsObj != null && (wo.PhysicsObj.TransientState & ACE.Server.Physics.TransientStateFlags.OnWalkable) != 0) flags |= PositionFlags.IsGrounded;
            if (rot.W == 0.0f) flags |= PositionFlags.OrientationHasNoW;
            if (rot.X == 0.0f) flags |= PositionFlags.OrientationHasNoX;
            if (rot.Y == 0.0f) flags |= PositionFlags.OrientationHasNoY;
            if (rot.Z == 0.0f) flags |= PositionFlags.OrientationHasNoZ;

            Writer.Write(wo.Guid.Full);
            Writer.Write((uint)flags);
            Writer.Write(loc.Cell);
            var p = loc.Pos;
            Writer.Write(p.X); Writer.Write(p.Y); Writer.Write(p.Z);
            if ((flags & PositionFlags.OrientationHasNoW) == 0) Writer.Write(rot.W);
            if ((flags & PositionFlags.OrientationHasNoX) == 0) Writer.Write(rot.X);
            if ((flags & PositionFlags.OrientationHasNoY) == 0) Writer.Write(rot.Y);
            if ((flags & PositionFlags.OrientationHasNoZ) == 0) Writer.Write(rot.Z);
            if ((flags & PositionFlags.HasVelocity) != 0) { Writer.Write(velocity.X); Writer.Write(velocity.Y); Writer.Write(velocity.Z); }
            if ((flags & PositionFlags.HasPlacementID) != 0) Writer.Write((uint)placement);

            var sm = wo.Sequences;
            WriteCurrent(Writer, sm, SequenceType.ObjectInstance);
            WriteNext(Writer, sm, SequenceType.ObjectPosition);
            if (adminMove) WriteNext(Writer, sm, SequenceType.ObjectTeleport);
            else WriteCurrent(Writer, sm, SequenceType.ObjectTeleport);
            WriteCurrent(Writer, sm, SequenceType.ObjectForcePosition);
        }
    }

    /// UpdatePositionLean + sequences fetched from pre-resolved slots (simulates SequenceManager array slots, ~2 ns/lookup)
    public class UpdatePositionLeanSlots : GameMessage
    {
        public UpdatePositionLeanSlots(WorldObject wo, UShortSequence inst, UShortSequence pos, UShortSequence tele, UShortSequence force)
            : base(GameMessageOpcode.UpdatePosition, GameMessageGroup.SmartboxQueue, 68)
        {
            var loc = wo.Location;
            var rot = loc.Rotation;
            var velocity = wo.PhysicsObj != null ? wo.PhysicsObj.Velocity : Vector3.Zero;
            var placement = wo.Placement;
            var flags = PositionFlags.None;
            if (velocity != Vector3.Zero) flags |= PositionFlags.HasVelocity;
            if (placement != null) flags |= PositionFlags.HasPlacementID;
            if (rot.W == 0.0f) flags |= PositionFlags.OrientationHasNoW;
            if (rot.X == 0.0f) flags |= PositionFlags.OrientationHasNoX;
            if (rot.Y == 0.0f) flags |= PositionFlags.OrientationHasNoY;
            if (rot.Z == 0.0f) flags |= PositionFlags.OrientationHasNoZ;
            Writer.Write(wo.Guid.Full);
            Writer.Write((uint)flags);
            Writer.Write(loc.Cell);
            var p = loc.Pos;
            Writer.Write(p.X); Writer.Write(p.Y); Writer.Write(p.Z);
            if ((flags & PositionFlags.OrientationHasNoW) == 0) Writer.Write(rot.W);
            if ((flags & PositionFlags.OrientationHasNoX) == 0) Writer.Write(rot.X);
            if ((flags & PositionFlags.OrientationHasNoY) == 0) Writer.Write(rot.Y);
            if ((flags & PositionFlags.OrientationHasNoZ) == 0) Writer.Write(rot.Z);
            if ((flags & PositionFlags.HasPlacementID) != 0) Writer.Write((uint)placement);
            Writer.Write(inst.CurrentValue); Writer.Write(pos.NextValue); Writer.Write(tele.CurrentValue); Writer.Write(force.CurrentValue);
        }
    }

    /// UpdatePosition with ONLY the sequence fix applied (PositionPack/Origin kept, for the
    /// compatibility-preserving option: GameMessageUpdatePosition.PositionPack is a public field).
    public class UpdatePositionSeqOnly : GameMessage
    {
        public UpdatePositionSeqOnly(WorldObject wo)
            : base(GameMessageOpcode.UpdatePosition, GameMessageGroup.SmartboxQueue, 68)
        {
            var pp = new PositionPackNoSeq(wo);
            Writer.Write(wo.Guid.Full);
            Writer.Write((uint)pp.Flags);
            Writer.Write(pp.Origin.CellID); Writer.Write(pp.Origin.Position.X); Writer.Write(pp.Origin.Position.Y); Writer.Write(pp.Origin.Position.Z);
            if ((pp.Flags & PositionFlags.OrientationHasNoW) == 0) Writer.Write(pp.Rotation.W);
            if ((pp.Flags & PositionFlags.OrientationHasNoX) == 0) Writer.Write(pp.Rotation.X);
            if ((pp.Flags & PositionFlags.OrientationHasNoY) == 0) Writer.Write(pp.Rotation.Y);
            if ((pp.Flags & PositionFlags.OrientationHasNoZ) == 0) Writer.Write(pp.Rotation.Z);
            var sm = wo.Sequences;
            WriteCurrent(Writer, sm, SequenceType.ObjectInstance);
            WriteNext(Writer, sm, SequenceType.ObjectPosition);
            WriteCurrent(Writer, sm, SequenceType.ObjectTeleport);
            WriteCurrent(Writer, sm, SequenceType.ObjectForcePosition);
        }
        class PositionPackNoSeq
        {
            public PositionFlags Flags; public Origin Origin; public Quaternion Rotation;
            public PositionPackNoSeq(WorldObject wo)
            {
                Origin = new Origin(wo.Location.Cell, wo.Location.Pos);
                Rotation = wo.Location.Rotation;
                var pl = wo.Placement;
                if (pl != null) Flags |= PositionFlags.HasPlacementID;
                if (Rotation.W == 0) Flags |= PositionFlags.OrientationHasNoW;
                if (Rotation.X == 0) Flags |= PositionFlags.OrientationHasNoX;
                if (Rotation.Y == 0) Flags |= PositionFlags.OrientationHasNoY;
                if (Rotation.Z == 0) Flags |= PositionFlags.OrientationHasNoZ;
            }
        }
    }

    /// UpdateMotion with the same MovementData object graph, but sequences written as scalars.
    public class UpdateMotionSeqOnly : GameMessage
    {
        public UpdateMotionSeqOnly(WorldObject wo, Motion motion)
            : base(GameMessageOpcode.Motion, GameMessageGroup.SmartboxQueue, 88)
        {
            var md = new MovementData(wo, motion);
            var sm = wo.Sequences;
            Writer.Write(wo.Guid.Full);
            WriteCurrent(Writer, sm, SequenceType.ObjectInstance);
            // MovementDataExtensions.Write(header: true) with the sequence part inlined:
            WriteNext(Writer, sm, SequenceType.ObjectMovement);
            if (md.IsAutonomous) WriteCurrent(Writer, sm, SequenceType.ObjectServerControl);
            else WriteNext(Writer, sm, SequenceType.ObjectServerControl);
            Writer.Write(md.IsAutonomous ? (byte)1 : (byte)0);
            Writer.Align();
            Writer.Write(md, false);
        }
    }

    public class SystemChatLean : GameMessage
    {
        public SystemChatLean(string message, ChatMessageType t)
            : base(GameMessageOpcode.ServerMessage, GameMessageGroup.UIQueue, 4 + 4 + ((2 + (message?.Length ?? 0) + 3) & ~3))
        {
            WriteString16L(Writer, message);
            Writer.Write((int)t);
        }
    }

    public class HearSpeechLean : GameMessage
    {
        public HearSpeechLean(string messageText, string senderName, uint senderID, ChatMessageType chatMessageType)
            : base(GameMessageOpcode.HearSpeech, GameMessageGroup.UIQueue,
                   4 + ((2 + (messageText?.Length ?? 0) + 3) & ~3) + ((2 + (senderName?.Length ?? 0) + 3) & ~3) + 8)
        {
            WriteString16L(Writer, messageText);
            WriteString16L(Writer, senderName);
            Writer.Write(senderID);
            Writer.Write((uint)chatMessageType);
        }
    }

    /// GameEvent with the capacity bug fixed (+16 for the GameEvent header). Mirrors GameEventWeenieError.
    public class WeenieErrorFixed : GameEventMessage
    {
        public WeenieErrorFixed(Session session, WeenieError errorType)
            : base(GameEventType.WeenieError, GameMessageGroup.UIQueue, session, 8 + 16) { Writer.Write((uint)errorType); }
    }
    public class UpdateHealthFixed : GameEventMessage
    {
        public UpdateHealthFixed(Session session, uint id, float h)
            : base(GameEventType.UpdateHealth, GameMessageGroup.UIQueue, session, 12 + 16) { Writer.Write(id); Writer.Write(h); }
    }

    // ---- runner ---------------------------------------------------------------------------------

    static void P(string name, (double bytes, double ns) r) => Console.WriteLine($"{name,-58} alloc={r.bytes,7:F0} B  {r.ns,8:F1} ns");

    static bool Same(GameMessage a, GameMessage b)
    {
        var x = a.Data.ToArray(); var y = b.Data.ToArray();
        return x.AsSpan().SequenceEqual(y);
    }

    public static void Run()
    {
        // ---- correctness: byte-identical output on twin objects in identical sequence state ----
        var w1 = Stubs.MakeWorldObject(); var w2 = Stubs.MakeWorldObject();
        bool ok = true;
        for (int i = 0; i < 70000; i++)      // run past the ushort wrap of ObjectPosition
            ok &= Same(new GameMessageUpdatePosition(w1), new UpdatePositionLean(w2));
        Console.WriteLine($"UpdatePositionLean byte-identical over 70,000 msgs (incl. ushort wrap): {ok}");
        ok = Same(new GameMessageUpdatePosition(w1, true), new UpdatePositionLean(w2, true));
        Console.WriteLine($"UpdatePositionLean adminMove byte-identical: {ok}");
        w1.Placement = Placement.Resting; w2.Placement = Placement.Resting;
        w1.Location = new ACE.Entity.Position(0x01D90108, new Vector3(1, 2, 3), new Quaternion(0, 0, 0, 1));
        w2.Location = new ACE.Entity.Position(0x01D90108, new Vector3(1, 2, 3), new Quaternion(0, 0, 0, 1));
        ok = Same(new GameMessageUpdatePosition(w1), new UpdatePositionLean(w2));
        Console.WriteLine($"UpdatePositionLean (placement + axis-aligned rotation) byte-identical: {ok}  len={new UpdatePositionLean(w2).Data.Length}");

        ok = true;
        var motions = new[] {
            new Motion(MotionStance.NonCombat, MotionCommand.Ready),
            new Motion(MotionStance.SwordCombat, MotionCommand.AttackHigh1, 1.25f),
            new Motion(MotionStance.NonCombat, MotionCommand.WalkForward, 0.5f) { IsAutonomous = true },
        };
        for (int i = 0; i < 70000; i++)
            ok &= Same(new GameMessageUpdateMotion(w1, motions[i % 3]), new UpdateMotionSeqOnly(w2, motions[i % 3]));
        Console.WriteLine($"UpdateMotionSeqOnly byte-identical over 70,000 msgs: {ok}");

        var strings = new[] { "", "a", "ab", "abc", "abcd", "Drudge Skulker", "Café – “quoted” €", new string('x', 300), "Anyone selling a Pyreal Mote? PST please" };
        ok = true;
        foreach (var str in strings)
        {
            var m1 = new MemoryStream(); var b1 = new BinaryWriter(m1); b1.WriteString16L(str);
            var m2 = new MemoryStream(); var b2 = new BinaryWriter(m2); WriteString16L(b2, str);
            var m3 = new MemoryStream(); var b3 = new BinaryWriter(m3); WriteString16L_Ascii(b3, str);
            ok &= m1.ToArray().AsSpan().SequenceEqual(m2.ToArray()) && m1.ToArray().AsSpan().SequenceEqual(m3.ToArray());
        }
        foreach (uint v in new uint[] { 0, 1, 32767, 32768, 65535, 65536, 0x7FFFFFFF, 0x12345678 })
        {
            var m1 = new MemoryStream(); var b1 = new BinaryWriter(m1); b1.WritePackedDword(v);
            var m2 = new MemoryStream(); var b2 = new BinaryWriter(m2); WritePackedDword(b2, v);
            ok &= m1.ToArray().AsSpan().SequenceEqual(m2.ToArray());
        }
        Console.WriteLine($"WriteString16L / WritePackedDword prototypes byte-identical (incl. non-ASCII cp1252, 300-char): {ok}");
        var s1 = Stubs.MakeSession(); var s2 = Stubs.MakeSession();
        Console.WriteLine($"WeenieErrorFixed byte-identical: {Same(new GameEventWeenieError(s1, WeenieError.YoureTooBusy), new WeenieErrorFixed(s2, WeenieError.YoureTooBusy))}");
        Console.WriteLine($"SystemChatLean byte-identical: {Same(new GameMessageSystemChat("Café hello", ChatMessageType.Broadcast), new SystemChatLean("Café hello", ChatMessageType.Broadcast))}");
        Console.WriteLine($"HearSpeechLean byte-identical: {Same(new GameMessageHearSpeech("hello there", "Bob", 5, ChatMessageType.Speech), new HearSpeechLean("hello there", "Bob", 5, ChatMessageType.Speech))}");

        // ---- cost, current vs prototype, same run ----
        Console.WriteLine();
        var wo = Stubs.MakeWorldObject();
        var s = Stubs.MakeSession();
        var mo2 = 0; var mo = new Motion(MotionStance.NonCombat, MotionCommand.Ready);
        string c40 = "Anyone selling a Pyreal Mote? PST please", n14 = "Drudge Skulker";
        for (int round = 0; round < 2; round++)
        {
            Console.WriteLine($"--- round {round + 1} ---");
            P("GameMessageUpdatePosition [current]", Program.Measure(() => new GameMessageUpdatePosition(wo)));
            P("UpdatePositionSeqOnly (keep intermediates, no seq byte[])", Program.Measure(() => new UpdatePositionSeqOnly(wo)));
            P("UpdatePositionLean (no PositionPack/Origin/seq byte[])", Program.Measure(() => new UpdatePositionLean(wo)));
            var sm0 = wo.Sequences;
            var si = (UShortSequence)sm0.GetSequence(SequenceType.ObjectInstance, 0); var sp = (UShortSequence)sm0.GetSequence(SequenceType.ObjectPosition, 0);
            var st = (UShortSequence)sm0.GetSequence(SequenceType.ObjectTeleport, 0); var sf = (UShortSequence)sm0.GetSequence(SequenceType.ObjectForcePosition, 0);
            P("UpdatePositionLean + sequence slots (no lock/dict)", Program.Measure(() => new UpdatePositionLeanSlots(wo, si, sp, st, sf)));
            P("GameMessageUpdateMotion(wo, motion) [current]", Program.Measure(() => new GameMessageUpdateMotion(wo, mo)));
            P("UpdateMotionSeqOnly", Program.Measure(() => new UpdateMotionSeqOnly(wo, mo)));
            P("GameMessageSystemChat(40) [current]", Program.Measure(() => new GameMessageSystemChat(c40, ChatMessageType.Broadcast)));
            P("SystemChatLean(40)", Program.Measure(() => new SystemChatLean(c40, ChatMessageType.Broadcast)));
            P("GameMessageHearSpeech(40,14) [current]", Program.Measure(() => new GameMessageHearSpeech(c40, n14, 1, ChatMessageType.Speech)));
            P("HearSpeechLean(40,14)", Program.Measure(() => new HearSpeechLean(c40, n14, 1, ChatMessageType.Speech)));
            P("GameEventWeenieError [current]", Program.Measure(() => new GameEventWeenieError(s, WeenieError.YoureTooBusy)));
            P("WeenieErrorFixed (+16 capacity)", Program.Measure(() => new WeenieErrorFixed(s, WeenieError.YoureTooBusy)));
            P("GameEventUpdateHealth [current]", Program.Measure(() => new GameEventUpdateHealth(s, 1, 0.5f)));
            P("UpdateHealthFixed (+16 capacity)", Program.Measure(() => new UpdateHealthFixed(s, 1, 0.5f)));
        }
    }
}
