using System;
using System.Linq;

using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

static class Extra
{
    public static void Run()
    {
        Program.Measure(() => new object(), 2000, 500);
        Console.WriteLine("=== X1 (IN.5). ObjectsKnownByMe stage: list-and-scan (real, Player_Inventory.cs:615) vs the existing keyed ObjectMaint.GetKnownObject ===");
        foreach (var n in new[] { 50, 500 })
        {
            var sc = Appraise.Build(n, 0, 2, true);
            var ex = sc.Examiner;
            var hit = sc.Monster2.Guid; var miss = new ObjectGuid(0x8FFFFFF0);
            var a = ex.GetKnownObjects().FirstOrDefault(o => o.Guid == hit); var b = ex.ObjMaint.GetKnownObject(hit.Full)?.WeenieObj.WorldObject;
            var c = ex.GetKnownObjects().FirstOrDefault(o => o.Guid == miss); var d = ex.ObjMaint.GetKnownObject(miss.Full)?.WeenieObj.WorldObject;
            Console.WriteLine($"  known={sc.Known}: same object on hit: {ReferenceEquals(a, b) && a != null}, both null on miss: {c == null && d == null}");
            Program.P("    real, miss", Program.Measure(() => ex.GetKnownObjects().FirstOrDefault(o => o.Guid == miss), 4000, 600));
            Program.P("    keyed, miss", Program.Measure(() => { var r = ex.ObjMaint.GetKnownObject(miss.Full)?.WeenieObj.WorldObject; GC.KeepAlive(r); }, 4000, 600));
            Program.P("    keyed, hit", Program.Measure(() => { var r = ex.ObjMaint.GetKnownObject(hit.Full)?.WeenieObj.WorldObject; GC.KeepAlive(r); }, 4000, 600));
        }

        Console.WriteLine();
        Console.WriteLine("=== X3 (IN.8). Player.OnTalk(message): runs on every say, emote AND spell cast (Player_Magic.cs:602) ===");
        foreach (var n in new[] { 50, 500 })
        {
            var sc = Appraise.Build(n, 0, 2, true);
            var ex = sc.Examiner;
            Program.P($"    known={sc.Known,4} (all creatures, none with emotes): OnTalk(\"Zojak Quafeth\")", Program.Measure(() => ex.OnTalk("Zojak Quafeth"), 4000, 600));
            Program.P($"    known={sc.Known,4}: ObjMaint.GetKnownObjectsValuesAsCreature() alone", Program.Measure(() => ex.PhysicsObj.ObjMaint.GetKnownObjectsValuesAsCreature(), 4000, 600));
        }
        {
            var sc = Appraise.Build(4, 0, 2, true);
            var ex = sc.Examiner;
            Console.WriteLine();
            Console.WriteLine("=== X4. small pieces ===");
            Program.P("    Player.GetRunRate() (MovementData.cs:99 evaluates it for every MoveToState with HoldKey.Run)", Program.Measure(() => ex.GetRunRate()));
            Program.P("    new WorldObjectInfo(creature) (QueryHealth allocates two per call)", Program.Measure(() => new ACE.Server.Entity.WorldObjectInfo(sc.Monster)));
            Program.P("    Creature.GetCreatureSkill(AssessCreature).Current + target Deception.Current (Examine)", Program.Measure(() => { var a = ex.GetCreatureSkill(ACE.Entity.Enum.Skill.AssessCreature).Current; var b = sc.Monster.GetCreatureSkill(ACE.Entity.Enum.Skill.Deception).Current; }));
        }

        Console.WriteLine();
        Console.WriteLine("=== X2. server option show_aura_buff (default false): appraising a weapon wielded by a player with 60 enchantments ===");
        {
            var s = Appraise.Build(40, 20, 12, true);
            var ex = s.Examiner; var g = s.WieldedByOther.Guid.Full;
            Program.P("    show_aura_buff=false (default)", Program.Measure(() => { ex.HandleActionIdentifyObject(g); s.ClearBundles(); }));
            PropertyManager.ModifyBool("show_aura_buff", true);
            Program.P("    show_aura_buff=true", Program.Measure(() => { ex.HandleActionIdentifyObject(g); s.ClearBundles(); }, 5000, 1000));
            PropertyManager.ModifyBool("show_aura_buff", false);
        }
    }
}
