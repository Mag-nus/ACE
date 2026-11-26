using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

static class Appraise
{
    static void Describe(string name, WorldObject wo, Player examiner)
    {
        var ai = new AppraiseInfo(wo, examiner, true);
        var msg = new GameEventIdentifyObjectResponse(examiner.Session, wo, true);
        var b = wo.Biota;
        Console.WriteLine($"   [{name}] biota: int={b.PropertiesInt?.Count ?? 0} i64={b.PropertiesInt64?.Count ?? 0} bool={b.PropertiesBool?.Count ?? 0} float={b.PropertiesFloat?.Count ?? 0} str={b.PropertiesString?.Count ?? 0} did={b.PropertiesDID?.Count ?? 0} iid={b.PropertiesIID?.Count ?? 0} spells={b.PropertiesSpellBook?.Count ?? 0} ench={b.PropertiesEnchantmentRegistry?.Count ?? 0}");
        Console.WriteLine($"   [{name}] sent:  int={ai.PropertiesInt.Count} i64={ai.PropertiesInt64.Count} bool={ai.PropertiesBool.Count} float={ai.PropertiesFloat.Count} str={ai.PropertiesString.Count} did={ai.PropertiesDID.Count} iid(built, never written)={ai.PropertiesIID.Count} spellbook={ai.SpellBook.Count} flags=[{ai.Flags}]  message={msg.Data.Length} bytes");
    }

    public class Scene
    {
        public Player Examiner, Target, Target2; public Creature Monster, Monster2; public MeleeWeapon Weapon; public Clothing Armor, WornArmor; public Corpse Corpse;
        public WorldObject InMainPack, InSidePack, InCorpseWeapon, InCorpseArmor, WieldedByOther;
        public ACE.Server.Entity.Landblock Landblock;
        public Action ClearBundles; public int Known;
    }

    /// One landblock with `creatures` monsters in it, `adjCreatures` in each of 8 adjacents, 2 other players, one open corpse viewed by the examiner.
    public static Scene Build(int creatures = 40, int adjCreatures = 20, int corpseLoot = 12, bool silent = false)
    {
        var s = new Scene();
        s.Examiner = B.MakePlayer(0x50000001, "Examiner");
        s.Target = B.MakePlayer(0x50000009, "Target");
        s.Target2 = B.MakePlayer(0x5000000A, "Targetb");
        foreach (var sk in new[] { Skill.AssessCreature, Skill.AssessPerson }) s.Examiner.GetCreatureSkill(sk).Ranks = 2000;   // so the skill roll succeeds (worst case = full profile)
        var objs = new List<WorldObject> { s.Examiner, s.Target, s.Target2 };
        for (int k = 0; k < creatures; k++) objs.Add(B.MakeMonster(0x80000100 + (uint)k));
        if (creatures < 2) { objs.Add(B.MakeMonster(0x80000090)); objs.Add(B.MakeMonster(0x80000091)); }
        s.Monster = (Creature)objs[3]; s.Monster2 = (Creature)objs[4];
        uint next = 0x80400000;
        s.Corpse = B.MakeCorpse(0x80300000, corpseLoot, ref next);
        objs.Add(s.Corpse);
        s.Landblock = B.MakeLandblock(objs, 8, adjCreatures);
        foreach (var o in objs) B.SetLandblock(o, s.Landblock);
        s.Weapon = B.MakeLootWeapon(0x80000200);
        s.Armor = B.MakeLootArmor(0x80000201);
        s.WornArmor = s.Target.EquippedObjects.Values.OfType<Clothing>().First();
        s.WieldedByOther = s.Target2.EquippedObjects.Values.OfType<MeleeWeapon>().First();
        s.InMainPack = B.MakeLootWeapon(0x80000202, 8, s.Examiner.Guid.Full); s.Examiner.Inventory[s.InMainPack.Guid] = s.InMainPack;
        var lastPack = s.Examiner.Inventory.Values.OfType<Container>().Last();
        s.InSidePack = B.MakeLootWeapon(0x80000203, 8, lastPack.Guid.Full); lastPack.Inventory[s.InSidePack.Guid] = s.InSidePack;
        s.InCorpseWeapon = s.Corpse.Inventory.Values.OfType<MeleeWeapon>().Last();
        s.InCorpseArmor = s.Corpse.Inventory.Values.OfType<Clothing>().Last();
        s.Corpse.IsOpen = true; s.Corpse.Viewer = s.Examiner.Guid.Full;
        s.Examiner.LastOpenedContainerId = s.Corpse.Guid;
        B.Know(s.Examiner, objs);
        s.Known = objs.Count - 1;
        s.ClearBundles = B.BundleClearer(s.Examiner);
        if (!silent)
        {
            Console.WriteLine($"scene: landblock objects={objs.Count} (creatures incl. players={objs.Count(o => o is Creature)}), 8 adjacents x {adjCreatures} creatures; examiner main pack={s.Examiner.Inventory.Count} entries ({s.Examiner.Inventory.Values.Count(i => i is Container)} side packs x 20), equipped={s.Examiner.EquippedObjects.Count}; corpse items={s.Corpse.Inventory.Count}; known objects={s.Known}");
        }
        return s;
    }

    static int Handle(Scene s, uint guid)
    {
        s.Examiner.HandleActionIdentifyObject(guid);
        int len = B.LastMessageLength(s.Examiner);
        s.ClearBundles();
        return len;
    }

