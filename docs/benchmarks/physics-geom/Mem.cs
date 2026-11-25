using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.BSP;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;
using ACE.Server.Physics.Entity;

public static class Env
{
    public static void Init()
    {
        // PhysicsEngine.Instance.Server = true without running its ctor (no ObjectMaint / SmartBox needed)
        var pe = (PhysicsEngine)RuntimeHelpers.GetUninitializedObject(typeof(PhysicsEngine));
        pe.Server = true;
        PhysicsEngine.Instance = pe;
        var t = new List<float>();
        for (int i = 0; i < 256; i++) t.Add(i * 2.0f);
        LandDefs.LandHeightTable = t;
    }
}

public static class Mem
{
    public static long Alloc(Func<object> f, int n, out object keep)
    {
        var arr = new object[n];
        f(); // warm / type init
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < n; i++) arr[i] = f();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        keep = arr;
        return (a1 - a0) / n;
    }

    public static (long alloc, long retained) AllocRetained(Func<object> f, int n)
    {
        var arr = new object[n];
        f();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long m0 = GC.GetTotalMemory(true);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < n; i++) arr[i] = f();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        long m1 = GC.GetTotalMemory(true);
        GC.KeepAlive(arr);
        return ((a1 - a0) / n, (m1 - m0 - (long)n * 8) / n);
    }

    static void P(string name, long bytes) => Console.WriteLine($"  {name,-58} {bytes,8} B");

    public static void Run()
    {
        Env.Init();
        object k;
        Console.WriteLine("== Per-instance allocated bytes (GetAllocatedBytesForCurrentThread / n) ==");
        P("new AFrame()", Alloc(() => new AFrame(), 10000, out k));
        P("new Position()  (Position + AFrame)", Alloc(() => new Position(), 10000, out k));
        P("new Sphere()", Alloc(() => new Sphere(), 10000, out k));
        P("new BBox()", Alloc(() => new BBox(), 10000, out k));
        P("new Vertex()", Alloc(() => new Vertex(), 10000, out k));
        P("new CylSphere()", Alloc(() => new CylSphere(), 10000, out k));
        P("new BSPNode()  (shell only)", Alloc(() => new BSPNode(), 10000, out k));
        P("new BSPLeaf()  (shell only)", Alloc(() => new BSPLeaf(), 10000, out k));
        P("new ReaderWriterLockSlim(SupportsRecursion)", Alloc(() => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion), 10000, out k));
        P("new List<int>()  (empty)", Alloc(() => new List<int>(), 10000, out k));
        P("new ObjCell(id)", Alloc(() => new ObjCell(1), 10000, out k));
        P("new LandCell(id)", Alloc(() => new LandCell(1), 10000, out k));
        P("new EnvCell()  (default ctor, Init)", Alloc(() => new EnvCell(), 10000, out k));
        P("new PhysicsPart()", Alloc(() => new PhysicsPart(), 10000, out k));
        var tpl = new PhysicsPart();
        P("PhysicsPart.MakePhysicsPart(template)", Alloc(() => PhysicsPart.MakePhysicsPart(tpl), 10000, out k));
        P("new Polygon(idx, 3, Landblock)  (terrain poly shell)", Alloc(() => new Polygon(0, 3, CullMode.Landblock), 10000, out k));
        P("new CellArray()", Alloc(() => new CellArray(), 10000, out k));

        Console.WriteLine();
        Console.WriteLine("== LandblockStruct (real ctor, synthetic CellLandblock 9x9) ==");
        var clb = new ACE.DatLoader.FileTypes.CellLandblock();
        typeof(ACE.DatLoader.FileTypes.FileType).GetProperty("Id")!.SetValue(clb, 0xA9B4FFFFu);
        for (int i = 0; i < 81; i++) { clb.Height.Add((byte)(i % 50)); clb.Terrain.Add((ushort)(i % 7 << 2)); }
        var (lbA, lbR) = AllocRetained(() => new LandblockStruct(clb), 200);
        P("new LandblockStruct(CellLandblock): allocated", lbA);
        P("new LandblockStruct(CellLandblock): retained", lbR);
        var (initA, _) = AllocRetained(() => new LandblockStruct(), 200);
        P("  of which LandblockStruct.Init() (64 LandCells + dict, DISCARDED)", initA);
        var lbs = new LandblockStruct(clb);
        Console.WriteLine($"  LandCells.Count={lbs.LandCells.Count} Polygons={lbs.Polygons.Count} Vertices={lbs.VertexArray.Vertices.Count}");

        Console.WriteLine();
        Console.WriteLine("== CellStruct (real ACE.Server ctor, synthetic dat geometry) ==");
        foreach (var spec in Specs())
        {
            var dat = SynthDat.MakeDatCellStruct(spec.s);
            var (a, r) = AllocRetained(() => new CellStruct(dat), 500);
            Console.WriteLine($" [{spec.name}] V={spec.s.Vertices} render={spec.s.RenderPolys} phys={spec.s.PhysicsPolys} drawInterior={spec.s.DrawingInterior} physLeaves={spec.s.PhysicsLeaves}");
            P("  new CellStruct(dat): allocated", a);
            P("  new CellStruct(dat): retained", r);
            var (dA, dR) = AllocRetained(() => new BSPTree(dat.DrawingBSP, dat.Polygons, dat.VertexArray), 500);
            P("    DrawingBSP alone (unused on server): retained", dR);
            var (pA, pR) = AllocRetained(() => { var d = new Dictionary<ushort, Polygon>(); foreach (var kv in dat.Polygons) d.Add(kv.Key, PolygonCache.Get(kv.Value, dat.VertexArray)); return d; }, 500);
            P("    Polygons dict (render polys; server reads only portals): retained", pR);
            var (phA, phR) = AllocRetained(() => new BSPTree(dat.PhysicsBSP, dat.PhysicsPolygons, dat.VertexArray), 500);
            P("    PhysicsBSP: retained", phR);
            var (ppA, ppR) = AllocRetained(() => { var d = new Dictionary<ushort, Polygon>(); foreach (var kv in dat.PhysicsPolygons) d.Add(kv.Key, PolygonCache.Get(kv.Value, dat.VertexArray)); return d; }, 500);
            P("    PhysicsPolygons dict (no in-tree reader): retained", ppR);
            var (cA, cR) = AllocRetained(() => new BSPTree(dat.CellBSP, dat.PhysicsPolygons, dat.VertexArray), 500);
            P("    CellBSP: retained", cR);
            var oneQuad = dat.Polygons[0];
            var (q1, q1r) = AllocRetained(() => new Polygon(oneQuad, dat.VertexArray), 2000);
            P("    one physics Polygon from dat (4-vertex quad): retained", q1r);
        }
    }

    public static IEnumerable<(string name, SynthDat.Spec s)> Specs()
    {
        yield return ("small corridor", new SynthDat.Spec { Vertices = 24, RenderPolys = 20, PhysicsPolys = 12, DrawingInterior = 14, PhysicsLeaves = 6, Portals = 2 });
        yield return ("medium room", new SynthDat.Spec { Vertices = 60, RenderPolys = 60, PhysicsPolys = 30, DrawingInterior = 40, PhysicsLeaves = 14, Portals = 3 });
    }
}
