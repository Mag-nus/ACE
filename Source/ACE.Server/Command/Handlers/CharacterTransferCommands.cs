using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using log4net;

using ACE.Database;
using ACE.Database.Entity;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using Biota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.Command.Handlers
{
    public static class CharacterTransferCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static ClothingBaseLookup clothingBaseLookup;

        private class Server
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

        private static readonly Collection<Server> Servers = new Collection<Server>
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

            optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

            var context = new ShardDbContext(optionsBuilder.Options);

            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            return context;
        }

        private static Character GetCharacter(Server server, uint id)
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

        private static Biota GetBiota(Server server, string name)
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

        private static PossessedBiotas GetPossessedBiotasInParallel(Server severName, uint id)
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

                Parallel.ForEach(results, result =>
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

        private static List<Biota> GetWieldedItemsInParallel(Server server, uint parentId)
        {
            using (var context = GetShardDbContext(server))
            {
                var wieldedItems = new ConcurrentBag<Biota>();

                var results = context.BiotaPropertiesIID
                    .Where(r => r.Type == (ushort)PropertyInstanceId.Wielder && r.Value == parentId)
                    .ToList();

                Parallel.ForEach(results, result =>
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


        // restoreretailcharacter
        [CommandHandler("restoreretailcharacter", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 3, "Restores a pcapped retail character into your characters list", "[server name] [retail character name] [new character name]")]
        public static void HandleRestoreRetailCharacter(Session session, params string[] parameters)
        {
            if (clothingBaseLookup == null)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Initializing ClothingBaseLookup...", ChatMessageType.Broadcast));
                clothingBaseLookup = new ClothingBaseLookup();
            }

            if (session.Characters.Count >= (uint)PropertyManager.GetLong("max_chars_per_account").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"You have no more free character slots.", ChatMessageType.Broadcast));
                return;
            }

            var serverName = parameters[0];
            var server = Servers.FirstOrDefault(r => r.Names.Contains(serverName, StringComparer.OrdinalIgnoreCase));
            if (server == null)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("[server name] not found", ChatMessageType.Broadcast));
                return;
            }

            var newName = parameters[2].Trim();

            DatabaseManager.Shard.IsCharacterNameAvailable(newName, isAvailable =>
            {
                if (!isAvailable)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat($"{newName} is not available to use for the restored character, try another name.", ChatMessageType.Broadcast));
                    return;
                }

                // todo: This is a lot of work that is done on the database thread (callback).. it should be off-loaded onto another thread
                // todo: It's mainly ImportWorldObject and getting the cached weenies. If the weenies are pre-cached, the performance may be acceptable

                session.Network.EnqueueSend(new GameMessageSystemChat("Pulling retail player biota...", ChatMessageType.Broadcast));

                var retailName = parameters[1];

                log.Info($"Account {session.AccountId}:{session.Account}, Player 0x{session.Player.Guid}:{session.Player.Name} requested restore of {serverName}:{retailName}");

                var retailBiota = GetBiota(server, retailName);

                if (retailBiota == null)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat($"Retail player biota was not found with name: {retailName}", ChatMessageType.Broadcast));
                    return;
                }


                var human = DatabaseManager.World.GetCachedWeenie("human");
                // Removes the generic knife and buckler, hidden Javelin, 30 stack of arrows, and 5 stack of coins that are given to all characters
                // Starter Gear from the JSON file are added to the character later in the CharacterCreateEx() process
                human.PropertiesCreateList = null;

                var guid = GuidManager.NewPlayerGuid();

                var player = new Player(human, guid, session.AccountId);


                // Try to restore the character apperance information
                foreach (var entry in retailBiota.BiotaPropertiesPalette)
                {
                    // Hair
                    if (entry.Offset == 0x18 && entry.Length == 0x08)
                        player.HairPaletteDID = entry.SubPaletteId;

                    // Skin
                    if (entry.Offset == 0x00 && entry.Length == 0x18)
                        player.SkinPaletteDID = entry.SubPaletteId;

                    // Eyes
                    if (entry.Offset == 0x20 && entry.Length == 0x08)
                        player.EyesPaletteDID = entry.SubPaletteId;
                }

                int tensFound = 0;
                foreach (var entry in retailBiota.BiotaPropertiesTextureMap)
                {
                    if (entry.Index == 0x10 && tensFound == 0)
                    {
                        player.Character.DefaultHairTexture = entry.OldId;
                        player.Character.HairTexture = entry.NewId;
                        tensFound++;
                    }
                    // Eyes
                    else if (entry.Index == 0x10 && tensFound == 1)
                    {
                        player.DefaultEyesTextureDID = entry.OldId;
                        player.EyesTextureDID = entry.NewId;
                        tensFound++;
                    }
                    // Nose
                    else if (entry.Index == 0x10 && tensFound == 2)
                    {
                        player.DefaultNoseTextureDID = entry.OldId;
                        player.NoseTextureDID = entry.NewId;
                        tensFound++;
                    }
                    // Mouth
                    else if (entry.Index == 0x10 && tensFound == 3)
                    {
                        player.DefaultMouthTextureDID = entry.OldId;
                        player.MouthTextureDID = entry.NewId;
                        tensFound++;
                    }
                }


                // Import all the property buckets
                foreach (var property in retailBiota.BiotaPropertiesInt)
                {
                    // Filter out properties that come from equipped items
                    if (property.Type >= (int)PropertyInt.GearDamage && property.Type <= (int)PropertyInt.GearMaxHealth)
                        continue;
                    if (property.Type >= (int)PropertyInt.GearPKDamageRating && property.Type <= (int)PropertyInt.GearPKDamageResistRating)
                        continue;
                    // We don't pull in allegiances
                    if (property.Type == (int)PropertyInt.AllegianceCpPool || property.Type == (int)PropertyInt.AllegianceRank || property.Type == (int)PropertyInt.MonarchsRank || property.Type == (int)PropertyInt.AllegianceFollowers
                        || property.Type == (int)PropertyInt.AllegianceMinLevel || property.Type == (int)PropertyInt.AllegianceMaxLevel || property.Type == (int)PropertyInt.AllegianceSwearTimestamp)
                        continue;
                    // We don't pull in house information
                    if (property.Type == (int)PropertyInt.HouseStatus || property.Type == (int)PropertyInt.HouseType)
                        continue;
                    player.SetProperty((PropertyInt)property.Type, property.Value);
                }
                foreach (var property in retailBiota.BiotaPropertiesInt64)
                    player.SetProperty((PropertyInt64)property.Type, property.Value);
                foreach (var property in retailBiota.BiotaPropertiesBool)
                {
                    if (property.Type == (int)PropertyBool.Attackable)
                        continue;
                    player.SetProperty((PropertyBool)property.Type, property.Value);
                }
                foreach (var property in retailBiota.BiotaPropertiesFloat)
                    player.SetProperty((PropertyFloat)property.Type, property.Value);
                foreach (var property in retailBiota.BiotaPropertiesString)
                {
                    // We don't pull in allegiances
                    if (property.Type == (int)PropertyString.MonarchsName || property.Type == (int)PropertyString.MonarchsTitle || property.Type == (int)PropertyString.AllegianceName)
                        continue;
                    // We don't pull in vassal/patron relationships
                    if (property.Type == (int)PropertyString.PatronsTitle)
                        continue;
                    player.SetProperty((PropertyString)property.Type, property.Value);
                }
                foreach (var property in retailBiota.BiotaPropertiesDID)
                    player.SetProperty((PropertyDataId)property.Type, property.Value);
                foreach (var property in retailBiota.BiotaPropertiesIID)
                {
                    // We don't pull in allegiances
                    if (property.Type == (int)PropertyInstanceId.Allegiance || property.Type == (int)PropertyInstanceId.Monarch)
                        continue;
                    // We don't pull in vassal/patron relationships
                    if (property.Type == (int)PropertyInstanceId.Patron)
                        continue;
                    // We don't pull in house information
                    if (property.Type == (int)PropertyInstanceId.HouseOwner || property.Type == (int)PropertyInstanceId.House)
                        continue;
                    player.SetProperty((PropertyInstanceId)property.Type, property.Value);
                }

                foreach (var property in retailBiota.BiotaPropertiesPosition)
                    player.SetPosition((PositionType)property.PositionType, new Position(property.ObjCellId, property.OriginX, property.OriginY, property.OriginZ, property.AnglesX, property.AnglesY, property.AnglesZ, property.AnglesW));

                foreach (var property in retailBiota.BiotaPropertiesAttribute)
                {
                    var attribute = player.Attributes[(PropertyAttribute)property.Type];
                    attribute.StartingValue = property.InitLevel;
                    attribute.Ranks = property.LevelFromCP;
                    attribute.ExperienceSpent = property.CPSpent;
                }

                {
                    var property = retailBiota.BiotaPropertiesAttribute2nd.FirstOrDefault(r => r.Type == (ushort)PropertyAttribute2nd.Health);
                    if (property != null)
                    {
                        var vital = player.GetCreatureVital(PropertyAttribute2nd.Health);
                        vital.StartingValue = property.InitLevel;
                        vital.Ranks = property.LevelFromCP;
                        vital.ExperienceSpent = property.CPSpent;
                        vital.Current = property.CurrentLevel;
                    }
                }
                {
                    var property = retailBiota.BiotaPropertiesAttribute2nd.FirstOrDefault(r => r.Type == (ushort)PropertyAttribute2nd.Stamina);
                    if (property != null)
                    {
                        var vital = player.GetCreatureVital(PropertyAttribute2nd.Stamina);
                        vital.StartingValue = property.InitLevel;
                        vital.Ranks = property.LevelFromCP;
                        vital.ExperienceSpent = property.CPSpent;
                        vital.Current = property.CurrentLevel;
                    }
                }
                {
                    var property = retailBiota.BiotaPropertiesAttribute2nd.FirstOrDefault(r => r.Type == (ushort)PropertyAttribute2nd.Mana);
                    if (property != null)
                    {
                        var vital = player.GetCreatureVital(PropertyAttribute2nd.Mana);
                        vital.StartingValue = property.InitLevel;
                        vital.Ranks = property.LevelFromCP;
                        vital.ExperienceSpent = property.CPSpent;
                        vital.Current = property.CurrentLevel;
                    }
                }

                foreach (var property in retailBiota.BiotaPropertiesSkill)
                {
                    var cs = player.GetCreatureSkill((Skill)property.Type);
                    cs.AdvancementClass = (SkillAdvancementClass)property.SAC;
                    cs.Ranks = property.LevelFromPP;
                    cs.ExperienceSpent = property.PP;
                    cs.InitLevel = property.InitLevel;
                }

                foreach (var property in retailBiota.BiotaPropertiesSpellBook)
                    player.AddKnownSpell((uint)property.Spell);

                // We don't import all the enchantments. Instead, we just add str/arcane buffs to help activate equipment
                player.EnchantmentManager.DispelAllEnchantments();
                player.EnchantmentManager.Add(new Spell(4325), player, null); // Strength Self 8
                player.EnchantmentManager.Add(new Spell(3738), player, null); // Prodigal Strength
                player.EnchantmentManager.Add(new Spell(4305), player, null); // Focus Self 8
                player.EnchantmentManager.Add(new Spell(3705), player, null); // Prodigal Focus
                player.EnchantmentManager.Add(new Spell(2348), player, null); // Brilliance
                player.EnchantmentManager.Add(new Spell(4510), player, null); // Incantation of Arcane Enlightenment Self
                player.EnchantmentManager.Add(new Spell(3682), player, null); // Prodigal Arcane Enlightenment
                player.EnchantmentManager.Add(new Spell(4548), player, null); // Incantation of Fealty Self
                player.EnchantmentManager.Add(new Spell(3701), player, null); // Prodigal Fealty


                session.Network.EnqueueSend(new GameMessageSystemChat("Pulling retail possessions...", ChatMessageType.Broadcast));

                var possessedBiotas = new Collection<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();

                var possessions = GetPossessedBiotasInParallel(server, retailBiota.Id);

                session.Network.EnqueueSend(new GameMessageSystemChat("Converting retail possessions...", ChatMessageType.Broadcast));

                var guidConversions = new Dictionary<uint, uint>
                {
                    { retailBiota.Id, player.Guid.Full },
                };

                // Sort the inventory by InventoryOrder
                var sortedInventory = possessions.Inventory.OrderByDescending(r => r.BiotaPropertiesInt.FirstOrDefault(p => p.Type == (ushort)PropertyInt.InventoryOrder)?.Value).ToList();

                // Main pack and side slot items
                foreach (var possession in sortedInventory.Where(r => r.BiotaPropertiesIID.FirstOrDefault(p => p.Type == (ushort)PropertyInstanceId.Container && p.Value == retailBiota.Id) != null))
                {
                    var wo = ImportWorldObject(possession);

                    if (wo == null)
                        continue;

                    guidConversions[possession.Id] = wo.Guid.Full;

                    player.TryAddToInventory(wo);

                    possessedBiotas.Add((wo.Biota, wo.BiotaDatabaseLock));
                }

                // Side pack items
                foreach (var possession in sortedInventory.Where(r => r.BiotaPropertiesIID.FirstOrDefault(p => p.Type == (ushort)PropertyInstanceId.Container && p.Value == retailBiota.Id) == null))
                {
                    var wo = ImportWorldObject(possession);

                    if (wo == null)
                        continue;

                    guidConversions[possession.Id] = wo.Guid.Full;

                    var containerId = possession.GetProperty(PropertyInstanceId.Container) ?? 0;

                    if (guidConversions.TryGetValue(containerId, out var value))
                    {
                        if (player.GetInventoryItem(value) is Container container)
                            container.TryAddToInventory(wo);
                        else
                            player.TryAddToInventory(wo);
                    }
                    else
                        player.TryAddToInventory(wo);

                    possessedBiotas.Add((wo.Biota, wo.BiotaDatabaseLock));
                }

                foreach (var possession in possessions.WieldedItems)
                {
                    var wo = ImportWorldObject(possession);

                    if (wo == null)
                        continue;

                    guidConversions[possession.Id] = wo.Guid.Full;

                    if (wo is Book) // Item is likely an IOU
                        player.TryAddToInventory(wo);
                    else
                    {
                        // We don't wield selectable items (weapons, orbs, etc..), it bugs the player
                        var wieldLocation = possession.GetProperty(PropertyInt.CurrentWieldedLocation) ?? 0;
                        if (wieldLocation == 0 || (wieldLocation & (int)EquipMask.Selectable) != 0 || !player.TryEquipObject(wo, (EquipMask)wieldLocation))
                            player.TryAddToInventory(wo);
                    }

                    possessedBiotas.Add((wo.Biota, wo.BiotaDatabaseLock));
                }


                session.Network.EnqueueSend(new GameMessageSystemChat("Pulling retail character...", ChatMessageType.Broadcast));

                var retailCharacter = GetCharacter(server, retailBiota.Id);

                if (retailCharacter != null)
                {
                    player.Character.CharacterOptions1 = retailCharacter.CharacterOptions1;
                    player.Character.CharacterOptions2 = retailCharacter.CharacterOptions2;
                    if (retailCharacter.GameplayOptions != null && retailCharacter.GameplayOptions.Length > 0)
                    {
                        player.Character.GameplayOptions = new byte[retailCharacter.GameplayOptions.Length];
                        Buffer.BlockCopy(retailCharacter.GameplayOptions, 0, player.Character.GameplayOptions, 0, player.Character.GameplayOptions.Length);
                    }

                    player.Character.SpellbookFilters = retailCharacter.SpellbookFilters;
                    player.Character.HairTexture = retailCharacter.HairTexture;
                    player.Character.DefaultHairTexture = retailCharacter.DefaultHairTexture;

                    // We don't import CharacterPropertiesContract

                    player.Character.CharacterPropertiesFillCompBook.Clear();
                    foreach (var entry in retailCharacter.CharacterPropertiesFillCompBook)
                        player.Character.AddFillComponent((uint)entry.SpellComponentId, (uint)entry.QuantityToRebuy, player.CharacterDatabaseLock, out _);

                    // We don't import CharacterPropertiesFriendList

                    // We don't import CharacterPropertiesQuestRegistry

                    player.Character.CharacterPropertiesShortcutBar.Clear();
                    foreach (var entry in retailCharacter.CharacterPropertiesShortcutBar)
                    {
                        if (guidConversions.TryGetValue(entry.ShortcutObjectId, out var value))
                            player.Character.AddOrUpdateShortcut(entry.ShortcutBarIndex, value, player.CharacterDatabaseLock);
                    }

                    player.Character.CharacterPropertiesSpellBar.Clear();
                    var spellsToAdd = retailCharacter.CharacterPropertiesSpellBar.OrderBy(r => r.SpellBarNumber).ThenBy(s => s.SpellBarIndex);
                    foreach (var spell in spellsToAdd)
                        player.Character.AddSpellToBar(spell.SpellBarNumber, spell.SpellBarIndex, spell.SpellId, player.CharacterDatabaseLock);

                    foreach (var entry in retailCharacter.CharacterPropertiesTitleBook)
                        player.AddTitle((CharacterTitle)entry.TitleId);
                }


                session.Network.EnqueueSend(new GameMessageSystemChat("Saving new character, please wait...", ChatMessageType.Broadcast));

                player.Name = newName;
                player.Character.Name = newName;

                var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(player.CreationTimestamp ?? 0);
                player.SetProperty(PropertyString.DateOfBirth, $"{dateTimeOffset.UtcDateTime:dd MMMM yyyy}");

                // If we have no linked lifestone, give the player a default
                if (player.Sanctuary == null)
                {
                    if (player.Instantiation != null)
                        player.SetPosition(PositionType.Sanctuary, new Position(player.Instantiation));
                    else
                        player.SetPosition(PositionType.Sanctuary, new Position(2103705613, 31.9F, 104.6F, 11.9F, 0, 0, -0.816642F, 0.577145F)); // Yaraq
                }

                // This prevents the welcome to dereth message
                player.Character.TotalLogins++;

                DatabaseManager.Shard.AddCharacterInParallel(player.Biota, player.BiotaDatabaseLock, possessedBiotas, player.Character, player.CharacterDatabaseLock, result =>
                {
                    PlayerManager.AddOfflinePlayer(player);
                    session.Characters.Add(player.Character);

                    log.Info($"Account {session.AccountId}:{session.Account}, Player 0x{session.Player.Guid}:{session.Player.Name} restored of {serverName}:{retailName} into {newName}");

                    session.Network.EnqueueSend(new GameMessageSystemChat("Saving new character completed. You will be logged out now...", ChatMessageType.Broadcast));

                    session.LogOffPlayer();
                });
            });
        }

        private static WorldObject ImportWorldObject(Biota biota)
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
                    if (biota.WeenieType == 21)         altWeenieClassName = "backpack";    // Container
                    else if (biota.WeenieType == 35)    altWeenieClassName = "wand";        // Caster
                    else if (biota.WeenieType == 38)    altWeenieClassName = "gem";         // Gem
                }

                if (altWeenieClassName == null)
                {
                    var validLocations = biota.GetProperty(PropertyInt.ValidLocations);

                    if (validLocations == (int)EquipMask.NeckWear)              altWeenieClassName = "necklace";
                    else if (validLocations == (int)EquipMask.TrinketOne)       altWeenieClassName = "ace41513-pathwardentrinket";
                    //else if (validLocations == (int)EquipMask.Cloak)            altWeenieClassName = "shirt"; // todo this is no good
                    else if (validLocations == (int)EquipMask.WristWear)        altWeenieClassName = "bracelet";
                    else if (validLocations == (int)EquipMask.FingerWear)       altWeenieClassName = "ring";

                    else if (validLocations == (int)EquipMask.HeadWear)         altWeenieClassName = "helmet";
                    else if (validLocations == (int)EquipMask.HandWear)         altWeenieClassName = "glovescloth";
                    else if (validLocations == (int)EquipMask.AbdomenArmor)     altWeenieClassName = "girthleather";
                    else if (validLocations == (int)EquipMask.FootWear)         altWeenieClassName = "shoes";

                    else if (validLocations == (int)EquipMask.Shield)           altWeenieClassName = "shieldround";
                    else if (validLocations == (int)EquipMask.MeleeWeapon)      altWeenieClassName = "swordlong";
                    else if (validLocations == (int)EquipMask.MissileWeapon)    altWeenieClassName = "bowlong";
                    else if (validLocations == (int)EquipMask.MissileAmmo)      altWeenieClassName = "arrow";
                }

                if (altWeenieClassName == null)
                {
                    // https://asheron.fandom.com/wiki/Currency
                    var name = biota.GetProperty(PropertyString.Name);

                    if (name == "Ancient Mhoire Coin")          altWeenieClassName = "coinstack";
                    else if (name == "A'nekshay Token")         altWeenieClassName = "coinstack";
                    else if (name == "Colosseum Coin")          altWeenieClassName = "coinstack";
                    else if (name == "Dark Tusker Paw")         altWeenieClassName = "coinstack";
                    else if (name == "Hero Token")              altWeenieClassName = "coinstack";
                    else if (name == "Ornate Gear Marker")      altWeenieClassName = "coinstack";
                    else if (name == "Pitted Slag")             altWeenieClassName = "coinstack";
                    else if (name == "Small Olthoi Venom Sac")  altWeenieClassName = "coinstack";
                    else if (name == "Spectral Ingot")          altWeenieClassName = "coinstack";
                    else if (name == "Stipend")                 altWeenieClassName = "coinstack";
                    else if (name == "Writ of Apology")         altWeenieClassName = "coinstack";
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
