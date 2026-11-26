using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACE.Entity;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;

// Cost of the per-object loop in Landblock.TickPhysics:  foreach (wo in worldObjects.Values) wo.UpdateObjectPhysics();
// Real WorldObject.UpdateObjectPhysics (virtual), real PhysicsObj instances, objects that take the early-outs.
static class P
{
    static readonly FieldInfo physField = typeof(WorldObject).GetField("<PhysicsObj>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo transientField = typeof(PhysicsObj).GetField("TransientState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    static WorldObject MakeItem(uint guid, bool active)
    {
        var wo = Stubs.MakeWorldObject(guid);
        var po = new PhysicsObj();
        po.InitialUpdates = 5;                       // past the first-two-updates window
        if (active) po.TransientState |= TransientStateFlags.Active;
        physField.SetValue(wo, po);
        return wo;
    }
    static WorldObject MakeCreature(uint guid)
    {
        var c = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
        var po = new PhysicsObj(); po.InitialUpdates = 5; po.TransientState |= TransientStateFlags.Active;
        physField.SetValue(c, po);
        c.LastPhysicsUpdate = double.MaxValue / 4;   // always inside the 5 Hz throttle window (11 of 12 ticks in production)
        typeof(WorldObject).GetField("<Guid>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, new ObjectGuid(guid));
        return c;
    }

    static void Main(string[] args)
    {
        Console.WriteLine($"ServerGC={System.Runtime.GCSettings.IsServerGC}  cores={Environment.ProcessorCount}");
        foreach (var kind in new[] { "item-inactive", "item-active-idle", "creature-throttled", "mixed" })
            foreach (var n in new[] { 1_000, 20_000, 100_000, 300_000 })
                Run(kind, n);
    }

    static void Run(string kind, int n)
    {
        // Landblock.worldObjects is a Dictionary<ObjectGuid, WorldObject>; 150 objects per landblock here.
        const int perBlock = 150;
        var blocks = new List<Dictionary<ObjectGuid, WorldObject>>();
        var junk = new List<object>();
        var rnd = new Random(1);
        uint g = 0x80000001;
        for (int i = 0; i < n; i++)
        {
            if (i % perBlock == 0) blocks.Add(new Dictionary<ObjectGuid, WorldObject>());
            WorldObject wo = kind switch
            {
                "item-inactive" => MakeItem(g, false),
                "item-active-idle" => MakeItem(g, true),
                "creature-throttled" => MakeCreature(g),
                _ => (i % 3 == 0) ? MakeCreature(g) : MakeItem(g, i % 3 == 1),
            };
            blocks[^1][new ObjectGuid(g)] = wo; g++;
            // other live data a real object drags along (inventory, emotes, part arrays ...): spreads objects over the heap
            junk.Add(new byte[rnd.Next(2000, 6000)]);
        }
        // shuffle block order the way a server's landblocks interleave in time
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        long calls = 0; int hits = 0;
        void Tick() { foreach (var b in blocks) foreach (var wo in b.Values) { if (wo.UpdateObjectPhysics()) hits++; calls++; } }
        for (int w = 0; w < 3; w++) { var swW = Stopwatch.StartNew(); while (swW.ElapsedMilliseconds < 150) Tick(); Thread.Sleep(300); }
        calls = 0;
        double best = double.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long c0 = calls; var sw = Stopwatch.StartNew(); int ticks = 0;
            while (sw.ElapsedMilliseconds < 300) { Tick(); ticks++; }
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / (calls - c0));
        }
        Console.WriteLine($"{kind,-20} n={n,7}: {best,6:F1} ns per object per tick   -> at 60 Hz: {best * n * 60 / 1e6,8:F1} ms of CPU per second  (hits {hits})");
        GC.KeepAlive(junk);
    }
}
