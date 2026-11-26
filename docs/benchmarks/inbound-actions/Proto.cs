using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

/// Prototypes of the proposed local fixes, each checked for identical results against the real code.
static class Proto
{
    // ---- IN.2: GetAllProperty*Where without LINQ ------------------------------------------------
    // Same body as WorldObject_Properties.cs:664-694 with `.Where(lambda)` replaced by foreach + if, and the
    // IDictionary downcast to Dictionary so the struct enumerator is used. `presize` adds a counting pass.
    public static Dictionary<K, V> CopyWhere<K, V>(IDictionary<K, V> biotaDict, Dictionary<K, V?> ephemeral, HashSet<ushort> keys, ReaderWriterLockSlim rwLock, bool presize)
        where K : struct, Enum where V : struct
    {
        Dictionary<K, V> results;
        rwLock.EnterReadLock();
        try
        {
            if (biotaDict is Dictionary<K, V> d)
            {
                if (presize)
                {
                    int n = 0;
                    foreach (var kvp in d) { var k = kvp.Key; if (keys.Contains(Unsafe.As<K, ushort>(ref k))) n++; }
                    results = new Dictionary<K, V>(n);
                    if (n != 0)
                        foreach (var kvp in d) { var k = kvp.Key; if (keys.Contains(Unsafe.As<K, ushort>(ref k))) results[kvp.Key] = kvp.Value; }
                }
                else
                {
                    results = new Dictionary<K, V>();
                    foreach (var kvp in d) { var k = kvp.Key; if (keys.Contains(Unsafe.As<K, ushort>(ref k))) results[kvp.Key] = kvp.Value; }
                }
            }
            else
            {
                results = new Dictionary<K, V>();
                if (biotaDict != null)
                    foreach (var kvp in biotaDict) { var k = kvp.Key; if (keys.Contains(Unsafe.As<K, ushort>(ref k))) results[kvp.Key] = kvp.Value; }
            }
        }
        finally { rwLock.ExitReadLock(); }

        if (ephemeral != null)
        {
            foreach (var property in ephemeral)
            {
                var k = property.Key;
                if (!keys.Contains(Unsafe.As<K, ushort>(ref k))) continue;
                if (property.Value.HasValue) results[property.Key] = property.Value.Value;
                else results.Remove(property.Key);
            }
        }
        return results;
    }

    public static Dictionary<PropertyString, string> CopyWhereString(IDictionary<PropertyString, string> biotaDict, Dictionary<PropertyString, string> ephemeral, HashSet<ushort> keys, ReaderWriterLockSlim rwLock)
    {
        var results = new Dictionary<PropertyString, string>();
        rwLock.EnterReadLock();
        try
        {
            if (biotaDict is Dictionary<PropertyString, string> d)
                foreach (var kvp in d) { if (keys.Contains((ushort)kvp.Key)) results[kvp.Key] = kvp.Value; }
            else if (biotaDict != null)
                foreach (var kvp in biotaDict) { if (keys.Contains((ushort)kvp.Key)) results[kvp.Key] = kvp.Value; }
        }
        finally { rwLock.ExitReadLock(); }
        if (ephemeral != null)
            foreach (var property in ephemeral)
            {
                if (!keys.Contains((ushort)property.Key)) continue;
                if (property.Value != null) results[property.Key] = property.Value; else results.Remove(property.Key);
            }
        return results;
    }

    // ---- IN.3: PackableHashTable write without SortedDictionary -----------------------------------
    // Same bytes as AppraiseInfo.cs:796-872: order by (key % numBuckets, key). Keys are unique, so the order is total and any sort gives it.
    static class BucketOrder<K, V> where K : struct, Enum
    {
        // cached delegates: Span.Sort(Comparison) allocates nothing, whereas Sort(span, IComparer) allocates a Comparison per call
        public static readonly Comparison<KeyValuePair<K, V>> By8 = static (a, b) => Cmp(a, b, 8);
        public static readonly Comparison<KeyValuePair<K, V>> By16 = static (a, b) => Cmp(a, b, 16);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Cmp(KeyValuePair<K, V> a, KeyValuePair<K, V> b, int numBuckets)
        {
            var ka = a.Key; var kb = b.Key;
            int ia = Unsafe.As<K, ushort>(ref ka), ib = Unsafe.As<K, ushort>(ref kb);
            int r = (ia % numBuckets) - (ib % numBuckets);
            return r != 0 ? r : ia - ib;
        }
    }

