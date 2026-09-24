namespace TripleTriadApi.Models
{
    public static class MatchRuleExtensions
    {
        // Rules are stored as a comma separated list of names; rule names never contain commas.
        private const char StorageSeparator = ',';

        /// <summary>Names of every rule the game supports.</summary>
        public static string[] SupportedRuleNames()
        {
            return Enum.GetNames<MatchRule>();
        }

        /// <summary>
        /// Rule names for the wire (e.g. <c>["Same"]</c>): the enabled rules, deduplicated and in
        /// enum declaration order.
        /// </summary>
        public static string[] ToNames(this IEnumerable<MatchRule> rules)
        {
            var enabled = rules.Distinct().ToList();

            return SupportedRuleNames()
                .Where(name => enabled.Contains(Enum.Parse<MatchRule>(name)))
                .ToArray();
        }

        /// <summary>
        /// True when two rule lists enable exactly the same rules, whatever order they arrive in.
        ///
        /// Quick Match uses this to decide whether a waiting match is one the searcher asked for: the player picked
        /// the rules, so a waiting match played under different rules is somebody else's game and gets left alone.
        /// Comparing sets rather than sequences matters because the order a client sends is not meaningful — and both
        /// sides are already deduplicated by <see cref="TryParseAll"/> / <see cref="ParseStorageString"/>.
        /// </summary>
        public static bool SameSet(IEnumerable<MatchRule> left, IEnumerable<MatchRule> right)
        {
            var leftRules = left.Distinct().ToList();
            var rightRules = right.Distinct().ToList();

            return leftRules.Count == rightRules.Count && leftRules.All(rightRules.Contains);
        }

        /// <summary>
        /// Parses a list of rule names (case-insensitive) into the list of enabled rules.
        /// A null list means "no rules"; any blank or unknown name makes the whole list invalid so
        /// typos never silently create a match with the wrong rules.
        /// </summary>
        public static bool TryParseAll(IEnumerable<string>? names, out List<MatchRule> rules)
        {
            rules = [];

            if (names is null)
            {
                return true;
            }

            foreach (var name in names)
            {
                var parsed = FindByName(name);
                if (parsed is null)
                {
                    rules = [];
                    return false;
                }

                if (!rules.Contains(parsed.Value))
                {
                    rules.Add(parsed.Value);
                }
            }

            return true;
        }

        /// <summary>
        /// Serializes the enabled rules for the match's database column (comma separated names;
        /// an empty string when no rule is enabled).
        /// </summary>
        public static string ToStorageString(this IEnumerable<MatchRule> rules)
        {
            return string.Join(StorageSeparator, rules.Distinct().Select(rule => rule.ToString()));
        }

        /// <summary>
        /// Parses the match's database column value. Blank entries and names that no longer exist
        /// (e.g. a rule removed in a later version) are skipped so old rows never break the app.
        /// </summary>
        public static List<MatchRule> ParseStorageString(string? value)
        {
            var rules = new List<MatchRule>();

            if (string.IsNullOrWhiteSpace(value))
            {
                return rules;
            }

            foreach (var name in value.Split(StorageSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var parsed = FindByName(name);
                if (parsed is not null && !rules.Contains(parsed.Value))
                {
                    rules.Add(parsed.Value);
                }
            }

            return rules;
        }

        /// <summary>
        /// Matches a name against the supported rules (case-insensitive, exact); returns null when
        /// it is not a known rule.
        /// </summary>
        private static MatchRule? FindByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var trimmed = name.Trim();

            foreach (var ruleName in SupportedRuleNames())
            {
                if (string.Equals(ruleName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return Enum.Parse<MatchRule>(ruleName);
                }
            }

            return null;
        }
    }
}
