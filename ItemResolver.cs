using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace StardewAI
{
    /// <summary>
    /// Resolves a plain item name ("prismatic shards", "Ancient Seeds") to a real qualified item id
    /// using the game's own item registry. This removes the #1 small-model failure: recalling
    /// numeric item ids from memory (e.g. asking for a Prismatic Shard and getting id 499 = Ancient Seeds).
    /// Must be called on the main thread (reads game data).
    /// </summary>
    public static class ItemResolver
    {
        private sealed class Entry
        {
            public string QualifiedId = "";
            public string Display = "";
            public string Internal = "";
            public string NormDisplay = "";
            public string NormInternal = "";
            public int Rank;
        }

        private static List<Entry>? Index;
        private static string? IndexLanguage;

        /// <summary>
        /// Resolve a name to a qualified item id, or null if nothing matches.
        /// strict = exact (name or singular/plural) matches only; loose also allows substring matches.
        /// </summary>
        public static string? Resolve(string? query, bool strict)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            EnsureIndex();
            if (Index == null || Index.Count == 0)
                return null;

            string q = Normalize(query);
            if (q.Length == 0)
                return null;

            var candidates = Variants(q);

            // 1) Exact display/internal name match (including singular/plural variants).
            foreach (var c in candidates)
            {
                var hit = Index
                    .Where(e => e.NormDisplay == c || e.NormInternal == c)
                    .OrderBy(e => e.Rank)
                    .FirstOrDefault();
                if (hit != null)
                    return hit.QualifiedId;
            }

            if (strict)
                return null;

            // 2) Substring match, preferring the shortest (most specific) name.
            foreach (var c in candidates)
            {
                if (c.Length < 4)
                    continue;
                var hit = Index
                    .Where(e => e.NormDisplay.Contains(c) || e.NormInternal.Contains(c))
                    .OrderBy(e => e.Display.Length)
                    .ThenBy(e => e.Rank)
                    .FirstOrDefault();
                if (hit != null)
                    return hit.QualifiedId;
            }

            return null;
        }

        private static void EnsureIndex()
        {
            string lang = LocalizedContentManager.CurrentLanguageCode.ToString();
            if (Index != null && IndexLanguage == lang)
                return;

            var list = new List<Entry>();
            foreach (IItemDataDefinition type in ItemRegistry.ItemTypes)
            {
                IEnumerable<string> ids;
                try { ids = type.GetAllIds(); }
                catch { continue; }

                foreach (var localId in ids)
                {
                    try
                    {
                        ParsedItemData d = type.GetData(localId);
                        if (d == null || d.IsErrorItem)
                            continue;
                        string display = d.DisplayName ?? "";
                        string internalName = d.InternalName ?? "";
                        if (display.Length == 0 && internalName.Length == 0)
                            continue;

                        list.Add(new Entry
                        {
                            QualifiedId = d.QualifiedItemId,
                            Display = display,
                            Internal = internalName,
                            NormDisplay = Normalize(display),
                            NormInternal = Normalize(internalName),
                            Rank = RankFor(d.QualifiedItemId)
                        });
                    }
                    catch
                    {
                        // Skip any malformed entry.
                    }
                }
            }

            Index = list;
            IndexLanguage = lang;
        }

        /// <summary>Prefer regular objects over furniture/hats/etc. when names collide.</summary>
        private static int RankFor(string qualifiedId)
        {
            if (qualifiedId.StartsWith("(O)")) return 0;
            if (qualifiedId.StartsWith("(BC)")) return 1;
            if (qualifiedId.StartsWith("(W)") || qualifiedId.StartsWith("(T)")) return 2;
            return 3;
        }

        private static List<string> Variants(string q)
        {
            var v = new List<string> { q };
            if (q.EndsWith("ies") && q.Length > 4) v.Add(q.Substring(0, q.Length - 3) + "y");
            if (q.EndsWith("es") && q.Length > 3) v.Add(q.Substring(0, q.Length - 2));
            if (q.EndsWith("s") && q.Length > 2) v.Add(q.Substring(0, q.Length - 1));
            if (!q.EndsWith("s")) v.Add(q + "s");
            return v.Distinct().ToList();
        }

        internal static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.ToLowerInvariant().Trim();
            if (s.StartsWith("the ")) s = s.Substring(4);
            return new string(s.Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
