using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HarmonyLib;

using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;

// Runs the REAL ObjCell.find_cell_list over synthetic landblocks (client-mode LScape lookup, no LandblockManager),
// counts LScape.get_landcell calls with a Harmony prefix, and emulates the proposed fix
// (LandCell.add_all_outside_cells(Position,...) sets cellArray.AddedOutside = true) with a Harmony postfix.
public static class CellListBench
{
    public static int GetLandcellCalls;
    public static bool EmulateFix;
    static void CountPrefix() => GetLandcellCalls++;
    static void FixPostfix(CellArray cellArray) { if (EmulateFix) cellArray.AddedOutside = true; }

    public static void Run()
    {
        Env.Init();
        // build 3x3 landblocks around A9B4 (Server=true while constructing: avoids client-only UV code)
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                uint id = (uint)(((0xA9 + dx) << 24) | ((0xB4 + dy) << 16) | 0xFFFF);
                var clb = Lookup.MakeClb();
                typeof(ACE.DatLoader.FileTypes.FileType).GetProperty("Id")!.SetValue(clb, id);
                LScape.Landblocks[id] = new Landblock(clb);
            }
        PhysicsEngine.Instance.Server = false;   // LScape.get_landblock -> LScape.Landblocks (no LandblockManager)

        var h = new Harmony("geombench");
        h.Patch(AccessTools.Method(typeof(LScape), nameof(LScape.get_landcell)), prefix: new HarmonyMethod(typeof(CellListBench), nameof(CountPrefix)));
        var aaoc = typeof(LandCell).GetMethods().First(m => m.Name == "add_all_outside_cells" && m.GetParameters()[0].ParameterType == typeof(Position));
        h.Patch(aaoc, postfix: new HarmonyMethod(typeof(CellListBench), nameof(FixPostfix)));

        var cases = new (string name, Vector3 origin)[]
        {
            ("cell interior (1 cell)", new Vector3(36, 36, 0)),
            ("near cell edge (2 cells)", new Vector3(47.7f, 36, 0)),
            ("near cell corner (4 cells)", new Vector3(47.7f, 47.7f, 0)),
            ("near landblock corner (4 cells, 4 landblocks)", new Vector3(191.7f, 191.7f, 0)),
        };
        foreach (var (name, o) in cases)
        {
            var cellId = (uint)(0xA9B40000 | (uint)((int)(o.X / 24) * 8 + (int)(o.Y / 24) + 1));
            var pos = new Position(cellId, new AFrame(o, Quaternion.Identity));
            var spheres = new List<Sphere> { new Sphere(o + new Vector3(0, 0, 0.48f), 0.48f), new Sphere(o + new Vector3(0, 0, 1.35f), 0.48f) };
            string Run1(bool fix, out int calls)
            {
                EmulateFix = fix; GetLandcellCalls = 0;
                var ca = new CellArray(); ObjCell cur = ObjCell.EmptyCell;
                ObjCell.find_cell_list(pos, 2, spheres, ca, ref cur, null);
                calls = GetLandcellCalls;
                return string.Join(",", ca.Cells.Keys.OrderBy(k => k).Select(k => k.ToString("X8"))) + " cur=" + cur?.ID.ToString("X8");
            }
            var before = Run1(false, out var c0);
            var after = Run1(true, out var c1);
            // within one transition the CellArray is reused across insertion attempts and never cleared (bugs-physics H.2)
            int reuseCalls(bool fix) { EmulateFix = fix; GetLandcellCalls = 0; var ca = new CellArray(); for (int a = 0; a < 5; a++) { ObjCell cur = ObjCell.EmptyCell; ObjCell.find_cell_list(pos, 2, spheres, ca, ref cur, null); } return GetLandcellCalls; }
            Console.WriteLine($"== {name}: cells [{before}]");
            Console.WriteLine($"   get_landcell calls per find_cell_list: current {c0}, with AddedOutside fix {c1};  identical result: {before == after}");
            Console.WriteLine($"   5 successive find_cell_list on one CellArray (as in one transition): current {reuseCalls(false)}, fixed {reuseCalls(true)}");
            EmulateFix = false;
            var tA = Timer.Time("   real find_cell_list (current)", 400_000, n => { float s = 0; for (int i = 0; i < n; i++) { var ca = new CellArray(); ObjCell cur = ObjCell.EmptyCell; ObjCell.find_cell_list(pos, 2, spheres, ca, ref cur, null); s += ca.Cells.Count; } return s; }, 3);
            EmulateFix = true;
            var tB = Timer.Time("   real find_cell_list (fix emulated)", 400_000, n => { float s = 0; for (int i = 0; i < n; i++) { var ca = new CellArray(); ObjCell cur = ObjCell.EmptyCell; ObjCell.find_cell_list(pos, 2, spheres, ca, ref cur, null); s += ca.Cells.Count; } return s; }, 3);
            Console.WriteLine($"   ratio current/fixed {tA.ns / tB.ns:F2}x  (client-mode lookup: NO global LandblockManager lock; production pays P.1's lock per call)");
        }
    }
}