    public delegate void WriteValue<V>(BinaryWriter w, V value);

    public static void WriteTable<K, V>(BinaryWriter writer, Dictionary<K, V> props, ushort numBuckets, WriteValue<V> writeValue) where K : struct, Enum
    {
        PackableHashTable.WriteHeader(writer, props.Count, numBuckets);
        int n = props.Count;
        if (n == 0) return;
        var rented = ArrayPool<KeyValuePair<K, V>>.Shared.Rent(n);
        int i = 0;
        foreach (var kvp in props) rented[i++] = kvp;
        var span = rented.AsSpan(0, n);
        if (n > 1) span.Sort(numBuckets == 16 ? BucketOrder<K, V>.By16 : BucketOrder<K, V>.By8);
        foreach (ref readonly var kvp in span)
        {
            var k = kvp.Key;
            writer.Write((uint)Unsafe.As<K, ushort>(ref k));
            writeValue(writer, kvp.Value);
        }
        span.Clear();   // drop string references before returning the buffer
        ArrayPool<KeyValuePair<K, V>>.Shared.Return(rented);
    }

    static readonly WriteValue<int> WInt = static (w, v) => w.Write(v);
    static readonly WriteValue<long> WLong = static (w, v) => w.Write(v);
    static readonly WriteValue<bool> WBool = static (w, v) => w.Write(Convert.ToUInt32(v));
    static readonly WriteValue<double> WDouble = static (w, v) => w.Write(v);
    static readonly WriteValue<uint> WUInt = static (w, v) => w.Write(v);
    static readonly WriteValue<string> WString = static (w, v) => ACE.Server.Network.Extensions.WriteString16L(w, v);

    /// The table writes of AppraiseInfoExtensions.Write(writer, info) (only non-empty tables, as the Flags dictate), real vs prototype.
    static void WriteTablesReal(BinaryWriter w, AppraiseInfo ai)
    {
        if (ai.PropertiesInt.Count > 0) w.Write(ai.PropertiesInt); if (ai.PropertiesInt64.Count > 0) w.Write(ai.PropertiesInt64); if (ai.PropertiesBool.Count > 0) w.Write(ai.PropertiesBool);
        if (ai.PropertiesFloat.Count > 0) w.Write(ai.PropertiesFloat); if (ai.PropertiesString.Count > 0) w.Write(ai.PropertiesString); if (ai.PropertiesDID.Count > 0) w.Write(ai.PropertiesDID);
    }
    static void WriteTablesProto(BinaryWriter w, AppraiseInfo ai)
    {
        if (ai.PropertiesInt.Count > 0) WriteTable(w, ai.PropertiesInt, 16, WInt); if (ai.PropertiesInt64.Count > 0) WriteTable(w, ai.PropertiesInt64, 8, WLong);
        if (ai.PropertiesBool.Count > 0) WriteTable(w, ai.PropertiesBool, 8, WBool); if (ai.PropertiesFloat.Count > 0) WriteTable(w, ai.PropertiesFloat, 8, WDouble);
        if (ai.PropertiesString.Count > 0) WriteTable(w, ai.PropertiesString, 8, WString); if (ai.PropertiesDID.Count > 0) WriteTable(w, ai.PropertiesDID, 8, WUInt);
    }

