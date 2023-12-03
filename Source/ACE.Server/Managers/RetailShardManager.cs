using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using ACE.Database;
using ACE.Database.Entity;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Managers
{
    public static class RetailShardManager
    {
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

            optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

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
    }
}
