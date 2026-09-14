using TripleTriadApi.Models;

namespace TripleTriadApi.Tests.Models
{
    /// <summary>
    /// Tests for the rule conversion helpers (wire names + the match's storage column).
    /// </summary>
    public class MatchRuleExtensionsTests
    {
        [Fact]
        public void TryParseAll_Null_MeansNoRules()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(null, out var rules));
            Assert.Empty(rules);
        }

        [Fact]
        public void TryParseAll_EmptyList_MeansNoRules()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(Array.Empty<string>(), out var rules));
            Assert.Empty(rules);
        }

        [Fact]
        public void TryParseAll_IsCaseInsensitive()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(new[] { "same" }, out var rules));
            Assert.Equal(new[] { MatchRule.Same }, rules);
        }

        [Fact]
        public void TryParseAll_DeduplicatesRepeatedRules()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(new[] { "Same", "same" }, out var rules));
            Assert.Equal(new[] { MatchRule.Same }, rules);
        }

        [Theory]
        [InlineData("None")] // there is no "none" rule: an empty list means no rules
        [InlineData("Plus")] // not implemented yet
        [InlineData("NotARule")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Same,Same")] // a comma separated string is not a single rule name
        public void TryParseAll_RejectsAnythingThatIsNotARule(string name)
        {
            Assert.False(MatchRuleExtensions.TryParseAll(new[] { name }, out var rules));
            Assert.Empty(rules);
        }

        [Fact]
        public void ToNames_ReturnsOnlyEnabledRules()
        {
            Assert.Empty(new List<MatchRule>().ToNames());
            Assert.Equal(new[] { "Same" }, new List<MatchRule> { MatchRule.Same }.ToNames());
            Assert.Equal(
                new[] { "Same" },
                new List<MatchRule> { MatchRule.Same, MatchRule.Same }.ToNames()
            );
        }

        [Fact]
        public void SupportedRuleNames_ListsEveryRule()
        {
            Assert.Equal(new[] { "Same" }, MatchRuleExtensions.SupportedRuleNames());
        }

        [Fact]
        public void StorageString_RoundTripsEnabledRules()
        {
            Assert.Equal(string.Empty, new List<MatchRule>().ToStorageString());
            Assert.Equal("Same", new List<MatchRule> { MatchRule.Same }.ToStorageString());

            Assert.Empty(MatchRuleExtensions.ParseStorageString(null));
            Assert.Empty(MatchRuleExtensions.ParseStorageString(""));
            Assert.Equal(new[] { MatchRule.Same }, MatchRuleExtensions.ParseStorageString("Same"));
            Assert.Equal(new[] { MatchRule.Same }, MatchRuleExtensions.ParseStorageString(" same "));
        }

        [Fact]
        public void ParseStorageString_IgnoresUnknownOrRemovedRules()
        {
            // A row written by a newer version (or a rule removed in a later version) must not
            // break the app, so unknown names are skipped instead of throwing.
            Assert.Equal(
                new[] { MatchRule.Same },
                MatchRuleExtensions.ParseStorageString("Same,Removed")
            );
            Assert.Empty(MatchRuleExtensions.ParseStorageString("Removed"));
        }
    }
}
