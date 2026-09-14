using TripleTriadApi.Models;

namespace TripleTriadApi.Tests.Models
{
    /// <summary>
    /// Tests for the rule <-> wire-name conversion used by the API (e.g. <c>["Same"]</c>).
    /// </summary>
    public class MatchRuleExtensionsTests
    {
        [Fact]
        public void TryParseAll_Null_MeansNoRules()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(null, out var rules));
            Assert.Equal(MatchRule.None, rules);
        }

        [Fact]
        public void TryParseAll_EmptyList_MeansNoRules()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(Array.Empty<string>(), out var rules));
            Assert.Equal(MatchRule.None, rules);
        }

        [Fact]
        public void TryParseAll_IsCaseInsensitive()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(new[] { "same" }, out var rules));
            Assert.Equal(MatchRule.Same, rules);
        }

        [Fact]
        public void TryParseAll_IgnoresBlankEntries()
        {
            Assert.True(MatchRuleExtensions.TryParseAll(new[] { "", "  ", "Same" }, out var rules));
            Assert.Equal(MatchRule.Same, rules);
        }

        [Theory]
        [InlineData("None")]
        [InlineData("Plus")]
        [InlineData("Combo")]
        [InlineData("NotARule")]
        public void TryParseAll_RejectsUnsupportedNames(string name)
        {
            Assert.False(MatchRuleExtensions.TryParseAll(new[] { name }, out var rules));
            Assert.Equal(MatchRule.None, rules);
        }

        [Fact]
        public void ToNames_ReturnsOnlyEnabledRules()
        {
            Assert.Empty(MatchRule.None.ToNames());
            Assert.Equal(new[] { "Same" }, MatchRule.Same.ToNames());
            Assert.Equal(new[] { "Same" }, (MatchRule.None | MatchRule.Same).ToNames());
        }

        [Fact]
        public void SupportedRuleNames_ListsEveryRuleExceptNone()
        {
            Assert.Equal(new[] { "Same" }, MatchRuleExtensions.SupportedRuleNames());
        }
    }
}
