using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public static class CharacterTransferCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);


        // restoreretailcharacter
        [CommandHandler("restoreretailcharacter", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 3, "Restores a pcapped retail character into your characters list", "[server name] [retail character name] [new character name]")]
        public static void HandleRestoreRetailCharacter(Session session, params string[] parameters)
        {
            if (session.Characters.Count >= (uint)PropertyManager.GetLong("max_chars_per_account").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"You have no more free character slots.", ChatMessageType.Broadcast));
                return;
            }

            var serverName = parameters[0];
            var server = RetailShardManager.Servers.FirstOrDefault(r => r.Names.Contains(serverName, StringComparer.OrdinalIgnoreCase));
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

                var retailBiota = RetailShardManager.GetBiota(server, retailName);

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

                var possessions = RetailShardManager.GetPossessedBiotasInParallel(server, retailBiota.Id);

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
                    var wo = RetailShardManager.CreateCurrentShardSafeWorldObjectFromRetailServerBiota(possession);

                    if (wo == null)
                        continue;

                    guidConversions[possession.Id] = wo.Guid.Full;

                    player.TryAddToInventory(wo);

                    possessedBiotas.Add((wo.Biota, wo.BiotaDatabaseLock));
                }

                // Side pack items
                foreach (var possession in sortedInventory.Where(r => r.BiotaPropertiesIID.FirstOrDefault(p => p.Type == (ushort)PropertyInstanceId.Container && p.Value == retailBiota.Id) == null))
                {
                    var wo = RetailShardManager.CreateCurrentShardSafeWorldObjectFromRetailServerBiota(possession);

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
                    var wo = RetailShardManager.CreateCurrentShardSafeWorldObjectFromRetailServerBiota(possession);

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

                var retailCharacter = RetailShardManager.GetCharacter(server, retailBiota.Id);

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
    }
}
