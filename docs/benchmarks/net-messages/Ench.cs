using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using ACE.Entity.Enum;
using ACE.Entity.Models;

static class Ench
{
    public static void Run()
    {
        // A buffed player: 48 enchantments across 48 categories (typical full buff set: attributes, skills, vitals, armor, protections)
        var reg = new Collection<PropertiesEnchantmentRegistry>();
        var rnd = new Random(1);
        for (int i = 0; i < 48; i++)
        {
            EnchantmentTypeFlags t; uint key;
            if (i < 6) { t = EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.SingleStat; key = (uint)(i + 1); }
            else if (i < 9) { t = EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.SingleStat; key = (uint)(1 + (i - 6) * 2); }
            else { t = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.SingleStat; key = (uint)i; }
            reg.Add(new PropertiesEnchantmentRegistry { SpellId = 1000 + i, SpellCategory = (SpellCategory)(100 + i), PowerLevel = 400, StartTime = -rnd.Next(1000), Duration = 3600, StatModType = t, StatModKey = key, StatModValue = 10, LayerId = 1 });
        }
        var rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        var setSpells = new HashSet<int>();
        var r = Program.Measure(() => reg.GetEnchantmentsTopLayerByStatModType(EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.Additive, 1u, rw, setSpells, true), 20000);
        Console.WriteLine($"GetEnchantmentsTopLayerByStatModType (48-entry registry, 1 match): alloc={r.bytes:F0} B  {r.ns:F0} ns");
        var r2 = Program.Measure(() => reg.GetEnchantmentsTopLayerByStatModType(EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.Multiplicative, 2u, rw, setSpells, true), 20000);
        Console.WriteLine($"GetEnchantmentsTopLayerByStatModType (48-entry registry, 0 match):  alloc={r2.bytes:F0} B  {r2.ns:F0} ns");
        var small = new Collection<PropertiesEnchantmentRegistry>();
        for (int i = 0; i < 2; i++) small.Add(new PropertiesEnchantmentRegistry { SpellId = 2000 + i, SpellCategory = (SpellCategory)(300 + i), PowerLevel = 100, StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.SingleStat, StatModKey = 6 });
        var r3 = Program.Measure(() => small.GetEnchantmentsTopLayerByStatModType(EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.Multiplicative, 2u, rw, setSpells, true), 20000);
        Console.WriteLine($"GetEnchantmentsTopLayerByStatModType (2-entry monster registry, 0 match): alloc={r3.bytes:F0} B  {r3.ns:F0} ns");
    }
}
