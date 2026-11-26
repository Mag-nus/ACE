using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

/// Player.FindObject(guid, SearchLocations.Everywhere) scaling - REAL code on a real Player type with a reflection-built Landblock.
static class Find
{
    public static void Run()
    {
        Console.WriteLine("=== C. Player.FindObject(guid, Everywhere) by where the object is (scene: 43 creatures in landblock, 8 adjacents x 20) ===");
        var s = Appraise.Build(40, 20, 12);
        var ex = s.Examiner;
        void F(string name, uint guid, Player.SearchLocations loc = Player.SearchLocations.Everywhere)
        {
            var g = new ObjectGuid(guid);
            var found = ex.FindObject(g, loc, out _, out _, out _);
            Program.P("  " + name, Program.Measure(() => ex.FindObject(g, loc, out _, out _, out _)), found == null ? "(null)" : "");
        }
        Program.Measure(() => new object(), 2000, 500);   // throwaway: lets the harness itself tier up
        F("self", ex.Guid.Full);
        F("item in own main pack", s.InMainPack.Guid.Full);
        F("item in own 7th side pack", s.InSidePack.Guid.Full);
        F("own equipped item", ex.EquippedObjects.Keys.First().Full);
        F("monster in current landblock", s.Monster.Guid.Full);
        F("item in the open corpse", s.InCorpseWeapon.Guid.Full);
        F("item wielded by another player", s.WieldedByOther.Guid.Full);
        F("stale guid (miss everywhere)", 0x8FFFFFF0);
        F("stale guid, LocationsICanMove (inventory handlers)", 0x8FFFFFF0, Player.SearchLocations.LocationsICanMove);
        F("monster, SearchLocations.Landblock only", s.Monster.Guid.Full, Player.SearchLocations.Landblock);

        Console.WriteLine();
        Console.WriteLine("=== D. Container.GetInventoryItem(guid) MISS (paid first by every FindObject for anything not in the main pack) ===");
        foreach (var (loose, packs, per) in new[] { (10, 0, 0), (60, 7, 20), (95, 7, 24), (95, 8, 24) })
        {
            var p = B.MakePlayer(0x50000100, "Inv", 20, 0, false, false);
            uint next = 0x80700000;
            for (int k = 0; k < loose; k++) { var it = B.MakeMisc(next++, p.Guid.Full); p.Inventory[it.Guid] = it; }
            for (int k = 0; k < packs; k++) { var pack = B.MakePack(next++, p.Guid.Full, per, ref next); p.Inventory[pack.Guid] = pack; }
            var g = new ObjectGuid(0x8FFFFFF0);
            Program.P($"  main pack {loose} loose + {packs} side packs x {per}", Program.Measure(() => p.GetInventoryItem(g, out _)));
        }

        Console.WriteLine();
        Console.WriteLine("=== E. stale-guid FindObject(Everywhere) vs. landblock population and known objects ===");
        Console.WriteLine("    (objects = creatures in the current landblock; each of 8 adjacents holds objects/4; known = ObjMaint.KnownObjects)");
        foreach (var n in new[] { 0, 50, 200, 500, 1000 })
        {
            var sc = Appraise.Build(n, n / 4, 4, true);
            var g = new ObjectGuid(0x8FFFFFF0);
            var e2 = sc.Examiner;
            Program.P($"  objects={n,4} adjacents=8x{n / 4,3} known={sc.Known,4}", Program.Measure(() => e2.FindObject(g, Player.SearchLocations.Everywhere, out _, out _, out _), 4000, 600));
        }

        Console.WriteLine();
        Console.WriteLine("=== F. the two O(N) stages on their own (objects=500, adjacents 8x125) ===");
        {
            var sc = Appraise.Build(500, 125, 4, true);
            var g = new ObjectGuid(0x8FFFFFF0);
            var e2 = sc.Examiner;
            Program.P("  Landblock.GetWieldedObject(guid) miss (walks every object in 9 landblocks)", Program.Measure(() => sc.Landblock.GetWieldedObject(g), 4000, 600));
            Program.P("  Player.GetKnownObjects() (lock + Where + Select + ToList)", Program.Measure(() => e2.GetKnownObjects(), 4000, 600));
            Program.P("  GetKnownObjects().FirstOrDefault(o => o.Guid == guid) miss", Program.Measure(() => e2.GetKnownObjects().FirstOrDefault(o => o.Guid == g), 4000, 600));
            Program.P("  Landblock.GetObject(guid) miss (9 landblocks x 3 lookups)", Program.Measure(() => sc.Landblock.GetObject(g), 4000, 600));
        }
    }
}
