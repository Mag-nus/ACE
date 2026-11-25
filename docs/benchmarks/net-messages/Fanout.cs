using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.Sequence;

/// Replicas of the broadcast fan-out shape (ObjectMaint.GetKnownPlayersValuesAsPlayer + NetworkSession.EnqueueSend(params)).
static class Fanout
{
    class WO { }
    class PlayerR : WO { }
    class Weenie { public WO WorldObject; }
    class PO { public Weenie WeenieObj; }

    static readonly object[] bundleLocks = { new object() };
    static int sink;
    // same signature shape as NetworkSession.EnqueueSend(params GameMessage[])
    static void EnqueueSendParams(params GameMessage[] messages) { foreach (var m in messages) lock (bundleLocks[0]) sink++; }
    static void EnqueueSendOne(GameMessage message) { lock (bundleLocks[0]) sink++; }
    static void EnqueueSendSpan(params ReadOnlySpan<GameMessage> messages) { foreach (var m in messages) lock (bundleLocks[0]) sink++; }

    static void P(string name, (double bytes, double ns) r) => Console.WriteLine($"{name,-66} alloc={r.bytes,7:F0} B  {r.ns,8:F1} ns");

    public static void Run()
    {
        foreach (var n in new[] { 1, 10, 50, 150 })
        {
            var known = new Dictionary<uint, PO>();
            for (uint i = 0; i < n; i++) known[i] = new PO { WeenieObj = new Weenie { WorldObject = new PlayerR() } };
            var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            P($"GetKnownPlayersValuesAsPlayer replica, {n} known players", Program.Measure(() =>
            {
                rw.EnterReadLock();
                try { var l = known.Values.Select(v => v.WeenieObj.WorldObject).OfType<PlayerR>().ToList(); GC.KeepAlive(l); }
                finally { rw.ExitReadLock(); }
            }));
        }
        GameMessage msg = null;   // the message object itself is irrelevant to the array allocation
        P("EnqueueSend(params GameMessage[]) with 1 msg  [current, per call]", Program.Measure(() => EnqueueSendParams(msg)));
        P("EnqueueSend(params GameMessage[]) with 2 msgs [current, per call]", Program.Measure(() => EnqueueSendParams(msg, msg)));
        P("EnqueueSend(GameMessage) single overload", Program.Measure(() => EnqueueSendOne(msg)));
        P("EnqueueSend(params ReadOnlySpan<GameMessage>) with 2 msgs", Program.Measure(() => EnqueueSendSpan(msg, msg)));

        var wo = Stubs.MakeWorldObject();
        var sm = wo.Sequences;
        var slots = new ISequence[9];
        P("SequenceManager.GetSequence(ObjectPosition)  [lock + Dictionary]", Program.Measure(() => sm.GetSequence(SequenceType.ObjectPosition, 0)));
        P("array-slot lookup with lock-free lazy create (prototype)", Program.Measure(() =>
        {
            var s = Volatile.Read(ref slots[(int)SequenceType.ObjectPosition]);
            if (s == null) { Interlocked.CompareExchange(ref slots[(int)SequenceType.ObjectPosition], new UShortSequence(), null); s = slots[(int)SequenceType.ObjectPosition]; }
            GC.KeepAlive(s);
        }));
    }
}
