using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.WorldObjects;

using Character = ACE.Database.Models.Shard.Character;
using Position = ACE.Entity.Position;

/// Builders: real ACE.Server objects with no database and no dat files.
static class B
{
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static object Uninit(Type t) => RuntimeHelpers.GetUninitializedObject(t);
    public static T Uninit<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    public static FieldInfo Field(Type t, string name)
    {
        for (var c = t; c != null; c = c.BaseType)
        {
            var f = c.GetField(name, BF) ?? c.GetField($"<{name}>k__BackingField", BF);
            if (f != null) return f;
        }
        throw new Exception($"no field {name} on {t}");
    }
    public static void Set(object o, Type t, string name, object v) => Field(t, name).SetValue(o, v);
    public static object Get(object o, Type t, string name) => Field(t, name).GetValue(o);

    public static void Bootstrap()
    {
        // --- portal.dat stand-in: an uninitialised PortalDatDatabase carrying only the four tables the measured paths read.
        //     CharGen stays null (Creature.GenerateNewFace returns before using it when Heritage/Gender are absent).
        var pd = Uninit(typeof(PortalDatDatabase));
        var sat = new ACE.DatLoader.FileTypes.SecondaryAttributeTable();
        SetFormula(sat.MaxHealth.Formula, 2, (uint)PropertyAttribute.Endurance, 0);      // retail: END / 2
        SetFormula(sat.MaxStamina.Formula, 1, (uint)PropertyAttribute.Endurance, 0);     // END
        SetFormula(sat.MaxMana.Formula, 1, (uint)PropertyAttribute.Self, 0);             // SELF
        Set(pd, typeof(PortalDatDatabase), "SecondaryAttributeTable", sat);
        var skt = new ACE.DatLoader.FileTypes.SkillTable();
        foreach (var sk in Enum.GetValues<Skill>())
        {
            var fm = new ACE.DatLoader.Entity.SkillFormula();
            SetFormula(fm, 3, (uint)PropertyAttribute.Coordination, (uint)PropertyAttribute.Quickness);   // (COORD + QUICK) / 3, the common shape
            skt.SkillBaseHash[(uint)sk] = new ACE.DatLoader.Entity.SkillBase(fm);
        }
        Set(pd, typeof(PortalDatDatabase), "SkillTable", skt);
        var spt = new ACE.DatLoader.FileTypes.SpellTable();
        var world = ACE.Database.DatabaseManager.World;
        var spellCache = (System.Collections.Concurrent.ConcurrentDictionary<uint, ACE.Database.Models.World.Spell>)Get(world, world.GetType(), "spellCache");
        for (uint id = 2000; id < 2300; id++)
        {
            var sb = new ACE.DatLoader.Entity.SpellBase();
            Set(sb, sb.GetType(), "Name", "Bench Spell " + id);
            Set(sb, sb.GetType(), "School", id >= 2090 && id < 2120 ? MagicSchool.ItemEnchantment : (id < 2090 ? MagicSchool.CreatureEnchantment : MagicSchool.LifeMagic));
            Set(sb, sb.GetType(), "Formula", new List<uint> { 1, 2, 3, 4, 5, 6 });
            spt.Spells[id] = sb;
            spellCache[id] = new ACE.Database.Models.World.Spell { Id = id, Name = "Bench Spell " + id };
        }
        Set(pd, typeof(PortalDatDatabase), "SpellTable", spt);
        Set(null, typeof(DatManager), "PortalDat", pd);

        // --- world DB stand-in: DatabaseManager.World is constructed lazily-connected; seed the caches the paths read so no WorldDbContext is ever opened.
        //     Player's TYPE INITIALISER needs this: Player_Location.cs:24 `MarketplaceDrop = DatabaseManager.World.GetCachedWeenie("portalmarketplace")...`
        var nameCache = (System.Collections.Concurrent.ConcurrentDictionary<string, uint>)Get(world, world.GetType(), "weenieClassNameToClassIdCache");
        var weenieCache = (System.Collections.Concurrent.ConcurrentDictionary<uint, Weenie>)Get(world, world.GetType(), "weenieCache");
        weenieCache[0xFFFFFE] = null;
        nameCache["portalmarketplace"] = 0xFFFFFE;
        for (int k = 1; k <= 5; k++) { nameCache["portalpkarenanew" + k] = 0xFFFFFE; nameCache["portalpklarenanew" + k] = 0xFFFFFE; }   // Player_Location.cs:471-559 static lists
        foreach (var wcid in new uint[] { 21u, 136u })   // corpse, pack: AppraiseInfo.cs:236 reads the weenie's base Value for containers
        {
            var w = NewWeenie(wcid, WeenieType.Container);
            w.PropertiesInt[PropertyInt.Value] = 0;
            weenieCache[wcid] = w;
        }

        // --- ConfigManager.Config: defaults (Player.Name reads Config.Server.Accounts.OverrideCharacterPermissions whenever Session != null)
        Set(null, typeof(ACE.Common.ConfigManager), "Config", new ACE.Common.MasterConfiguration());

        // --- PropertyManager: seed the cache with every default so GetBool/GetLong/GetDouble never reach DatabaseManager.ShardConfig.
        foreach (var kv in DefaultPropertyManager.DefaultBooleanProperties) PropertyManager.ModifyBool(kv.Key, kv.Value.Item);
        foreach (var kv in DefaultPropertyManager.DefaultLongProperties) PropertyManager.ModifyLong(kv.Key, kv.Value.Item);
        foreach (var kv in DefaultPropertyManager.DefaultDoubleProperties) PropertyManager.ModifyDouble(kv.Key, kv.Value.Item, true);
    }

