using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using Microsoft.EntityFrameworkCore;

using ACE.Common;
using ACE.Database;
using ACE.Database.Entity;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity;
using ACE.Server.Command.Handlers;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers
{
    public static class RetailShardManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ClothingBaseLookup clothingBaseLookup = new ClothingBaseLookup();


        public class Server
        {
            public readonly string Database;
            public readonly List<string> Names = new List<string>();

            public Server(string database, params string[] names)
            {
                Database = database;
                foreach (var name in names)
                    Names.Add(name);
            }
        }

        public static readonly Collection<Server> Servers = new Collection<Server>
        {
            new Server("ace_shard_retail_dt", "Darktide",       "dt"),
            new Server("ace_shard_retail_ff", "Frostfell",      "ff"),
            new Server("ace_shard_retail_hg", "Harvestgain",    "hg"),
            new Server("ace_shard_retail_lc", "Leafcull",       "lc"),
            new Server("ace_shard_retail_mt", "Morningthaw",    "mt"),
            new Server("ace_shard_retail_sc", "Solclaim",       "sc"),
            new Server("ace_shard_retail_td", "Thistledown",    "td"),
            new Server("ace_shard_retail_vt", "Verdantine",     "vt"),
            new Server("ace_shard_retail_we", "WintersEbb",     "we"),
        };

        private static ShardDbContext GetShardDbContext(Server server)
        {
            var config = Common.ConfigManager.Config.MySql.Shard;

            var optionsBuilder = new DbContextOptionsBuilder<ShardDbContext>();

            var connectionString = $"server={config.Host};port={config.Port};user={config.Username};password={config.Password};database={server.Database}";

            optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), builder =>
            {
                builder.EnableRetryOnFailure(10);
            });

            var context = new ShardDbContext(optionsBuilder.Options);

            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            return context;
        }


        public static Character GetCharacter(Server server, uint id)
        {
            using (var context = GetShardDbContext(server))
            {
                var result = context.Character
                    .Include(r => r.CharacterPropertiesContractRegistry)
                    .Include(r => r.CharacterPropertiesFillCompBook)
                    .Include(r => r.CharacterPropertiesFriendList)
                    .Include(r => r.CharacterPropertiesQuestRegistry)
                    .Include(r => r.CharacterPropertiesShortcutBar)
                    .Include(r => r.CharacterPropertiesSpellBar)
                    .Include(r => r.CharacterPropertiesTitleBook)
                    .FirstOrDefault(r => r.Id == id && !r.IsDeleted);

                return result;
            }
        }

        private static Biota GetBiota(Server server, uint id)
        {
            using (var context = GetShardDbContext(server))
            {
                var shardDatabase = new ShardDatabase();

                return shardDatabase.GetBiota(context, id);
            }
        }

        /// <summary>
        /// Note that there may be guid collisions vs the current live shard.
        /// </summary>
        public static Biota GetBiota(Server server, string name)
        {
            using (var context = GetShardDbContext(server))
            {
                var result = context.BiotaPropertiesString.FirstOrDefault(r => r.Type == (ushort)PropertyString.Name && r.Value == name);

                if (result == null)
                    return null;

                var shardDatabase = new ShardDatabase();

                return shardDatabase.GetBiota(context, result.ObjectId);
            }
        }


        /// <summary>
        /// Note that there may be guid collisions vs the current live shard.
        /// </summary>
        public static PossessedBiotas GetPossessedBiotasInParallel(Server severName, uint id)
        {
            var inventory = GetInventoryInParallel(severName, id, true);

            var wieldedItems = GetWieldedItemsInParallel(severName, id);

            return new PossessedBiotas(inventory, wieldedItems);
        }

        private static List<Biota> GetInventoryInParallel(Server server, uint parentId, bool includedNestedItems)
        {
            using (var context = GetShardDbContext(server))
            {
                var inventory = new ConcurrentBag<Biota>();

                var results = context.BiotaPropertiesIID
                    .Where(r => r.Type == (ushort)PropertyInstanceId.Container && r.Value == parentId)
                    .ToList();

                Parallel.ForEach(results, ConfigManager.Config.Server.Threading.DatabaseParallelOptions, result =>
                {
                    using (var context2 = GetShardDbContext(server))
                    {
                        var shardDatabase = new ShardDatabase();

                        var biota = shardDatabase.GetBiota(context2, result.ObjectId);

                        if (biota != null)
                        {
                            inventory.Add(biota);

                            if (includedNestedItems && biota.WeenieType == (int)WeenieType.Container)
                            {
                                var subItems = GetInventoryInParallel(server, biota.Id, false);

                                foreach (var subItem in subItems)
                                    inventory.Add(subItem);
                            }
                        }
                    }
                });

                return inventory.ToList();
            }
        }

        public static List<Biota> GetWieldedItemsInParallel(Server server, uint parentId)
        {
            using (var context = GetShardDbContext(server))
            {
                var wieldedItems = new ConcurrentBag<Biota>();

                var results = context.BiotaPropertiesIID
                    .Where(r => r.Type == (ushort)PropertyInstanceId.Wielder && r.Value == parentId)
                    .ToList();

                Parallel.ForEach(results, ConfigManager.Config.Server.Threading.DatabaseParallelOptions, result =>
                {
                    using (var context2 = GetShardDbContext(server))
                    {
                        var shardDatabase = new ShardDatabase();

                        var biota = shardDatabase.GetBiota(context2, result.ObjectId);

                        if (biota != null)
                            wieldedItems.Add(biota);
                    }
                });

                return wieldedItems.ToList();
            }
        }


        /// <summary>
        /// This returns all players across all servers that were last seen on landblockId.
        /// Note that there may be guid collisions across servers and vs the current live shard.
        /// </summary>
        public static List<(Server, Biota)> GetAllPlayersOnLandblock(ushort landblockId)
        {
            var playerBiotas = new List<(Server, Biota)>();

            var min = (uint)(landblockId << 16);
            var max = min | 0xFFFF;

            var shardDatabase = new ShardDatabase();

            foreach (var server in Servers)
            {
                using (var context = GetShardDbContext(server))
                { 
                    var results = context.BiotaPropertiesPosition
                        .Where(p => p.PositionType == 1 && p.ObjCellId >= min && p.ObjCellId <= max && p.ObjectId >= ObjectGuid.PlayerMin && p.ObjectId <= ObjectGuid.PlayerMax)
                        .ToList();

                    foreach (var result in results)
                    {
                        var biota = shardDatabase.GetBiota(context, result.ObjectId);
                        playerBiotas.Add((server, biota));
                    }
                }
            }

            return playerBiotas;
        }


        public static WorldObject CreateCurrentShardSafeWorldObjectFromRetailServerBiota(Biota biota)
        {
            bool altWeenieUsed = false;

            var weenie = DatabaseManager.World.GetCachedWeenie(biota.WeenieClassId);

            if (weenie == null)
            {
                string altWeenieClassName = null;

                // Not all retail WCIDs are defined yet. Here we replace them with similar counterparts

                // Only use WeenieType for the most basic items. The export process guestimates the WeenieType, so we shouldn't trust it fully during the import process
                if (altWeenieClassName == null)
                {
                    if (biota.WeenieType == 21) altWeenieClassName = "backpack";    // Container
                    else if (biota.WeenieType == 35) altWeenieClassName = "wand";        // Caster
                    else if (biota.WeenieType == 38) altWeenieClassName = "gem";         // Gem
                }

                if (altWeenieClassName == null)
                {
                    var validLocations = biota.GetProperty(PropertyInt.ValidLocations);

                    if (validLocations == (int)EquipMask.NeckWear) altWeenieClassName = "necklace";
                    else if (validLocations == (int)EquipMask.TrinketOne) altWeenieClassName = "ace41513-pathwardentrinket";
                    //else if (validLocations == (int)EquipMask.Cloak)            altWeenieClassName = "shirt"; // todo this is no good
                    else if (validLocations == (int)EquipMask.WristWear) altWeenieClassName = "bracelet";
                    else if (validLocations == (int)EquipMask.FingerWear) altWeenieClassName = "ring";

                    else if (validLocations == (int)EquipMask.HeadWear) altWeenieClassName = "helmet";
                    else if (validLocations == (int)EquipMask.HandWear) altWeenieClassName = "glovescloth";
                    else if (validLocations == (int)EquipMask.AbdomenArmor) altWeenieClassName = "girthleather";
                    else if (validLocations == (int)EquipMask.FootWear) altWeenieClassName = "shoes";

                    else if (validLocations == (int)EquipMask.Shield) altWeenieClassName = "shieldround";
                    else if (validLocations == (int)EquipMask.MeleeWeapon) altWeenieClassName = "swordlong";
                    else if (validLocations == (int)EquipMask.MissileWeapon) altWeenieClassName = "bowlong";
                    else if (validLocations == (int)EquipMask.MissileAmmo) altWeenieClassName = "arrow";
                }

                if (altWeenieClassName == null)
                {
                    // https://asheron.fandom.com/wiki/Currency
                    var name = biota.GetProperty(PropertyString.Name);

                    if (name == "Ancient Mhoire Coin") altWeenieClassName = "coinstack";
                    else if (name == "A'nekshay Token") altWeenieClassName = "coinstack";
                    else if (name == "Colosseum Coin") altWeenieClassName = "coinstack";
                    else if (name == "Dark Tusker Paw") altWeenieClassName = "coinstack";
                    else if (name == "Hero Token") altWeenieClassName = "coinstack";
                    else if (name == "Ornate Gear Marker") altWeenieClassName = "coinstack";
                    else if (name == "Pitted Slag") altWeenieClassName = "coinstack";
                    else if (name == "Small Olthoi Venom Sac") altWeenieClassName = "coinstack";
                    else if (name == "Spectral Ingot") altWeenieClassName = "coinstack";
                    else if (name == "Stipend") altWeenieClassName = "coinstack";
                    else if (name == "Writ of Apology") altWeenieClassName = "coinstack";
                }

                if (altWeenieClassName == null)
                    return CreateIOU(biota);

                weenie = DatabaseManager.World.GetCachedWeenie(altWeenieClassName);

                if (weenie == null)
                    return CreateIOU(biota);

                altWeenieUsed = true;
            }

            if (weenie.WeenieType == 0)
                return CreateIOU(biota);

            var wo = WorldObjectFactory.CreateNewWorldObject(weenie);

            // Determine what the retail properties are, these properties are server only and weren't pcapped
            // PropertyInt.PaletteTemplate
            // PropertyInt.UiEffects
            // PropertyInt.TargetType

            // PropertyDataId.Setup
            // PropertyDataId.PaletteBase
            // PropertyDataId.ClothingBase
            // PropertyDataId.Icon

            if (altWeenieUsed)
            {
                wo.RemoveProperty(PropertyInt.PaletteTemplate);
                wo.RemoveProperty(PropertyFloat.Shade);
                wo.RemoveProperty(PropertyFloat.Shade2);
                wo.RemoveProperty(PropertyFloat.Shade3);
                wo.RemoveProperty(PropertyFloat.Shade4);
                //wo.RemoveProperty(PropertyInt.TsysMutationData);
                //wo.RemoveProperty(PropertyDataId.ClothingBase);
            }

            foreach (var property in biota.BiotaPropertiesInt)
                wo.SetProperty((PropertyInt)property.Type, property.Value);
            foreach (var property in biota.BiotaPropertiesInt64)
                wo.SetProperty((PropertyInt64)property.Type, property.Value);
            foreach (var property in biota.BiotaPropertiesBool)
                wo.SetProperty((PropertyBool)property.Type, property.Value);
            foreach (var property in biota.BiotaPropertiesFloat)
            {
                // Some float properties were sent over the wire as 1, instead of the actual server value
                if ((PropertyFloat)property.Type == PropertyFloat.CriticalFrequency && property.Value == 1)
                    continue;

                wo.SetProperty((PropertyFloat)property.Type, property.Value);
            }
            foreach (var property in biota.BiotaPropertiesString)
                wo.SetProperty((PropertyString)property.Type, property.Value);
            foreach (var property in biota.BiotaPropertiesDID)
                wo.SetProperty((PropertyDataId)property.Type, property.Value);
            foreach (var property in biota.BiotaPropertiesIID)
                wo.SetProperty((PropertyInstanceId)property.Type, property.Value);

            if (wo.Biota.PropertiesSpellBook == null)
                wo.Biota.PropertiesSpellBook = new Dictionary<int, float>();
            foreach (var property in biota.BiotaPropertiesSpellBook)
                wo.Biota.PropertiesSpellBook[property.Spell] = property.Probability;

            // Remove the enchantments effects from the item. We will not re-add any enchantments to the item
            foreach (var enchantment in biota.BiotaPropertiesEnchantmentRegistry)
            {
                var spell = DatabaseManager.World.GetCachedSpell((uint)enchantment.SpellId);

                if (!spell.StatModType.HasValue || !spell.StatModKey.HasValue || !spell.StatModVal.HasValue)
                {
                    log.Debug($"CharacterTransfer: Unable to subtract spell {spell.Id} from item 0x{wo.Guid}:{wo.Name}. Missing Values in database spell.");
                    continue;
                }

                bool isSingleStat = ((spell.StatModType.Value & (int)EnchantmentTypeFlags.SingleStat) != 0);
                bool isAdditive = ((spell.StatModType.Value & (int)EnchantmentTypeFlags.Additive) != 0);

                if ((spell.StatModType.Value & (int)EnchantmentTypeFlags.Int) != 0)
                {
                    if (isSingleStat)
                    {
                        var value = wo.GetProperty((PropertyInt)spell.StatModKey) ?? 0;

                        if (isAdditive)
                            value -= (int)(spell.StatModVal ?? 0);
                        else
                        {
                            log.Debug($"CharacterTransfer: Unable to subtract spell {spell.Id} from item 0x{wo.Guid}:{wo.Name}. StatModType 0x{spell.StatModType:X8} not implemented.");
                            continue;
                        }

                        wo.SetProperty((PropertyInt)spell.StatModKey, value);
                    }
                    else
                    {
                        log.Debug($"CharacterTransfer: Unable to subtract spell {spell.Id} from item 0x{wo.Guid}:{wo.Name}. StatModType 0x{spell.StatModType:X8} not implemented.");
                    }
                }
                else if ((spell.StatModType.Value & (int)EnchantmentTypeFlags.Float) != 0)
                {
                    if (isSingleStat)
                    {
                        var value = wo.GetProperty((PropertyFloat)spell.StatModKey) ?? 0;

                        if (isAdditive)
                            value -= (int)(spell.StatModVal ?? 0);
                        else
                        {
                            log.Debug($"CharacterTransfer: Unable to subtract spell {spell.Id} from item 0x{wo.Guid}:{wo.Name}. StatModType 0x{spell.StatModType:X8} not implemented.");
                            continue;
                        }

                        wo.SetProperty((PropertyFloat)spell.StatModKey, value);
                    }
                    else
                    {
                        log.Debug($"CharacterTransfer: Unable to subtract spell {spell.Id} from item 0x{wo.Guid}:{wo.Name}. StatModType 0x{spell.StatModType:X8} not implemented.");
                    }
                }
                else
                {
                    log.Debug($"CharacterTransfer: Unable to subtract spell {spell.Id} from item 0x{wo.Guid}:{wo.Name}. StatModType 0x{spell.StatModType:X8} not implemented.");
                }
            }

            // Try to determine the correct ClothingBase and PaletteTemplate from PCap data using OptimShi's ClothingBaseLookup
            if (wo.ClothingBase != null && wo.PaletteTemplate != null)
            {
                var validLocations = biota.GetProperty(PropertyInt.ValidLocations) ?? 0;

                if (((EquipMask)validLocations & EquipMask.Clothing) != 0 || ((EquipMask)validLocations & EquipMask.Armor) != 0 || wo is Container)
                {
                    var results = clothingBaseLookup.DoSearch(biota);

                    if (results.Count > 0)
                    {
                        //var origClothingBase = wo.ClothingBase;
                        //var origPaletteTemplate = wo.PaletteTemplate;
                        wo.ClothingBase = results[0].ClothingBase;
                        wo.PaletteTemplate = results[0].PaletteTemplate;

                        var shade = clothingBaseLookup.GetShade(biota, wo.ClothingBase ?? 0, wo.PaletteTemplate ?? 0);

                        if (shade != null)
                        {
                            wo.Shade = shade;
                            wo.RemoveProperty(PropertyFloat.Shade2);
                            wo.RemoveProperty(PropertyFloat.Shade3);
                            wo.RemoveProperty(PropertyFloat.Shade4);
                        }

                        //log.Debug($"{wo.Name} changed ClothingBase from {origClothingBase:X8} to {wo.ClothingBase:X8}, PaletteTemplate from {origPaletteTemplate:X8} to {wo.PaletteTemplate:X8}");
                    }
                    //else
                    //log.Debug($"{wo.Name} returned no results from DoSearch()");
                }
            }

            // we don't import enchantments

            if (altWeenieUsed)
                wo.SetProperty(PropertyDataId.IconUnderlay, 0x109A); // black/white pixelated

            // Clean location properties
            wo.Placement = Placement.Resting;
            wo.ParentLocation = null;
            wo.Location = null;

            // Clean up container properties
            wo.OwnerId = null;
            wo.ContainerId = null;
            wo.InventoryOrder = null;

            // Clean wielded properties
            wo.RemoveProperty(PropertyInt.CurrentWieldedLocation);
            wo.RemoveProperty(PropertyInstanceId.Wielder);
            wo.Wielder = null;

            wo.IsAffecting = false;

            // Fixes
            if (wo is ManaStone)
            {
                if (wo.UiEffects == UiEffects.Magical && (wo.ItemCurMana == null || wo.ItemCurMana == 0))
                {
                    wo.ItemCurMana = 10000;
                    wo.Use = "Use on a magic item to give the stone's stored Mana to that item.";
                }
                if (wo.UiEffects != UiEffects.Magical && wo.ItemCurMana > 0)
                    wo.UiEffects = UiEffects.Magical;
                if (wo.UiEffects != UiEffects.Magical && wo.ItemCurMana == null)
                {
                    wo.ItemCurMana = 0;
                    wo.Use = "Use on a magic item to destroy that item and drain its Mana.";
                }
            }

            // Convenience
            if (wo.ItemCurMana != null && wo.ItemMaxMana != null)
                wo.ItemCurMana = wo.ItemMaxMana;

            return wo;
        }

        private static WorldObject CreateIOU(Biota biota)
        {
            var iou = (Book)WorldObjectFactory.CreateNewWorldObject("parchment");

            iou.SetProperties("IOU", "An IOU for a missing database object.", $"Sorry about that chief... but I couldn't import your {biota.Id:X8}:{biota.GetProperty(PropertyString.Name)}", "ACEmulator", "prewritten");
            iou.AddPage(uint.MaxValue, "ACEmulator", "prewritten", false, $"{biota.WeenieClassId}\n\nSorry but the database does not have a weenie for weenieClassId #{biota.WeenieClassId} so in lieu of that here is an IOU for that item.", out _);
            iou.Bonded = BondedStatus.Bonded;
            iou.Attuned = AttunedStatus.Attuned;
            iou.SetProperty(PropertyBool.IsSellable, false);
            iou.Value = 0;
            iou.EncumbranceVal = 0;

            return iou;
        }
    }
}
