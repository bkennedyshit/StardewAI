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
                Tool("addItem", "Give the player an item by its English name (the mod looks up the real item).", new JObject
                {
                    ["itemName"] = Prop("string", "Item name exactly as the player said it, e.g. 'Prismatic Shard'."),
                    ["quantity"] = Prop("integer", "How many to give (1-999).")
                }, "itemName"),

                Tool("removeItem", "Remove an item from the player's inventory by name.", new JObject
                {
                    ["itemName"] = Prop("string", "Display name (partial match)."),
                    ["quantity"] = Prop("integer", "How many to remove. Omit to remove the whole stack.")
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

                Tool("setSkillLevel", "Raise a skill (or all skills) to an exact level. Use for 'level 10' / 'max my skills'.", new JObject
                {
                    ["skill"] = Enum("Skill name, or 'all' for every skill.", "farming", "fishing", "foraging", "mining", "combat", "all"),
                    ["level"] = Prop("integer", "Target level 0-10.")
                }, "skill", "level"),

                Tool("setSkillXp", "Grant a specific amount of skill XP.", new JObject
                {
                    ["skill"] = Enum("Skill name, or 'all'.", "farming", "fishing", "foraging", "mining", "combat", "all"),
                    ["xp"] = Prop("integer", "XP to add.")
                }, "skill", "xp"),

                Tool("setFriendship", "Set friendship with an NPC to a number of hearts, or add points.", new JObject
                {
                    ["npc"] = Prop("string", "NPC name."),
                    ["hearts"] = Prop("integer", "Set to this many hearts (0-10)."),
                    ["points"] = Prop("integer", "Signed points to add instead (250 = 1 heart).")
                }, "npc"),

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