    // ---- IN.4: ArmorLevel without BodyParts.GetFlags (Enum.GetValues + Cast boxing) -----------------
    // (a) same shape as ArmorLevel.cs:40-80, the flag list replaced by a mask test; (b) one pass over EquippedObjects.
    static readonly BodyPart[] Parts = { BodyPart.Head, BodyPart.Chest, BodyPart.Abdomen, BodyPart.UpperArm, BodyPart.LowerArm, BodyPart.Hand, BodyPart.UpperLeg, BodyPart.LowerLeg, BodyPart.Foot };

    public static uint ArmorLevelMask(Creature creature, BodyPart bodyPart)
    {
        var mask = BodyParts.GetCoverageMask(bodyPart) & ~CoverageMask.Unknown;
        var layers = creature.EquippedObjects.Values.Where(e => e is Clothing && e.ClothingPriority is CoverageMask c && (c & mask) != 0).ToList();
        var totalAL = 0;
        foreach (var layer in layers)
            totalAL += (layer.GetProperty(PropertyInt.ArmorLevel) ?? 0) + layer.EnchantmentManager.GetArmorMod();
        totalAL = Math.Max(0, totalAL);
        if (layers.Count > 0 && !layers.Any(i => i.IsEnchantable)) totalAL += 9999;
        return (uint)totalAL;
    }

    /// AttributeMaskHelper.GetAttributeHighlights + GetAttributeColors (AttributeMask.cs) evaluate ModifierType twice per stat; this reads it once.
    public static void AttributeMasksOnce(Creature c, out ACE.Server.Network.Enum.AttributeMask highlights, out ACE.Server.Network.Enum.AttributeMask colors)
    {
        highlights = 0; colors = 0;
        void Add(ModifierType m, ACE.Server.Network.Enum.AttributeMask bit, ref ACE.Server.Network.Enum.AttributeMask h, ref ACE.Server.Network.Enum.AttributeMask col)
        {
            if (m != ModifierType.None) h |= bit;
            if (m == ModifierType.Buffed) col |= bit;
        }
        Add(c.Strength.ModifierType, ACE.Server.Network.Enum.AttributeMask.Strength, ref highlights, ref colors);
        Add(c.Endurance.ModifierType, ACE.Server.Network.Enum.AttributeMask.Endurance, ref highlights, ref colors);
        Add(c.Quickness.ModifierType, ACE.Server.Network.Enum.AttributeMask.Quickness, ref highlights, ref colors);
        Add(c.Coordination.ModifierType, ACE.Server.Network.Enum.AttributeMask.Coordination, ref highlights, ref colors);
        Add(c.Focus.ModifierType, ACE.Server.Network.Enum.AttributeMask.Focus, ref highlights, ref colors);
        Add(c.Self.ModifierType, ACE.Server.Network.Enum.AttributeMask.Self, ref highlights, ref colors);
        Add(c.Health.ModifierType, ACE.Server.Network.Enum.AttributeMask.Health, ref highlights, ref colors);
        Add(c.Stamina.ModifierType, ACE.Server.Network.Enum.AttributeMask.Stamina, ref highlights, ref colors);
        Add(c.Mana.ModifierType, ACE.Server.Network.Enum.AttributeMask.Mana, ref highlights, ref colors);
    }

    public static void ArmorLevelOnePass(Creature creature, Span<uint> result)
    {
        Span<int> total = stackalloc int[9]; Span<int> count = stackalloc int[9]; Span<bool> anyEnchantable = stackalloc bool[9];
        Span<CoverageMask> masks = stackalloc CoverageMask[9];
        for (int p = 0; p < 9; p++) masks[p] = BodyParts.GetCoverageMask(Parts[p]) & ~CoverageMask.Unknown;
        foreach (var e in creature.EquippedObjects.Values)
        {
            if (!(e is Clothing)) continue;
            var cp = e.ClothingPriority;
            if (cp == null) continue;
            bool read = false; int al = 0; bool ench = false;
            for (int p = 0; p < 9; p++)
            {
                if ((cp.Value & masks[p]) == 0) continue;
                if (!read) { al = (e.GetProperty(PropertyInt.ArmorLevel) ?? 0) + e.EnchantmentManager.GetArmorMod(); ench = e.IsEnchantable; read = true; }
                total[p] += al; count[p]++; anyEnchantable[p] |= ench;
            }
        }
        for (int p = 0; p < 9; p++)
        {
            var t = Math.Max(0, total[p]);
            if (count[p] > 0 && !anyEnchantable[p]) t += 9999;
            result[p] = (uint)t;
        }
    }