    static void SetFormula(ACE.DatLoader.Entity.SkillFormula f, uint divisor, uint a1, uint a2) { f.W = 0; f.X = 1; f.Y = 0; f.Z = divisor; f.Attr1 = a1; f.Attr2 = a2; }

    // ---------------------------------------------------------------- biota / weenie

    public static Biota NewBiota(uint guid, uint wcid, WeenieType wt) => new Biota
    {
        Id = guid, WeenieClassId = wcid, WeenieType = wt,
        PropertiesInt = new Dictionary<PropertyInt, int>(),
        PropertiesInt64 = new Dictionary<PropertyInt64, long>(),
        PropertiesBool = new Dictionary<PropertyBool, bool>(),
        PropertiesFloat = new Dictionary<PropertyFloat, double>(),
        PropertiesString = new Dictionary<PropertyString, string>(),
        PropertiesDID = new Dictionary<PropertyDataId, uint>(),
        PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
        PropertiesEnchantmentRegistry = new Collection<PropertiesEnchantmentRegistry>(),
    };

    public static Weenie NewWeenie(uint wcid, WeenieType wt) => new Weenie
    {
        WeenieClassId = wcid, WeenieType = wt, ClassName = "bench",
        PropertiesInt = new Dictionary<PropertyInt, int>(),
        PropertiesInt64 = new Dictionary<PropertyInt64, long>(),
        PropertiesBool = new Dictionary<PropertyBool, bool>(),
        PropertiesFloat = new Dictionary<PropertyFloat, double>(),
        PropertiesString = new Dictionary<PropertyString, string>(),
        PropertiesDID = new Dictionary<PropertyDataId, uint>(),
        PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
    };

