using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using HarmonyLib;

using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

/// <summary>
/// Re-runs the earlier PG.1 micro-benchmark (docs/benchmarks/physics-geom/CellListBench.cs: real ObjCell.find_cell_list, fresh
/// CellArray per call, 2-sphere player, four positions) in one of several patch configurations. Patches are attached at process
/// start, before any physics code is compiled - a patch attached after the callers reached Tier-1 does not take effect at call
/// sites where the JIT inlined the target. One configuration per process:
///   pristine     nothing attached (run it from out/base and from out/pg1 to get the true current / fixed pair)
///   noop-prefix  an empty Harmony prefix on LScape.get_landcell
///   old          the earlier harness: counting prefix on get_landcell + postfix on add_all_outside_cells, fix toggled by a flag
///   transpiler   PG.1 as a Harmony transpiler, no prefix
/// Default lookup: production (Server = true: LandblockManager.GetLandblock + lock). --client: the earlier harness's lookup
/// (Server = false: LScape.Landblocks dictionary, no lock).
/// </summary>
public static class HarmonyOverhead
{
    public static bool EmulateFix;
    public static long Calls;
    static void CountPrefix() => Calls++;
    static void NoopPrefix() { }
    static void FixPostfix(CellArray cellArray) { if (EmulateFix) cellArray.AddedOutside = true; }

    static int Loop(Position pos, List<Sphere> spheres, int n)
    {
        int s = 0;
        for (int i = 0; i < n; i++)
        {
            var ca = new CellArray(); ObjCell cur = ObjCell.EmptyCell;
            ObjCell.find_cell_list(pos, 2, spheres, ca, ref cur, null);
            s += ca.Cells.Count;
        }
        return s;
    }

    static (double ns, double bytes) Time(Position pos, List<Sphere> spheres, int n = 300_000)
    {
        for (int w = 0; w < 4; w++) { Loop(pos, spheres, n / 2); Thread.Sleep(300); }
        double best = double.MaxValue; double bytes = 0;
        for (int r = 0; r < 9; r++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            Loop(pos, spheres, n);
            long t1 = Stopwatch.GetTimestamp();
            bytes = (GC.GetAllocatedBytesForCurrentThread() - b0) / (double)n;
            best = Math.Min(best, (t1 - t0) * 1e9 / Stopwatch.Frequency / n);
        }
        return (best, bytes);
    }

    public static void Run(string config, bool client)
    {
        var h = new Harmony("phys-e2e.overhead");
        var getLandcell = AccessTools.Method(typeof(LScape), nameof(LScape.get_landcell));
        switch (config)
        {
            case "pristine": break;
            case "noop-prefix": h.Patch(getLandcell, prefix: new HarmonyMethod(typeof(HarmonyOverhead), nameof(NoopPrefix))); break;
            case "old":
                h.Patch(getLandcell, prefix: new HarmonyMethod(typeof(HarmonyOverhead), nameof(CountPrefix)));
                h.Patch(Fixes.AddAllOutsideCellsSphere, postfix: new HarmonyMethod(typeof(HarmonyOverhead), nameof(FixPostfix)));
                break;
            case "transpiler": Fixes.Pg1(); break;
            default: throw new ArgumentException("config: pristine | noop-prefix | old | transpiler");
        }

        Scenarios.BuildWorlds();
        var r = Scenarios.Flat;
        if (client)
        {
            for (int ix = 0; ix < r.Nx; ix++)
                for (int iy = 0; iy < r.Ny; iy++)
                    LScape.Landblocks[r.BlockId(ix, iy)] = LScape.get_landblock(r.BlockId(ix, iy));
            PhysicsEngine.Instance.Server = false;
        }
        var cases = new (string name, Vector2 g)[]
        {
            ("1 cell", new Vector2(192 + 36, 192 + 36)),
            ("cell edge (2 cells)", new Vector2(192 + 47.7f, 192 + 36)),
            ("cell corner (4 cells)", new Vector2(192 + 47.7f, 192 + 47.7f)),
            ("landblock corner (4 cells)", new Vector2(192 + 191.7f, 192 + 191.7f)),
        };
        Console.Write($"[{Fixes.Variant}] {(client ? "client-mode lookup" : "server-mode lookup")} config={config,-12}");
        foreach (var (name, g) in cases)
        {
            var (cell, o) = r.At(g.X, g.Y, 0);
            var pos = new Position(cell, new AFrame(o, Quaternion.Identity));
            var spheres = new List<Sphere> { new Sphere(o + new Vector3(0, 0, 0.48f), 0.48f), new Sphere(o + new Vector3(0, 0, 1.35f), 0.48f) };
            if (config == "old")
            {
                EmulateFix = false; Calls = 0; Loop(pos, spheres, 1); long c0 = Calls;
                EmulateFix = true; Calls = 0; Loop(pos, spheres, 1); long c1 = Calls;
                EmulateFix = false; var a = Time(pos, spheres);
                EmulateFix = true; var b = Time(pos, spheres);
                EmulateFix = false;
                Console.Write($" | {name}: calls {c0}->{c1}, current {a.ns:F0} ns {a.bytes:F0} B, fixed {b.ns:F0} ns {b.bytes:F0} B, ratio {a.ns / b.ns:F2}x");
            }
            else
            {
                var a = Time(pos, spheres);
                Console.Write($" | {name}: {a.ns:F0} ns {a.bytes:F0} B");
            }
        }
        Console.WriteLine();
    }
}
