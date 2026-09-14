namespace TripleTriadApi.Models
{
    public static class MatchRuleExtensions
    {
        /// <summary>
        /// Names of every rule the game supports (never includes <see cref="MatchRule.None"/>).
        /// </summary>
        public static string[] SupportedRuleNames()
        {
            return Enum.GetValues<MatchRule>()
                .Where(rule => rule != MatchRule.None)
                .Select(rule => rule.ToString())
                .ToArray();
        }

        /// <summary>
        /// Rule names contained in the bitmask, ignoring <see cref="MatchRule.None"/>.
        /// This is the shape used on the wire (e.g. <c>["Same"]</c>).
        /// </summary>
        public static string[] ToNames(this MatchRule rules)
        {
            return SupportedRuleNames()
                .Where(name => rules.HasFlag(Enum.Parse<MatchRule>(name)))
                .ToArray();
        }

        /// <summary>
        /// Parses a list of rule names (case-insensitive) into a bitmask.
        /// Returns false when any name is unknown or maps to <see cref="MatchRule.None"/>.
        /// A null list means "no rules".
        /// </summary>
        public static bool TryParseAll(IEnumerable<string>? names, out MatchRule rules)
        {
            rules = MatchRule.None;

            if (names is null)
            {
                return true;
            }

            foreach (var name in names)
            {
                // Blank entries are ignored so an empty array (or [""]) means "no rules".
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (
                    !Enum.TryParse<MatchRule>(name, ignoreCase: true, out var parsed)
                    || parsed == MatchRule.None
                )
                {
                    return false;
                }

                rules |= parsed;
            }

            return true;
        }
    }
}
