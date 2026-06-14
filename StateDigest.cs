using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Objects;

namespace StardewAI
{
    public class StateDigest
    {
        public WorldState World { get; set; }
        public PlayerState Player { get; set; }
        public FarmState Farm { get; set; }
    }

    public class WorldState
    {
        public string Season { get; set; }
        public int Day { get; set; }
        public int Year { get; set; }
        public string Weather { get; set; }
        public int TimeOfDay { get; set; }
    }

    public class PlayerState
    {
        public string Name { get; set; }
        public int Money { get; set; }
        public int Energy { get; set; }
        public int MaxEnergy { get; set; }
        public List<InventoryItem> TopInventory { get; set; }
    }

    public class InventoryItem
    {
        public string Name { get; set; }
        public int Quantity { get; set; }
        public int Quality { get; set; }
    }

    public class FarmState
    {
        public int TotalPlots { get; set; }
        public int OccupiedPlots { get; set; }
        public List<CropSummary> CropSummaries { get; set; }
    }

    public class CropSummary
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public int NeedWater { get; set; }
        public int ReadyToHarvest { get; set; }
    }

    public static class StateDigestBuilder
    {
        public static StateDigest Build()
        {
            var player = Game1.player;
            var farm = Game1.getFarm();

            return new StateDigest
            {
                World = BuildWorld(),
                Player = BuildPlayer(player),
                Farm = BuildFarm(farm)
            };
        }

        private static WorldState BuildWorld()
        {
            return new WorldState
            {
                Season = Game1.currentSeason,
                Day = Game1.dayOfMonth,
                Year = Game1.year,
                Weather = GetWeatherString(),
                TimeOfDay = Game1.timeOfDay
            };
        }

        private static string GetWeatherString()
        {
            if (Game1.isLightning) return "storming";
            if (Game1.isSnowing) return "snowing";
            if (Game1.isRaining) return "raining";
            return "sunny";
        }

        private static PlayerState BuildPlayer(Farmer player)
        {
            var inventory = new List<InventoryItem>();

            foreach (var item in player.Items)
            {
                if (item == null) continue;
                inventory.Add(new InventoryItem
                {
                    Name = item.DisplayName,
                    Quantity = item.Stack,
                    Quality = (item is StardewValley.Object obj) ? obj.Quality : 0
                });
            }

            // Cap to top 15 by value or just take first 15 for now
            var top15 = inventory.OrderByDescending(i => i.Quantity).Take(15).ToList();

            return new PlayerState
            {
                Name = player.Name,
                Money = player.Money,
                Energy = (int)player.Stamina,
                MaxEnergy = player.MaxStamina,
                TopInventory = top15
            };
        }

        private static FarmState BuildFarm(Farm farm)
        {
            int occupied = 0;
            int total = 0;
            var cropDict = new Dictionary<string, CropSummary>();

            foreach (var pair in farm.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt)
                {
                    total++;
                    if (dirt.crop != null)
                    {
                        occupied++;
                        string name = GetCropName(dirt.crop);
                        if (!cropDict.TryGetValue(name, out var summary))
                        {
                            summary = new CropSummary { Name = name };
                            cropDict[name] = summary;
                        }

                        summary.Count++;
                        if (dirt.state.Value == HoeDirt.dry)
                        {
                            summary.NeedWater++;
                        }
                        if (dirt.crop.fullyGrown.Value)
                        {
                            summary.ReadyToHarvest++;
                        }
                    }
                }
            }

            return new FarmState
            {
                TotalPlots = total,
                OccupiedPlots = occupied,
                CropSummaries = cropDict.Values.ToList()
            };
        }

        private static string GetCropName(Crop crop)
        {
            try
            {
                var data = Game1.objectData;
                string indexStr = crop.indexOfHarvest.Value.ToString();
                if (data.ContainsKey(indexStr))
                    return data[indexStr].DisplayName ?? indexStr;
            }
            catch { }
            return "Unknown Crop";
        }
    }
}
