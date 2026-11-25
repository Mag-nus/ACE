using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;

public static class Timer
{
    // returns best-of-reps ns/op and allocated bytes/op of the last rep
    public static (double ns, double bytes) Time(string name, int n, Func<int, float> body, int reps = 5)
    {
        body(1000); // warm (tier-up handled by loop count below)
        for (int w = 0; w < 3; w++) body(n / 10);
        double best = double.MaxValue; long alloc = 0; float sink = 0;
        for (int r = 0; r < reps; r++)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            sink += body(n);
            sw.Stop();
            alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / n);
        }
        Console.WriteLine($"  {name,-62} {best,8:F2} ns/op  {(double)alloc / n,7:F1} B/op   (sink {sink:G3})");
        return (best, (double)alloc / n);
    }
}

public static class MathBench
{
    static Quaternion RandQ(Random r)
    {
        var q = new Quaternion((float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f, (float)r.NextDouble() - .5f);
        return Quaternion.Normalize(q);
    }

    // ---- prototypes ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GlobalToLocalVecQ(Quaternion q, Vector3 v) => Vector3.Transform(v, Quaternion.Conjugate(q));

    // Position.LocalToLocal(pos, offset) = this.Frame.GlobalToLocal(blockOffset + pos.Frame.LocalToGlobal(offset))
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 LocalToLocalQ(Position self, Position pos, Vector3 offset)
    {
        var cellOffset = pos.Frame.Origin + Vector3.Transform(offset, pos.Frame.Orientation);
        var blockOffset = LandDefs.GetBlockOffset(self.ObjCellID, pos.ObjCellID);
        return Vector3.Transform(blockOffset + cellOffset - self.Frame.Origin, Quaternion.Conjugate(self.Frame.Orientation));
    }

    // BBox.LocalToLocal: one composed rotation + translation, then 8 corners
    public static void BoxLocalToLocalQ(ref Vector3 min, ref Vector3 max, Vector3 fMin, Vector3 fMax, Position fromPos, Position toPos)
    {
        var invTo = Quaternion.Conjugate(toPos.Frame.Orientation);
        var rot = invTo * fromPos.Frame.Orientation; // apply from, then inverse to  (q = conj(to) * from)
        var blockOffset = LandDefs.GetBlockOffset(toPos.ObjCellID, fromPos.ObjCellID);
        var trans = Vector3.Transform(blockOffset + fromPos.Frame.Origin - toPos.Frame.Origin, invTo);
        Vector3 c0 = Vector3.Transform(fMin, rot) + trans;
        min = c0; max = c0;
        Span<Vector3> cs = stackalloc Vector3[7]
        {
            new(fMax.X, fMax.Y, fMax.Z), new(fMax.X, fMax.Y, fMin.Z), new(fMax.X, fMin.Y, fMax.Z), new(fMin.X, fMax.Y, fMax.Z),
            new(fMin.X, fMax.Y, fMin.Z), new(fMax.X, fMin.Y, fMin.Z), new(fMin.X, fMin.Y, fMax.Z)
        };
        foreach (var c in cs)
        {
            var v = Vector3.Transform(c, rot) + trans;
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }
    }

    public static float HeadingQ(Quaternion q)
    {
        // matrix M21 = 2(xy - wz), M22 = 1 - 2(xx + zz)  (System.Numerics CreateFromQuaternion layout)
        float m21 = 2f * (q.X * q.Y - q.W * q.Z);
        float m22 = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        var heading = (float)Math.Atan2(m22, m21);
        return (450.0f - heading * (180f / MathF.PI)) % 360.0f;
    }

    public static void Run()
    {
        Env.Init();
        var r = new Random(1);
        const int N = 1024;
        var frames = new AFrame[N]; var vecs = new Vector3[N]; var poss = new Position[N]; var poss2 = new Position[N];
        for (int i = 0; i < N; i++)
        {
            frames[i] = new AFrame(new Vector3(r.Next(192), r.Next(192), r.Next(50)), RandQ(r));
            vecs[i] = new Vector3((float)r.NextDouble() * 10, (float)r.NextDouble() * 10, (float)r.NextDouble() * 10);
            poss[i] = new Position(0xA9B40001, new AFrame(frames[i].Origin, frames[i].Orientation));
            poss2[i] = new Position(0xA9B40010, new AFrame(new Vector3(r.Next(192), r.Next(192), 0), RandQ(r)));
        }

        // numeric equivalence
        double maxD = 0, maxH = 0;
        for (int i = 0; i < N; i++)
        {
            var a = frames[i].GlobalToLocalVec(vecs[i]);
            var b = GlobalToLocalVecQ(frames[i].Orientation, vecs[i]);
            maxD = Math.Max(maxD, (a - b).Length());
            var p = poss[i].LocalToLocal(poss2[i], vecs[i]); var q = LocalToLocalQ(poss[i], poss2[i], vecs[i]);
            maxD = Math.Max(maxD, (p - q).Length());
            float h1 = frames[i].get_heading(), h2 = HeadingQ(frames[i].Orientation);
            var dh = Math.Abs(h1 - h2); if (dh > 180) dh = 360 - dh; maxH = Math.Max(maxH, dh);
        }
        Console.WriteLine($"max |matrix - quaternion| over {N} random frames (vectors up to 10 m): {maxD:E2} m;  heading max diff {maxH:E2} deg");

        Console.WriteLine("== AFrame.GlobalToLocalVec (real ACE) vs Vector3.Transform(v, Conjugate(q)) ==");
        var t1 = Timer.Time("real AFrame.GlobalToLocalVec (Matrix build+transpose)", 20_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += frames[i & (N - 1)].GlobalToLocalVec(vecs[i & (N - 1)]).X; return s; });
        var t2 = Timer.Time("prototype Transform(v, Conjugate(q))", 20_000_000, n => { float s = 0; for (int i = 0; i < n; i++) { var f = frames[i & (N - 1)]; s += GlobalToLocalVecQ(f.Orientation, vecs[i & (N - 1)]).X; } return s; });
        Console.WriteLine($"  ratio real/proto = {t1.ns / t2.ns:F2}x");

        Console.WriteLine("== Position.LocalToLocal (real) vs quaternion prototype ==");
        var t3 = Timer.Time("real Position.LocalToLocal", 20_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += poss[i & (N - 1)].LocalToLocal(poss2[i & (N - 1)], vecs[i & (N - 1)]).X; return s; });
        var t4 = Timer.Time("prototype LocalToLocalQ", 20_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += LocalToLocalQ(poss[i & (N - 1)], poss2[i & (N - 1)], vecs[i & (N - 1)]).X; return s; });
        Console.WriteLine($"  ratio real/proto = {t3.ns / t4.ns:F2}x");

        Console.WriteLine("== AFrame.get_heading (real, full matrix) vs 2-element prototype ==");
        var t5 = Timer.Time("real AFrame.get_heading", 10_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += frames[i & (N - 1)].get_heading(); return s; });
        var t6 = Timer.Time("prototype HeadingQ", 10_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += HeadingQ(frames[i & (N - 1)].Orientation); return s; });
        Console.WriteLine($"  ratio real/proto = {t5.ns / t6.ns:F2}x");

        Console.WriteLine("== BBox.LocalToLocal (real: new BBox + 8x LocalToLocal) vs composed-transform prototype ==");
        var boxes = new BBox[N];
        for (int i = 0; i < N; i++) boxes[i] = new BBox { Min = new Vector3(-1, -1, 0), Max = new Vector3(1, 1, 2) };
        double maxB = 0;
        for (int i = 0; i < N; i++)
        {
            var b = new BBox(); b.LocalToLocal(boxes[i], poss2[i], poss[i]);
            Vector3 mn = default, mx = default; BoxLocalToLocalQ(ref mn, ref mx, boxes[i].Min, boxes[i].Max, poss2[i], poss[i]);
            maxB = Math.Max(maxB, Math.Max((b.Min - mn).Length(), (b.Max - mx).Length()));
        }
        Console.WriteLine($"  max |real - proto| box corner diff: {maxB:E2} m");
        var t7 = Timer.Time("real new BBox().LocalToLocal(...)", 5_000_000, n => { float s = 0; for (int i = 0; i < n; i++) { var b = new BBox(); b.LocalToLocal(boxes[i & (N - 1)], poss2[i & (N - 1)], poss[i & (N - 1)]); s += b.Min.X; } return s; });
        var t8 = Timer.Time("prototype BoxLocalToLocalQ (no alloc)", 5_000_000, n => { float s = 0; for (int i = 0; i < n; i++) { Vector3 mn = default, mx = default; BoxLocalToLocalQ(ref mn, ref mx, boxes[i & (N - 1)].Min, boxes[i & (N - 1)].Max, poss2[i & (N - 1)], poss[i & (N - 1)]); s += mn.X; } return s; });
        Console.WriteLine($"  ratio real/proto = {t7.ns / t8.ns:F2}x");

        Console.WriteLine("== BBox.GetCorners + Plane.intersect_box (real) vs span-corner prototype ==");
        var planes = new System.Numerics.Plane[N];
        for (int i = 0; i < N; i++) planes[i] = new System.Numerics.Plane(Vector3.Normalize(vecs[i] + Vector3.One), (float)r.NextDouble() - 3f);
        var t9 = Timer.Time("real PlaneExtensions.intersect_box", 5_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += (int)ACE.Server.Physics.Extensions.PlaneExtensions.intersect_box(planes[i & (N - 1)], boxes[i & (N - 1)]); return s; });
        var t10 = Timer.Time("prototype intersect_box (inline corners, no alloc)", 5_000_000, n => { float s = 0; for (int i = 0; i < n; i++) s += (int)IntersectBoxQ(planes[i & (N - 1)], boxes[i & (N - 1)].Min, boxes[i & (N - 1)].Max); return s; });
        Console.WriteLine($"  ratio real/proto = {t9.ns / t10.ns:F2}x");
        int mism = 0; for (int i = 0; i < N; i++) if (ACE.Server.Physics.Extensions.PlaneExtensions.intersect_box(planes[i], boxes[i]) != IntersectBoxQ(planes[i], boxes[i].Min, boxes[i].Max)) mism++;
        Console.WriteLine($"  result mismatches: {mism}/{N}");
    }

    // identical decision logic to PlaneExtensions.intersect_box, corners generated on the fly
    public static ACE.Entity.Enum.Sidedness IntersectBoxQ(System.Numerics.Plane p, Vector3 mn, Vector3 mx)
    {
        const float EPS = ACE.Server.Physics.PhysicsGlobals.EPSILON;
        ACE.Entity.Enum.Sidedness result;
        var dist = Vector3.Dot(mn, p.Normal) + p.D;
        if (dist <= EPS)
        {
            if (dist >= -EPS) return ACE.Entity.Enum.Sidedness.Crossing;
            result = ACE.Entity.Enum.Sidedness.Negative;
        }
        else result = ACE.Entity.Enum.Sidedness.Positive;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 4) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 1) != 0 ? mx.Z : mn.Z);
            var d = Vector3.Dot(p.Normal, c) + p.D + EPS;
            var side = d <= EPS ? (d < -EPS ? 1 : 2) : 0; // Front=0 Behind=1 Close=2
            if ((int)result != side) return ACE.Entity.Enum.Sidedness.Crossing;
        }
        return result;
    }
}
