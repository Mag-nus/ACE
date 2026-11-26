using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;

/// <summary>
/// Harmony counting prefixes. ONLY used by the `count` mode: every patched method loses inlining and pays a trampoline,
/// so no timing or allocation figure is ever taken in a process that has these attached.
/// </summary>
public static partial class Counters
{
    public static readonly long[] C = new long[96];
    public static readonly List<string> Names = new();
    static Harmony h;

    static void Add(string name, MethodBase m)
    {
        if (m == null) throw new Exception("counter target not found: " + name);
        int slot = Names.Count;
        if (slot >= C.Length) throw new Exception("too many counters");
        Names.Add(name);
        h.Patch(m, prefix: new HarmonyMethod(typeof(Counters).GetMethod("P" + slot, BindingFlags.NonPublic | BindingFlags.Static)));
    }

    static void AddCtors(string name, Type t)
    {
        // one slot shared by all instance ctors of the type
        int slot = Names.Count;
        Names.Add(name);
        var prefix = new HarmonyMethod(typeof(Counters).GetMethod("P" + slot, BindingFlags.NonPublic | BindingFlags.Static));
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            h.Patch(c, prefix: prefix);
    }

    static MethodInfo M(Type t, string name, params Type[] args) =>
        args.Length == 0 ? AccessTools.Method(t, name) : AccessTools.Method(t, name, args);

