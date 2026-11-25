using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

// Identical source compiled against (A) the snapshot ACE.Server.dll (AFrame = class)
// and (B) the scratch build with AFrame = struct. Compare B/op and ns ratios.
public static class AB
{
    static object sink;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Keep(object o) => sink = o;

    static void Measure(string name, Action body, int iters = 200_000)
    {
        for (int w = 0; w < 3; w++) { for (int i = 0; i < 20_000; i++) body(); Thread.Sleep(300); }
        double best = double.MaxValue, bytes = 0;
        for (int rep = 0; rep < 5; rep++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread(); var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) body();
            sw.Stop(); bytes = (double)(GC.GetAllocatedBytesForCurrentThread() - b0) / iters;
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
        }
        Console.WriteLine($"{name,-64} {best,8:F1} ns {bytes,8:F1} B/op");
    }

    public static void Main()
    {
        Console.WriteLine("AFrame is " + (typeof(AFrame).IsValueType ? "STRUCT" : "class"));
        var p0 = new Position(0xA9B40019); p0.Frame.Origin = new Vector3(100, 100, 50);
        Measure("new Position()", () => Keep(new Position()));
        Measure("new Position(Position)", () => Keep(new Position(p0)));
        Measure("new Transition()", () => Keep(new Transition()));
        var spheres = new List<Sphere> { new Sphere(new Vector3(0, 0, 0.475f), 0.475f), new Sphere(new Vector3(0, 0, 1.35f), 0.475f) };
        var end = new Position(p0); end.Frame.Origin += new Vector3(0.3f, 0.2f, 0);
        Measure("Transition ctor + InitSphere(2) + InitPath", () => { var t = new Transition(); t.InitSphere(2, spheres, 1f); t.InitPath(null, p0, end); Keep(t); });
        var tr = new Transition(); tr.InitSphere(2, spheres, 1f); tr.InitPath(null, p0, end); var sp = tr.SpherePath;
        sp.SetCheckPos(sp.CurPos, null);
        Measure("SpherePath.SetCheckPos", () => sp.SetCheckPos(sp.CurPos, null));
        Measure("SpherePath.SaveCheckPos + RestoreCheckPos", () => { sp.SaveCheckPos(); sp.RestoreCheckPos(); });
        Measure("Transition.SetCurrentCheckPos", () => tr.SetCurrentCheckPos());
        var cellPos = new Position(0xA9B40019); cellPos.Frame.Origin = new Vector3(96, 96, 0);
        Measure("SpherePath.CacheLocalSpaceSphere", () => sp.CacheLocalSpaceSphere(cellPos, 1f));

        var obj = new PhysicsObj();
        obj.CurCell = new ObjCell(0xA9B40019);
        obj.Position = new Position(p0);
        Measure("PhysicsObj.UpdateObjectInternal (bare obj)", () => { obj.TransientState = TransientStateFlags.Active; obj.UpdateObjectInternal(1.0 / 30); });
        Measure("PhysicsObj.set_frame(Position.Frame)", () => obj.set_frame(obj.Position.Frame));
        var f = new AFrame();
        Measure("PhysicsObj.UpdatePositionInternal", () => obj.UpdatePositionInternal(1.0 / 30, ref f));
        obj.Velocity = new Vector3(1, 1, 0);
        Measure("UpdateObjectInternal moving (Velocity 1,1,0)", () => { obj.TransientState = TransientStateFlags.Active; obj.Velocity = new Vector3(1, 1, 0); obj.UpdateObjectInternal(1.0 / 30); });
        Console.WriteLine("check final origin: " + obj.Position.Frame.Origin);
    }
}
