using Newtonsoft.Json.Linq;

namespace StardewAI
{
    /// <summary>
    /// OpenAI-style "tools" schema mirroring the validated action set. Used when
    /// <see cref="ModConfig.UseFunctionCalling"/> is enabled as an alternative to the
    /// single-JSON-blob protocol. The endpoint returns tool_calls which AIBridge
    /// normalizes back into the same actions array the executor already understands.
    /// </summary>
    public static class ActionTools
    {
        public static readonly JArray Schema = Build();

        private static JArray Build()
        {
            return new JArray
            {
                Tool("addItem", "Give the player an item by id.", new JObject
                {
                    ["itemId"] = Prop("string", "Item id, e.g. '499'."),
                    ["quantity"] = Prop("integer", "How many to give.")
                }, "itemId"),

                Tool("removeItem", "Remove an item from the player's inventory by name.", new JObject
                {
                    ["itemName"] = Prop("string", "Display name (partial match).")
                }, "itemName"),

                Tool("setWeather", "Set the current weather.", new JObject
                {
                    ["weather"] = Enum("Weather to set.", "sunny", "rain", "storm", "snow")
                }, "weather"),

                Tool("setSeason", "Set the current season.", new JObject
                {
                    ["season"] = Enum("Season to set.", "spring", "summer", "fall", "winter")
                }, "season"),

                Tool("addMoney", "Add (or subtract) gold. Result is clamped to >= 0.", new JObject
                {
                    ["amount"] = Prop("integer", "Signed gold amount.")
                }, "amount"),

                Tool("setTime", "Set the time of day (600-2600, 10-min increments).", new JObject
                {
                    ["time"] = Prop("integer", "Time, e.g. 1300 for 1pm.")
                }, "time"),

                Tool("setDay", "Set the day of the month (1-28).", new JObject
                {
                    ["day"] = Prop("integer", "Day of month.")
                }, "day"),

                Tool("warp", "Warp the player to a named location.", new JObject
                {
                    ["location"] = Prop("string", "Location name, e.g. 'Saloon', 'Farm'.")
                }, "location"),

                Tool("setSkillXp", "Grant skill XP.", new JObject
                {
                    ["skill"] = Enum("Skill name.", "farming", "fishing", "foraging", "mining", "combat"),
                    ["xp"] = Prop("integer", "XP to add.")
                }, "skill", "xp"),

                Tool("setFriendship", "Adjust friendship points with an NPC.", new JObject
                {
                    ["npc"] = Prop("string", "NPC name."),
                    ["points"] = Prop("integer", "Signed friendship points.")
                }, "npc", "points"),

                Tool("waterAllCrops", "Water every dry crop on the farm.", new JObject()),

                Tool("clearCrops", "Remove all crops from the farm.", new JObject()),

                Tool("assignTask",
                    "Send a Junimo-style elf helper to work a bounded area of the farm tile-by-tile. Use for helper/farmhand/elf requests.",
                    new JObject
                    {
                        ["chore"] = Enum("Chore for the elf.", "water", "harvest", "refill"),
                        ["area"] = new JObject
                        {
                            ["type"] = "object",
                            ["description"] = "Bounded tile area. Omit for the whole area.",
                            ["properties"] = new JObject
                            {
                                ["x"] = Prop("integer", "Left tile."),
                                ["y"] = Prop("integer", "Top tile."),
                                ["w"] = Prop("integer", "Width in tiles."),
                                ["h"] = Prop("integer", "Height in tiles.")
                            }
                        },
                        ["repeat"] = Prop("boolean", "Keep repeating the chore over the area.")
                    }, "chore"),

                Tool("cancelTask", "Stop and remove the elf helper.", new JObject()),

                Tool("message", "Reply to the player without changing game state.", new JObject())
            };
        }

        private static JObject Tool(string name, string description, JObject properties, params string[] required)
        {
            var req = new JArray();
            foreach (var r in required)
                req.Add(r);

            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = req
                    }
                }
            };
        }

        private static JObject Prop(string type, string description)
        {
            return new JObject { ["type"] = type, ["description"] = description };
        }

        private static JObject Enum(string description, params string[] values)
        {
            var arr = new JArray();
            foreach (var v in values)
                arr.Add(v);
            return new JObject { ["type"] = "string", ["description"] = description, ["enum"] = arr };
        }
    }
}
