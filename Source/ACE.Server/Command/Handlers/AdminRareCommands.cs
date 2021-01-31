using System;
using System.Collections.Generic;
using System.Text;

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
                    AddWeeniesToInventory(session.Player, new List<uint> { 30511, 30514, 30515, 30517, 30519, 30522, 30524, 30526, 30529 }, null, true, true);
                    // Leikotha's Tears
                    AddWeeniesToInventory(session.Player, new List<uint> { 30513, 30516, 30518, 30520, 30521, 30523, 30525, 30528 }, null, true, true);
                    // Dusk
                    AddWeeniesToInventory(session.Player, new List<uint> { 30532, 30530 }, null, true, true);
                    // Patriarch's Twilight
                    AddWeeniesToInventory(session.Player, new List<uint> { 30533, 30531 }, null, true, true);
                    // Helm
                    AddWeeniesToInventory(session.Player, new List<uint> { 30512, 30527 }, null, true, true);
                    // Gauntlets
                    AddWeeniesToInventory(session.Player, new List<uint> { 30510, 30534 }, null, true, true);
                    // Boots
                    AddWeeniesToInventory(session.Player, new List<uint> { 30367, 30368, 30369 }, null, true, true);
                    break;
                case "jewelry":
                    // Necklaces
                    AddWeeniesToInventory(session.Player, new List<uint> { 30357, 30358, 30359 }, null, true, true);
                    // Bracelets
                    AddWeeniesToInventory(session.Player, new List<uint> { 30352, 30353, 30354, 30355, 30356, 30366 }, null, true, true);
                    // Rings
                    AddWeeniesToInventory(session.Player, new List<uint> { 30360, 30361, 30362, 30363, 30364, 30365 }, null, true, true);
                    break;
                case "heavy":
                    AddWeeniesToInventory(session.Player, new List<uint> { 30310, 30314, 30315, 30320, 30324, 30328, 30329, 30333, 30338, 30342, 30343 }, null, true, true);
                    break;
                case "2h":
                    AddWeeniesToInventory(session.Player, new List<uint> { 42662, 42663, 42664, 42665, 42666 }, null, true, true);
                    break;
                case "wand":
                    AddWeeniesToInventory(session.Player, new List<uint> { 30374, 30375, 30376, 30377, 30378, 43848 }, null, true, true);
                    break;
                case "shield":
                    AddWeeniesToInventory(session.Player, new List<uint> { 30370, 30371, 30372, 30373 }, null, true, true);
                    break;
                case "mag":
                    // Gelidite
                    AddWeeniesToInventory(session.Player, new List<uint> { 30511, 30514, 30515, 30517, 30519, 30522, 30524, 30526, 30529 }, null, true, true);

                    // braceletrareelementalharmony
                    var loot = WorldObjectFactory.CreateNewWorldObject(30354);
                    if (loot != null) // weenie doesn't exist
                    {
                        // Make sure the item is full of mana
                        if (loot.ItemCurMana.HasValue)
                            loot.ItemCurMana = loot.ItemMaxMana;

                        UpgradeItemSpells(loot);
                        loot.Name = "Upgraded " + loot.Name;

                        loot.Bonded = BondedStatus.Bonded;

                        // Add extra spells
                        loot.Biota.GetOrAddKnownSpell(6085, loot.BiotaDatabaseLock, out _); // "Legendary Slashing Ward"
                        loot.Biota.GetOrAddKnownSpell(6084, loot.BiotaDatabaseLock, out _); // "Legendary Piercing Ward"
                        loot.Biota.GetOrAddKnownSpell(6081, loot.BiotaDatabaseLock, out _); // "Legendary Bludgeoning Ward"

                        session.Player.TryCreateInInventoryWithNetworking(loot);
                    }

                    // necklaceraregoldensnake
                    loot = WorldObjectFactory.CreateNewWorldObject(30357);
                    if (loot != null) // weenie doesn't exist
                    {
                        // Make sure the item is full of mana
                        if (loot.ItemCurMana.HasValue)
                            loot.ItemCurMana = loot.ItemMaxMana;

                        UpgradeItemSpells(loot);
                        loot.Name = "Upgraded " + loot.Name;

                        loot.Bonded = BondedStatus.Bonded;

                        // Add extra spells
                        loot.Biota.GetOrAddKnownSpell(4530, loot.BiotaDatabaseLock, out _); // "Incantation of Creature Enchantment Mastery Self"
                        loot.Biota.GetOrAddKnownSpell(6046, loot.BiotaDatabaseLock, out _); // "Legendary Creature Enchantment Aptitude"
                        loot.Biota.GetOrAddKnownSpell(4582, loot.BiotaDatabaseLock, out _); // "Incantation of Life Magic Mastery Self"
                        loot.Biota.GetOrAddKnownSpell(6060, loot.BiotaDatabaseLock, out _); // "Legendary Life Magic Aptitude"

                        session.Player.TryCreateInInventoryWithNetworking(loot);
                    }

                    break;
            }
        }

        private static void AddWeeniesToInventory(Player player, IEnumerable<uint> weenieIds, ushort? stackSize = null, bool upgradeSpells = false, bool bondItem = false)
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

                if (upgradeSpells)
                {
                    if (UpgradeItemSpells(loot))
                        loot.Name = "Upgraded " + loot.Name;
                }

                if (bondItem)
                    loot.Bonded = BondedStatus.Bonded;

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

        private static bool UpgradeItemSpells(WorldObject wo)
        {
            bool upgraded = false;

            foreach (var spellUpgrade in SpellUpgradeMap)
            {
                if (wo.Biota.TryRemoveKnownSpell(spellUpgrade.Key, wo.BiotaDatabaseLock))
                {
                    wo.Biota.GetOrAddKnownSpell(spellUpgrade.Value, wo.BiotaDatabaseLock, out _);
                    upgraded = true;
                }
            }

            return upgraded;
        }
    }
}
