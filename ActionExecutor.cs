using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.TerrainFeatures;

namespace StardewAI
{
    public static class ActionExecutor
    {
        private static IMonitor Monitor;

        /// <summary>Human-readable summary of the most recent message + actions (for the OBS overlay).</summary>
        public static volatile string LastAction = "(none yet)";

        /// <summary>Item ids the AI may never give (matched against bare and qualified ids).</summary>
        private static HashSet<string> BlockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Init(IMonitor monitor, IEnumerable<string> blockedItemIds = null)
        {
            Monitor = monitor;
            BlockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (blockedItemIds != null)
            {
                foreach (var id in blockedItemIds)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        BlockedItems.Add(id.Trim());
                }
            }
        }

        public static void Execute(string aiResponseJson)
        {
            JObject response;
            try
            {
                response = JObject.Parse(aiResponseJson);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to parse AI response: {ex.Message}\nRaw: {aiResponseJson}", LogLevel.Error);
                return;
            }

            string message = response["message"]?.ToString();
            if (!string.IsNullOrEmpty(message))
            {
                Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
                // Also print inline into the native chat box so the conversation reads back.
                try { Game1.chatBox?.addInfoMessage($"AI: {message}"); }
                catch { /* chat box not ready */ }
            }

            string reasoning = response["reasoning"]?.ToString();
            if (!string.IsNullOrEmpty(reasoning))
            {
                Monitor.Log($"[AI Reasoning] {reasoning}", LogLevel.Info);
            }

            var actions = response["actions"] as JArray;
            if (actions == null) return;

            var executed = new List<string>();
            foreach (var action in actions)
            {
                string type = action["type"]?.ToString();
                try
                {
                    switch (type)
                    {
                        case "addItem": AddItem(action); break;
                        case "removeItem": RemoveItem(action); break;
                        case "setWeather": SetWeather(action); break;
                        case "setSeason": SetSeason(action); break;
                        case "addMoney": AddMoney(action); break;
                        case "setTime": SetTime(action); break;
                        case "setDay": SetDay(action); break;
                        case "warp": Warp(action); break;
                        case "setSkillXp": SetSkillXp(action); break;
                        case "setFriendship": SetFriendship(action); break;
                        case "waterAllCrops": WaterAllCrops(); break;
                        case "clearCrops": ClearCrops(); break;
                        case "assignTask": TaskRunner.Assign(action); break;
                        case "cancelTask": TaskRunner.Cancel(); break;
                        case "message": break;
                        default:
                            Monitor.Log($"Unknown action type: {type}", LogLevel.Warn);
                            break;
                    }
                    if (!string.IsNullOrEmpty(type) && type != "message")
                        executed.Add(type);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Failed to execute action '{type}': {ex.Message}", LogLevel.Error);
                }
            }

            // Update the OBS overlay snapshot.
            string actionsSummary = executed.Count > 0 ? string.Join(", ", executed) : "(no actions)";
            LastAction = string.IsNullOrEmpty(message)
                ? actionsSummary
                : $"{message}  [{actionsSummary}]";
        }

        private static void AddItem(JToken action)
        {
            string itemId = action["itemId"]?.ToString()?.Trim();
            int quantity = action["quantity"]?.Value<int>() ?? 1;

            if (string.IsNullOrEmpty(itemId)) return;
            quantity = Math.Clamp(quantity, 1, 999);

            // 1) Reject explicitly blocked items (matched against bare and qualified ids).
            if (IsBlocked(itemId))
            {
                Monitor.Log($"Refused blocked item '{itemId}'.", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage($"Item '{itemId}' is on your block list — skipped.", HUDMessage.error_type));
                return;
            }

            // 2) Validate the id resolves to a REAL item. This rejects hallucinated/broken ids
            //    that would otherwise produce an "Error Item" (the red-circle placeholder) and
            //    could end up dropped on the ground when the inventory is full.
            ItemMetadata metadata = ItemRegistry.GetMetadata(itemId);
            if (metadata == null || !metadata.Exists())
            {
                Monitor.Log($"Refused unknown/invalid item id '{itemId}'.", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage($"I don't recognize item '{itemId}', so I didn't spawn it.", HUDMessage.error_type));
                return;
            }

            // 3) Create with allowNull so malformed ids return null rather than an error item.
            Item item = ItemRegistry.Create(itemId, quantity, allowNull: true);
            if (item == null || IsErrorItem(item))
            {
                Monitor.Log($"Refused broken item created from id '{itemId}'.", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage($"Item '{itemId}' looked broken, so I skipped it.", HUDMessage.error_type));
                return;
            }

            Game1.player.addItemByMenuIfNecessary(item);
            Monitor.Log($"Added {quantity}x {item.DisplayName}", LogLevel.Info);
        }

