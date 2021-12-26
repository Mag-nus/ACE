using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Command.Handlers
{
    public static class AdminRareCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [CommandHandler("createrares", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 1)]
        public static void HandleCreateRares(Session session, params string[] parameters)
        {
            switch (parameters?[0].ToLower())
            {
                case "armor":
                    // Gelidite
                    AddWeeniesToInventory(session.Player, new List<uint> { 30511, 30514, 30515, 30517, 30519, 30522, 30524, 30526, 30529 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Leikotha's Tears
                    AddWeeniesToInventory(session.Player, new List<uint> { 30513, 30516, 30518, 30520, 30521, 30523, 30525, 30528 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Dusk
                    AddWeeniesToInventory(session.Player, new List<uint> { 30532, 30530 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Patriarch's Twilight
                    AddWeeniesToInventory(session.Player, new List<uint> { 30533, 30531 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Helm
                    AddWeeniesToInventory(session.Player, new List<uint> { 30512, 30527 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Gauntlets
                    AddWeeniesToInventory(session.Player, new List<uint> { 30510, 30534 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Boots
                    AddWeeniesToInventory(session.Player, new List<uint> { 30367, 30368, 30369 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    break;
                case "jewelry":
                    // Necklaces
                    AddWeeniesToInventory(session.Player, new List<uint> { 30357, 30358, 30359 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Bracelets
                    AddWeeniesToInventory(session.Player, new List<uint> { 30352, 30353, 30354, 30355, 30356, 30366 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    // Rings
                    AddWeeniesToInventory(session.Player, new List<uint> { 30360, 30361, 30362, 30363, 30364, 30365 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    break;
                case "heavy":
                    AddWeeniesToInventory(session.Player, new List<uint> { 30310, 30314, 30315, 30320, 30324, 30328, 30329, 30333, 30338, 30342, 30343 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    break;
                case "2h":
                    // Add
                    // 4395,"Aura of Incantation of Blood Drinker Self"
                    // 4400,"Aura of Incantation of Defender Self"
                    // 4405,"Aura of Incantation of Heart Seeker Self"
                    // 4417,"Aura of Incantation of Swift Killer Self"
                    // 6089,"Legendary Blood Thirst"
                    // 6091,"Legendary Defender"
                    // 6094,"Legendary Heart Thirst"
                    // 6100,"Legendary Swift Hunter"
                    // 5032,"Incantation of Two Handed Combat Mastery Self"
                    // 6073,"Legendary Two Handed Combat Aptitude"
                    // 2966,"Aura of Murderous Thirst"
                    AddWeeniesToInventory(session.Player, new List<uint> { 42662, 42663, 42664, 42665, 42666 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 4395, 4400, 4405, 4417, 6089, 6091, 6094, 6100, 5032, 6073, 2966 });
                    break;
                case "2hop":
                    // Add
                    // 4395,"Aura of Incantation of Blood Drinker Self"
                    // 4400,"Aura of Incantation of Defender Self"
                    // 4405,"Aura of Incantation of Heart Seeker Self"
                    // 4417,"Aura of Incantation of Swift Killer Self"
                    // 6089,"Legendary Blood Thirst"
                    // 6091,"Legendary Defender"
                    // 6094,"Legendary Heart Thirst"
                    // 6100,"Legendary Swift Hunter"
                    // 5032,"Incantation of Two Handed Combat Mastery Self"
                    // 6073,"Legendary Two Handed Combat Aptitude"
                    // 2966,"Aura of Murderous Thirst"
                    AddWeeniesToInventory(session.Player, new List<uint> { 42662, 42663, 42664, 42665, 42666 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.AddAllPerksToWeapons | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 4395, 4400, 4405, 4417, 6089, 6091, 6094, 6100, 5032, 6073, 2966 });
                    break;
                case "wand":
                    // 5182,"Aura of Incantation of Spirit Drinker"
                    // 4418,"Aura of Incantation of Hermetic Link Self"
                    // 4400,"Aura of Incantation of Defender Self"
                    // 6098,"Legendary Spirit Thirst"
                    // 6087,"Legendary Hermetic Link"
                    // 6091,"Legendary Defender"
                    // 6075,"Legendary War Magic Aptitude"
                    // 4638,"Incantation of War Magic Mastery Self"
                    // 6074,"Legendary Void Magic Aptitude"
                    // 5418,"Incantation of Void Magic Mastery Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30374, 30375, 30376, 30377, 30378, 43848 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 5182, 4418, 4400, 6098, 6087, 6091, 6075, 4638, 6074, 5418 });
                    break;
                case "shield":
                    AddWeeniesToInventory(session.Player, new List<uint> { 30370, 30371, 30372, 30373 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded);
                    break;
                case "mag":
                    // 6095,"Legendary Impenetrability"
                    // 4391,"Incantation of Acid Bane"
                    // 4393,"Incantation of Blade Bane"
                    // 4397,"Incantation of Bludgeon Bane"
                    // 4401,"Incantation of Flame Bane"
                    // 4403,"Incantation of Frost Bane"
                    // 4409,"Incantation of Lightning Bane"
                    // 4412,"Incantation of Piercing Bane"

                    // Gelidite
                    // 4602,"Incantation of Mana Conversion Mastery Self"
                    // 4305,"Incantation of Focus Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30511 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4602, 4305, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4498,"Incantation of Rejuvenation Self"
                    // 4560,"Incantation of Invulnerability Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30514 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4498 , 4560, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4329,"Incantation of Willpower Self"
                    // 4494,"Incantation of Mana Renewal Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30515 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4329 , 4494, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4596,"Incantation of Magic Resistance Self"
                    // 4090,"Scarab's Shell"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30517 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4596 , 4090, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4299,"Incantation of Endurance Self"
                    // 4558,"Incantation of Impregnability Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30519 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4299 , 4558, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4510,"Incantation of Arcane Enlightenment Self"
                    // 4496,"Incantation of Regeneration Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30522 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4510 , 4496, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4548,"Incantation of Fealty Self"
                    // 4325,"Incantation of Strength Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30524 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4548, 4325, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4556,"Incantation of Healing Mastery Self"
                    // 4297,"Incantation of Coordination Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30526 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4556 , 4297, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });
                    // Gelidite
                    // 4616,"Incantation of Sprint Self"
                    // 4319,"Incantation of Quickness Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30529 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4616 , 4319, 6095, 4391, 4393, 4397, 4401, 4403, 4409, 4412 });

                    // ace39978_gladiatorialtunic = 39978,
                    // 6068,"Legendary Salvaging Aptitude"
                    // 4499,"Incantation of Arcanum Salvaging Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 39978 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 6068, 4499, 6095 });
                    // ace39977_gladiatorialleggings = 39977,
                    // 6071,"Legendary Sprint"
                    // 4616,"Incantation of Sprint Self"
                    // 6058,"Legendary Jumping Prowess"
                    // 4572,"Incantation of Jumping Mastery Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 39977 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 6071, 4616, 6058, 4572, 6095 });

                    // necklaceraregoldensnake
                    // Add:
                    // "Incantation of Creature Enchantment Mastery Self"
                    // "Legendary Creature Enchantment Aptitude"
                    // "Incantation of Life Magic Mastery Self"
                    // "Legendary Life Magic Aptitude"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30357 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 4530, 6046, 4582, 6060 });

                    // braceletrareelementalharmony
                    // Add:
                    // 6085,"Legendary Slashing Ward"
                    // 6084,"Legendary Piercing Ward"
                    // 6081,"Legendary Bludgeoning Ward"
                    // 4460,"Incantation of Acid Protection Self"
                    // 4462,"Incantation of Blade Protection Self"
                    // 4464,"Incantation of Bludgeoning Protection Self"
                    // 4466,"Incantation of Cold Protection Self"
                    // 4468,"Incantation of Fire Protection Self"
                    // 4470,"Incantation of Lightning Protection Self"
                    // 4472,"Incantation of Piercing Protection Self"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30354 }, null, UpgradeOptions.Spells | UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, null, new List<int> { 6085, 6084, 6081, 4460, 4462, 4464, 4466, 4468, 4470, 4472 });

                    // braceletraredreamseerbangle
                    // 2666,"Essence Glutton"
                    // 2006,"Warrior's Ultimate Vitality"
                    // 4071,"Empyrean Stamina Absorbtion"
                    // 2010,"Warrior's Ultimate Vigor"
                    // 4070,"Empyrean Mana Absorbtion"
                    // 2014,"Wizard's Ultimate Intellect"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30353 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 2666, 2006, 4071, 2010, 4070, 2014 });

                    // ringrareweeping = 30364,
                    // 6329,"Gauntlet Critical Damage Boost II"
                    // 6331,"Gauntlet Damage Boost II"
                    // 6333,"Gauntlet Damage Reduction II"
                    // 6335,"Gauntlet Critical Damage Reduction II"
                    // 6337,"Gauntlet Healing Boost II"
                    // 6340,"Gauntlet Vitality III"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30364 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 6329, 6331, 6333, 6335, 6337, 6340 });

                    // ringrareweeping = 30364,
                    // 4853, "Master Negator's Magic Resistance"
                    // 4861, "Master Guardian's Invulnerability"
                    // 4865, "Master Wayfarer's Impregnability"
                    // 4906, "Apprentice Challenger's Rejuvenation"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30364 }, null, UpgradeOptions.DoubleMaxMana | UpgradeOptions.HalveManaRate | UpgradeOptions.Bonded, new List<int> { int.MaxValue }, new List<int> { 4853, 4861, 4865, 4906 });

                    // Infinite mana stone
                    AddWeeniesToInventory(session.Player, new List<uint> { 30254 }, null, UpgradeOptions.Bonded);

                    // Medicated Kits
                    // healingkitrarevolatilehealth = 30250,
                    // healingkitrarevolatilemana = 30251,
                    // healingkitrarevolatilestamina = 30252,
                    AddWeeniesToInventory(session.Player, new List<uint> { 30250, 30251, 30252 }, null, UpgradeOptions.MakeKitsUnlimitedUse | UpgradeOptions.Bonded);

                    break;
            }
        }

        [Flags]
        enum UpgradeOptions
        {
            None                    = 0x0000,

            Spells                  = 0x0001,

            DoubleMaxMana           = 0x0004,
            HalveManaRate           = 0x0008,

            MakeKitsUnlimitedUse    = 0x0010,

            AddAllPerksToWeapons    = 0x0020,

            Bonded                  = 0x0040,

            All                     = 0xFFFF,
        }

        private static void AddWeeniesToInventory(Player player, IEnumerable<uint> weenieIds, ushort? stackSize = null, UpgradeOptions upgradeOptions = UpgradeOptions.None, IList<int> spellsToRemove = null, IList<int> spellsToAdd = null)
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


                if (spellsToRemove != null) // Remove spells before upgrade
                {
                    if (spellsToRemove.Count == 1 && spellsToRemove[0] == int.MaxValue) // remove all spells
                        loot.Biota.ClearSpells(loot.BiotaDatabaseLock);
                    else
                    {
                        foreach (var spell in spellsToRemove)
                            loot.Biota.TryRemoveKnownSpell(spell, loot.BiotaDatabaseLock);
                    }
                }

                if (upgradeOptions.HasFlag(UpgradeOptions.Spells))
                    UpgradeItemSpells(loot);

                if (spellsToRemove != null) // Remove spells after upgrade
                {
                    foreach (var spell in spellsToRemove)
                        loot.Biota.TryRemoveKnownSpell(spell, loot.BiotaDatabaseLock);
                }

                if (spellsToAdd != null)
                {
                    foreach (var spell in spellsToAdd)
                        loot.Biota.GetOrAddKnownSpell(spell, loot.BiotaDatabaseLock, out _);
                }



                if (upgradeOptions.HasFlag(UpgradeOptions.DoubleMaxMana) && loot.ItemMaxMana.HasValue)
                    loot.ItemMaxMana = loot.ItemMaxMana * 2;

                if (upgradeOptions.HasFlag(UpgradeOptions.HalveManaRate) && loot.ManaRate.HasValue)
                    loot.ManaRate = loot.ManaRate / 2;


                if (upgradeOptions.HasFlag(UpgradeOptions.MakeKitsUnlimitedUse) && loot.ItemType == ItemType.Misc && loot.HealkitMod.HasValue && loot.Structure.HasValue && loot.MaxStructure.HasValue)
                {
                    loot.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Structure, -1);
                    loot.MaxStructure = null;
                    loot.UnlimitedUse = true;
                }


                if (upgradeOptions.HasFlag(UpgradeOptions.AddAllPerksToWeapons) && loot.ItemType == ItemType.MeleeWeapon)
                {
                    AddImbuedEffect(loot, ImbuedEffectType.CriticalStrike);
                    AddImbuedEffect(loot, ImbuedEffectType.CripplingBlow);
                    AddImbuedEffect(loot, ImbuedEffectType.ArmorRending);
                }


                if (upgradeOptions.HasFlag(UpgradeOptions.Bonded))
                    loot.Bonded = BondedStatus.Bonded;


                if (upgradeOptions != UpgradeOptions.None)
                    loot.Name = "Upgraded " + loot.Name;


                // Make sure the item is full of mana
                if (loot.ItemCurMana.HasValue)
                    loot.ItemCurMana = loot.ItemMaxMana;

                player.TryCreateInInventoryWithNetworking(loot);
            }
        }

        private static readonly Dictionary<int, int> SpellUpgradeMap = new Dictionary<int, int>
        {
            { 4911, 6102 }, // "Epic Armor"

            { 4678, 6085 }, // "Epic Slashing Ward"
            { 4677, 6084 }, // "Epic Piercing Ward"
            { 4674, 6081 }, // "Epic Bludgeoning Ward"
            { 4675, 6082 }, // "Epic Flame Ward"
            { 4676, 6083 }, // "Epic Frost Ward"
            { 4673, 6080 }, // "Epic Acid Ward"
            { 4679, 6079 }, // "Epic Storm Ward"


            { 3965, 6107 }, // "Epic Strength"
            { 4226, 6104 }, // "Epic Endurance"
            { 3963, 6103 }, // "Epic Coordination"
            { 4019, 6106 }, // "Epic Quickness"
            { 3964, 6105 }, // "Epic Focus"
            { 4227, 6101 }, // "Epic Willpower"

            { 4680, 6077 }, // "Epic Health Gain"
            { 4682, 6076 }, // "Epic Stamina Gain"
            { 4681, 6078 }, // "Epic Mana Gain"


            { 4696, 6055 }, // "Epic Invulnerability"
            { 4695, 6054 }, // "Epic Impregnability"
            { 4704, 6063 }, // "Epic Magic Resistance"

            { 4689, 6046 }, // "Epic Creature Enchantment Aptitude"
            { 4697, 6056 }, // "Epic Item Enchantment Aptitude"
            { 4700, 6060 }, // "Epic Life Magic Aptitude"

            { 4705, 6064 }, // "Epic Mana Conversion Prowess"

            { 4684, 6041 }, // "Epic Arcane Prowess"

            { 4699, 6058 }, // "Epic Jumping Prowess"
            { 4710, 6071 }, // "Epic Sprint"

            { 4686, 6043 }, // "Epic Light Weapon Aptitude"
            { 4702, 6043 }, // "Epic Light Weapon Aptitude"
            { 4709, 6043 }, // "Epic Light Weapon Aptitude"
            { 4711, 6043 }, // "Epic Light Weapon Aptitude"
            { 4714, 6043 }, // "Epic Light Weapon Aptitude"
            { 4687, 6044 }, // "Epic Missile Weapon Aptitude"
            { 4690, 6044 }, // "Epic Missile Weapon Aptitude"
            { 4713, 6044 }, // "Epic Missile Weapon Aptitude"
            { 4691, 6047 }, // "Epic Finesse Weapon Aptitude"
            { 5893, 6049 }, // "Epic Dirty Fighting Prowess"
            { 5894, 6050 }, // "Epic Dual Wield Aptitude"
            { 4694, 6053 }, // "Epic Healing Prowess"
            { 5895, 6067 }, // "Epic Recklessness Prowess"
            { 5896, 6069 }, // "Epic Shield Aptitude"
            { 5897, 6070 }, // "Epic Sneak Attack Prowess"
            { 4712, 6072 }, // "Epic Heavy Weapon Aptitude"
            { 5034, 6073 }, // "Epic Two Handed Combat Aptitude"
            { 5429, 6074 }, // "Epic Void Magic Aptitude"
            { 4715, 6075 }, // "Epic War Magic Aptitude"

            { 4692, 6051 }, // "Epic Fealty"
            { 4232, 6059 }, // "Epic Leadership"

            { 4020, 6048 }, // "Epic Deception Prowess"
            { 4706, 6065 }, // "Epic Monster Attunement"
            { 4707, 6066 }, // "Epic Person Attunement"

            { 4683, 6040 }, // "Epic Alchemical Prowess"
            { 4688, 6045 }, // "Epic Cooking Prowess"
            { 4693, 6052 }, // "Epic Fletching Prowess"
            { 4701, 6061 }, // "Epic Lockpick Prowess"
            { 4708, 6068 }, // "Epic Salvaging Aptitude"

            { 4912, 6039 }, // "Epic Weapon Tinkering Expertise"
            { 4685, 6042 }, // "Epic Armor Tinkering Expertise"
            { 4698, 6057 }, // "Epic Item Tinkering Expertise"
            { 4703, 6062 }, // "Epic Magic Item Tinkering Expertise"


            { 4667, 6095 }, // "Epic Impenetrability"
            { 4669, 6097 }, // "Epic Slashing Bane"
            { 4668, 6096 }, // "Epic Piercing Bane"
            { 4662, 6090 }, // "Epic Bludgeoning Bane"
            { 4664, 6092 }, // "Epic Flame Bane"
            { 4665, 6093 }, // "Epic Frost Bane"
            { 4660, 6088 }, // "Epic Acid Bane"
            { 4671, 6099 }, // "Epic Storm Bane"


            { 4661, 6089 }, // "Legendary Blood Thirst"
            { 4670, 6098 }, // "Legendary Spirit Thirst"
            { 4672, 6100 }, // "Legendary Swift Hunter"
            { 4666, 6094 }, // "Legendary Heart Thirst"
            { 4663, 6091 }, // "Legendary Defender"
            { 6086, 6087 }, // "Epic Hermetic Link"

        };

        private static void UpgradeItemSpells(WorldObject wo)
        {
            foreach (var spellUpgrade in SpellUpgradeMap)
            {
                if (wo.Biota.TryRemoveKnownSpell(spellUpgrade.Key, wo.BiotaDatabaseLock))
                    wo.Biota.GetOrAddKnownSpell(spellUpgrade.Value, wo.BiotaDatabaseLock, out _);
            }
        }

        private static void AddImbuedEffect(WorldObject wo, ImbuedEffectType imbuedEffectType)
        {
            if (!wo.HasImbuedEffect(imbuedEffectType))
            {
                if (!wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect).HasValue)
                    wo.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect, (int)imbuedEffectType);
                else if (!wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect2).HasValue)
                    wo.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect2, (int)imbuedEffectType);
                else if (!wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect3).HasValue)
                    wo.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect3, (int)imbuedEffectType);
                else if (!wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect4).HasValue)
                    wo.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect4, (int)imbuedEffectType);
                else if (!wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect5).HasValue)
                    wo.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbuedEffect5, (int)imbuedEffectType);
            }
        }
    }
}
