using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

public static class Program
{
    static string Opt(string[] args, string name, string def = null)
    {
        foreach (var a in args) if (a.StartsWith("--" + name + "=")) return a.Substring(name.Length + 3);
        return def;
    }
    static bool Flag(string[] args, string name) => args.Contains("--" + name);

    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "help";
        var pos = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();

        // fixes are attached before anything else touches the physics code
        var fixes = (Opt(args, "harmony", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (fixes.Length > 0) Fixes.Apply(fixes);

        switch (mode)
        {
            case "list":
                foreach (var s in Scenarios.All) Console.WriteLine(s);
                return 0;
            case "measure":
                foreach (var id in pos) Modes.Measure(id, int.Parse(Opt(args, "reps", "9")), Flag(args, "classes"));
                return 0;
            case "count":
                Modes.Count(pos[0], int.Parse(Opt(args, "n", "4000")));
                return 0;
            case "types":
                TypeBreakdown.Run(pos[0], int.Parse(Opt(args, "mb", "600")));
                return 0;
            case "walk":
                Modes.Walk(int.Parse(Opt(args, "n", "100000")), int.Parse(Opt(args, "seed", "1")), Opt(args, "dump"), Opt(args, "compare"));
                return 0;
            case "freq":
                Modes.Freq();
                return 0;
            case "harmony":
                HarmonyOverhead.Run(pos.Length > 0 ? pos[0] : "pristine", Flag(args, "client"));
                return 0;
            case "place":
                Modes.Place(int.Parse(Opt(args, "n", "1500")), float.Parse(Opt(args, "side", "60")));
                return 0;
            case "lbload":
                Modes.LandblockLoad();
                return 0;
            case "smoke":
                Modes.Smoke();
                return 0;
            default:
                Console.WriteLine("modes: list | measure <scenario>... [--harmony=pg1|pg1noop] [--reps=9] [--classes] | count <scenario> [--n=4000] | types <scenario> [--mb=600]");
                Console.WriteLine("       walk [--n=100000] [--seed=1] [--dump=file] [--compare=file] | freq | harmony <pristine|noop-prefix|old|transpiler> [--client] | lbload | smoke");
                Console.WriteLine("fix variants are separate run directories: out/<variant>/PhysE2E.dll (see make_variants.py, build.sh)");
                return 1;
        }
    }
}

public static class Modes
{
    public static void Header()
    {
        Console.WriteLine($"# runtime {Environment.Version}, ServerGC={System.Runtime.GCSettings.IsServerGC}, Server={PhysicsEngine.Instance?.Server}, fixes={Fixes.Label}");
    }

    /// <summary>
    /// Placement check: n creatures entered into the world (enter_world -> FindPlacementPosition / FindPlacementPos ring search)
    /// at random spots in a side x side metre square of the hills region, dense enough that most have to be pushed off an
    /// occupied spot. Prints a hash over every object's final cell / position, for comparison between variants.
    /// </summary>
    public static void Place(int n, float side)
    {
        Scenarios.BuildWorlds();
        Header();
        var r = Scenarios.Hills;
        const int rounds = 6;
        ulong firstHash = 0; bool same = true; string last = "";
        for (int round = 0; round < rounds; round++)
        {
            var rnd = new Random(99);
            var objs = new List<PhysicsObj>(n);
            ulong hash = 14695981039346656037UL; int placed = 0, moved = 0; double sumShift = 0;
            long bytes = 0, ticks = 0;
            for (int i = 0; i < n; i++)
            {
                float gx = 650f + (float)rnd.NextDouble() * side, gy = 650f + (float)rnd.NextDouble() * side;
                float heading = (float)rnd.NextDouble() * 360f;
                var o = Objects.Make(Scenarios.SetupCreature, Scenarios.CreatureState, creature: true, monster: true, movementManager: true);
                var (cell, origin) = r.At(gx, gy);
                var q = Scenarios.Heading(heading);
                long b0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                bool ok = Objects.Place(o, cell, origin, q);        // LScape.get_landcell + PhysicsObj.enter_world
                ticks += Stopwatch.GetTimestamp() - t0; bytes += GC.GetAllocatedBytesForCurrentThread() - b0;
                objs.Add(o);
                hash = (hash ^ (ok ? 1u : 0u)) * 1099511628211UL;
                if (!ok) continue;
                placed++;
                hash = Bench.HashStep(hash, o);
                var p = o.Position;
                float px = ((int)(p.ObjCellID >> 24) - r.Bx0) * 192f + p.Frame.Origin.X, py = ((int)((p.ObjCellID >> 16) & 0xFF) - r.By0) * 192f + p.Frame.Origin.Y;
                double shift = Math.Sqrt((px - gx) * (px - gx) + (py - gy) * (py - gy));
                if (shift > 0.01) { moved++; sumShift += shift; }
            }
            if (round == 0) firstHash = hash; else if (hash != firstHash) same = false;
            last = $"placed {placed}, pushed off the requested spot {moved} (mean shift {(moved > 0 ? sumShift / moved : 0):F2} m), hash={hash:X16}; last round: {bytes / n} B and {ticks * 1e6 / Stopwatch.Frequency / n:F1} us per enter_world";
            foreach (var o in objs) Objects.Remove(o);
            Objects.KeepAlive.Clear();
            Thread.Sleep(300);
        }
        Console.WriteLine($"place [{Fixes.Label}] n={n} in {side}x{side} m, {rounds} rounds (same hash every round: {same}): {last}");
    }

