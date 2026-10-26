using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace StardewAI
{
    /// <summary>
    /// Instant, deterministic parser for the most common requests ("give me 10 prismatic shards",
    /// "max all my skills", "make it rain", "warp to the beach", "give me 50k").
    ///
    /// If EVERY clause of a message is understood here, the actions run immediately with no LLM
    /// call at all — zero latency and zero CPU/GPU load. Anything it isn't sure about falls through
    /// to the model unchanged. Must be called on the main thread (item lookup reads game data).
    /// </summary>
    public static class QuickCommands
    {
        private static readonly Regex Splitter = new Regex(@"\s*(?:,(?!\d{3})|;|\band then\b|\bthen\b|\band\b|&|\+)\s*", RegexOptions.IgnoreCase);

        private static readonly string[] Skills = { "farming", "fishing", "foraging", "mining", "combat" };

        /// <summary>Try to fully handle the message. Returns executor JSON, or null to defer to the LLM.</summary>
        public static string? TryParse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string cleaned = Polite(text);
            var clauses = Splitter.Split(cleaned).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (clauses.Count == 0)
                return null;

            var actions = new JArray();
            var parts = new List<string>();
            string? lastVerb = null; // lets "give me 10 shards and 5 diamonds" carry "give" into clause 2

            foreach (var raw in clauses)
            {
                string clause = raw;
                if (lastVerb != null && Regex.IsMatch(clause, @"^\d"))
                    clause = lastVerb + " " + clause;

                if (!TryClause(clause, actions, parts, out string? verb))
                    return null; // not confident about this clause -> let the model handle the whole message
                lastVerb = verb;
            }

            if (actions.Count == 0)
                return null;

            return new JObject
            {
                ["reasoning"] = "Handled instantly by the built-in command parser (no model call).",
                ["message"] = "Done: " + string.Join(", ", parts) + ".",
                ["actions"] = actions
            }.ToString();
        }

        private static bool TryClause(string c, JArray actions, List<string> parts, out string? verb)
        {
            verb = null;
            string s = c.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');

            // ---------- skills ----------
            // "max all skills", "all skills to level 10", "level 10 farming", "max my fishing", "set mining to 8"
            var skillMatch = Regex.Match(s,
                @"^(?:max(?:imize|imise)?|set|make|level up|raise|boost|up|put|get)?\s*(?:out\s+)?(?:all\s+)?(?:of\s+)?(?:my\s+)?(?<skill>all skills|every skill|skills|farming|fishing|foraging|mining|combat)(?:\s+skill)?(?:\s+(?:levels?|lvl))?(?:\s+(?:to|at))?\s*(?:level|lvl)?\s*(?<lvl>\d{1,2}|max(?:imum)?|ten)?(?:\s+out)?$");
            if (!skillMatch.Success)
                skillMatch = Regex.Match(s,
                    @"^(?:set|make|get|put)?\s*(?:me\s+)?(?:to\s+)?(?:level|lvl)\s*(?<lvl>\d{1,2})\s+(?:in\s+|on\s+)?(?:all\s+)?(?:my\s+)?(?<skill>all skills|every skill|skills|farming|fishing|foraging|mining|combat)(?:\s+skills?)?$");
            if (skillMatch.Success && (s.Contains("max") || skillMatch.Groups["lvl"].Success))
            {
                string skill = skillMatch.Groups["skill"].Value;
                if (skill.Contains("skill") && !Skills.Contains(skill)) skill = "all";
                int level = 10;
                string lvl = skillMatch.Groups["lvl"].Value;
                if (lvl.Length > 0 && int.TryParse(lvl, out int parsed)) level = parsed;
                if (level < 0 || level > 10) return false;

                actions.Add(new JObject { ["type"] = "setSkillLevel", ["skill"] = skill, ["level"] = level });
                parts.Add(skill == "all" ? $"all skills set to level {level}" : $"{skill} set to level {level}");
                return true;
            }

            // ---------- elf helper ----------
            if (Regex.IsMatch(s, @"^(?:stop|cancel|dismiss|remove|send away|get rid of)\s+(?:the\s+|my\s+)?(?:elf|helper|junimo|farmhand)$"))
            {
                actions.Add(new JObject { ["type"] = "cancelTask" });
                parts.Add("elf dismissed");
                return true;
            }
            var elf = Regex.Match(s, @"^(?:send|get|have|make|let|summon|call)?\s*(?:me\s+)?(?:an?\s+|the\s+|my\s+)?(?:elf|helper|junimo|farmhand)\s+(?:to\s+)?(?<chore>water|harvest|refill|grab|pick|collect|pick up)(?:\s+(?:all\s+)?(?:of\s+)?(?:my\s+|the\s+)?(?:crops?|plants?|farm|field|everything|watering can|can))?(?<rep>\s+(?:every day|daily|forever|constantly|all day|nonstop))?$");
            if (elf.Success)
            {
                string chore = elf.Groups["chore"].Value;
                if (chore != "water" && chore != "refill") chore = "harvest";
                bool repeat = elf.Groups["rep"].Success && elf.Groups["rep"].Value.Trim().Length > 0;
                actions.Add(new JObject { ["type"] = "assignTask", ["chore"] = chore, ["area"] = "farm", ["repeat"] = repeat });
                parts.Add($"elf sent to {chore}");
                return true;
            }

            // ---------- friendship ----------
            // "max friendship with abigail", "make abigail love me", "10 hearts with sebastian"
            var friend = Regex.Match(s, @"^(?:max(?:imize)?|set)\s+(?:my\s+)?(?:friendship|hearts|relationship)\s+(?:with|for)\s+(?<npc>[a-z]+)$");
            if (!friend.Success)
                friend = Regex.Match(s, @"^make\s+(?<npc>[a-z]+)\s+(?:love|like|adore)\s+me$");
            if (!friend.Success)
                friend = Regex.Match(s, @"^(?:give me\s+|get\s+)?(?<h>\d{1,2})\s+hearts?\s+(?:with|for)\s+(?<npc>[a-z]+)$");
            if (friend.Success)
            {
                string npc = friend.Groups["npc"].Value;
                int hearts = friend.Groups["h"].Success ? Math.Clamp(int.Parse(friend.Groups["h"].Value), 0, 10) : 10;
                string name = char.ToUpperInvariant(npc[0]) + npc.Substring(1);
                actions.Add(new JObject { ["type"] = "setFriendship", ["npc"] = name, ["hearts"] = hearts });
                parts.Add($"{hearts} hearts with {name}");
                return true;
            }

            // ---------- weather ----------
            var weather = Regex.Match(s, @"^(?:make it|set (?:the )?weather to|change (?:the )?weather to|turn it|let it|i want)?\s*(?<w>rain(?:y|ing)?|sunny|sun|clear|storm(?:y|ing)?|thunderstorm|snow(?:y|ing)?)(?:\s+(?:today|now|weather))?$");
            if (weather.Success)
            {
                string w = weather.Groups["w"].Value;
                string val = w.StartsWith("rain") ? "rain"
                    : w.StartsWith("storm") || w == "thunderstorm" ? "storm"
                    : w.StartsWith("snow") ? "snow" : "sunny";
                actions.Add(new JObject { ["type"] = "setWeather", ["weather"] = val });
                parts.Add($"weather set to {val}");
                return true;
            }

            // ---------- season ----------
            var season = Regex.Match(s, @"^(?:make it|set (?:the )?season to|change (?:the )?season to|switch to|go to)?\s*(?<se>spring|summer|fall|autumn|winter)$");
            if (season.Success)
            {
                string se = season.Groups["se"].Value == "autumn" ? "fall" : season.Groups["se"].Value;
                actions.Add(new JObject { ["type"] = "setSeason", ["season"] = se });
                parts.Add($"season set to {se}");
                return true;
            }

            // ---------- water crops ----------
            if (Regex.IsMatch(s, @"^water (?:all\s+)?(?:of\s+)?(?:my\s+|the\s+)?(?:crops|plants|farm|field|everything)(?:\s+for me)?$"))
            {
                actions.Add(new JObject { ["type"] = "waterAllCrops" });
                parts.Add("crops watered");
                return true;
            }

            // ---------- time ----------
            string ts = Regex.Replace(s, @"\b([ap])\.?m\.?$", "$1m").Replace("o'clock", "").Replace("oclock", "").Trim();
            var time = Regex.Match(ts, @"^(?:(?:set|change|make|put)\s+(?:the\s+|a\s+)?(?:time|clock)(?:\s+(?:to|at))?|make it|time|it'?s)\s*(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)?$");
            if (time.Success && (time.Groups["ap"].Success || !ts.StartsWith("make it")))
            {
                int h = int.Parse(time.Groups["h"].Value);
                int m = time.Groups["m"].Success ? int.Parse(time.Groups["m"].Value) : 0;
                string ap = time.Groups["ap"].Value;
                if (ap == "pm" && h < 12) h += 12;
                if (ap == "am" && h == 12) h = 24; // midnight
                if (h < 2) h += 24;                 // 1am -> 2500
                int t = h * 100 + m;
                if (t < 600 || t > 2600) return false;
                actions.Add(new JObject { ["type"] = "setTime", ["time"] = t });
                parts.Add($"time set to {time.Groups["h"].Value}{(time.Groups["m"].Success ? ":" + time.Groups["m"].Value : "")}{ap}");
                return true;
            }

            // ---------- warp ----------
            var warp = Regex.Match(s, @"^(?:warp|teleport|tp|take|send|bring|go|move)\s+(?:me\s+)?(?:back\s+)?(?:to\s+|into\s+|in\s+|over to\s+)?(?<loc>.+)$");
            if (warp.Success)
            {
                string loc = warp.Groups["loc"].Value.Trim();
                if (loc.Length > 0 && ActionExecutor.CanResolveLocation(loc))
                {
                    actions.Add(new JObject { ["type"] = "warp", ["location"] = loc });
                    parts.Add($"warped to {loc}");
                    return true;
                }
                return false;
            }

            // ---------- money ----------
            var money = Regex.Match(s, @"^(?:give|add|get|gimme|send|grant)?\s*(?:me\s+)?(?<n>\d[\d,\.]*)\s*(?<mul>k|m|thousand|million)?\s*(?<cur>g|gold|money|coins|dollars|bucks)?$");
            if (money.Success && (money.Groups["mul"].Success || money.Groups["cur"].Success))
            {
                if (TryAmount(money.Groups["n"].Value, money.Groups["mul"].Value, out long amt) && amt > 0 && amt <= 100_000_000)
                {
                    actions.Add(new JObject { ["type"] = "addMoney", ["amount"] = (int)amt });
                    parts.Add($"+{amt:N0}g");
                    verb = "give";
                    return true;
                }
            }

            // ---------- items ----------
            // "give me 10 prismatic shards", "spawn 5 diamonds", "10x iridium bar", "a stardrop"
            var item = Regex.Match(s, @"^(?:give|add|spawn|get|gimme|send|grant|i want|i need)?\s*(?:me\s+)?(?<q>\d{1,4}|a|an|one|some)?\s*x?\s*(?<name>[a-z'\- ]{3,})$");
            if (item.Success)
            {
                string name = item.Groups["name"].Value.Trim();
                name = Regex.Replace(name, @"^(?:of\s+)?(?:the\s+)?", "");
                if (name.Length < 3) return false;

                // Only accept an exact (singular/plural) name match here; fuzzy guesses go to the model.
                string? id = ItemResolver.Resolve(name, strict: true);
                if (id == null) return false;

                string q = item.Groups["q"].Value;
                int qty = q.Length == 0 || q == "a" || q == "an" || q == "one" ? 1
                        : q == "some" ? 10
                        : int.Parse(q, CultureInfo.InvariantCulture);
                qty = Math.Clamp(qty, 1, 999);

                actions.Add(new JObject { ["type"] = "addItem", ["itemName"] = name, ["itemId"] = id, ["quantity"] = qty });
                parts.Add($"{qty}x {name}");
                verb = "give";
                return true;
            }

            return false;
        }

        private static bool TryAmount(string n, string mul, out long amount)
        {
            amount = 0;
            n = n.Replace(",", "");
            if (!double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return false;
            switch (mul)
            {
                case "k": case "thousand": v *= 1_000; break;
                case "m": case "million": v *= 1_000_000; break;
            }
            amount = (long)Math.Round(v);
            return true;
        }

        /// <summary>Strip polite filler so "can you please give me 10 diamonds" parses like "give me 10 diamonds".</summary>
        private static string Polite(string text)
        {
            string s = text.Trim();
            s = Regex.Replace(s, @"^(?:hey|hi|yo|ok|okay)[,!\s]+", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^(?:can you|could you|would you|will you|pls|please|i'd like you to|i want you to)\s+", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^(?:please|pls)\s+", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+(?:please|pls|thanks|thank you|for me)[.!?]*$", "", RegexOptions.IgnoreCase);
            return s;
        }
    }
}
