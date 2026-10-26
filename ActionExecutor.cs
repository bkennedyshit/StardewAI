using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
                response = JObject.Parse(ExtractJson(aiResponseJson));
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to parse AI response: {ex.Message}\nRaw: {aiResponseJson}", LogLevel.Error);
                Game1.addHUDMessage(new HUDMessage("The AI reply didn't make sense, so nothing was changed. Try rephrasing.", HUDMessage.error_type));
                try { Game1.chatBox?.addErrorMessage("AI: couldn't understand the model's reply — nothing was changed."); }
                catch { /* chat box not ready */ }
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
                        case "setSkillLevel": SetSkillLevel(action); break;
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
            string itemName = (action["itemName"] ?? action["name"] ?? action["item"])?.ToString()?.Trim();
            string itemId = action["itemId"]?.ToString()?.Trim();
            int quantity = action["quantity"]?.Value<int>() ?? 1;
            quantity = Math.Clamp(quantity, 1, 999);

            // 0) Prefer the NAME. Small models are bad at recalling numeric ids (e.g. they answer
            //    "Prismatic Shard" with 499 = Ancient Seeds), but good at repeating the name the
            //    player typed. Resolve the name against the game's real item registry first.
            if (!string.IsNullOrEmpty(itemName))
            {
                string resolved = ItemResolver.Resolve(itemName, strict: false);
                if (resolved != null)
                {
                    if (!string.IsNullOrEmpty(itemId) && !string.Equals(ItemRegistry.QualifyItemId(itemId), resolved, StringComparison.OrdinalIgnoreCase))
                        Monitor.Log($"Model id '{itemId}' disagreed with name '{itemName}'; using name -> {resolved}.", LogLevel.Debug);
                    itemId = resolved;
                }
            }
            // The model sometimes puts a name in the id field ("itemId": "Diamond").
            else if (!string.IsNullOrEmpty(itemId) && !Regex.IsMatch(itemId, @"^(\(\w+\))?\d+$"))
            {
                itemId = ItemResolver.Resolve(itemId, strict: false) ?? itemId;
            }

            if (string.IsNullOrEmpty(itemId)) return;

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
            string itemName = (action["itemName"] ?? action["name"])?.ToString()?.ToLower();
            if (string.IsNullOrEmpty(itemName)) return;
            int? quantity = action["quantity"]?.Value<int>();

            foreach (var item in Game1.player.Items)
            {
                if (item != null && item.DisplayName.ToLower().Contains(itemName))
                {
                    if (quantity.HasValue && quantity.Value > 0 && quantity.Value < item.Stack)
                    {
                        item.Stack -= quantity.Value;
                        Monitor.Log($"Removed {quantity.Value}x {item.DisplayName}", LogLevel.Info);
                    }
                    else
                    {
                        Game1.player.removeItemFromInventory(item);
                        Monitor.Log($"Removed {item.DisplayName}", LogLevel.Info);
                    }
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
            string raw = action["location"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(raw)) return;

            string resolved = ResolveLocationName(raw);
            if (resolved == null)
            {
                Monitor.Log($"Unknown location: {raw}", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage($"I couldn't find a location called '{raw}', so I didn't warp you.", HUDMessage.error_type));
                return;
            }

            var loc = Game1.getLocationFromName(resolved);
            Point tile = GetSafeArrivalTile(loc);
            Game1.warpFarmer(resolved, tile.X, tile.Y, false);
            Monitor.Log($"Warped to: {resolved} ({tile.X},{tile.Y})", LogLevel.Info);
        }

        /// <summary>
        /// Stardew's <see cref="Game1.getLocationFromName"/> needs the exact internal name (e.g.
        /// "AnimalShop", "IslandSouth"), but the model tends to produce friendly names ("Marnie's
        /// Ranch", "Ginger Island"). This resolves common aliases, then falls back to a normalized
        /// match against the real internal name and all loaded locations.
        /// </summary>
        private static string ResolveLocationName(string input)
        {
            // 1) Already a valid internal name.
            if (Game1.getLocationFromName(input) != null)
                return input;

            // 2) Direct alias hit.
            if (LocationAliases.TryGetValue(input, out var mapped) && Game1.getLocationFromName(mapped) != null)
                return mapped;

            // 3) Normalized alias hit (ignore case, spaces, apostrophes, "the ").
            string norm = NormalizeName(input);
            foreach (var kv in LocationAliases)
            {
                if (NormalizeName(kv.Key) == norm && Game1.getLocationFromName(kv.Value) != null)
                    return kv.Value;
            }

            // 4) Normalized match against every loaded location's internal name.
            foreach (var loc in Game1.locations)
            {
                if (loc?.Name != null && NormalizeName(loc.Name) == norm)
                    return loc.Name;
            }

            return null;
        }

        private static string NormalizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            if (s.StartsWith("the ")) s = s.Substring(4);
            return new string(s.Where(char.IsLetterOrDigit).ToArray());
        }

        /// <summary>
        /// Pick a passable tile to land on that isn't a warp tile (so the player doesn't immediately
        /// bounce back out). Falls back to the map center, then (8,8), wrapped in try/catch.
        /// </summary>
        private static Point GetSafeArrivalTile(GameLocation loc)
        {
            try
            {
                int w = loc.map?.Layers?[0]?.LayerWidth ?? 40;
                int h = loc.map?.Layers?[0]?.LayerHeight ?? 40;

                var warpTiles = new HashSet<Point>();
                foreach (var warp in loc.warps)
                    warpTiles.Add(new Point(warp.X, warp.Y));

                bool IsGood(int x, int y)
                {
                    if (x <= 0 || y <= 0 || x >= w || y >= h) return false;
                    if (warpTiles.Contains(new Point(x, y))) return false;
                    return loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport);
                }

                foreach (var c in new[] { new Point(w / 2, h / 2), new Point(8, 8), new Point(10, 10) })
                    if (IsGood(c.X, c.Y)) return c;

                for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                        if (IsGood(x, y)) return new Point(x, y);

                return new Point(w / 2, h / 2);
            }
            catch
            {
                return new Point(8, 8);
            }
        }

        /// <summary>Friendly name -> Stardew internal location name.</summary>
        private static readonly Dictionary<string, string> LocationAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ginger island"] = "IslandSouth", ["island"] = "IslandSouth",
            ["marnie's ranch"] = "AnimalShop", ["marnies ranch"] = "AnimalShop", ["marnie's"] = "AnimalShop", ["ranch"] = "AnimalShop", ["animal shop"] = "AnimalShop",
            ["spa"] = "BathHouse_Pool", ["bathhouse"] = "BathHouse_Pool", ["bath house"] = "BathHouse_Pool",
            ["saloon"] = "Saloon", ["stardrop saloon"] = "Saloon", ["bar"] = "Saloon",
            ["pierre's"] = "SeedShop", ["pierres"] = "SeedShop", ["general store"] = "SeedShop", ["seed shop"] = "SeedShop",
            ["clinic"] = "Hospital", ["harvey's clinic"] = "Hospital",
            ["mayor's house"] = "ManorHouse", ["mayor's manor"] = "ManorHouse", ["manor"] = "ManorHouse",
            ["blacksmith"] = "Blacksmith", ["clint's"] = "Blacksmith",
            ["carpenter's shop"] = "ScienceHouse", ["carpenter"] = "ScienceHouse", ["robin's house"] = "ScienceHouse", ["science house"] = "ScienceHouse",
            ["wizard's tower"] = "WizardHouse", ["wizard tower"] = "WizardHouse", ["wizard's house"] = "WizardHouse",
            ["adventurer's guild"] = "AdventureGuild", ["adventure guild"] = "AdventureGuild", ["guild"] = "AdventureGuild",
            ["community center"] = "CommunityCenter", ["community centre"] = "CommunityCenter",
            ["joja"] = "JojaMart", ["joja mart"] = "JojaMart",
            ["movie theater"] = "MovieTheater", ["theater"] = "MovieTheater", ["theatre"] = "MovieTheater",
            ["casino"] = "Club",
            ["fish shop"] = "FishShop", ["willy's"] = "FishShop", ["fish market"] = "FishShop",
            ["oasis"] = "SandyHouse", ["sandy's shop"] = "SandyHouse",
            ["desert"] = "Desert", ["calico desert"] = "Desert",
            ["bus stop"] = "BusStop",
            ["the mines"] = "Mine", ["mines"] = "Mine", ["mine"] = "Mine",
            ["sewer"] = "Sewer", ["sewers"] = "Sewer",
            ["beach"] = "Beach",
            ["forest"] = "Forest", ["cindersap forest"] = "Forest",
            ["secret woods"] = "Woods", ["woods"] = "Woods",
            ["mountain"] = "Mountain",
            ["town"] = "Town", ["pelican town"] = "Town",
            ["railroad"] = "Railroad",
            ["farm"] = "Farm",
            ["farmhouse"] = "FarmHouse", ["farm house"] = "FarmHouse", ["home"] = "FarmHouse", ["house"] = "FarmHouse",
            ["greenhouse"] = "Greenhouse", ["green house"] = "Greenhouse"
        };

        // Vanilla XP thresholds for levels 0..10.
        private static readonly int[] LevelXp = { 0, 100, 380, 770, 1300, 2150, 3300, 4800, 6900, 10000, 15000 };
        private static readonly string[] SkillNames = { "farming", "fishing", "foraging", "mining", "combat" };

        /// <summary>Map a skill name to its index, or -2 for "all". -1 if unknown.</summary>
        private static int SkillIndex(string skillName)
        {
            switch (skillName)
            {
                case "farming": return 0;
                case "fishing": return 1;
                case "foraging": return 2;
                case "mining": return 3;
                case "combat": return 4;
                case "all": case "all skills": case "every": case "everything": case "skills": return -2;
                default: return -1;
            }
        }

        private static IEnumerable<int> SkillTargets(string skillName)
        {
            int idx = SkillIndex(skillName);
            if (idx == -2) return new[] { 0, 1, 2, 3, 4 };
            if (idx >= 0) return new[] { idx };
            return Array.Empty<int>();
        }

        private static void SetSkillXp(JToken action)
        {
            string skillName = action["skill"]?.ToString()?.ToLower()?.Trim();
            int xp = action["xp"]?.Value<int>() ?? 0;

            if (string.IsNullOrEmpty(skillName) || xp <= 0) return;

            foreach (int i in SkillTargets(skillName))
            {
                Game1.player.gainExperience(i, xp);
                Monitor.Log($"Added {xp} XP to {SkillNames[i]}", LogLevel.Info);
            }
        }

        /// <summary>
        /// Raise a skill (or "all") to an exact level by granting exactly the XP needed. Going
        /// through gainExperience keeps vanilla behavior: level-up + profession choice at bedtime.
        /// Never lowers a level.
        /// </summary>
        private static void SetSkillLevel(JToken action)
        {
            string skillName = action["skill"]?.ToString()?.ToLower()?.Trim() ?? "all";
            int level = Math.Clamp(action["level"]?.Value<int>() ?? 10, 0, 10);
            int target = LevelXp[level];

            var raised = new List<string>();
            foreach (int i in SkillTargets(skillName))
            {
                int current = Game1.player.experiencePoints[i];
                if (current < target)
                {
                    Game1.player.gainExperience(i, target - current);
                    raised.Add(SkillNames[i]);
                }
            }

            if (raised.Count > 0)
            {
                Monitor.Log($"Raised {string.Join(", ", raised)} to level {level}.", LogLevel.Info);
                Game1.addHUDMessage(new HUDMessage($"Skills reach level {level} — sleep to pick your professions.", HUDMessage.newQuest_type));
            }
            else
            {
                Monitor.Log($"setSkillLevel: nothing to raise for '{skillName}' to {level}.", LogLevel.Debug);
            }
        }

        private static void SetFriendship(JToken action)
        {
            string npc = action["npc"]?.ToString()?.Trim();
            int points = action["points"]?.Value<int>() ?? 0;

            if (string.IsNullOrEmpty(npc)) return;

            // Case-insensitive match against NPCs the player has met.
            string key = Game1.player.friendshipData.Keys
                .FirstOrDefault(k => string.Equals(k, npc, StringComparison.OrdinalIgnoreCase));

            if (key != null)
            {
                var f = Game1.player.friendshipData[key];
                // Vanilla caps: 8 hearts for a datable NPC you aren't dating (the game clamps the
                // rest away and shows the last 2 hearts locked), 10 for everyone else, 14 when
                // dating/engaged/married.
                bool romantic = f.IsDating() || f.IsEngaged() || f.IsMarried();
                bool datable = Game1.getCharacterFromName(key)?.datable.Value == true;
                int cap = romantic ? 3500 : datable ? 2000 : 2500;
                int? hearts = action["hearts"]?.Value<int>();
                int newPoints = hearts.HasValue ? hearts.Value * 250 : f.Points + points;
                if (newPoints > cap && datable && !romantic)
                    Game1.addHUDMessage(new HUDMessage($"{key} maxes at 8 hearts until you give them a bouquet.", HUDMessage.newQuest_type));
                f.Points = Math.Clamp(newPoints, 0, cap);
                Monitor.Log($"Friendship with {key} now {f.Points} points", LogLevel.Info);
            }
            else
            {
                Monitor.Log($"NPC {npc} not found in friendship data", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage($"You haven't met '{npc}' yet.", HUDMessage.error_type));
            }
        }

        /// <summary>True if a friendly or internal location name resolves (used by the instant parser).</summary>
        public static bool CanResolveLocation(string name) => ResolveLocationName(name) != null;

        /// <summary>Pull a JSON object out of a reply that may be wrapped in ``` fences or chatter.</summary>
        private static string ExtractJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            return start >= 0 && end > start ? raw.Substring(start, end - start + 1) : raw;
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