    /// <summary>PG.7: cost of constructing one physics Landblock (real ctor + PostInit) from a parsed CellLandblock.</summary>
    public static void LandblockLoad()
    {
        World.Init();
        Header();
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0x7070FFFFu); w.Write(0u);
        for (int i = 0; i < 81; i++) w.Write((ushort)0);
        for (int i = 0; i < 81; i++) w.Write((byte)(40 + (i * 7) % 13));
        while (ms.Position % 4 != 0) w.Write((byte)0);
        var clb = new ACE.DatLoader.FileTypes.CellLandblock();
        ms.Position = 0; using (var r = new BinaryReader(ms)) clb.Unpack(r);
        object keep = null;
        for (int round = 0; round < 4; round++) { for (int i = 0; i < 300; i++) keep = new ACE.Server.Physics.Common.Landblock(clb); Thread.Sleep(300); }
        const int n = 400;
        var arr = new object[n];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long m0 = GC.GetTotalMemory(true);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) arr[i] = new ACE.Server.Physics.Common.Landblock(clb);
        long t1 = Stopwatch.GetTimestamp();
        long b1 = GC.GetAllocatedBytesForCurrentThread();
        long m1 = GC.GetTotalMemory(true);
        GC.KeepAlive(arr); GC.KeepAlive(keep);
        Console.WriteLine($"[{Fixes.Label}] new Physics.Common.Landblock(CellLandblock): allocated {(b1 - b0) / n} B, retained {(m1 - m0) / n} B, {(t1 - t0) * 1e6 / Stopwatch.Frequency / n:F1} us each (n={n})");
    }

    public static void Smoke()
    {
        var s = Scenarios.Create("b");
        var a = s.Obj;
        Console.WriteLine($"placed cell={a.Position.ObjCellID:X8} origin={a.Position.Frame.Origin} ts={a.TransientState} shadows={Bench.LiveShadows(a)} lbObjs={a.CurLandblock?.ServerObjects.Count}");
        for (int i = 0; i < 40; i++)
        {
            s.Step();
            Console.WriteLine($" upd {i}: cell={a.Position.ObjCellID:X8} origin={a.Position.Frame.Origin} ts={a.TransientState} cachedVel={a.CachedVelocity} shadows={Bench.LiveShadows(a)}");
        }
    }

    /// <summary>Bytes (exact) and time per update_object for one scenario, plus a hash of the first 20,000 positions.</summary>
    public static void Measure(string id, int reps, bool classes)
    {
        var s = Scenarios.Create(id);
        Header();
        // deterministic prefix: hash of the first 20k updates, comparable across processes / fix variants
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < 20000; i++) { s.Step(); hash = Bench.HashStep(hash, s.Obj); }
        Bench.Warm(s);
        int n = Bench.Calibrate(s);
        var early = Bench.Run(s, n, 1);
        Thread.Sleep(300);
        var agg = Bench.Run(s, n, reps);
        Console.WriteLine($"{id,-14} {s.Name}{(s.Note.Length > 0 ? "  (" + s.Note + ")" : "")}  [ShadowObjects dictionary entries: {s.Obj.ShadowObjects.Count}, live: {Bench.LiveShadows(s.Obj)}]");
        Console.WriteLine($"   [{Fixes.Label}] per update_object (dt {s.Dt:F4} s): {agg.BytesMin,9:F1} B (reps {agg.BytesMin:F1}..{agg.BytesMax:F1}; first rep {early.BytesMin:F1})   time best {agg.NsBest,8:F0} ns  median {agg.NsMedian,8:F0} ns   n={agg.N}x{reps}   hash20k={hash:X16}");
        if (classes)
        {
            var samples = Bench.SampleRun(s, Math.Min(200_000, n * 2));
            Bench.PrintClasses(samples);
        }
    }

    /// <summary>Call counts per update_object, overall and by update class. Harmony counting prefixes attached: never used for time/bytes.</summary>
    public static void Count(string id, int n)
    {
        Counters.Attach();
        var s = Scenarios.Create(id);
        Header();
        for (int i = 0; i < 500; i++) s.Step();      // settle (placement artefacts, first-contact)
        var names = Counters.Names;
        var buckets = new Dictionary<(int kind, int cells), (long[] sum, int n)>();
        var total = new long[names.Count];
        var o = s.Obj;
        for (int i = 0; i < n; i++)
        {
            uint c0 = o.Position.ObjCellID;
            var before = Counters.Snapshot();
            s.Step();
            var after = Counters.C;
            uint c1 = o.Position.ObjCellID;
            int kind = c0 == c1 ? 0 : (c0 >> 16) == (c1 >> 16) ? 1 : 2;
            var key = (kind, Bench.LiveShadows(o));
            if (!buckets.TryGetValue(key, out var b)) b = (new long[names.Count], 0);
            for (int k = 0; k < names.Count; k++) { long d = after[k] - before[k]; b.sum[k] += d; total[k] += d; }
            buckets[key] = (b.sum, b.n + 1);
        }
        Console.WriteLine($"{id}: {s.Name}   [{Fixes.Label}]   {n} updates, dt {s.Dt:F4}");
        var keys = buckets.Keys.OrderBy(k => k.kind).ThenBy(k => k.cells).ToList();
        string[] kn = { "same", "cell", "block" };
        Console.Write($"   {"calls per update_object",-58} {"all",8}");
        foreach (var k in keys) Console.Write($" {kn[k.kind] + "/" + k.cells + "c",9}");
        Console.WriteLine();
        Console.Write($"   {"(number of updates in class)",-58} {n,8}");
        foreach (var k in keys) Console.Write($" {buckets[k].n,9}");
        Console.WriteLine();
        for (int i = 0; i < names.Count; i++)
        {
            if (total[i] == 0) continue;
            Console.Write($"   {names[i],-58} {(double)total[i] / n,8:F2}");
            foreach (var k in keys) Console.Write($" {(double)buckets[k].sum[i] / buckets[k].n,9:F2}");
            Console.WriteLine();
        }
    }

    /// <summary>Long deterministic random walk; prints a hash over every update and can dump / compare the full trajectory.</summary>
    public static void Walk(int n, int seed, string dump, string compare)
    {
        var rw = Scenarios.MakeRandomWalk(seed);
        Header();
        var s = rw.S; var o = s.Obj;
        var traj = new Bench.TrajPoint[n];
        ulong hash = 14695981039346656037UL;
        var cells = new HashSet<uint>(); int cellChanges = 0, blockChanges = 0, touching = 0, sliding = 0, stuck = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            uint c0 = o.Position.ObjCellID;
            var p0 = s.Global();
            s.Step();
            uint c1 = o.Position.ObjCellID;
            hash = Bench.HashStep(hash, o);
            traj[i] = Bench.Point(o);
            cells.Add(c1);
            if (c0 != c1) { cellChanges++; if ((c0 >> 16) != (c1 >> 16)) blockChanges++; }
            if (o.CollisionTable.Count > 0) touching++;
            if ((o.TransientState & TransientStateFlags.Sliding) != 0) sliding++;
            if (o.PartArray.Sequence.Velocity.Y > 0 && Vector2.Distance(p0, s.Global()) < 1e-4f) stuck++;
        }
        sw.Stop();
        Console.WriteLine($"walk seed={seed} n={n} [{Fixes.Label}] obstacles={rw.Obstacles}: hash={hash:X16}  final cell={o.Position.ObjCellID:X8} origin={o.Position.Frame.Origin}");
        Console.WriteLine($"   distinct cells {cells.Count}, cell changes {cellChanges}, landblock changes {blockChanges}, updates with a live collision record {touching}, with Sliding set {sliding}, not moving although driven {stuck}; {sw.Elapsed.TotalMilliseconds / n * 1000:F2} us/update (untimed mode, incl. bookkeeping)");
        if (dump != null)
        {
            using var w = new BinaryWriter(File.Create(dump));
            w.Write(n);
            foreach (var p in traj) { w.Write(p.Cell); w.Write(p.Origin.X); w.Write(p.Origin.Y); w.Write(p.Origin.Z); w.Write(p.Rot.X); w.Write(p.Rot.Y); w.Write(p.Rot.Z); w.Write(p.Rot.W); w.Write(p.Shadows); w.Write(p.TS); }
            Console.WriteLine($"   trajectory written to {dump}");
        }
        if (compare != null)
        {
            using var r = new BinaryReader(File.OpenRead(compare));
            int m = r.ReadInt32(); int mism = 0, first = -1, shadowOnly = 0;
            for (int i = 0; i < Math.Min(n, m); i++)
            {
                uint cell = r.ReadUInt32(); var org = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); uint sh = r.ReadUInt32(); int ts = r.ReadInt32();
                bool posSame = cell == traj[i].Cell && org == traj[i].Origin && rot == traj[i].Rot && ts == traj[i].TS;
                if (!posSame) { mism++; if (first < 0) first = i; }
                else if (sh != traj[i].Shadows) shadowOnly++;
            }
            Console.WriteLine($"   compared with {compare} ({m} updates): position/cell/orientation/state mismatches {mism} (first at {first}); updates with identical position but a different shadow-cell set {shadowOnly}");
        }
    }

    /// <summary>update_object cadence under controlled time: how many UpdateObjectInternal calls and transitions per call.</summary>
    public static void Freq()
    {
        Counters.Attach();
        var s = Scenarios.Create("a");
        Header();
        var o = s.Obj;
        int iUO = Counters.Names.IndexOf("PhysicsObj.update_object"), iUI = Counters.Names.IndexOf("PhysicsObj.UpdateObjectInternal"), iTr = Counters.Names.IndexOf("PhysicsObj.transition");
        Console.WriteLine($"TickRate={PhysicsObj.TickRate:R} (float)  MinQuantum={PhysicsGlobals.MinQuantum:R}  MaxQuantum={PhysicsGlobals.MaxQuantum:R}  HugeQuantum={PhysicsGlobals.HugeQuantum:R}");
        Console.WriteLine("moving creature (4 m/s), update_object called once per step with the clock advanced by dt:");
        foreach (var (label, dt, jitter) in new[]
        {
            ("1/60 exactly (world tick at exactly 60 Hz)", 1.0 / 60, 0.0),
            ("1/60 + 0.2 ms", 1.0 / 60 + 0.0002, 0.0),
            ("1/60 with +-2 ms jitter", 1.0 / 60, 0.002),
            ("1/30 exactly", 1.0 / 30, 0.0),
            ("1/30 + 0.1 ms", 1.0 / 30 + 0.0001, 0.0),
            ("0.05", 0.05, 0.0),
            ("0.1 exactly", 0.1, 0.0),
            ("0.2 (monster tick)", 0.2, 0.0),
            ("0.2 with +-10 ms jitter", 0.2, 0.01),
            ("0.25", 0.25, 0.0),
            ("0.3", 0.3, 0.0),
            ("1.0", 1.0, 0.0),
            ("2.5 (> HugeQuantum)", 2.5, 0.0),
        })
        {
            // resync the object's clock
            o.UpdateTime = World.Time;
            var rnd = new Random(7);
            var b = Counters.Snapshot();
            int steps = 3000; int returnedTrue = 0; double simulated = 0;
            var hist = new SortedDictionary<long, int>();
            for (int i = 0; i < steps; i++)
            {
                s.Steer();
                double d = dt + (jitter == 0 ? 0 : (rnd.NextDouble() * 2 - 1) * jitter);
                simulated += d;
                World.SetTime(World.Time + d);
                long ui0 = Counters.C[iUI];
                if (o.update_object()) returnedTrue++;
                long k = Counters.C[iUI] - ui0;
                hist[k] = hist.GetValueOrDefault(k) + 1;
            }
            var a = Counters.C;
            double ui = a[iUI] - b[iUI], tr = a[iTr] - b[iTr];
            Console.WriteLine($"  dt {label,-42}: update_object returned true {100.0 * returnedTrue / steps,5:F1}% of calls; UpdateObjectInternal per call {ui / steps:F3} (histogram {string.Join(", ", hist.Select(kv => $"{kv.Key}x:{kv.Value}"))}); transitions per call {tr / steps:F3}; per simulated second: {ui / simulated:F1} UpdateObjectInternal, {tr / simulated:F1} transitions");
        }
    }

}
