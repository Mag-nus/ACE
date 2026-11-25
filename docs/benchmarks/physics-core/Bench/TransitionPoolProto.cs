using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;

// Prototype of a reset-for-reuse Transition, plus a reflection check that a used-then-reset
// instance is field-for-field indistinguishable from `new Transition()`.
public static class TransitionPoolProto
{
    public static void Reset(Transition t)
    {
        var oi = t.ObjectInfo;
        oi.Object = null; oi.State = 0; oi.Scale = 0; oi.StepUpHeight = 0; oi.StepDownHeight = 0;
        oi.Ethereal = false; oi.StepDown = false; oi.TargetID = 0;

        var ci = t.CollisionInfo;
        ci.LastKnownContactPlaneValid = false; ci.LastKnownContactPlane = default; ci.LastKnownContactPlaneIsWater = false;
        ci.ContactPlaneValid = false; ci.ContactPlane = default; ci.ContactPlaneCellID = 0; ci.LastKnownContactPlaneCellID = 0;
        ci.ContactPlaneIsWater = false; ci.SlidingNormalValid = false; ci.SlidingNormal = default; ci.CollisionNormalValid = false;
        ci.CollisionNormal = default; ci.AdjustOffset = default; ci.NumCollideObject = 0; ci.CollideObject.Clear();
        ci.LastCollidedObject = null; ci.CollidedWithEnvironment = false; ci.FramesStationaryFall = 0; ci.VerifiedRestrictions = false;

        var ca = t.CellArray;
        ca.AddedOutside = false; ca.LoadCells = false; ca.Cells.Clear(); ca.NumCells = 0;

        var sp = t.SpherePath;
        sp.NumSphere = 0; sp.LocalSphere.Clear(); sp.LocalLowPoint = default; sp.GlobalSphere.Clear(); sp.GlobalLowPoint = default;
        sp.LocalSpaceSphere.Clear(); sp.LocalSpaceLowPoint = default; sp.LocalSpaceCurrCenter.Clear(); sp.GlobalCurrCenter.Clear();
        ResetPos(sp.LocalSpacePos); sp.LocalSpaceZ = default; sp.BeginCell = null; sp.BeginPos = null; sp.EndPos = null; sp.CurCell = null;
        // CurPos/CheckPos/BackupCheckPos may be aliased (CurPos = CheckPos in ValidatePlacement*); never reuse an aliased instance
        sp.CurPos = new Position(); sp.CheckPos = new Position(); sp.BackupCheckPos = new Position();
        sp.GlobalOffset = default; sp.StepUp = false; sp.StepUpNormal = default; sp.Collide = false; sp.CheckCell = null;
        sp.InsertType = 0; sp.StepDown = false; sp.Backup = 0; sp.BackupCell = null; sp.ObstructionEthereal = false;
        sp.HitsInteriorCell = false; sp.BuildingCheck = false; sp.WalkableAllowance = 0; sp.WalkInterp = 0; sp.StepDownAmt = 0;
        sp.WalkableCheckPos = new Sphere(); sp.Walkable = null; sp.CheckWalkable = false; sp.WalkableUp = default; sp.WalkablePos = null;
        sp.WalkableScale = 0; sp.CellArrayValid = false; sp.NegStepUp = false; sp.NegCollisionNormal = default; sp.NegPolyHit = false;
        sp.PlacementAllowsSliding = true;
    }

    static void ResetPos(Position p) { p.ObjCellID = 0; p.Frame.Origin = Vector3.Zero; p.Frame.Orientation = Quaternion.Identity; }

    // Same as Reset, but keeps the three Position instances (copy-into); only valid if no alias escaped.
    public static int CountFields(Type t) => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length;

    public static List<string> Diff(object a, object b, string path, int depth = 0)
    {
        var diffs = new List<string>();
        if (a == null || b == null) { if (!(a == null && b == null)) diffs.Add($"{path}: null mismatch ({a == null} vs {b == null})"); return diffs; }
        var type = a.GetType();
        if (type.IsPrimitive || type.IsEnum || type.IsValueType || type == typeof(string)) { if (!Equals(a, b)) diffs.Add($"{path}: {a} vs {b}"); return diffs; }
        if (a is ICollection ca && b is ICollection cb) { if (ca.Count != cb.Count) diffs.Add($"{path}.Count: {ca.Count} vs {cb.Count}"); return diffs; }
        if (depth > 3) return diffs;
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            diffs.AddRange(Diff(f.GetValue(a), f.GetValue(b), path + "." + f.Name, depth + 1));
        return diffs;
    }

    public static void Run(Action<string, Action> measure)
    {
        Console.WriteLine($"   field counts: Transition={CountFields(typeof(Transition))} SpherePath={CountFields(typeof(SpherePath))} CollisionInfo={CountFields(typeof(CollisionInfo))} ObjectInfo={CountFields(typeof(ObjectInfo))} CellArray={CountFields(typeof(CellArray))}");

        // dirty a transition through the public API, then reset, then compare with a fresh one
        var spheres = new List<Sphere> { new Sphere(new Vector3(0, 0, 0.475f), 0.475f), new Sphere(new Vector3(0, 0, 1.35f), 0.475f) };
        var begin = new Position(0xA9B40019); begin.Frame.Origin = new Vector3(100, 100, 50);
        var end = new Position(0xA9B40019); end.Frame.Origin = new Vector3(100.3f, 100, 50);
        var obj = new PhysicsObj();
        var t = new Transition();
        t.InitObject(obj, ObjectInfoState.Contact | ObjectInfoState.OnWalkable);
        t.InitSphere(2, spheres, 1.0f); t.InitPath(null, begin, end);
        t.InitContactPlane(0xA9B40019, new Plane(Vector3.UnitZ, -50), false); t.InitSlidingNormal(Vector3.UnitX);
        t.SpherePath.SetCheckPos(t.SpherePath.CurPos, null); t.SpherePath.SaveCheckPos(); t.SpherePath.StepUp = true; t.SpherePath.Collide = true;
        t.SpherePath.CacheLocalSpaceSphere(begin, 1.0f); t.SetCurrentCheckPos();
        t.CollisionInfo.AddObject(obj, TransitionState.Collided); t.CellArray.add_cell(1, new ObjCell(1)); t.CollisionInfo.FramesStationaryFall = 2;
        var dirtyDiffs = Diff(t, new Transition(), "T");
        Reset(t);
        var diffs = Diff(t, new Transition(), "T");
        Console.WriteLine($"   reflection diff vs fresh: before reset {dirtyDiffs.Count} differences, after reset {diffs.Count}");
        foreach (var d in diffs.Take(10)) Console.WriteLine("     " + d);

        var pooled = new Transition();
        measure("PROTO pooled Transition: Reset() + InitSphere(2) + InitPath", () =>
        {
            Reset(pooled); pooled.InitSphere(2, spheres, 1.0f); pooled.InitPath(null, begin, end);
        });
        measure("baseline: new Transition() + InitSphere(2) + InitPath", () =>
        {
            var tt = new Transition(); tt.InitSphere(2, spheres, 1.0f); tt.InitPath(null, begin, end); GC.KeepAlive(tt);
        });
    }
}