    public static void Attach()
    {
        h = new Harmony("phys-e2e.count");
        var P = typeof(PhysicsObj); var T = typeof(Transition); var SP = typeof(SpherePath);
        Add("PhysicsObj.update_object", M(P, "update_object"));
        Add("PhysicsObj.UpdateObjectInternal", M(P, "UpdateObjectInternal"));
        Add("PhysicsObj.transition", M(P, "transition"));
        Add("PhysicsObj.SetPositionInternal(Transition)", M(P, "SetPositionInternal", typeof(Transition)));
        Add("PhysicsObj.set_frame", M(P, "set_frame"));
        Add("PhysicsObj.change_cell", M(P, "change_cell"));
        Add("PhysicsObj.calc_cross_cells", M(P, "calc_cross_cells"));
        Add("PhysicsObj.handle_all_collisions", M(P, "handle_all_collisions"));
        Add("PhysicsObj.report_collision_end", M(P, "report_collision_end"));
        Add("PhysicsObj.track_object_collision", M(P, "track_object_collision"));
        Add("PhysicsObj.remove_shadows_from_cells", M(P, "remove_shadows_from_cells"));
        Add("PhysicsObj.add_shadows_to_cell(CellArray)", M(P, "add_shadows_to_cell", typeof(CellArray)));
        Add("PhysicsObj.FindObjCollisions", M(P, "FindObjCollisions", typeof(Transition)));
        Add("PhysicsObj.get_object_info", M(P, "get_object_info"));
        Add("PhysicsObj.handle_visible_cells_non_player", M(P, "handle_visible_cells_non_player"));
        Add("PhysicsObj.UpdatePositionInternal", M(P, "UpdatePositionInternal"));

        Add("Transition..ctor", AccessTools.Constructor(T, Type.EmptyTypes));
        Add("Transition.FindTransitionalPosition", M(T, "FindTransitionalPosition"));
        Add("Transition.TransitionalInsert", M(T, "TransitionalInsert"));
        Add("Transition.InsertIntoCell", M(T, "InsertIntoCell"));
        Add("Transition.CheckOtherCells", M(T, "CheckOtherCells"));
        Add("Transition.ValidateTransition", M(T, "ValidateTransition"));
        Add("Transition.SetCurrentCheckPos", M(T, "SetCurrentCheckPos"));
        Add("Transition.StepDown", M(T, "StepDown"));
        Add("Transition.StepUp", M(T, "StepUp"));
        Add("Transition.EdgeSlide", M(T, "EdgeSlide"));
        Add("Transition.BuildCellArray", M(T, "BuildCellArray"));
        Add("Transition.AdjustOffset", M(T, "AdjustOffset"));
        Add("Transition.CheckWalkable", M(T, "CheckWalkable"));

        Add("SpherePath.SetCheckPos", M(SP, "SetCheckPos"));
        Add("SpherePath.SaveCheckPos", M(SP, "SaveCheckPos"));
        Add("SpherePath.RestoreCheckPos", M(SP, "RestoreCheckPos"));
        Add("SpherePath.CacheLocalSpaceSphere", M(SP, "CacheLocalSpaceSphere"));
        Add("SpherePath.CacheGlobalSphere", M(SP, "CacheGlobalSphere"));
        Add("SpherePath.AddOffsetToCheckPos(Vector3)", M(SP, "AddOffsetToCheckPos", typeof(System.Numerics.Vector3)));
        Add("SpherePath.SetCollide", M(SP, "SetCollide"));
        Add("SpherePath.SetWalkable", M(SP, "SetWalkable"));

        Add("ObjCell.find_cell_list(Position,int,List<Sphere>,...)", M(typeof(ObjCell), "find_cell_list", typeof(Position), typeof(int), typeof(List<Sphere>), typeof(CellArray), typeof(ObjCell).MakeByRefType(), typeof(SpherePath)));
        Add("ObjCell.FindObjCollisions", M(typeof(ObjCell), "FindObjCollisions"));
        Add("LandCell.FindEnvCollisions", M(typeof(LandCell), "FindEnvCollisions"));
        Add("LandCell.find_transit_cells(sphere)", M(typeof(LandCell), "find_transit_cells", typeof(Position), typeof(int), typeof(List<Sphere>), typeof(CellArray), typeof(SpherePath)));
        Add("LandCell.add_all_outside_cells(sphere)", M(typeof(LandCell), "add_all_outside_cells", typeof(Position), typeof(int), typeof(List<Sphere>), typeof(CellArray)));
        Add("LandCell.add_outside_cell(x,y)", M(typeof(LandCell), "add_outside_cell", typeof(CellArray), typeof(float), typeof(float)));
        Add("LScape.get_landcell", M(typeof(LScape), "get_landcell"));
        Add("LScape.get_landblock", M(typeof(LScape), "get_landblock"));
        Add("LandblockManager.GetLandblock (P.1 lock)", M(typeof(LandblockManager), "GetLandblock"));
        Add("CylSphere.IntersectsSphere(Position,float,Transition)", M(typeof(CylSphere), "IntersectsSphere", typeof(Position), typeof(float), typeof(Transition)));
        Add("Sphere.IntersectsSphere(Position,float,Transition,bool)", M(typeof(Sphere), "IntersectsSphere", typeof(Position), typeof(float), typeof(Transition), typeof(bool)));
        Add("ObjectMaint.GetVisibleObjects(cell,type)", M(typeof(ObjectMaint), "GetVisibleObjects", typeof(ObjCell), typeof(ObjectMaint.VisibleObjectType)));
        Add("Landblock.GetServerObjects", M(typeof(Landblock), "GetServerObjects"));
        Add("Sequence.apply_physics", M(typeof(Sequence), "apply_physics"));
        Add("MoveToManager.UseTime", M(typeof(MoveToManager), "UseTime"));

        AddCtors("new Position (any ctor)", typeof(Position));
        AddCtors("new AFrame (any ctor)", typeof(AFrame));
        AddCtors("new Sphere (any ctor)", typeof(Sphere));
        AddCtors("new CylSphere (any ctor)", typeof(CylSphere));
        AddCtors("new ShadowObj", typeof(ShadowObj));
        AddCtors("new CellArray", typeof(CellArray));
        AddCtors("new SpherePath", typeof(SpherePath));
        AddCtors("new ObjectInfo", typeof(ObjectInfo));
        AddCtors("new CollisionInfo", typeof(CollisionInfo));
        AddCtors("new CollisionRecord", typeof(CollisionRecord));
    }

    public static long[] Snapshot() => (long[])C.Clone();
}
