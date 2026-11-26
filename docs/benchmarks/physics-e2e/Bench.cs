using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

using ACE.Server.Physics;
using ACE.Server.Physics.Common;

public static class Bench
{
    /// <summary>
    /// Warm up past tiered JIT (Tier-0 -> instrumented Tier-1 -> Tier-1): run for ~runMs, then sleep past the 100 ms tiering delay, several rounds.
    /// </summary>
    public static void Warm(Scn s, int rounds = 4, int runMs = 300, int sleepMs = 300)
    {
        for (int r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < runMs)
                for (int i = 0; i < 200; i++) s.Step();
            Thread.Sleep(sleepMs);
        }
    }

    public struct Agg
    {
        public double NsBest, NsMedian, BytesMin, BytesMax;
        public int N;
    }

    /// <summary>Whole-run averages: reps of n updates; bytes are exact per-thread allocated bytes / n; time is best and median of reps.</summary>
    public static Agg Run(Scn s, int n, int reps = 9)
    {
        var ns = new double[reps]; double bmin = double.MaxValue, bmax = 0;
        for (int r = 0; r < reps; r++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++) s.Step();
            long t1 = Stopwatch.GetTimestamp();
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            ns[r] = (t1 - t0) * 1e9 / Stopwatch.Frequency / n;
            double b = (double)(b1 - b0) / n;
            bmin = Math.Min(bmin, b); bmax = Math.Max(bmax, b);
        }
        Array.Sort(ns);
        return new Agg { NsBest = ns[0], NsMedian = ns[reps / 2], BytesMin = bmin, BytesMax = bmax, N = n };
    }

    /// <summary>Picks n so one rep takes roughly targetMs.</summary>
    public static int Calibrate(Scn s, int targetMs = 120)
    {
        var sw = Stopwatch.StartNew();
        int n = 0;
        while (sw.ElapsedMilliseconds < 40) { for (int i = 0; i < 100; i++) s.Step(); n += 100; }
        return Math.Max(1000, (int)((long)n * targetMs / Math.Max(1, sw.ElapsedMilliseconds)) / 1000 * 1000);
    }

    // ------------------------------------------------------------------ per-update samples, classified by what the update did

    public const int ClsNone = 0, ClsCell = 1, ClsBlock = 2;

    public struct Sample { public int Bytes; public int Ticks; public byte Kind; public byte Cells; }

    public static int LiveShadows(PhysicsObj o)
    {
        int n = 0;
        foreach (var sh in o.ShadowObjects.Values) if (sh.Cell != null) n++;
        return n;
    }

    public static Sample[] SampleRun(Scn s, int n)
    {
        var arr = new Sample[n];
        var o = s.Obj;
        for (int i = 0; i < n; i++)
        {
            uint c0 = o.Position.ObjCellID;
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            s.Step();
            long t1 = Stopwatch.GetTimestamp();
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            uint c1 = o.Position.ObjCellID;
            arr[i].Bytes = (int)(b1 - b0);
            arr[i].Ticks = (int)(t1 - t0);
            arr[i].Kind = (byte)(c0 == c1 ? ClsNone : (c0 >> 16) == (c1 >> 16) ? ClsCell : ClsBlock);
            arr[i].Cells = (byte)Math.Min(9, LiveShadows(o));
        }
        return arr;
    }

    static readonly string[] KindName = { "no cell change", "cell change", "landblock change" };

    public static void PrintClasses(Sample[] arr)
    {
        double tickNs = 1e9 / Stopwatch.Frequency;
        foreach (var g in arr.GroupBy(x => (x.Kind, x.Cells)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Cells))
        {
            var bytes = g.Select(x => x.Bytes).OrderBy(x => x).ToArray();
            var ticks = g.Select(x => x.Ticks).OrderBy(x => x).ToArray();
            Console.WriteLine($"      {KindName[g.Key.Kind],-17} shadowCells={g.Key.Cells}  n={bytes.Length,7}  bytes mean {bytes.Average(),8:F1} (min {bytes[0]}, median {bytes[bytes.Length / 2]}, max {bytes[^1]})  time median {ticks[ticks.Length / 2] * tickNs,7:F0} ns  p10 {ticks[ticks.Length / 10] * tickNs,7:F0} ns");
        }
    }

    // ------------------------------------------------------------------ trajectory hash (identical-results check)

    public static ulong HashStep(ulong h, PhysicsObj o)
    {
        const ulong P = 1099511628211UL;
        var p = o.Position;
        h = (h ^ p.ObjCellID) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Origin.X)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Origin.Y)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Origin.Z)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Orientation.X)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Orientation.Y)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Orientation.Z)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(p.Frame.Orientation.W)) * P;
        h = (h ^ (uint)o.TransientState) * P;
        h = (h ^ (o.CurCell == null ? 0u : o.CurCell.ID)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(o.Velocity.Z)) * P;
        h = (h ^ BitConverter.SingleToUInt32Bits(o.ContactPlane.D)) * P;
        h = (h ^ (uint)LiveShadowHash(o)) * P;
        return h;
    }

    /// <summary>Order-independent hash of the set of cells the object currently has shadows in.</summary>
    static uint LiveShadowHash(PhysicsObj o)
    {
        uint x = 0;
        foreach (var sh in o.ShadowObjects.Values) if (sh.Cell != null) x += sh.Cell.ID * 2654435761u;
        return x;
    }

    public struct TrajPoint { public uint Cell; public Vector3 Origin; public Quaternion Rot; public uint Shadows; public int TS; }

    public static TrajPoint Point(PhysicsObj o) => new TrajPoint
    {
        Cell = o.Position.ObjCellID, Origin = o.Position.Frame.Origin, Rot = o.Position.Frame.Orientation, Shadows = LiveShadowHash(o), TS = (int)o.TransientState
    };
}
