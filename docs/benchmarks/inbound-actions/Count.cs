using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Entity.Models;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

using HarmonyLib;

/// Counts Biota lock acquisitions per appraisal by Harmony-prefixing every public static method in ACE.Entity.Models that takes a ReaderWriterLockSlim
/// (BiotaExtensions.*, PropertiesEnchantmentRegistryExtensions.*). The GetAllProperty*Where copies take the lock inline (7 more, added by hand).
static class Count
{
    static readonly Dictionary<string, int> calls = new Dictionary<string, int>();
    static bool on;

    public static void Prefix(MethodBase __originalMethod)
    {
        if (!on) return;
        var p = __originalMethod.GetParameters();
        var key = __originalMethod.Name + (p.Length > 1 && p[1].ParameterType.IsEnum ? "(" + p[1].ParameterType.Name + ")" : "");
        calls[key] = calls.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    public static void Run()
    {
        var s = Appraise.Build(40, 20, 12, true);
        var ex = s.Examiner;
        // warm caches first (EnchantmentManagerWithCaching memoises), so the counts are steady-state
        foreach (var wo in new WorldObject[] { s.Monster, s.Target, s.Weapon, s.Armor, s.WornArmor, s.Corpse }) new AppraiseInfo(wo, ex, true);

        var h = new Harmony("bench.count");
        var prefix = new HarmonyMethod(typeof(Count).GetMethod(nameof(Prefix)));
        int patched = 0;
        foreach (var t in new[] { typeof(BiotaExtensions), typeof(PropertiesEnchantmentRegistryExtensions) })
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.IsGenericMethodDefinition || !m.GetParameters().Any(p => p.ParameterType == typeof(ReaderWriterLockSlim))) continue;
                h.Patch(m, prefix: prefix); patched++;
            }
        Console.WriteLine($"=== K. Biota lock acquisitions per appraisal (steady state; {patched} lock-taking extension methods instrumented with Harmony) ===");

        void C(string name, Action a)
        {
            calls.Clear(); on = true; a(); on = false;
            int total = calls.Values.Sum();
            int single = calls.Where(k => k.Key.StartsWith("GetProperty")).Sum(k => k.Value);
            Console.WriteLine($"  {name,-58} {total + 7,4} locks = 7 table copies + {single} single-property reads + {total - single} other");
            Console.WriteLine("       " + string.Join(", ", calls.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
        }
        C("AppraiseInfo: monster", () => new AppraiseInfo(s.Monster, ex, true));
        C("AppraiseInfo: player (16 equipped)", () => new AppraiseInfo(s.Target, ex, true));
        C("AppraiseInfo: loot weapon", () => new AppraiseInfo(s.Weapon, ex, true));
        C("AppraiseInfo: loot armor", () => new AppraiseInfo(s.Armor, ex, true));
        C("AppraiseInfo: worn armor with banes", () => new AppraiseInfo(s.WornArmor, ex, true));
        C("AppraiseInfo: corpse", () => new AppraiseInfo(s.Corpse, ex, true));
        Console.WriteLine("  -- whole handler (FindObject + Examine + response + OnAppraisal); two targets alternated so Examine runs --");
        ex.HandleActionIdentifyObject(s.Monster2.Guid.Full);
        C("HandleActionIdentifyObject: monster", () => ex.HandleActionIdentifyObject(s.Monster.Guid.Full));
        C("HandleActionIdentifyObject: player", () => ex.HandleActionIdentifyObject(s.Target.Guid.Full));
        C("HandleActionIdentifyObject: item in open corpse", () => ex.HandleActionIdentifyObject(s.InCorpseWeapon.Guid.Full));
        C("HandleActionIdentifyObject: stale guid", () => ex.HandleActionIdentifyObject(0x8FFFFFF0));
        s.ClearBundles();
    }
}
