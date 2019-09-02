using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Database.Models.World;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Factories
{
    public static class PlayerFactoryEx
    {
        private static readonly Random rand = new Random();

        /// <summary>
        /// Heritage: Gear Knight
        /// 10 for all attributes
        /// trained Creature/Item/Life/Mana Conversion. This will make sure the player starts off with foci
        /// </summary>
        private static readonly byte[] baseGearKnight =
        {
            0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0xF0, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, 0x88, 0x2E, 0x44, 0x17, 0xA2, 0x0B,
            0xD1, 0x3F, 0xC7, 0xBF, 0xE3, 0xDF, 0xF1, 0xEF, 0xE8, 0x3F, 0xD4, 0x1E, 0x6A, 0x0F, 0xB5, 0x87, 0xDA, 0x3F,
            0xAD, 0x76, 0x56, 0x3B, 0xAB, 0x9D, 0xD5, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x0A, 0x00,
            0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x07, 0x00,
            0x4E, 0x6F, 0x20, 0x4E, 0x61, 0x6D, 0x65, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        private static CharacterCreateInfo CreateCharacterCreateInfo(string name, uint strength, uint endurance, uint coordination, uint quickness, uint focus, uint self, bool randomizeHeritageAndApperance = true)
        {
            var characterCreateInfo = new CharacterCreateInfo();

            using (var memoryStream = new MemoryStream(baseGearKnight))
            using (var binaryReader = new BinaryReader(memoryStream))
                characterCreateInfo.Unpack(binaryReader);

            characterCreateInfo.Name = name;

            characterCreateInfo.StrengthAbility = strength;
            characterCreateInfo.EnduranceAbility = endurance;
            characterCreateInfo.CoordinationAbility = coordination;
            characterCreateInfo.QuicknessAbility = quickness;
            characterCreateInfo.FocusAbility = focus;
            characterCreateInfo.SelfAbility = self;

            if (randomizeHeritageAndApperance)
                RandomizeHeritage(characterCreateInfo);

            return characterCreateInfo;
        }

        private static void RandomizeHeritage(CharacterCreateInfo characterCreateInfo)
        {
            var heritage = (uint)rand.Next(1, 12);
            var heritageGroup = DatManager.PortalDat.CharGen.HeritageGroups[heritage];

            characterCreateInfo.Heritage = heritage;
            characterCreateInfo.Gender = (uint)heritageGroup.Genders.ElementAt(rand.Next(0, heritageGroup.Genders.Count)).Key;

            var sex = heritageGroup.Genders[(int)characterCreateInfo.Gender];

            characterCreateInfo.Apperance.HairColor = (uint)rand.Next(0, sex.HairColorList.Count);
            characterCreateInfo.Apperance.HairStyle = (uint)rand.Next(0, sex.HairStyleList.Count);

            characterCreateInfo.Apperance.Eyes = (uint)rand.Next(0, sex.EyeStripList.Count);
            characterCreateInfo.Apperance.EyeColor = (uint)rand.Next(0, sex.EyeColorList.Count);
            characterCreateInfo.Apperance.Nose = (uint)rand.Next(0, sex.NoseStripList.Count);
            characterCreateInfo.Apperance.Mouth = (uint)rand.Next(0, sex.MouthStripList.Count);

            // todo randomize skin
        }


        /// <summary>
        /// Creates a fully leveled/augmented 275 Heavy Weapons character player
        /// </summary>
        private static Player Create275Base(CharacterCreateInfo characterCreateInfo, Weenie weenie, ObjectGuid guid, uint accountId)
        {
            PlayerFactory.Create(characterCreateInfo, weenie, guid, accountId, WeenieType.Creature, out var player);

            // Remove junk inventory
            player.TryDequipObject(player.EquippedObjects.FirstOrDefault(k => k.Value.Name.Contains("Leather Boots")).Key, out var wo, out _);
            if (wo != null)
                player.TryRemoveFromInventory(wo.Guid);

            player.TryRemoveFromInventory(player.Inventory.FirstOrDefault(k => k.Value.Name.Contains("Training Wand")).Key);
            player.TryRemoveFromInventory(player.Inventory.FirstOrDefault(k => k.Value.Name.Contains("Letter From Home")).Key);

            LevelUpPlayer(player);

            return player;
        }

        private static void LevelUpPlayer(Player player)
        {
            player.AvailableExperience += 191226310247;
            player.TotalExperience += 191226310247;
            player.Level = 275;
            player.AvailableSkillCredits += 46;
            player.TotalSkillCredits += 46;

            // todo add spec arcane lore quest flag + spec arcane lore

            // todo add Hunting Aun Ralirea quest flag + skill credit
            // todo add Chasing Oswald quest flag + skill credit

            // todo add all augmentations except the element protection and attribute raising ones

            // todo add Luminance quest flags + 2 luminance quest flags + skill credits
        }


        /// <summary>
        /// Creates a fully leveled 275 Heavy Weapons character player
        /// No augmentations are included
        /// </summary>
        public static Player Create275HeavyWeapons(Weenie weenie, ObjectGuid guid, uint accountId, string name)
        {
            var characterCreateInfo = CreateCharacterCreateInfo(name, 100, 10, 100, 100, 10, 10);

            var player = Create275Base(characterCreateInfo, weenie, guid, accountId);

            // Trained skills
            player.TrainSkill(Skill.HeavyWeapons, 6);
            player.TrainSkill(Skill.Healing, 6);
            player.TrainSkill(Skill.MeleeDefense, 10);
            player.TrainSkill(Skill.MissileDefense, 6);
            player.TrainSkill(Skill.Shield, 2);

            // Specialized skills
            player.SpecializeSkill(Skill.HeavyWeapons, 6);
            player.SpecializeSkill(Skill.Healing, 4);
            player.SpecializeSkill(Skill.MagicDefense, 12);
            player.SpecializeSkill(Skill.MeleeDefense, 10);
            player.SpecializeSkill(Skill.Shield, 2);

            // 0 remaining skill points.
            // If/When we add the 4 skill points in LevelUpPlayer, we can spend them here as well

            // todo aug endurance

            SpendAllXp(player);

            AddCommonInventory(player, RelicAlduressa);

            var hits = 0;
            while (hits < 12)
            {
                var item = LootGenerationFactory.CreateMeleeWeapon(7, true);
                if (item?.WeaponSkill == Skill.HeavyWeapons)
                {
                    player.TryAddToInventory(item);
                    hits++;
                }
            }

            AddAllSpells(player);

            return player;
        }

        /// <summary>
        /// Creates a fully leveled 275 Missile Weapons character player
        /// No augmentations are included
        /// </summary>
        public static Player Create275MissileWeapons(Weenie weenie, ObjectGuid guid, uint accountId, string name)
        {
            var characterCreateInfo = CreateCharacterCreateInfo(name, 10, 100, 100, 10, 10, 100);

            var player = Create275Base(characterCreateInfo, weenie, guid, accountId);

            // Trained skills
            player.TrainSkill(Skill.Healing, 6);
            player.TrainSkill(Skill.MeleeDefense, 10);
            player.TrainSkill(Skill.MissileDefense, 6);
            player.TrainSkill(Skill.MissileWeapons, 6);

            // Specialized skills
            player.SpecializeSkill(Skill.Healing, 4);
            player.SpecializeSkill(Skill.MagicDefense, 12);
            player.SpecializeSkill(Skill.MeleeDefense, 10);
            player.SpecializeSkill(Skill.MissileWeapons, 6);

            // 4 remaining skill points.
            // If/When we add the 4 skill points in LevelUpPlayer, we can spend them here as well

            // todo aug what attribute?

            SpendAllXp(player);

            AddCommonInventory(player, NobleRelic);

            var hits = 0;
            while (hits < 12)
            {
                var item = LootGenerationFactory.CreateMissileWeapon(7, true);
                if (item?.WeaponSkill == Skill.MissileWeapons)
                {
                    player.TryAddToInventory(item);
                    hits++;
                }
            }

            AddAllSpells(player);

            return player;
        }

        /// <summary>
        /// Creates a fully leveled 275 War Magic character player
        /// No augmentations are included
        /// </summary>
        public static Player Create275WarMagic(Weenie weenie, ObjectGuid guid, uint accountId, string name)
        {
            var characterCreateInfo = CreateCharacterCreateInfo(name, 10, 100, 10, 10, 100, 100);

            var player = Create275Base(characterCreateInfo, weenie, guid, accountId);

            // Trained skills
            player.TrainSkill(Skill.MeleeDefense, 10);
            player.TrainSkill(Skill.MissileDefense, 6);
            player.TrainSkill(Skill.Summoning, 8);
            player.TrainSkill(Skill.WarMagic, 16);

            // Specialized skills
            player.SpecializeSkill(Skill.LifeMagic, 8);
            player.SpecializeSkill(Skill.Summoning, 4);
            player.SpecializeSkill(Skill.WarMagic, 12);

            // 0 remaining skill points
            // If/When we add the 4 skill points in LevelUpPlayer, we can spend them here as well

            // todo aug what attribute?

            SpendAllXp(player);

            AddCommonInventory(player, AncientRelic);

            var hits = 0;
            while (hits < 12)
            {
                var item = LootGenerationFactory.CreateCaster(7, true);
                if (item?.WieldSkillType == (int)Skill.WarMagic)
                {
                    player.TryAddToInventory(item);
                    hits++;
                }
            }

            AddAllSpells(player);

            return player;
        }

        private static void SpendAllXp(Player player)
        {
            player.SpendAllXp(false);

            player.Health.Current = player.Health.MaxValue;
            player.Stamina.Current = player.Stamina.MaxValue;
            player.Mana.Current = player.Mana.MaxValue;
        }

        public static readonly HashSet<uint> CommonSpellComponents = new HashSet<uint> { 691, 689, 686, 688, 687, 690, 8897, 7299, 37155, 20631 };

        private static readonly HashSet<uint> NobleRelic = new HashSet<uint> { 33584, 33585, 33586, 33587, 33588 };
        private static readonly HashSet<uint> RelicAlduressa = new HashSet<uint> { 33574, 33575, 33576, 33577, 33578 };
        private static readonly HashSet<uint> AncientRelic = new HashSet<uint> { 33579, 33580, 33581, 33582, 33583 };

        private static void AddCommonInventory(Player player, params HashSet<uint>[] additionalGroups)
        {
            // MMD
            AddWeeniesToInventory(player, new HashSet<uint> { 20630 });

            // Spell Components
            AddWeeniesToInventory(player, CommonSpellComponents);

            // Focusing Stone
            AddWeeniesToInventory(player, new HashSet<uint> { 8904 });

            AddWeeniesToInventory(player, new HashSet<uint> { 5893 }); // Hoary Robe
            AddWeeniesToInventory(player, new HashSet<uint> { 14594 }); // Helm of the Elements

            foreach (var group in additionalGroups)
                AddWeeniesToInventory(player, group);

            // todo Drudge Scrying Orb

            // todo Buffing wand that has all defenses maxed
        }

        private static void AddWeeniesToInventory(Player player, HashSet<uint> weenieIds, ushort? stackSize = null)
        {
            foreach (uint weenieId in weenieIds)
            {
                var loot = WorldObjectFactory.CreateNewWorldObject(weenieId);

                if (loot == null) // weenie doesn't exist
                    continue;

                if (stackSize == null)
                    stackSize = loot.MaxStackSize;

                if (stackSize > 1)
                    loot.SetStackSize(stackSize);

                // Make sure the item is full of mana
                if (loot.ItemCurMana.HasValue)
                    loot.ItemCurMana = loot.ItemMaxMana;

                player.TryAddToInventory(loot);
            }
        }

        private static void AddAllSpells(Player player)
        {
            for (uint spellLevel = 1; spellLevel <= 8; spellLevel++)
            {
                player.LearnSpellsInBulk(MagicSchool.CreatureEnchantment, spellLevel, false);
                player.LearnSpellsInBulk(MagicSchool.ItemEnchantment, spellLevel, false);
                player.LearnSpellsInBulk(MagicSchool.LifeMagic, spellLevel, false);
                player.LearnSpellsInBulk(MagicSchool.VoidMagic, spellLevel, false);
                player.LearnSpellsInBulk(MagicSchool.WarMagic, spellLevel, false);
            }
        }


        public static void MakeSurePlayerHasFullStackForWeenies(Player player, HashSet<uint> weenieIds)
        {
            foreach (uint weenieId in weenieIds)
            {
                var amountFound = 0;
                var maxStackSize = 0;

                foreach (var item in player.GetAllPossessions())
                {
                    if (item.WeenieClassId == weenieId)
                    {
                        amountFound += item.StackSize ?? 1;
                        maxStackSize = item.MaxStackSize ?? 1;
                    }
                }

                if (amountFound > 0 && amountFound >= maxStackSize)
                    continue;

                var loot = WorldObjectFactory.CreateNewWorldObject(weenieId);

                if (loot == null) // weenie doesn't exist
                    continue;

                if (loot.MaxStackSize > 1)
                {
                    var amountToAdd = loot.MaxStackSize - amountFound;

                    loot.StackSize = amountToAdd;
                    loot.EncumbranceVal = (loot.StackUnitEncumbrance ?? 0) * (amountToAdd ?? 1);
                    loot.Value = (loot.StackUnitValue ?? 0) * (amountToAdd ?? 1);
                }

                player.TryAddToInventory(loot);
            }
        }
    }
}