        private static bool IsBlocked(string itemId)
        {
            if (BlockedItems.Count == 0) return false;
            if (BlockedItems.Contains(itemId)) return true;
            // Also compare against the fully-qualified form (e.g. "809" -> "(O)809").
            string qualified = ItemRegistry.QualifyItemId(itemId);
            return !string.IsNullOrEmpty(qualified) && BlockedItems.Contains(qualified);
        }

        private static bool IsErrorItem(Item item)
        {
            if (item == null) return true;
            if (string.Equals(item.Name, "Error Item", StringComparison.OrdinalIgnoreCase)) return true;
            string qid = item.QualifiedItemId ?? "";
            return qid.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RemoveItem(JToken action)
        {
            string itemName = action["itemName"]?.ToString()?.ToLower();
            if (string.IsNullOrEmpty(itemName)) return;

            foreach (var item in Game1.player.Items)
            {
                if (item != null && item.DisplayName.ToLower().Contains(itemName))
                {
                    Game1.player.removeItemFromInventory(item);
                    Monitor.Log($"Removed {item.DisplayName}", LogLevel.Info);
                    break;
                }
            }
        }

        private static void SetWeather(JToken action)
        {
            string weather = action["weather"]?.ToString()?.ToLower();

            bool rain = false, storm = false, snow = false;
            switch (weather)
            {
                case "rain": rain = true; break;
                case "storm": rain = true; storm = true; break;
                case "snow": snow = true; break;
                case "sunny": default: break;
            }

            // 1) Persist to the 1.6 location-context weather (source of truth across days/locations).
            var worldState = Game1.netWorldState.Value;
            var locWeather = worldState.GetWeatherForLocation(Game1.currentLocation.GetLocationContextId());
            locWeather.IsRaining = rain;
            locWeather.IsSnowing = snow;
            locWeather.IsLightning = storm;
            locWeather.IsDebrisWeather = false;

            // 2) Drive the LIVE renderer/audio via the legacy globals. Skipping this is what
            //    left the game in a half-state ("glitch"): data said rain, the scene never refreshed.
            Game1.isRaining = rain;
            Game1.isSnowing = snow;
            Game1.isLightning = storm;
            Game1.isDebrisWeather = false;

            // 3) Force a visual/audio refresh of the current scene.
            try
            {
                Game1.updateWeatherIcon();

                // Rebuild the on-screen weather particles so rain/snow actually appears now.
                if (Game1.IsRainingHere() || snow)
                    Game1.randomizeRainPositions();
                else
                    Game1.debrisWeather?.Clear();
            }
            catch (Exception ex)
            {
                Monitor.Log($"Weather refresh warning: {ex.Message}", LogLevel.Trace);
            }

            Monitor.Log($"Weather set to: {weather}", LogLevel.Info);
        }

        private static void SetSeason(JToken action)
        {
            string seasonStr = action["season"]?.ToString()?.ToLower();
            if (string.IsNullOrEmpty(seasonStr)) return;

            Season season;
            switch (seasonStr)
            {
                case "spring": season = Season.Spring; break;
                case "summer": season = Season.Summer; break;
                case "fall": season = Season.Fall; break;
                case "winter": season = Season.Winter; break;
                default: return;
            }

            Game1.season = season;
            Game1.setGraphicsForSeason();
            Monitor.Log($"Season set to: {season}", LogLevel.Info);
        }

        private static void AddMoney(JToken action)
        {
            int amount = action["amount"]?.Value<int>() ?? 0;
            Game1.player.Money = Math.Max(0, Game1.player.Money + amount);
            Monitor.Log($"Money adjusted by: {amount}", LogLevel.Info);
        }

        private static void SetTime(JToken action)
        {
            int time = action["time"]?.Value<int>() ?? 600;
            time = Math.Clamp(time, 600, 2600);
            time = time - (time % 10); // Align to 10 min increments
            Game1.timeOfDay = time;
            Monitor.Log($"Time set to: {time}", LogLevel.Info);
        }

        private static void SetDay(JToken action)
        {
            int day = action["day"]?.Value<int>() ?? 1;
            day = Math.Clamp(day, 1, 28);
            Game1.dayOfMonth = day;
            Monitor.Log($"Day set to: {day}", LogLevel.Info);
        }

        private static void Warp(JToken action)
        {
            string locationName = action["location"]?.ToString();
            if (string.IsNullOrEmpty(locationName)) return;

            var loc = Game1.getLocationFromName(locationName);
            if (loc != null)
            {
                // Try to find a sensible warp point or default to 10,10
                Game1.warpFarmer(locationName, 10, 10, false);
                Monitor.Log($"Warped to: {locationName}", LogLevel.Info);
            }
            else
            {
                Monitor.Log($"Unknown location: {locationName}", LogLevel.Warn);
            }
        }

        private static void SetSkillXp(JToken action)
        {
            string skillName = action["skill"]?.ToString()?.ToLower();
            int xp = action["xp"]?.Value<int>() ?? 0;
            
            if (string.IsNullOrEmpty(skillName) || xp <= 0) return;

            int skillIndex = -1;
            switch (skillName)
            {
                case "farming": skillIndex = 0; break;
                case "fishing": skillIndex = 1; break;
                case "foraging": skillIndex = 2; break;
                case "mining": skillIndex = 3; break;
                case "combat": skillIndex = 4; break;
            }

            if (skillIndex != -1)
            {
                Game1.player.gainExperience(skillIndex, xp);
                Monitor.Log($"Added {xp} XP to {skillName}", LogLevel.Info);
            }
        }

        private static void SetFriendship(JToken action)
        {
            string npc = action["npc"]?.ToString();
            int points = action["points"]?.Value<int>() ?? 0;

            if (string.IsNullOrEmpty(npc)) return;

            if (Game1.player.friendshipData.ContainsKey(npc))
            {
                Game1.player.friendshipData[npc].Points = Math.Max(0, Game1.player.friendshipData[npc].Points + points);
                Monitor.Log($"Friendship with {npc} adjusted by {points}", LogLevel.Info);
            }
            else
            {
                Monitor.Log($"NPC {npc} not found in friendship data", LogLevel.Warn);
            }
        }

        private static void WaterAllCrops()
        {
            int count = 0;
            var farm = Game1.getFarm();
            foreach (var pair in farm.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && dirt.state.Value == HoeDirt.dry)
                {
                    dirt.state.Value = HoeDirt.watered;
                    count++;
                }
            }
            Monitor.Log($"Watered {count} crops.", LogLevel.Info);
        }

        private static void ClearCrops()
        {
            var farm = Game1.getFarm();
            var toRemove = new List<Vector2>();

            foreach (var pair in farm.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null)
                {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (var key in toRemove)
            {
                if (farm.terrainFeatures[key] is HoeDirt dirt)
                    dirt.crop = null;
            }

            Monitor.Log($"Cleared {toRemove.Count} crops.", LogLevel.Info);
        }
    }
}
