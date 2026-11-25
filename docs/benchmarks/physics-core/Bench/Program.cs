using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.Extensions.Logging;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;

// Physics-core micro-measurements. Allocation numbers are exact per-thread bytes;
// timings are only meaningful as ratios within one run (shared 4-core VM).
public static class Program
{
    static readonly List<(string name, double ns, double bytes)> results = new();

    static void Measure(string name, Action body, int iters = 200_000)
    {
        // warm-up, then let the tiering delay (100 ms of no new tier-0 JIT) expire so the
        // measured code is Tier-1, as it is in a long-running server. Repeat twice.
        for (int w = 0; w < 3; w++)
        {
            for (int i = 0; i < Math.Min(iters, 20_000); i++) body();
            Thread.Sleep(300);
        }
        var best = double.MaxValue; double bytes = 0;
        for (int rep = 0; rep < 5; rep++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) body();
            sw.Stop();
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            bytes = (double)(b1 - b0) / iters;
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
        }
        results.Add((name, best, bytes));
        Console.WriteLine($"{name,-72} {best,9:F1} ns  {bytes,8:F1} B/op");
    }

    static Position MakePos(uint cell, float x, float y, float z, float headingDeg = 0)
    {
        var p = new Position(cell);
        p.Frame.Origin = new Vector3(x, y, z);
        p.Frame.set_heading(headingDeg);
        return p;
    }

    public static void Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : "";
        Console.WriteLine($"Runtime {Environment.Version}, ServerGC={System.Runtime.GCSettings.IsServerGC}");

        // ---------------- object sizes (calibration) ----------------
        if (filter == "" || filter == "sizes")
        {
            Measure("new AFrame()", () => Keep(new AFrame()));
            Measure("new Position()  [Position + inner AFrame]", () => Keep(new Position()));
            var p0 = MakePos(0xA9B40019, 10, 10, 50);
            Measure("new Position(Position) copy-ctor", () => Keep(new Position(p0)));
            Measure("new Sphere()", () => Keep(new Sphere()));
            Measure("new Transition()  [ctor -> Init(), single]", () => Keep(new Transition()));
            Measure("new MovementParameters()", () => Keep(new MovementParameters()));
            Measure("new ObjectInfo()", () => Keep(new ObjectInfo()));
            Measure("new CollisionRecord(t,false)", () => Keep(new CollisionRecord(1.0, false)));
            Measure("new List<Sphere>{ s }  (Transition.InitSphere(Sphere) wrapper)", () => Keep(new List<Sphere> { s_sphere }));
        }

        // ---------------- SpherePath per-step primitives (real code) ----------------
        if (filter == "" || filter == "spherepath")
        {
            var t = new Transition();
            var spheres = new List<Sphere> { new Sphere(new Vector3(0, 0, 0.475f), 0.475f), new Sphere(new Vector3(0, 0, 1.35f), 0.475f) };
            t.InitSphere(2, spheres, 1.0f);
            var begin = MakePos(0xA9B40019, 100, 100, 50, 30);
            var end = MakePos(0xA9B40019, 100.3f, 100.2f, 50, 31);
            t.InitPath(null, begin, end);
            var sp = t.SpherePath;
            sp.SetCheckPos(sp.CurPos, null);
            sp.SaveCheckPos();
            var cellPos = MakePos(0xA9B40019, 96, 96, 0, 0);
            Measure("SpherePath.SetCheckPos (new Position + CacheGlobalSphere(null))", () => sp.SetCheckPos(sp.CurPos, null));
            Measure("SpherePath.SaveCheckPos", () => sp.SaveCheckPos());
            Measure("SpherePath.RestoreCheckPos", () => sp.RestoreCheckPos());
            Measure("SpherePath.AddOffsetToCheckPos (Vector3? path)", () => sp.AddOffsetToCheckPos(new Vector3(0.0001f, 0, 0)));
            Measure("SpherePath.CacheGlobalSphere(null)", () => sp.CacheGlobalSphere(null));
            Measure("SpherePath.CacheGlobalCurrCenter", () => sp.CacheGlobalCurrCenter());
            Measure("SpherePath.CacheLocalSpaceSphere(cellPos,1)", () => sp.CacheLocalSpaceSphere(cellPos, 1.0f));
            Measure("Transition.SetCurrentCheckPos (2 Positions + 2 recaches)", () => t.SetCurrentCheckPos());
            Measure("SpherePath.InitPath (CurPos copy + recache)", () => sp.InitPath(null, begin, end));
            Measure("Transition.InitSphere(1, Sphere, 1) on fresh Transition (incl. ctor)", () => { var tt = new Transition(); tt.InitSphere(1, PhysicsGlobals.DummySphere, 1.0f); Keep(tt); });
            Measure("Transition ctor + InitSphere(2) + InitPath  [fixed per-transition setup]", () =>
            {
                var tt = new Transition(); tt.InitSphere(2, spheres, 1.0f); tt.InitPath(null, begin, end); Keep(tt);
            });

            // prototype: alias-guarded in-place SetCheckPos
            Measure("PROTO SetCheckPos in-place copy (alias-guarded)", () => SetCheckPosInPlace(sp, sp.CurPos, null));
        }

        // ---------------- PhysicsObj per-frame scaffold (real code, bare object) ----------------
        if (filter == "" || filter == "physobj")
        {
            var obj = new PhysicsObj();
            obj.CurCell = new ObjCell(0xA9B40019);
            obj.Position = MakePos(0xA9B40019, 100, 100, 50, 30);
            obj.TransientState = TransientStateFlags.Active;
            Measure("PhysicsObj.UpdateObjectInternal(1/30) bare obj, no PartArray", () => { obj.TransientState = TransientStateFlags.Active; obj.UpdateObjectInternal(1.0 / 30); });
            Measure("PhysicsObj.set_frame(Position.Frame)", () => obj.set_frame(obj.Position.Frame));
            var f = new AFrame();
            Measure("PhysicsObj.UpdatePositionInternal(1/30, ref f)", () => obj.UpdatePositionInternal(1.0 / 30, ref f));
            var tr = new Transition();
            Measure("PhysicsObj.get_object_info(trans,false) bare", () => Keep(obj.get_object_info(tr, false)));
            obj.Velocity = new Vector3(1, 1, 0);
            Measure("PhysicsObj.UpdatePhysicsInternal moving, Omega=0", () => { var ff = new AFrame(); obj.Velocity = new Vector3(1, 1, 0); obj.UpdatePhysicsInternal(1f / 30, ref ff); });
            obj.Velocity = Vector3.Zero;

            // track_object_collision -> CollisionRecord per call (other obj non-static)
            var other = new PhysicsObj(); other.State &= ~PhysicsState.Static;
            obj.CollisionTable = new Dictionary<uint, CollisionRecord>();
        }

        // ---------------- AFrame micro-ops ----------------
        if (filter == "" || filter == "aframe")
        {
            var a = new AFrame(new Vector3(1, 2, 3), Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)));
            var b = new AFrame(new Vector3(0.1f, 0.2f, 0), Quaternion.Identity);
            Measure("AFrame.Combine(a,b) static (allocates result)", () => Keep(AFrame.Combine(a, b)));
            Measure("AFrame.GRotate(Vector3.Zero)  [current: YPR + mul + normalize]", () => a.GRotate(Vector3.Zero));
            Measure("PROTO GRotate fast path: Normalize only when omega==0", () => GRotateFast(a, Vector3.Zero));
            var q0 = a.Orientation;
            // bit-identity check of the fast path
            int mism = 0; var rnd = new Random(1);
            for (int i = 0; i < 100000; i++)
            {
                var q = Quaternion.Normalize(new Quaternion((float)rnd.NextDouble() - .5f, (float)rnd.NextDouble() - .5f, (float)rnd.NextDouble() - .5f, (float)rnd.NextDouble() - .5f)) * 1.0001f;
                var x1 = new AFrame(Vector3.Zero, q); x1.GRotate(Vector3.Zero);
                var x2 = new AFrame(Vector3.Zero, q); GRotateFast(x2, Vector3.Zero);
                if (x1.Orientation != x2.Orientation) mism++;
            }
            Console.WriteLine($"   GRotate fast-path bit mismatches over 100k random unnormalised quats: {mism}");
            Measure("AFrame.set_heading(45)  [current, ends with dead get_heading()]", () => a.set_heading(45));
            Measure("PROTO set_heading without trailing dead get_heading()", () => SetHeadingNoDead(a, 45));
            Measure("AFrame.get_heading() (Matrix4x4 build)", () => Keep(a.get_heading()));
            Measure("AFrame.Equals(AFrame)", () => Keep(a.Equals(b)));
        }

        // ---------------- collections / LINQ on the hot path ----------------
        if (filter == "" || filter == "coll")
        {
            var ca = new CellArray();
            for (uint i = 0; i < 4; i++) ca.add_cell(0xA9B40019 + i, new ObjCell(0xA9B40019 + i));
            Measure("CellArray: for i<Count  Cells.Values.ElementAt(i)  (4 cells)", () =>
            {
                ObjCell last = null;
                for (var i = 0; i < ca.Cells.Count; i++) last = ca.Cells.Values.ElementAt(i);
                Keep(last);
            });
            Measure("CellArray: foreach Cells.Values (4 cells)", () =>
            {
                ObjCell last = null;
                foreach (var c in ca.Cells.Values) last = c;
                Keep(last);
            });
            var ca9 = new CellArray();
            for (uint i = 0; i < 9; i++) ca9.add_cell(0xA9B40019 + i, new ObjCell(0xA9B40019 + i));
            Measure("CellArray: ElementAt loop (9 cells)", () =>
            {
                ObjCell last = null;
                for (var i = 0; i < ca9.Cells.Count; i++) last = ca9.Cells.Values.ElementAt(i);
                Keep(last);
            });
            Measure("CellArray: foreach (9 cells)", () =>
            {
                ObjCell last = null;
                foreach (var c in ca9.Cells.Values) last = c;
                Keep(last);
            });
            var objs = new List<PhysicsObj> { new PhysicsObj(), new PhysicsObj() };
            objs[0].ID = 5; objs[1].ID = 7; uint target = 7;
            Measure("CollideObject.FirstOrDefault(c => c.ID == x) (closure)", () => Keep(objs.FirstOrDefault(c => c.ID == target)));
        }

        // ---------------- logging at disabled level ----------------
        if (filter == "" || filter == "log")
        {
            using var lf = LoggerFactory.Create(bld => bld.SetMinimumLevel(LogLevel.Warning));
            var log = lf.CreateLogger("bench");
            var p = MakePos(0xA9B40019, 1, 2, 3);
            string name = "Bob"; uint id = 0x50000001; double q = 1.0 / 30;
            Measure("log.LogDebug(5 args incl uint,double) when Debug disabled", () =>
                log.LogDebug("{Name} ({ID:X8}).UpdateObjectInternal({Quantum}) - failed transition from {Position} to {NewPos}", name, id, q, p, p));
            Measure("guarded: if (log.IsEnabled(Debug)) log.LogDebug(...)", () =>
            {
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("{Name} ({ID:X8}).UpdateObjectInternal({Quantum}) - failed transition from {Position} to {NewPos}", name, id, q, p, p);
            });
        }

        // ---------------- locked Biota property read (PK status in FindObjCollisions) ----------------
        if (filter == "" || filter == "lock")
        {
            var biota = new Biota();
            biota.PropertiesInt = new Dictionary<PropertyInt, int>();
            for (int i = 1; i < 60; i++) biota.PropertiesInt[(PropertyInt)i] = i;
            biota.PropertiesInt[PropertyInt.PlayerKillerStatus] = 2;
            var rw = new ReaderWriterLockSlim();
            var wr = new WeakReference<object>(biota);
            int cached = 2;
            Measure("Biota.GetProperty(PropertyInt, rwLock)  [RW read-lock + dict]", () => Keep(biota.GetProperty(PropertyInt.PlayerKillerStatus, rw)));
            Measure("WeakReference.TryGetTarget + GetProperty  (~ WeenieObj.IsImpenetrable)", () => { wr.TryGetTarget(out var o); Keep(((Biota)o).GetProperty(PropertyInt.PlayerKillerStatus, rw)); });
            Measure("plain cached int field read", () => Keep(Volatile.Read(ref cached)));
        }

        // ---------------- struct prototype of the per-frame scaffold ----------------
        if (filter == "" || filter == "proto")
        {
            var cur = new PosC { Cell = 0xA9B40019, F = new AFrameC { O = new Vector3(100, 100, 50), Q = Quaternion.Identity } };
            var off = new AFrameC { O = new Vector3(0.1f, 0, 0), Q = Quaternion.Identity };
            Measure("PROTO class: new Pos + new AFrame offset + Combine + set_frame copy", () =>
            {
                var newPos = new PosC { Cell = cur.Cell, F = new AFrameC() };       // UpdateObjectInternal
                var offset = new AFrameC { O = off.O, Q = off.Q };                  // UpdatePositionInternal
                newPos.F = AFrameC.Combine(cur.F, offset);                           // Combine alloc
                cur.F = new AFrameC { O = newPos.F.O, Q = newPos.F.Q };               // set_frame copy
            });
            var curS = new PosS { Cell = 0xA9B40019, F = new AFrameS(new Vector3(100, 100, 50), Quaternion.Identity) };
            var offS = new AFrameS(new Vector3(0.1f, 0, 0), Quaternion.Identity);
            Measure("PROTO struct: same flow with readonly-free struct AFrame inlined", () =>
            {
                var newPos = new PosSV { Cell = curS.Cell };
                var offset = offS;
                AFrameS.Combine(in curS.F, in offset, out newPos.F);
                curS.F = newPos.F;
            });
        }

        if (filter == "" || filter == "pool")
            TransitionPoolProto.Run((n, a) => Measure(n, a));

        if (filter == "" || filter == "collend")
        {
            var o = new PhysicsObj();
            Measure("PhysicsObj.report_collision_end(false), empty CollisionTable", () => o.report_collision_end(false));
            Measure("PROTO report_collision_end with Count==0 early-out", () => { if (o.CollisionTable.Count != 0) o.report_collision_end(false); });
            var t = new Transition();
            Measure("PhysicsObj.handle_all_collisions (no collisions)", () => o.handle_all_collisions(t.CollisionInfo, false, false));
        }

        if (filter == "" || filter == "objsize")
        {
            Measure("new PhysicsObj() (bare, no PartArray)", () => Keep(new PhysicsObj()), 50_000);
            Measure("new ObjectMaint(obj)", () => Keep(new ObjectMaint(null)), 50_000);
            Measure("new ChildList()", () => Keep(new ChildList()), 50_000);
        }

        Console.WriteLine("done");
    }

    static readonly Sphere s_sphere = new Sphere(new Vector3(0, 0, 0.5f), 0.5f);
    static object sink;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Keep(object o) => sink = o;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Keep(float o) => sink = null;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Keep(bool o) => sink = null;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Keep(int? o) => sink = null;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Keep(int o) => sink = null;

    // verbatim GRotate with a zero-omega fast path
    static void GRotateFast(AFrame f, Vector3 rotation)
    {
        if (rotation != Vector3.Zero)
            f.Orientation *= Quaternion.CreateFromYawPitchRoll(rotation.X, rotation.Y, rotation.Z);
        f.Orientation = Quaternion.Normalize(f.Orientation);
    }

    // verbatim AFrame.set_heading minus the unused trailing get_heading()
    static void SetHeadingNoDead(AFrame f, float degrees)
    {
        var rads = degrees * (float)Math.PI / 180.0f;
        var matrix = Matrix4x4.CreateFromQuaternion(f.Orientation);
        var heading = new Vector3((float)Math.Sin(rads), (float)Math.Cos(rads), matrix.M23 + matrix.M13);
        f.set_vector_heading(heading);
    }

    // proposed SetCheckPos: copy into the existing CheckPos unless it is aliased by CurPos
    static void SetCheckPosInPlace(SpherePath sp, Position position, ObjCell cell)
    {
        if (ReferenceEquals(sp.CheckPos, sp.CurPos) || ReferenceEquals(sp.CheckPos, position))
            sp.CheckPos = new Position(position);
        else
        {
            sp.CheckPos.ObjCellID = position.ObjCellID;
            sp.CheckPos.Frame.Origin = position.Frame.Origin;
            sp.CheckPos.Frame.Orientation = position.Frame.Orientation;
        }
        sp.CheckCell = cell;
        sp.CellArrayValid = false;
        sp.CacheGlobalSphere(null);
    }
}

// --- prototype types ---
public sealed class AFrameC
{
    public Vector3 O; public Quaternion Q;
    public static AFrameC Combine(AFrameC a, AFrameC b) => new AFrameC { O = a.O + Vector3.Transform(b.O, a.Q), Q = Quaternion.Multiply(a.Q, b.Q) };
}
public sealed class PosC { public uint Cell; public AFrameC F; }

public struct AFrameS
{
    public Vector3 Origin; public Quaternion Orientation;
    public AFrameS(Vector3 o, Quaternion q) { Origin = o; Orientation = q; }
    public static void Combine(in AFrameS a, in AFrameS b, out AFrameS r)
    {
        r.Origin = a.Origin + Vector3.Transform(b.Origin, a.Orientation);
        r.Orientation = Quaternion.Multiply(a.Orientation, b.Orientation);
    }
}
public sealed class PosS { public uint Cell; public AFrameS F; }
public struct PosSV { public uint Cell; public AFrameS F; }