    static readonly Position Holtburg = new Position(0xA9B40019, new Vector3(84.1f, 7.3f, 94.005f), Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)));

    /// Adds filler properties that are NOT [AssessmentProperty], so the copy has something to reject.
    static void Filler<K>(IDictionary<K, int> d, HashSet<ushort> assess, int n, int from) where K : struct, Enum
    {
        foreach (var k in Enum.GetValues<K>())
        {
            var id = Convert.ToInt32(k);
            if (n == 0) break;
            if (id < from || assess.Contains((ushort)id) || d.ContainsKey(k)) continue;
            d[k] = 1; n--;
        }
    }

    // ---------------------------------------------------------------- items

    /// A tier-6 style loot melee weapon: ~36 ints, 10 floats, 3 strings, 8 DIDs, 2 IIDs, 3 bools, spellbook of `spells`.
    public static void FillLootWeapon(Biota b, int spells, uint containerGuid)
    {
        var i = b.PropertiesInt;
        i[PropertyInt.ItemType] = (int)ItemType.MeleeWeapon; i[PropertyInt.EncumbranceVal] = 450; i[PropertyInt.Mass] = 180;
        i[PropertyInt.ValidLocations] = (int)EquipMask.MeleeWeapon; i[PropertyInt.Value] = 12345; i[PropertyInt.ItemUseable] = 1;
        i[PropertyInt.PaletteTemplate] = 20; i[PropertyInt.UiEffects] = 1; i[PropertyInt.Damage] = 44; i[PropertyInt.DamageType] = 1;
        i[PropertyInt.AttackType] = 4; i[PropertyInt.WeaponSkill] = (int)Skill.HeavyWeapons; i[PropertyInt.WeaponTime] = 30; i[PropertyInt.CombatUse] = 1;
        i[PropertyInt.TargetType] = 16; i[PropertyInt.ItemWorkmanship] = 7; i[PropertyInt.MaterialType] = 59; i[PropertyInt.GemCount] = 2; i[PropertyInt.GemType] = 20;
        i[PropertyInt.ItemSpellcraft] = 320; i[PropertyInt.ItemCurMana] = 1800; i[PropertyInt.ItemMaxMana] = 1800; i[PropertyInt.ItemDifficulty] = 280;
        i[PropertyInt.WieldRequirements] = 2; i[PropertyInt.WieldSkillType] = (int)Skill.HeavyWeapons; i[PropertyInt.WieldDifficulty] = 400;
        i[PropertyInt.TsysMutationData] = 0x1010402; i[PropertyInt.NumTimesTinkered] = 3; i[PropertyInt.ImbuedEffect] = 1; i[PropertyInt.WeaponType] = 2;
        i[PropertyInt.HookType] = 1; i[PropertyInt.HookPlacement] = 161; i[PropertyInt.RadarBlipColor] = 0; i[PropertyInt.PhysicsState] = 0x414;
        i[PropertyInt.MaxStackSize] = 1; i[PropertyInt.StackSize] = 1;

        var f = b.PropertiesFloat;
        f[PropertyFloat.DamageVariance] = 0.3; f[PropertyFloat.WeaponLength] = 0.95; f[PropertyFloat.WeaponDefense] = 1.15; f[PropertyFloat.WeaponOffense] = 1.12;
        f[PropertyFloat.ManaRate] = -0.0333; f[PropertyFloat.DefaultScale] = 1.1; f[PropertyFloat.Shade] = 0.4; f[PropertyFloat.Translucency] = 0;
        f[PropertyFloat.MaximumVelocity] = 0; f[PropertyFloat.CriticalFrequency] = 0.15;

        var s = b.PropertiesString;
        s[PropertyString.Name] = "Mahogany Flamberge"; s[PropertyString.LongDesc] = "Mahogany Flamberge of Blood Drinker"; s[PropertyString.TinkerName] = "Somebody Else";

        var d = b.PropertiesDID;
        d[PropertyDataId.Setup] = 0x02000001; d[PropertyDataId.SoundTable] = 0x20000014; d[PropertyDataId.Icon] = 0x06001234; d[PropertyDataId.IconOverlay] = 0x06005678;
        d[PropertyDataId.PaletteBase] = 0x7E000001; d[PropertyDataId.ClothingBase] = 0x10000001; d[PropertyDataId.PhysicsEffectTable] = 0x34000004;
        d[PropertyDataId.Spell] = 2101;

        b.PropertiesIID[PropertyInstanceId.Container] = containerGuid;
        b.PropertiesIID[PropertyInstanceId.Owner] = containerGuid;

        b.PropertiesBool[PropertyBool.Inscribable] = true; b.PropertiesBool[PropertyBool.Dyable] = false; b.PropertiesBool[PropertyBool.IsSellable] = true;

        b.PropertiesSpellBook = new Dictionary<int, float>();
        int[] ids = { 2101, 2116, 2096, 2106, 2053, 2059, 2067, 2084, 2091, 2073 };
        for (int k = 0; k < spells && k < ids.Length; k++) b.PropertiesSpellBook[ids[k]] = 2.0f;
    }

    public static MeleeWeapon MakeLootWeapon(uint guid, int spells = 8, uint containerGuid = 0x80009000)
    {
        var b = NewBiota(guid, 23456, WeenieType.MeleeWeapon);
        FillLootWeapon(b, spells, containerGuid);
        var wo = new MeleeWeapon(b);
        wo.Location = null;
        return wo;
    }

    /// A loot armor piece (Clothing): ~34 ints, 12 floats (incl. 7 ArmorModVs*), spellbook.
    public static void FillLootArmor(Biota b, int spells, uint containerGuid, CoverageMask coverage)
    {
        var i = b.PropertiesInt;
        i[PropertyInt.ItemType] = (int)ItemType.Armor; i[PropertyInt.EncumbranceVal] = 1100; i[PropertyInt.Mass] = 400;
        i[PropertyInt.ValidLocations] = (int)EquipMask.ChestArmor; i[PropertyInt.Value] = 9876; i[PropertyInt.ItemUseable] = 1;
        i[PropertyInt.PaletteTemplate] = 14; i[PropertyInt.UiEffects] = 1; i[PropertyInt.ClothingPriority] = (int)coverage; i[PropertyInt.ArmorType] = 4;
        i[PropertyInt.ArmorLevel] = 310; i[PropertyInt.TargetType] = 16; i[PropertyInt.ItemWorkmanship] = 8; i[PropertyInt.MaterialType] = 57;
        i[PropertyInt.GemCount] = 1; i[PropertyInt.GemType] = 22; i[PropertyInt.ItemSpellcraft] = 300; i[PropertyInt.ItemCurMana] = 1500; i[PropertyInt.ItemMaxMana] = 1500;
        i[PropertyInt.ItemDifficulty] = 250; i[PropertyInt.WieldRequirements] = 7; i[PropertyInt.WieldSkillType] = 1; i[PropertyInt.WieldDifficulty] = 150;
        i[PropertyInt.TsysMutationData] = 0x1010402; i[PropertyInt.NumTimesTinkered] = 2; i[PropertyInt.HookType] = 2; i[PropertyInt.HookPlacement] = 161;
        i[PropertyInt.RadarBlipColor] = 0; i[PropertyInt.PhysicsState] = 0x414; i[PropertyInt.MaxStackSize] = 1; i[PropertyInt.StackSize] = 1;
        i[PropertyInt.EquipmentSetId] = 13; i[PropertyInt.ItemAllegianceRankLimit] = 0;

        var f = b.PropertiesFloat;
        f[PropertyFloat.ArmorModVsSlash] = 1.2; f[PropertyFloat.ArmorModVsPierce] = 1.1; f[PropertyFloat.ArmorModVsBludgeon] = 0.9; f[PropertyFloat.ArmorModVsCold] = 0.8;
        f[PropertyFloat.ArmorModVsFire] = 0.7; f[PropertyFloat.ArmorModVsAcid] = 0.6; f[PropertyFloat.ArmorModVsElectric] = 0.5; f[PropertyFloat.ArmorModVsNether] = 1.0;
        f[PropertyFloat.ManaRate] = -0.05; f[PropertyFloat.DefaultScale] = 1.0; f[PropertyFloat.Shade] = 0.4; f[PropertyFloat.Translucency] = 0;

        var s = b.PropertiesString;
        s[PropertyString.Name] = "Steel Yoroi Cuirass"; s[PropertyString.LongDesc] = "Steel Yoroi Cuirass of Impenetrability";

        var d = b.PropertiesDID;
        d[PropertyDataId.Setup] = 0x02000001; d[PropertyDataId.SoundTable] = 0x20000014; d[PropertyDataId.Icon] = 0x06001234; d[PropertyDataId.IconOverlay] = 0x06005678;
        d[PropertyDataId.PaletteBase] = 0x7E000001; d[PropertyDataId.ClothingBase] = 0x10000001; d[PropertyDataId.PhysicsEffectTable] = 0x34000004;
        d[PropertyDataId.Spell] = 2108;

        b.PropertiesIID[PropertyInstanceId.Container] = containerGuid;
        b.PropertiesIID[PropertyInstanceId.Owner] = containerGuid;
        b.PropertiesBool[PropertyBool.Inscribable] = true; b.PropertiesBool[PropertyBool.Dyable] = true; b.PropertiesBool[PropertyBool.IsSellable] = true;

        b.PropertiesSpellBook = new Dictionary<int, float>();
        int[] ids = { 2108, 2113, 2094, 2102, 2098, 2104, 2110, 2092, 2091, 2073 };
        for (int k = 0; k < spells && k < ids.Length; k++) b.PropertiesSpellBook[ids[k]] = 2.0f;
    }

    public static Clothing MakeLootArmor(uint guid, int spells = 8, uint containerGuid = 0x80009000, CoverageMask coverage = CoverageMask.OuterwearChest | CoverageMask.OuterwearAbdomen | CoverageMask.OuterwearUpperArms)
    {
        var b = NewBiota(guid, 23457, WeenieType.Clothing);
        FillLootArmor(b, spells, containerGuid, coverage);
        var wo = new Clothing(b);
        wo.Location = null;
        return wo;
    }

    /// A plain inventory item (trophy / component / gem) - cheap biota.
    public static WorldObject MakeMisc(uint guid, uint containerGuid)
    {
        var b = NewBiota(guid, 3000, WeenieType.Generic);
        var i = b.PropertiesInt;
        i[PropertyInt.ItemType] = (int)ItemType.Misc; i[PropertyInt.EncumbranceVal] = 5; i[PropertyInt.Value] = 100; i[PropertyInt.StackSize] = 10; i[PropertyInt.MaxStackSize] = 100;
        i[PropertyInt.StackUnitEncumbrance] = 1; i[PropertyInt.StackUnitValue] = 10; i[PropertyInt.ItemUseable] = 1;
        b.PropertiesString[PropertyString.Name] = "Drudge Charm";
        b.PropertiesDID[PropertyDataId.Setup] = 0x02000001; b.PropertiesDID[PropertyDataId.Icon] = 0x06001234;
        b.PropertiesIID[PropertyInstanceId.Container] = containerGuid;
        return new GenericObject(b);
    }

    /// A side pack that lives in a player's inventory (ContainerId = player guid, so Container(Biota) does not go to the shard DB).
    public static Container MakePack(uint guid, uint playerGuid, int items, ref uint nextGuid)
    {
        var b = NewBiota(guid, 136, WeenieType.Container);
        b.PropertiesInt[PropertyInt.ItemType] = (int)ItemType.Container; b.PropertiesInt[PropertyInt.ItemsCapacity] = 24; b.PropertiesInt[PropertyInt.ContainersCapacity] = 0;
        b.PropertiesString[PropertyString.Name] = "Pack";
        b.PropertiesIID[PropertyInstanceId.Container] = playerGuid;
        var c = new Container(b);
        for (int k = 0; k < items; k++)
        {
            var it = MakeMisc(nextGuid++, guid);
            c.Inventory[it.Guid] = it;
        }
        return c;
    }

    // ---------------------------------------------------------------- creatures

    static void AddAttributes(Weenie w, uint baseAttr, uint hp)
    {
        w.PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>();
        foreach (var a in new[] { PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination, PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self })
            w.PropertiesAttribute[a] = new PropertiesAttribute { InitLevel = baseAttr };
        w.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();
        foreach (var a in new[] { PropertyAttribute2nd.MaxHealth, PropertyAttribute2nd.MaxStamina, PropertyAttribute2nd.MaxMana })
            w.PropertiesAttribute2nd[a] = new PropertiesAttribute2nd { InitLevel = hp, CurrentLevel = hp };
    }

    /// A level-185 monster built through the production constructor Creature(Weenie, ObjectGuid).
    public static Creature MakeMonster(uint guid)
    {
        var w = NewWeenie(40000, WeenieType.Creature);
        var i = w.PropertiesInt;
        i[PropertyInt.ItemType] = (int)ItemType.Creature; i[PropertyInt.CreatureType] = 31; i[PropertyInt.PaletteTemplate] = 2; i[PropertyInt.Level] = 185;
        i[PropertyInt.ItemsCapacity] = 255; i[PropertyInt.ContainersCapacity] = 255; i[PropertyInt.ItemUseable] = 1; i[PropertyInt.ArmorType] = 0;
        i[PropertyInt.RadarBlipColor] = 0; i[PropertyInt.PhysicsState] = 0x408; i[PropertyInt.TargetingTactic] = 13; i[PropertyInt.CombatTactic] = 1;
        i[PropertyInt.HomesickTargetingTactic] = 0; i[PropertyInt.XpOverride] = 1500000; i[PropertyInt.AiAllowedCombatStyle] = 1; i[PropertyInt.ShowableOnRadar] = 4;
        i[PropertyInt.DamageType] = 1; i[PropertyInt.Mass] = 600;
        var f = w.PropertiesFloat;
        f[PropertyFloat.HeartbeatInterval] = 5; f[PropertyFloat.HealthRate] = 5; f[PropertyFloat.StaminaRate] = 4; f[PropertyFloat.ManaRate] = 2;
        f[PropertyFloat.ArmorModVsSlash] = 1; f[PropertyFloat.ArmorModVsPierce] = 1; f[PropertyFloat.ArmorModVsBludgeon] = 1; f[PropertyFloat.ArmorModVsCold] = 1;
        f[PropertyFloat.ArmorModVsFire] = 1; f[PropertyFloat.ArmorModVsAcid] = 1; f[PropertyFloat.ArmorModVsElectric] = 1;
        f[PropertyFloat.ResistSlash] = 0.6; f[PropertyFloat.ResistPierce] = 0.6; f[PropertyFloat.ResistBludgeon] = 0.6; f[PropertyFloat.ResistFire] = 0.5;
        f[PropertyFloat.ResistCold] = 1.2; f[PropertyFloat.ResistAcid] = 0.7; f[PropertyFloat.ResistElectric] = 0.7;
        f[PropertyFloat.VisualAwarenessRange] = 35; f[PropertyFloat.DefaultScale] = 1.3; f[PropertyFloat.PowerupTime] = 1; f[PropertyFloat.ManaConversionMod] = 0;
        w.PropertiesBool[PropertyBool.Stuck] = true; w.PropertiesBool[PropertyBool.Attackable] = true; w.PropertiesBool[PropertyBool.Ethereal] = false;
        w.PropertiesString[PropertyString.Name] = "Tusker Guard";
        var d = w.PropertiesDID;
        d[PropertyDataId.Setup] = 0x02000001; d[PropertyDataId.MotionTable] = 0x09000001; d[PropertyDataId.SoundTable] = 0x20000014; d[PropertyDataId.CombatTable] = 0x30000000;
        d[PropertyDataId.QualityFilter] = 0x0E000003; d[PropertyDataId.PaletteBase] = 0x7E000001; d[PropertyDataId.ClothingBase] = 0x10000001; d[PropertyDataId.Icon] = 0x06001234;
        d[PropertyDataId.PhysicsEffectTable] = 0x34000004;
        AddAttributes(w, 300, 2000);
        w.PropertiesSkill = new Dictionary<Skill, PropertiesSkill>();
        foreach (var sk in new[] { Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense, Skill.HeavyWeapons, Skill.WarMagic, Skill.LifeMagic, Skill.CreatureEnchantment, Skill.ManaConversion, Skill.Run, Skill.Jump, Skill.ArcaneLore, Skill.Deception })
            w.PropertiesSkill[sk] = new PropertiesSkill { InitLevel = 300, SAC = SkillAdvancementClass.Trained };
        w.PropertiesSpellBook = new Dictionary<int, float> { [2128] = 2.02f, [2136] = 2.02f, [2162] = 2.02f, [2074] = 2.02f };
        var c = new Creature(w, new ObjectGuid(guid));
        c.Location = new Position(Holtburg);
        return c;
    }

    public static PropertiesEnchantmentRegistry Ench(int spellId, SpellCategory cat, EnchantmentTypeFlags type, uint key, float val, ushort layer = 1) => new PropertiesEnchantmentRegistry
    {
        EnchantmentCategory = 1, SpellId = spellId, LayerId = layer, SpellCategory = cat, PowerLevel = 400, StartTime = 100, Duration = 3600, CasterObjectId = 0x50000001,
        DegradeModifier = 0, DegradeLimit = -666, LastTimeDegraded = 0, StatModType = type, StatModKey = key, StatModValue = val,
    };

    /// A real Player type, built by running the Creature(Biota) constructor chain (WorldObject -> Container -> Creature) on an uninitialised Player.
    /// Player's own constructor is blocked by DatabaseManager.Authentication.GetAccountById (Player.cs:119) and DatManager.PortalDat.ReadFromDat<CombatManeuverTable> (Player.cs:182).
    public static Player MakePlayer(uint guid, string name, int extraInts = 110, int enchantments = 60, bool equip = true, bool inventory = true)
    {
        var b = NewBiota(guid, 1, WeenieType.Creature);
        var i = b.PropertiesInt;
        i[PropertyInt.ItemType] = (int)ItemType.Creature; i[PropertyInt.CreatureType] = 31; i[PropertyInt.Level] = 275; i[PropertyInt.Gender] = 1; i[PropertyInt.HeritageGroup] = 1;
        i[PropertyInt.Age] = 4000000; i[PropertyInt.NumDeaths] = 312; i[PropertyInt.PlayerKillerStatus] = 2; i[PropertyInt.CreationTimestamp] = 1500000000;
        i[PropertyInt.AllegianceRank] = 6; i[PropertyInt.CharacterTitleId] = 10; i[PropertyInt.NumCharacterTitles] = 44; i[PropertyInt.ChessRank] = 1200;
        i[PropertyInt.ContainersCapacity] = 7; i[PropertyInt.ItemsCapacity] = 102; i[PropertyInt.CoinValue] = 250000; i[PropertyInt.PhysicsState] = 0x408;
        i[PropertyInt.RadarBlipColor] = 9; i[PropertyInt.ItemUseable] = 1; i[PropertyInt.MeleeMastery] = 2; i[PropertyInt.RangedMastery] = 8;
        Filler(i, AssessmentProperties.PropertiesInt, extraInts, 190);   // augmentations, luminance, society, rating bookkeeping... (not assessment)
        var f = b.PropertiesFloat;
        f[PropertyFloat.HeartbeatInterval] = 5; f[PropertyFloat.HealthRate] = 5; f[PropertyFloat.StaminaRate] = 4; f[PropertyFloat.ManaRate] = 2; f[PropertyFloat.DefaultScale] = 1.0;
        f[PropertyFloat.ArmorModVsSlash] = 1; f[PropertyFloat.ArmorModVsPierce] = 1; f[PropertyFloat.ArmorModVsBludgeon] = 1; f[PropertyFloat.ArmorModVsCold] = 1;
        f[PropertyFloat.ArmorModVsFire] = 1; f[PropertyFloat.ArmorModVsAcid] = 1; f[PropertyFloat.ArmorModVsElectric] = 1; f[PropertyFloat.ResistSlash] = 1; f[PropertyFloat.ResistPierce] = 1;
        f[PropertyFloat.ResistBludgeon] = 1; f[PropertyFloat.ResistFire] = 1; f[PropertyFloat.ResistCold] = 1; f[PropertyFloat.ResistAcid] = 1; f[PropertyFloat.ResistElectric] = 1;
        f[PropertyFloat.GlobalXpMod] = 1; f[PropertyFloat.LoginTimestamp] = 1.7e9; f[PropertyFloat.CreationTimestamp] = 1.5e9; f[PropertyFloat.AbsorbMagicDamage] = 0.0;
        f.Remove(PropertyFloat.AbsorbMagicDamage);
        b.PropertiesBool[PropertyBool.Stuck] = false; b.PropertiesBool[PropertyBool.Attackable] = true; b.PropertiesBool[PropertyBool.IsPsr] = false; b.PropertiesBool[PropertyBool.Account15Days] = true;
        b.PropertiesBool[PropertyBool.FirstEnterWorldDone] = true; b.PropertiesBool[PropertyBool.ActdReceivedItems] = true;
        var s = b.PropertiesString;
        s[PropertyString.Name] = name; s[PropertyString.Title] = "the Indomitable"; s[PropertyString.Template] = "Aluvian Swashbuckler"; s[PropertyString.HeritageGroup] = "Aluvian";
        s[PropertyString.Sex] = "Male"; s[PropertyString.DateOfBirth] = "01 January 2019"; s[PropertyString.KillQuest] = "x";
        var d = b.PropertiesDID;
        d[PropertyDataId.Setup] = 0x02000001; d[PropertyDataId.MotionTable] = 0x09000001; d[PropertyDataId.SoundTable] = 0x20000001; d[PropertyDataId.CombatTable] = 0x30000000;
        d[PropertyDataId.PaletteBase] = 0x7E000001; d[PropertyDataId.ClothingBase] = 0x10000001; d[PropertyDataId.Icon] = 0x06001234; d[PropertyDataId.PhysicsEffectTable] = 0x34000004;
        d[PropertyDataId.EyesTexture] = 1; d[PropertyDataId.NoseTexture] = 2; d[PropertyDataId.MouthTexture] = 3; d[PropertyDataId.HairPalette] = 4; d[PropertyDataId.EyesPalette] = 5; d[PropertyDataId.SkinPalette] = 6;
        b.PropertiesInt64[PropertyInt64.TotalExperience] = 191226310247; b.PropertiesInt64[PropertyInt64.AvailableExperience] = 1000; b.PropertiesInt64[PropertyInt64.AvailableLuminance] = 500000;
        b.PropertiesInt64[PropertyInt64.MaximumLuminance] = 1500000;
        b.PropertiesIID[PropertyInstanceId.Monarch] = 0x50000002; b.PropertiesIID[PropertyInstanceId.Patron] = 0x50000003; b.PropertiesIID[PropertyInstanceId.Allegiance] = 0x60000001;

        b.PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>();
        foreach (var a in new[] { PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination, PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self })
            b.PropertiesAttribute[a] = new PropertiesAttribute { InitLevel = 100, LevelFromCP = 190 };
        b.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();
        foreach (var a in new[] { PropertyAttribute2nd.MaxHealth, PropertyAttribute2nd.MaxStamina, PropertyAttribute2nd.MaxMana })
            b.PropertiesAttribute2nd[a] = new PropertiesAttribute2nd { InitLevel = 0, LevelFromCP = 196, CurrentLevel = 300 };
        b.PropertiesSkill = new Dictionary<Skill, PropertiesSkill>();
        foreach (var sk in Enum.GetValues<Skill>())
        {
            if (sk == Skill.None || (int)sk > 54) continue;
            b.PropertiesSkill[sk] = new PropertiesSkill { InitLevel = 10, LevelFromPP = 200, SAC = ((int)sk % 3 == 0) ? SkillAdvancementClass.Specialized : SkillAdvancementClass.Trained };
        }

        // a buffed character: creature buffs (attributes/skills), life protections, item auras
        int n = 0;
        var reg = b.PropertiesEnchantmentRegistry;
        for (uint a = 1; a <= 6 && n < enchantments; a++, n++) reg.Add(Ench(2000 + (int)a, (SpellCategory)(1 + 2 * a), EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, a, 45));
        for (uint k = 0; k < 7 && n < enchantments; k++, n++) reg.Add(Ench(2100 + (int)k, (SpellCategory)(101 + 2 * k), EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative, 64 + k, 0.4f));
        for (uint k = 0; n < enchantments; k++, n++) reg.Add(Ench(2200 + (int)k, (SpellCategory)(200 + k), EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, 6 + (k % 45), 45));

        var p = (Player)Uninit(typeof(Player));
        var ctor = typeof(Creature).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Biota) }, null);
        ctor.Invoke(p, new object[] { b });

        // Player-level state the appraisal / FindObject paths touch (normally set by Player.SetEphemeralValues)
        var ch = new Character { Id = guid, Name = name, CharacterOptions1 = 0x50C4A54A, CharacterOptions2 = 0x00948700 };
        Set(p, typeof(Player), "Character", ch);
        var session = Uninit<Session>();
        session.GameEventSequence = 1;
        Set(session, typeof(Session), "Player", p);
        Set(p, typeof(Player), "Session", session);
        // NetworkSession with working bundles so EnqueueSend runs for real
        var net = Uninit<NetworkSession>();
        int qmax = (int)GameMessageGroup.QueueMax;
        var locks = new object[qmax]; var bundles = Array.CreateInstance(Field(typeof(NetworkSession), "currentBundles").FieldType.GetElementType(), qmax);
        for (int k = 0; k < qmax; k++) { locks[k] = new object(); bundles.SetValue(Activator.CreateInstance(bundles.GetType().GetElementType(), true), k); }
        Set(net, typeof(NetworkSession), "currentBundleLocks", locks);
        Set(net, typeof(NetworkSession), "currentBundles", bundles);
        Set(net, typeof(NetworkSession), "session", session);
        session.Network = net;
        Set(p, typeof(Player), "CharacterDatabaseLock", new System.Threading.ReaderWriterLockSlim());
        p.SquelchManager = new ACE.Server.WorldObjects.Managers.SquelchManager(p);
        p.Location = new Position(Holtburg);

        uint next = guid * 0 + 0x80100000 + ((guid & 0xFFFF) << 12);
        if (equip)
        {
            // 9 armor/clothing layers with overlapping coverage, each carrying impen + 7 banes in its own registry; weapon, shield slot left empty, 3 jewellery
            var cov = new[]
            {
                CoverageMask.Head, CoverageMask.OuterwearChest, CoverageMask.OuterwearAbdomen, CoverageMask.OuterwearUpperArms, CoverageMask.OuterwearLowerArms, CoverageMask.Hands,
                CoverageMask.OuterwearUpperLegs, CoverageMask.OuterwearLowerLegs, CoverageMask.Feet,
                CoverageMask.UnderwearChest | CoverageMask.UnderwearAbdomen | CoverageMask.UnderwearUpperArms | CoverageMask.UnderwearLowerArms,
                CoverageMask.UnderwearUpperLegs | CoverageMask.UnderwearLowerLegs | CoverageMask.UnderwearAbdomen,
            };
            foreach (var c in cov)
            {
                var a = MakeLootArmor(next++, 6, guid, c);
                a.SetProperty(PropertyInstanceId.Wielder, guid); a.Wielder = p;
                a.SetProperty(PropertyInt.CurrentWieldedLocation, (int)EquipMask.ChestArmor);
                AddBanes(a);
                p.EquippedObjects[a.Guid] = a;
            }
            var wpn = MakeLootWeapon(next++, 8, guid);
            wpn.SetProperty(PropertyInstanceId.Wielder, guid); wpn.Wielder = p; wpn.SetProperty(PropertyInt.CurrentWieldedLocation, (int)EquipMask.MeleeWeapon);
            p.EquippedObjects[wpn.Guid] = wpn;
            for (int k = 0; k < 4; k++)
            {
                var j = MakeMisc(next++, guid);
                j.SetProperty(PropertyInstanceId.Wielder, guid); j.Wielder = p; j.SetProperty(PropertyInt.CurrentWieldedLocation, (int)EquipMask.FingerWearLeft);
                p.EquippedObjects[j.Guid] = j;
            }
        }
        if (inventory)
        {
            // a bot-style inventory: 60 loose items in the main pack and 7 side packs with 20 items each
            for (int k = 0; k < 60; k++) { var it = MakeMisc(next++, guid); p.Inventory[it.Guid] = it; }
            for (int k = 0; k < 7; k++) { var pack = MakePack(next++, guid, 20, ref next); p.Inventory[pack.Guid] = pack; }
        }
        return p;
    }

    public static void AddBanes(WorldObject armor)
    {
        var reg = armor.Biota.PropertiesEnchantmentRegistry;
        reg.Add(Ench(2108, SpellCategory.ArmorValueRaising, EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, (uint)PropertyInt.ArmorLevel, 220));
        uint[] keys = { (uint)PropertyFloat.ArmorModVsSlash, (uint)PropertyFloat.ArmorModVsPierce, (uint)PropertyFloat.ArmorModVsBludgeon, (uint)PropertyFloat.ArmorModVsCold,
                        (uint)PropertyFloat.ArmorModVsFire, (uint)PropertyFloat.ArmorModVsAcid, (uint)PropertyFloat.ArmorModVsElectric };
        for (int k = 0; k < keys.Length; k++)
            reg.Add(Ench(2090 + k, (SpellCategory)(160 + 2 * k), EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, keys[k], 1.0f));
        typeof(ACE.Server.WorldObjects.Managers.EnchantmentManagerWithCaching).GetMethod("ClearCache", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(armor.EnchantmentManager, null);
    }

    /// A monster corpse built through Corpse(Weenie, ObjectGuid), holding `loot` items (alternating weapon / armor / misc).
    public static Corpse MakeCorpse(uint guid, int loot, ref uint nextGuid)
    {
        var w = NewWeenie(21, WeenieType.Corpse);
        var i = w.PropertiesInt;
        i[PropertyInt.ItemType] = (int)ItemType.Container; i[PropertyInt.ItemsCapacity] = 120; i[PropertyInt.ContainersCapacity] = 10; i[PropertyInt.ItemUseable] = 48;
        i[PropertyInt.PhysicsState] = 0x414; i[PropertyInt.RadarBlipColor] = 0; i[PropertyInt.Mass] = 5;
        w.PropertiesFloat[PropertyFloat.UseRadius] = 2; w.PropertiesFloat[PropertyFloat.DefaultScale] = 1.3;
        w.PropertiesString[PropertyString.Name] = "Corpse of Tusker Guard"; w.PropertiesString[PropertyString.LongDesc] = "Killed by Examiner.";
        w.PropertiesDID[PropertyDataId.Setup] = 0x02000001; w.PropertiesDID[PropertyDataId.MotionTable] = 0x09000001; w.PropertiesDID[PropertyDataId.Icon] = 0x06001234;
        w.PropertiesBool[PropertyBool.Stuck] = true; w.PropertiesBool[PropertyBool.Attackable] = false;
        var c = new Corpse(w, new ObjectGuid(guid));
        c.SetProperty(PropertyInstanceId.Killer, 0x50000001); c.SetProperty(PropertyInstanceId.Victim, 0x80000100);
        c.SetProperty(PropertyInt.Level, 185); c.SetProperty(PropertyInt.CreationTimestamp, 1700000000);
        c.Location = new Position(Holtburg);
        for (int k = 0; k < loot; k++)
        {
            WorldObject it = (k % 3) switch { 0 => MakeLootWeapon(nextGuid++, 8, guid), 1 => MakeLootArmor(nextGuid++, 8, guid), _ => MakeMisc(nextGuid++, guid) };
            c.Inventory[it.Guid] = it;
        }
        return c;
    }

    // ---------------------------------------------------------------- landblock

    /// An uninitialised Landblock (the constructor needs cell.dat and the world DB) with the three collections GetObject/GetWieldedObject read.
    public static Landblock MakeLandblock(IEnumerable<WorldObject> objects, int adjacents = 8, int creaturesPerAdjacent = 0, uint adjGuidBase = 0x90000000)
    {
        var lb = Uninit<Landblock>();
        var dict = new Dictionary<ObjectGuid, WorldObject>();
        foreach (var o in objects) dict[o.Guid] = o;
        Set(lb, typeof(Landblock), "worldObjects", dict);
        Set(lb, typeof(Landblock), "pendingAdditions", new Dictionary<ObjectGuid, WorldObject>());
        Set(lb, typeof(Landblock), "pendingRemovals", new List<ObjectGuid>());
        var adj = new List<Landblock>();
        for (int a = 0; a < adjacents; a++)
        {
            var al = Uninit<Landblock>();
            var ad = new Dictionary<ObjectGuid, WorldObject>();
            for (int k = 0; k < creaturesPerAdjacent; k++) { var m = MakeMonster(adjGuidBase++); ad[m.Guid] = m; }
            Set(al, typeof(Landblock), "worldObjects", ad);
            Set(al, typeof(Landblock), "pendingAdditions", new Dictionary<ObjectGuid, WorldObject>());
            Set(al, typeof(Landblock), "pendingRemovals", new List<ObjectGuid>());
            Set(al, typeof(Landblock), "Adjacents", new List<Landblock>());
            adj.Add(al);
        }
        Set(lb, typeof(Landblock), "Adjacents", adj);
        return lb;
    }

    /// Gives a WorldObject a bare PhysicsObj (no setup/dat), enough for ObjectMaint bookkeeping.
    public static ACE.Server.Physics.PhysicsObj GivePhysics(WorldObject wo)
    {
        if (wo.PhysicsObj != null) return wo.PhysicsObj;
        var po = new ACE.Server.Physics.PhysicsObj();
        po.ID = wo.Guid.Full;
        po.WeenieObj = new ACE.Server.Physics.Common.WeenieObject(wo);
        Set(wo, typeof(WorldObject), "PhysicsObj", po);
        return po;
    }

    /// Adds objects to the player's ObjMaint.KnownObjects through the real AddKnownObject.
    public static void Know(Player p, IEnumerable<WorldObject> objects)
    {
        var mine = GivePhysics(p);
        foreach (var o in objects) { if (o == p) continue; mine.ObjMaint.AddKnownObject(GivePhysics(o)); }
    }

    public static void SetLandblock(WorldObject wo, Landblock lb) => Set(wo, typeof(WorldObject), "CurrentLandblock", lb);

    /// Clears the UIQueue bundle so 20k measured sends do not grow a queue (Queue growth would show up as allocation).
    public static Action BundleClearer(Player p)
    {
        var net = p.Session.Network;
        var bundles = (Array)Get(net, typeof(NetworkSession), "currentBundles");
        var queues = new List<Queue<GameMessage>>();
        foreach (var bundle in bundles) queues.Add((Queue<GameMessage>)Get(bundle, bundle.GetType(), "messages"));
        return () => { foreach (var q in queues) q.Clear(); };
    }

    public static int LastMessageLength(Player p)
    {
        var net = p.Session.Network;
        var bundles = (Array)Get(net, typeof(NetworkSession), "currentBundles");
        int len = -1;
        foreach (var bundle in bundles)
        {
            var q = (Queue<GameMessage>)Get(bundle, bundle.GetType(), "messages");
            foreach (var m in q) len = (int)m.Data.Length;
        }
        return len;
    }
}