    // ---- IN.1: Container.GetInventoryItem without the LINQ + ToList per container ------------------
    // Same search order and result as Container.cs:264-287.
    public static WorldObject GetInventoryItem(Container c, ObjectGuid objectGuid, out Container container)
    {
        if (c.Inventory.TryGetValue(objectGuid, out var value)) { container = c; return value; }
        foreach (var item in c.Inventory.Values)
        {
            if (item.WeenieType != WeenieType.Container) continue;
            var side = (Container)item;
            var containerItem = GetInventoryItem(side, objectGuid, out _);
            if (containerItem != null) { container = side; return containerItem; }
        }
        container = null;
        return null;
    }

    static T Eph<T>(WorldObject wo, string name) where T : class => (T)B.Get(wo, typeof(WorldObject), name);

    static bool Same<K, V>(Dictionary<K, V> a, Dictionary<K, V> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a) if (!b.TryGetValue(kv.Key, out var v) || !EqualityComparer<V>.Default.Equals(v, kv.Value)) return false;
        return true;
    }

    public static void Run()
    {
        var s = Appraise.Build(40, 20, 12, true);
        var ex = s.Examiner;
        Program.Measure(() => new object(), 2000, 500);   // throwaway: lets the harness itself tier up

        var subjects = new (string, WorldObject)[] { ("monster", s.Monster), ("player", s.Target), ("loot weapon", s.Weapon), ("worn armor", s.WornArmor), ("corpse", s.Corpse) };

        Console.WriteLine("=== P1 (IN.2). the 7 GetAllProperty*Where copies: real vs no-LINQ vs no-LINQ + presize; 'skip IID' drops the copy that is never written ===");
        foreach (var (name, wo) in subjects)
        {
            var b = wo.Biota; var l = wo.BiotaDatabaseLock;
            var eI = Eph<Dictionary<PropertyInt, int?>>(wo, "ephemeralPropertyInts"); var eL = Eph<Dictionary<PropertyInt64, long?>>(wo, "ephemeralPropertyInt64s");
            var eB = Eph<Dictionary<PropertyBool, bool?>>(wo, "ephemeralPropertyBools"); var eF = Eph<Dictionary<PropertyFloat, double?>>(wo, "ephemeralPropertyFloats");
            var eS = Eph<Dictionary<PropertyString, string>>(wo, "ephemeralPropertyStrings"); var eD = Eph<Dictionary<PropertyDataId, uint?>>(wo, "ephemeralPropertyDataIds");
            var eN = Eph<Dictionary<PropertyInstanceId, uint?>>(wo, "ephemeralPropertyInstanceIds");
            bool ok = true;
            foreach (var pre in new[] { false, true })
            {
                ok &= Same(wo.GetAllPropertyIntWhere(AssessmentProperties.PropertiesInt), CopyWhere(b.PropertiesInt, eI, AssessmentProperties.PropertiesInt, l, pre));
                ok &= Same(wo.GetAllPropertyInt64Where(AssessmentProperties.PropertiesInt64), CopyWhere(b.PropertiesInt64, eL, AssessmentProperties.PropertiesInt64, l, pre));
                ok &= Same(wo.GetAllPropertyBoolsWhere(AssessmentProperties.PropertiesBool), CopyWhere(b.PropertiesBool, eB, AssessmentProperties.PropertiesBool, l, pre));
                ok &= Same(wo.GetAllPropertyFloatWhere(AssessmentProperties.PropertiesDouble), CopyWhere(b.PropertiesFloat, eF, AssessmentProperties.PropertiesDouble, l, pre));
                ok &= Same(wo.GetAllPropertyDataIdWhere(AssessmentProperties.PropertiesDataId), CopyWhere(b.PropertiesDID, eD, AssessmentProperties.PropertiesDataId, l, pre));
                ok &= Same(wo.GetAllPropertyInstanceIdWhere(AssessmentProperties.PropertiesInstanceId), CopyWhere(b.PropertiesIID, eN, AssessmentProperties.PropertiesInstanceId, l, pre));
            }
            ok &= Same(wo.GetAllPropertyStringWhere(AssessmentProperties.PropertiesString), CopyWhereString(b.PropertiesString, eS, AssessmentProperties.PropertiesString, l));
            var real = Program.Measure(() =>
            {
                wo.GetAllPropertyIntWhere(AssessmentProperties.PropertiesInt); wo.GetAllPropertyInt64Where(AssessmentProperties.PropertiesInt64);
                wo.GetAllPropertyBoolsWhere(AssessmentProperties.PropertiesBool); wo.GetAllPropertyFloatWhere(AssessmentProperties.PropertiesDouble);
                wo.GetAllPropertyStringWhere(AssessmentProperties.PropertiesString); wo.GetAllPropertyDataIdWhere(AssessmentProperties.PropertiesDataId);
                wo.GetAllPropertyInstanceIdWhere(AssessmentProperties.PropertiesInstanceId);
            });
            var iid = Program.Measure(() => wo.GetAllPropertyInstanceIdWhere(AssessmentProperties.PropertiesInstanceId));
            (double, double) P(bool pre, bool skipIid) => Program.Measure(() =>
            {
                CopyWhere(b.PropertiesInt, eI, AssessmentProperties.PropertiesInt, l, pre); CopyWhere(b.PropertiesInt64, eL, AssessmentProperties.PropertiesInt64, l, pre);
                CopyWhere(b.PropertiesBool, eB, AssessmentProperties.PropertiesBool, l, pre); CopyWhere(b.PropertiesFloat, eF, AssessmentProperties.PropertiesDouble, l, pre);
                CopyWhereString(b.PropertiesString, eS, AssessmentProperties.PropertiesString, l); CopyWhere(b.PropertiesDID, eD, AssessmentProperties.PropertiesDataId, l, pre);
                if (!skipIid) CopyWhere(b.PropertiesIID, eN, AssessmentProperties.PropertiesInstanceId, l, pre); else GC.KeepAlive(new Dictionary<PropertyInstanceId, uint>());
            });
            Console.WriteLine($"  [{name}] results identical: {ok}");
            Program.P("    real (WorldObject_Properties.cs:536-758)", real);
            Program.P("      of which GetAllPropertyInstanceIdWhere (result never written)", iid);
            Program.P("    proto: foreach + if", P(false, false));
            Program.P("    proto: foreach + if, counted presize", P(true, false));
            Program.P("    proto: foreach + if, counted presize, IID skipped", P(true, true));
        }

        Console.WriteLine();
        Console.WriteLine("=== P2 (IN.3). the 6 PackableHashTable writes: SortedDictionary (real) vs pooled array + struct comparer ===");
        foreach (var (name, wo) in subjects)
        {
            var ai = new AppraiseInfo(wo, ex, true);
            var m1 = new MemoryStream(2048); var w1 = new BinaryWriter(m1); var m2 = new MemoryStream(2048); var w2 = new BinaryWriter(m2);
            WriteTablesReal(w1, ai); WriteTablesProto(w2, ai);
            bool same = m1.ToArray().AsSpan().SequenceEqual(m2.ToArray());
            Console.WriteLine($"  [{name}] {m1.Length} bytes, byte-identical: {same}   (entries: int={ai.PropertiesInt.Count} float={ai.PropertiesFloat.Count} str={ai.PropertiesString.Count} did={ai.PropertiesDID.Count} bool={ai.PropertiesBool.Count})");
            Program.P("    real", Program.Measure(() => { m1.Position = 0; WriteTablesReal(w1, ai); }));
            Program.P("    proto", Program.Measure(() => { m2.Position = 0; WriteTablesProto(w2, ai); }));
        }
        {
            // a wide table, to show the Enum.CompareTo boxing on bucket ties: all 122 assessment ints
            var big = new Dictionary<PropertyInt, int>(); foreach (var k in AssessmentProperties.PropertiesInt) big[(PropertyInt)k] = k;
            var m1 = new MemoryStream(4096); var w1 = new BinaryWriter(m1); var m2 = new MemoryStream(4096); var w2 = new BinaryWriter(m2);
            w1.Write(big); WriteTable(w2, big, 16, WInt);
            Console.WriteLine($"  [122-entry int table] byte-identical: {m1.ToArray().AsSpan().SequenceEqual(m2.ToArray())}");
            Program.P("    real", Program.Measure(() => { m1.Position = 0; w1.Write(big); }));
            Program.P("    proto", Program.Measure(() => { m2.Position = 0; WriteTable(w2, big, 16, WInt); }));
        }

        Console.WriteLine();
        Console.WriteLine("=== P3 (IN.1). Container.GetInventoryItem: real vs foreach (no Where/ToList) ===");
        {
            var miss = new ObjectGuid(0x8FFFFFF0); var side = s.InSidePack.Guid; var main = s.InMainPack.Guid;
            bool ok = GetInventoryItem(ex, miss, out var c1) == ex.GetInventoryItem(miss, out var c2) && c1 == c2;
            ok &= GetInventoryItem(ex, side, out c1) == ex.GetInventoryItem(side, out c2) && c1 == c2 && c1 != null;
            ok &= GetInventoryItem(ex, main, out c1) == ex.GetInventoryItem(main, out c2) && c1 == c2 && c1 == ex;
            Console.WriteLine($"  same item and container for miss / side pack / main pack: {ok}   (60 loose + 7 packs x 20)");
            Program.P("    real, miss", Program.Measure(() => ex.GetInventoryItem(miss, out _)));
            Program.P("    proto, miss", Program.Measure(() => GetInventoryItem(ex, miss, out _)));
            Program.P("    real, item in 7th side pack", Program.Measure(() => ex.GetInventoryItem(side, out _)));
            Program.P("    proto, item in 7th side pack", Program.Measure(() => GetInventoryItem(ex, side, out _)));
        }

        Console.WriteLine();
        Console.WriteLine("=== P5 (IN.4). appraising a PLAYER: where the non-copy cost goes, and ArmorLevel prototypes ===");
        {
            var t = s.Target;
            var real = new ArmorLevel(t);
            uint[] rv = { real.Head, real.Chest, real.Abdomen, real.UpperArm, real.LowerArm, real.Hand, real.UpperLeg, real.LowerLeg, real.Foot };
            var a = new uint[9]; for (int k = 0; k < 9; k++) a[k] = ArmorLevelMask(t, Parts[k]);
            var b2 = new uint[9]; ArmorLevelOnePass(t, b2);
            Console.WriteLine($"  ArmorLevel values identical: mask version {rv.AsSpan().SequenceEqual(a)}, one-pass version {rv.AsSpan().SequenceEqual(b2)}   ({string.Join(",", rv)}; {t.EquippedObjects.Count} equipped, {t.EquippedObjects.Values.Count(e => e is Clothing)} clothing)");
            Program.P("    new CreatureProfile(player, true)", Program.Measure(() => new CreatureProfile(t, true)));
            {
                var h = ACE.Server.Network.Enum.AttributeMaskHelper.GetAttributeHighlights(t); var c = ACE.Server.Network.Enum.AttributeMaskHelper.GetAttributeColors(t);
                AttributeMasksOnce(t, out var h2, out var c2);
                Console.WriteLine($"  attribute masks identical (one ModifierType read per stat instead of two): {h == h2 && c == c2}   (highlights={h}, colors={c})");
                Program.P("      of which AttributeMaskHelper.GetAttributeHighlights + GetAttributeColors", Program.Measure(() => { ACE.Server.Network.Enum.AttributeMaskHelper.GetAttributeHighlights(t); ACE.Server.Network.Enum.AttributeMaskHelper.GetAttributeColors(t); }));
                Program.P("      proto: both masks from one ModifierType read per stat", Program.Measure(() => AttributeMasksOnce(t, out _, out _)));
                Program.P("      one Health.MaxValue (player: includes the Vitae lookup, perf-worldobjects Q.1)", Program.Measure(() => { var v = t.Health.MaxValue; }));
                Program.P("      same on the monster", Program.Measure(() => { var v = s.Monster.Health.MaxValue; }));
                Program.P("    new CreatureProfile(monster, true)  [for comparison]", Program.Measure(() => new CreatureProfile(s.Monster, true)));
            }
            Program.P("    ResistMaskHelper.GetHighlightMask + GetColorMask", Program.Measure(() => { ACE.Server.Network.Enum.ResistMaskHelper.GetHighlightMask(t); ACE.Server.Network.Enum.ResistMaskHelper.GetColorMask(t); }));
            Program.P("    13 rating getters + GetEquippedWeapon/Wand + GetHeritageBonus (AddRatings inputs)", Program.Measure(() =>
            {
                t.GetDamageRating(); var w = t.GetEquippedWeapon() ?? t.GetEquippedWand(); t.GetHeritageBonus(w); t.GetDamageResistRating(); t.GetCritRating(); t.GetCritDamageRating();
                t.GetCritResistRating(); t.GetCritDamageResistRating(); t.GetHealingBoostRating(); t.GetDotResistanceRating(); t.GetNetherResistRating(); t.GetLifeResistRating();
                t.GetGearMaxHealth(); t.GetPKDamageRating(); t.GetPKDamageResistRating();
            }));
            Program.P("    new ArmorLevel(player)  [real: 9 x BodyParts.GetFlags + Where + ToList + Any]", Program.Measure(() => new ArmorLevel(t)));
            Program.P("    BodyParts.GetFlags(coverage) x 9 alone", Program.Measure(() => { foreach (var bp in Parts) BodyParts.GetFlags(BodyParts.GetCoverageMask(bp)); }));
            Program.P("    proto (a): mask test, same shape", Program.Measure(() => { foreach (var bp in Parts) ArmorLevelMask(t, bp); }));
            Program.P("    proto (b): one pass over EquippedObjects", Program.Measure(() => { Span<uint> r = stackalloc uint[9]; ArmorLevelOnePass(t, r); }));
        }

        Console.WriteLine();
        Console.WriteLine("=== P4. ObjectGuid as dictionary key (confirms perf-entity-shared E.4 by measurement) ===");
        {
            var d = new Dictionary<ObjectGuid, WorldObject>(); for (uint k = 0; k < 100; k++) d[new ObjectGuid(0x80000000 + k)] = s.Monster;
            var hit = new ObjectGuid(0x80000032); var missg = new ObjectGuid(0x8FFFFFF0);
            var di = new Dictionary<uint, WorldObject>(); for (uint k = 0; k < 100; k++) di[0x80000000 + k] = s.Monster;
            Program.P("    Dictionary<ObjectGuid,WorldObject>.TryGetValue hit", Program.Measure(() => d.TryGetValue(hit, out _)));
            Program.P("    Dictionary<ObjectGuid,WorldObject>.TryGetValue miss", Program.Measure(() => d.TryGetValue(missg, out _)));
            Program.P("    Dictionary<uint,WorldObject>.TryGetValue hit", Program.Measure(() => di.TryGetValue(0x80000032, out _)));
        }
    }
}
