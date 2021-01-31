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
                    AddWeeniesToInventory(session.Player, new List<uint> { 30511, 30514, 30515, 30517, 30519, 30522, 30524, 30526, 30529 }, null, UpgradeOptions.All);
                    // Leikotha's Tears
                    AddWeeniesToInventory(session.Player, new List<uint> { 30513, 30516, 30518, 30520, 30521, 30523, 30525, 30528 }, null, UpgradeOptions.All);
                    // Dusk
                    AddWeeniesToInventory(session.Player, new List<uint> { 30532, 30530 }, null, UpgradeOptions.All);
                    // Patriarch's Twilight
                    AddWeeniesToInventory(session.Player, new List<uint> { 30533, 30531 }, null, UpgradeOptions.All);
                    // Helm
                    AddWeeniesToInventory(session.Player, new List<uint> { 30512, 30527 }, null, UpgradeOptions.All);
                    // Gauntlets
                    AddWeeniesToInventory(session.Player, new List<uint> { 30510, 30534 }, null, UpgradeOptions.All);
                    // Boots
                    AddWeeniesToInventory(session.Player, new List<uint> { 30367, 30368, 30369 }, null, UpgradeOptions.All);
                    break;
                case "jewelry":
                    // Necklaces
                    AddWeeniesToInventory(session.Player, new List<uint> { 30357, 30358, 30359 }, null, UpgradeOptions.All);
                    // Bracelets
                    AddWeeniesToInventory(session.Player, new List<uint> { 30352, 30353, 30354, 30355, 30356, 30366 }, null, UpgradeOptions.All);
                    // Rings
                    AddWeeniesToInventory(session.Player, new List<uint> { 30360, 30361, 30362, 30363, 30364, 30365 }, null, UpgradeOptions.All);
                    break;
                case "heavy":
                    AddWeeniesToInventory(session.Player, new List<uint> { 30310, 30314, 30315, 30320, 30324, 30328, 30329, 30333, 30338, 30342, 30343 }, null, UpgradeOptions.All);
                    break;
                case "2h":
                    AddWeeniesToInventory(session.Player, new List<uint> { 42662, 42663, 42664, 42665, 42666 }, null, UpgradeOptions.All);
                    break;
                case "wand":
                    AddWeeniesToInventory(session.Player, new List<uint> {30374, 30375, 30376, 30377, 30378, 43848}, null, UpgradeOptions.All);
                    break;
                case "shield":
                    AddWeeniesToInventory(session.Player, new List<uint> {30370, 30371, 30372, 30373}, null, UpgradeOptions.All);
                    break;
                case "mag":
                    // Gelidite
                    AddWeeniesToInventory(session.Player, new List<uint> {30511, 30514, 30515, 30517, 30519, 30522, 30524, 30526, 30529}, null, UpgradeOptions.All);

                    // braceletrareelementalharmony
                    // Add:
                    // "Legendary Slashing Ward"
                    // "Legendary Piercing Ward"
                    // "Legendary Bludgeoning Ward"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30354 }, null, UpgradeOptions.All, null, new List<int> { 6085, 6084, 6081 });

                    // necklaceraregoldensnake
                    // Add:
                    // "Incantation of Creature Enchantment Mastery Self"
                    // "Legendary Creature Enchantment Aptitude"
                    // "Incantation of Life Magic Mastery Self"
                    // "Legendary Life Magic Aptitude"
                    AddWeeniesToInventory(session.Player, new List<uint> { 30357 }, null, UpgradeOptions.All, null, new List<int> { 4530, 6046, 4582, 6060 });

                    // Infinite mana stone
                    AddWeeniesToInventory(session.Player, new List<uint> { 30254 }, null, UpgradeOptions.All);

                    break;
            }
        }

        [Flags]
        enum UpgradeOptions
        {
            None                = 0x0000,
            Spells              = 0x0001,
            Bonded              = 0x0002,
            DoubleMaxMana       = 0x0004,
            HalveManaRate       = 0x0008,

            All                 = 0xFFFF,
        }

        private static void AddWeeniesToInventory(Player player, IEnumerable<uint> weenieIds, ushort? stackSize = null, UpgradeOptions upgradeOptions = UpgradeOptions.None, IEnumerable<int> spellsToRemove = null, IEnumerable<int> spellsToAdd = null)
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


                if (upgradeOptions.HasFlag(UpgradeOptions.Spells))
                    UpgradeItemSpells(loot);


                if (upgradeOptions.HasFlag(UpgradeOptions.Bonded))
                    loot.Bonded = BondedStatus.Bonded;


                if (upgradeOptions.HasFlag(UpgradeOptions.DoubleMaxMana) && loot.ItemMaxMana.HasValue)
                    loot.ItemMaxMana = loot.ItemMaxMana * 2;

                if (upgradeOptions.HasFlag(UpgradeOptions.HalveManaRate) && loot.ManaRate.HasValue)
                    loot.ManaRate = loot.ManaRate / 2;


                if (spellsToRemove != null)
                {
                    foreach (var spell in spellsToRemove)
                        loot.Biota.TryRemoveKnownSpell(spell, loot.BiotaDatabaseLock);
                }

                if (spellsToAdd != null)
                {
                    foreach (var spell in spellsToAdd)
                        loot.Biota.GetOrAddKnownSpell(spell, loot.BiotaDatabaseLock, out _);
                }


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
    }
}