    public static void Run()
    {
        Console.WriteLine($"assessment sets: int={AssessmentProperties.PropertiesInt.Count} i64={AssessmentProperties.PropertiesInt64.Count} bool={AssessmentProperties.PropertiesBool.Count} float={AssessmentProperties.PropertiesDouble.Count} str={AssessmentProperties.PropertiesString.Count} did={AssessmentProperties.PropertiesDataId.Count} iid={AssessmentProperties.PropertiesInstanceId.Count}");
        Scene s = null;
        Program.Try("scene", () => s = Build());
        if (s == null) return;
        var ex = s.Examiner;

        Program.Try("describe", () =>
        {
            Describe("monster", s.Monster, ex); Describe("player", s.Target, ex); Describe("loot weapon", s.Weapon, ex); Describe("loot armor", s.Armor, ex);
            Describe("worn armor+8 banes", s.WornArmor, ex); Describe("corpse", s.Corpse, ex);
        });

        Program.Measure(() => new object(), 2000, 500);   // throwaway: lets the harness itself tier up
        Console.WriteLine();
        Console.WriteLine("=== A. new GameEventIdentifyObjectResponse(session, wo, success) : AppraiseInfo build + Write + GameEvent header (REAL code) ===");
        var cases = new (string, WorldObject)[] { ("monster (success)", s.Monster), ("player, 16 equipped, 60 enchantments", s.Target), ("loot weapon, 8 spells", s.Weapon), ("loot armor, 8 spells", s.Armor), ("worn armor, 6 spells + 8 banes", s.WornArmor), ("corpse", s.Corpse), ("misc stackable", s.Examiner.Inventory.Values.First(i => !(i is Container))) };
        foreach (var (name, wo) in cases)
            Program.Try(name, () =>
            {
                var full = Program.Measure(() => new GameEventIdentifyObjectResponse(ex.Session, wo, true));
                var build = Program.Measure(() => new AppraiseInfo(wo, ex, true));
                var ai = new AppraiseInfo(wo, ex, true);
                var ms = new MemoryStream(1024); var bw = new BinaryWriter(ms);
                var write = Program.Measure(() => { ms.Position = 0; bw.Write(ai); });
                var copies = Program.Measure(() =>
                {
                    wo.GetAllPropertyIntWhere(AssessmentProperties.PropertiesInt); wo.GetAllPropertyInt64Where(AssessmentProperties.PropertiesInt64);
                    wo.GetAllPropertyBoolsWhere(AssessmentProperties.PropertiesBool); wo.GetAllPropertyFloatWhere(AssessmentProperties.PropertiesDouble);
                    wo.GetAllPropertyStringWhere(AssessmentProperties.PropertiesString); wo.GetAllPropertyDataIdWhere(AssessmentProperties.PropertiesDataId);
                    wo.GetAllPropertyInstanceIdWhere(AssessmentProperties.PropertiesInstanceId);
                });
                Program.P("  " + name + " : full message", full);
                Program.P("      of which AppraiseInfo ctor", build);
                Program.P("          of which the 7 GetAllProperty*Where copies", copies);
                Program.P("      of which Write(AppraiseInfo) (6 SortedDictionary)", write);
            });
        Program.Try("monster fail", () => Program.P("  monster (assessment FAILED) : full message", Program.Measure(() => new GameEventIdentifyObjectResponse(ex.Session, s.Monster, false))));
        Program.Try("empty", () => Program.P("  not found : empty response", Program.Measure(() => new GameEventIdentifyObjectResponse(ex.Session, 0x80001234u))));

        Console.WriteLine();
        Console.WriteLine("=== B. Player.HandleActionIdentifyObject(guid) end to end, incl. FindObject, Examine, EnqueueSend, OnAppraisal (REAL code) ===");
        Console.WriteLine("    (alternating two targets = the Examine path every time; 'same' = the repeat path at Player.cs:281)");
        void H(string name, uint g1, uint g2)
        {
            Program.Try(name, () =>
            {
                int len = Handle(s, g1);
                bool flip = false;
                var r = Program.Measure(() => { flip = !flip; ex.HandleActionIdentifyObject(flip ? g1 : g2); s.ClearBundles(); });
                Program.P("  " + name, r, $"reply={len} B");
            });
        }
        H("monster, alternating 2 monsters", s.Monster.Guid.Full, s.Monster2.Guid.Full);
        H("monster, same target repeatedly", s.Monster.Guid.Full, s.Monster.Guid.Full);
        H("player, alternating 2 players", s.Target.Guid.Full, s.Target2.Guid.Full);
        H("player, same target repeatedly", s.Target.Guid.Full, s.Target.Guid.Full);
        H("self", ex.Guid.Full, ex.Guid.Full);
        H("loot weapon in own MAIN pack (first lookup hits)", s.InMainPack.Guid.Full, s.InMainPack.Guid.Full);
        H("loot weapon in own 7th SIDE pack", s.InSidePack.Guid.Full, s.InSidePack.Guid.Full);
        H("own equipped armor with banes", ex.EquippedObjects.Values.OfType<Clothing>().First().Guid.Full, ex.EquippedObjects.Values.OfType<Clothing>().Last().Guid.Full);
        H("loot weapon/armor inside the open corpse", s.InCorpseWeapon.Guid.Full, s.InCorpseArmor.Guid.Full);
        H("corpse itself", s.Corpse.Guid.Full, s.Corpse.Guid.Full);
        H("weapon wielded by another player", s.WieldedByOther.Guid.Full, s.WieldedByOther.Guid.Full);
        H("STALE guid (object gone): full FindObject miss, empty reply", 0x8FFFFFF0, 0x8FFFFFF1);
        H("guid 0 (deselect)", 0, 0);
    }
}
