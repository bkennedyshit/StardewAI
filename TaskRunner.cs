using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace StardewAI
{
    /// <summary>
    /// Deterministic "Player 2" elf laborer (phase 2). The LLM fires ONCE to assign a bounded
    /// chore via the <c>assignTask</c> action; this runner then executes it tick-by-tick on the
    /// main game thread, Junimo-style, with NO LLM calls in the loop. A helper sprite walks the
    /// field and performs the chore (water / harvest / refill) over a bounded tile area.
    /// </summary>
    public static class TaskRunner
    {
        private enum Chore { Water, Harvest, Refill }

        private static IMonitor Monitor;

        // Active assignment
        private static bool Active;
        private static Chore CurrentChore;
        private static bool Repeat;
        private static Rectangle? Area;            // null => whole location
        private static GameLocation Location;

        // Work list (tiles still needing the chore this pass)
        private static readonly List<Vector2> Pending = new List<Vector2>();

        // Waypoints (pixel positions) from the elf to the current target, from BFS pathfinding.
        private static readonly Queue<Vector2> Path = new Queue<Vector2>();
        private const int MaxPathNodes = 8000;

        // Helper sprite + movement state
        private static TemporaryAnimatedSprite Helper;
        private static Vector2 HelperPixel;        // current world-pixel position (top-left)
        private static Vector2? TargetPixel;        // pixel position of the tile being worked
        private static Vector2 TargetTile;          // tile currently targeted

        private const float MoveSpeed = 8f;         // pixels per tick
        private const float ArriveThreshold = 6f;   // pixels
        private const int AnimFrames = 4;

        public static bool IsActive => Active;

        public static void Init(IMonitor monitor)
        {
            Monitor = monitor;
        }

        /// <summary>Parse and start an assignTask action. Must be called on the main thread.</summary>
        public static void Assign(JToken action)
        {
            string choreStr = action["chore"]?.ToString()?.ToLowerInvariant();
            Chore chore;
            switch (choreStr)
            {
                case "water": chore = Chore.Water; break;
                case "harvest": chore = Chore.Harvest; break;
                case "refill": chore = Chore.Refill; break;
                default:
                    Monitor?.Log($"assignTask: unknown chore '{choreStr}' (use water|harvest|refill).", LogLevel.Warn);
                    return;
            }

            bool repeat = action["repeat"]?.Value<bool>() ?? false;
            Rectangle? area = ParseArea(action["area"]);

            var location = Game1.currentLocation ?? Game1.getFarm();
            if (location == null)
            {
                Monitor?.Log("assignTask: no valid location to work.", LogLevel.Warn);
                return;
            }

            Cancel(); // clear any prior assignment + sprite

            CurrentChore = chore;
            Repeat = repeat;
            Area = area;
            Location = location;
            Active = true;

            BuildWorkList();
            SpawnHelper();

            string areaDesc = area.HasValue
                ? $"a {area.Value.Width}x{area.Value.Height} area at ({area.Value.X},{area.Value.Y})"
                : "the whole area";
            Game1.addHUDMessage(new HUDMessage($"Elf assigned: {chore} over {areaDesc}.", HUDMessage.newQuest_type));
            Monitor?.Log($"Elf assigned chore={chore} repeat={repeat} tiles={Pending.Count}", LogLevel.Info);

            if (Pending.Count == 0 && !Repeat)
            {
                Game1.addHUDMessage(new HUDMessage("Elf found nothing to do here.", HUDMessage.error_type));
                Cancel();
            }
        }

        /// <summary>Cancel the active assignment and despawn the helper.</summary>
        public static void Cancel()
        {
            Active = false;
            Repeat = false;
            Area = null;
            Pending.Clear();
            Path.Clear();
            TargetPixel = null;
            DespawnHelper();
            Location = null;
        }

        /// <summary>Drive the elf one tick. Called from GameLoop.UpdateTicked on the main thread.</summary>
        public static void Update()
        {
            if (!Active || Location == null)
                return;

            // Only act while the world is loaded and the elf's location still exists.
            if (!Context.IsWorldReady)
                return;

            // Need a target? Pull the next tile that still needs work.
            if (TargetPixel == null)
            {
                if (!DequeueValidTarget())
                {
                    // Pass complete.
                    if (Repeat)
                    {
                        BuildWorkList();
                        if (Pending.Count == 0)
                            return; // nothing to do this pass; wait for next (e.g. crops regrow/dry)
                        return;
                    }

                    Game1.addHUDMessage(new HUDMessage($"Elf finished {CurrentChore}.", HUDMessage.newQuest_type));
                    Monitor?.Log($"Elf finished chore {CurrentChore}.", LogLevel.Info);
                    Cancel();
                    return;
                }
            }

            // Walk toward the next waypoint (or straight to the target if no path was found).
            Vector2 dest = Path.Count > 0 ? Path.Peek() : TargetPixel.Value;
            Vector2 delta = dest - HelperPixel;
            float dist = delta.Length();

            if (dist > ArriveThreshold)
            {
                Vector2 step = delta;
                step.Normalize();
                HelperPixel += step * Math.Min(MoveSpeed, dist);
                UpdateHelperSprite(step.X);
                return;
            }

            if (Path.Count > 0)
            {
                Path.Dequeue();
                if (Path.Count > 0)
                    return; // more waypoints to go
            }

            // Arrived — perform the chore on this tile, then clear the target.
            PerformChoreOnTile(TargetTile);
            TargetPixel = null;
        }

        // ----- work list -----

        private static void BuildWorkList()
        {
            Pending.Clear();
            if (Location == null)
                return;

            foreach (var pair in Location.terrainFeatures.Pairs)
            {
                Vector2 tile = pair.Key;
                if (Area.HasValue && !Area.Value.Contains((int)tile.X, (int)tile.Y))
                    continue;

                if (TileNeedsWork(pair.Value))
                    Pending.Add(tile);
            }
        }

        private static bool TileNeedsWork(TerrainFeature feature)
        {
            if (CurrentChore == Chore.Refill)
                return false; // refill is a one-shot, handled at assign-completion below

            if (feature is HoeDirt dirt)
            {
                switch (CurrentChore)
                {
                    case Chore.Water:
                        return dirt.crop != null && dirt.state.Value == HoeDirt.dry;
                    case Chore.Harvest:
                        return dirt.crop != null && IsHarvestable(dirt.crop);
                }
            }
            return false;
        }

        private static bool DequeueValidTarget()
        {
            // Refill has no tiles: do it immediately and finish the pass.
            if (CurrentChore == Chore.Refill)
            {
                RefillWateringCan();
                return false;
            }

            while (Pending.Count > 0)
            {
                // Nearest-first so the elf sweeps the field naturally instead of zig-zagging.
                Vector2 here = HelperTile();
                int best = 0;
                float bestDist = float.MaxValue;
                for (int i = 0; i < Pending.Count; i++)
                {
                    float d = Vector2.DistanceSquared(here, Pending[i]);
                    if (d < bestDist) { bestDist = d; best = i; }
                }

                Vector2 tile = Pending[best];
                Pending.RemoveAt(best);

                if (Location.terrainFeatures.TryGetValue(tile, out var feature) && TileNeedsWork(feature))
                {
                    TargetTile = tile;
                    TargetPixel = new Vector2(tile.X * 64f, tile.Y * 64f);
                    BuildPath(here, tile);
                    return true;
                }
            }
            return false;
        }

        private static Vector2 HelperTile() =>
            new Vector2((int)((HelperPixel.X + 32f) / 64f), (int)((HelperPixel.Y + 32f) / 64f));

        /// <summary>
        /// Breadth-first search over walkable tiles from the elf to the target. Fills <see cref="Path"/>
        /// with pixel waypoints. If no path exists (or the search is too large), Path stays empty and
        /// the elf hops straight there — the same as a vanilla Junimo would look.
        /// </summary>
        private static void BuildPath(Vector2 start, Vector2 goal)
        {
            Path.Clear();
            if (start == goal || Location == null)
                return;

            var cameFrom = new Dictionary<Vector2, Vector2>();
            var frontier = new Queue<Vector2>();
            frontier.Enqueue(start);
            cameFrom[start] = start;

            var dirs = new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) };
            bool found = false;

            while (frontier.Count > 0 && cameFrom.Count < MaxPathNodes)
            {
                var cur = frontier.Dequeue();
                if (cur == goal) { found = true; break; }

                foreach (var d in dirs)
                {
                    var next = cur + d;
                    if (cameFrom.ContainsKey(next))
                        continue;
                    if (next != goal && !IsWalkable(next))
                        continue;
                    cameFrom[next] = cur;
                    frontier.Enqueue(next);
                }
            }

            if (!found)
                return;

            var tiles = new List<Vector2>();
            for (var t = goal; t != start; t = cameFrom[t])
                tiles.Add(t);
            tiles.Reverse();
            foreach (var t in tiles)
                Path.Enqueue(new Vector2(t.X * 64f, t.Y * 64f));
        }

        private static bool IsWalkable(Vector2 tile)
        {
            try
            {
                var loc = Location;
                int w = loc.map?.Layers?[0]?.LayerWidth ?? 0;
                int h = loc.map?.Layers?[0]?.LayerHeight ?? 0;
                if (tile.X < 0 || tile.Y < 0 || tile.X >= w || tile.Y >= h)
                    return false;

                if (!loc.isTilePassable(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport))
                    return false;

                if (loc.objects.TryGetValue(tile, out var obj) && !obj.isPassable())
                    return false;

                if (loc.terrainFeatures.TryGetValue(tile, out var tf) && !tf.isPassable())
                    return false;

                foreach (var b in loc.buildings)
                    if (b.occupiesTile(tile))
                        return false;

                foreach (var rc in loc.resourceClumps)
                    if (rc.occupiesTile((int)tile.X, (int)tile.Y))
                        return false;

                var rect = new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64);
                foreach (var ltf in loc.largeTerrainFeatures)
                    if (ltf.getBoundingBox().Intersects(rect))
                        return false;

                return true;
            }
            catch
            {
                return true; // never let pathfinding break the elf
            }
        }

        private static void PerformChoreOnTile(Vector2 tile)
        {
            try
            {
                if (!Location.terrainFeatures.TryGetValue(tile, out var feature) || !(feature is HoeDirt dirt))
                    return;

                switch (CurrentChore)
                {
                    case Chore.Water:
                        if (dirt.crop != null && dirt.state.Value == HoeDirt.dry)
                        {
                            dirt.state.Value = HoeDirt.watered;
                            Location.playSound("slosh");
                        }
                        break;

                    case Chore.Harvest:
                        if (dirt.crop != null && IsHarvestable(dirt.crop))
                        {
                            var crop = dirt.crop;
                            if (crop.harvest((int)tile.X, (int)tile.Y, dirt))
                            {
                                // Single-harvest crop consumed → clear the dirt.
                                dirt.crop = null;
                            }
                            Location.playSound("harvest");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Monitor?.Log($"Elf chore error at {tile}: {ex.Message}", LogLevel.Trace);
            }
        }

        private static bool IsHarvestable(Crop crop)
        {
            if (crop == null || crop.dead.Value)
                return false;
            // Standard SDV "ready for harvest" test.
            return crop.currentPhase.Value >= crop.phaseDays.Count - 1
                   && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
        }

        private static void RefillWateringCan()
        {
            int refilled = 0;
            foreach (var item in Game1.player.Items)
            {
                if (item is WateringCan can)
                {
                    can.WaterLeft = can.waterCanMax;
                    refilled++;
                }
            }
            if (refilled > 0)
            {
                Game1.addHUDMessage(new HUDMessage("Elf refilled your watering can.", HUDMessage.newQuest_type));
                Monitor?.Log($"Elf refilled {refilled} watering can(s).", LogLevel.Info);
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage("Elf found no watering can to refill.", HUDMessage.error_type));
            }
        }

        // ----- helper sprite -----

        private static void SpawnHelper()
        {
            DespawnHelper();
            if (Location == null)
                return;

            // Start the elf near the player so it visibly walks out to the field.
            HelperPixel = Game1.player != null
                ? new Vector2(Game1.player.Position.X, Game1.player.Position.Y)
                : Vector2.Zero;

            Helper = new TemporaryAnimatedSprite(
                textureName: "Characters\\Junimo",
                sourceRect: new Rectangle(0, 0, 16, 16),
                animationInterval: 100f,
                animationLength: AnimFrames,
                numberOfLoops: 99999,
                position: HelperPixel,
                flicker: false,
                flipped: false)
            {
                scale = 4f,
                layerDepth = 1f,
                totalNumberOfLoops = 99999,
                interval = 100f,
                // Junimo green so it reads clearly as a little helper.
                color = Color.LimeGreen
            };

            Location.temporarySprites.Add(Helper);
        }

        private static void UpdateHelperSprite(float dirX)
        {
            if (Helper == null)
                return;
            Helper.position = HelperPixel;
            Helper.flipped = dirX < 0f;
            // Keep it drawing above the ground/crops it stands on.
            Helper.layerDepth = Math.Max(0.001f, (HelperPixel.Y + 64f) / 10000f);
        }

        private static void DespawnHelper()
        {
            if (Helper != null && Location != null)
                Location.temporarySprites.Remove(Helper);
            Helper = null;
        }

        // ----- parsing -----

        private static Rectangle? ParseArea(JToken areaToken)
        {
            if (areaToken == null)
                return null;

            // "farm" / "all" / "here" => whole location (null = unbounded scan)
            if (areaToken.Type == JTokenType.String)
            {
                string s = areaToken.ToString().ToLowerInvariant();
                if (s == "farm" || s == "all" || s == "here" || s == "everywhere")
                    return null;
                return null;
            }

            if (areaToken is JObject obj)
            {
                int x = obj["x"]?.Value<int>() ?? 0;
                int y = obj["y"]?.Value<int>() ?? 0;
                int w = obj["w"]?.Value<int>() ?? obj["width"]?.Value<int>() ?? 0;
                int h = obj["h"]?.Value<int>() ?? obj["height"]?.Value<int>() ?? 0;
                if (w > 0 && h > 0)
                    return new Rectangle(x, y, w, h);
            }
            return null;
        }
    }
}
