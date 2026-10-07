using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The emulated presence rule (plans/PLAN-025-bots/plan.md §3.2): activity is the chance a bot is online, and the
    /// floor guarantees at least three are, however the rolls land.
    /// </summary>
    public class BotPresenceTests
    {
        private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void OnlineBots_WithEveryRollFailing_StillFillsTheFloor()
        {
            // Activity 0 can never roll online, so the only bots up are the ones the floor promotes.
            var bots = Cohort(10, activity: 0);

            var online = BotPresence.OnlineBots(bots, Now);

            Assert.Equal(BotPresence.MinimumOnline, online.Count);
            Assert.Subset(bots.Select(bot => bot.Login).ToHashSet(), online);
        }

        [Fact]
        public void OnlineBots_IsNeverBelowTheFloor_AcrossManyBuckets()
        {
            var bots = Cohort(20, activity: 20);

            foreach (var minutes in Enumerable.Range(0, 240))
            {
                var online = BotPresence.OnlineBots(bots, Now.AddMinutes(minutes * 5));
                Assert.True(
                    online.Count >= BotPresence.MinimumOnline,
                    $"only {online.Count} online at bucket {minutes}"
                );
                Assert.True(online.Count <= bots.Count);
            }
        }

        [Fact]
        public void OnlineBots_IsStableWithinABucket_AndChangesAcrossThem()
        {
            var bots = Cohort(20, activity: 30);

            var a = BotPresence.OnlineBots(bots, Now);
            var b = BotPresence.OnlineBots(bots, Now.AddMinutes(1)); // same 5-minute window
            Assert.Equal(a, b);

            // A later bucket re-rolls: at least one bot's state differs over a day, or nothing is being emulated.
            var changed = Enumerable
                .Range(1, 288)
                .Select(i => BotPresence.OnlineBots(bots, Now.AddMinutes(i * 5)))
                .Any(other => !other.SetEquals(a));
            Assert.True(changed);
        }

        [Fact]
        public void ARollsPassingBot_IsOnline_AndAZeroActivityOneNeverIs()
        {
            // Enough always-online bots that the floor is met by the rolls alone — otherwise a zero-activity bot is
            // *promoted* to fill the floor, which is a different (and correct) rule, exercised elsewhere.
            var bots = new List<BotRegistry.Bot> { new("always", 100), new("never", 0) };
            bots.AddRange(Cohort(5, activity: 100));

            foreach (var minutes in Enumerable.Range(0, 50))
            {
                var at = Now.AddMinutes(minutes * 5);
                Assert.True(BotPresence.IsOnline(bots, "always", at));
                Assert.False(BotPresence.IsOnline(bots, "never", at));
            }
        }

        [Fact]
        public void ThePromotedBots_Rotate_RatherThanBeingTheSameThreeForever()
        {
            // Every roll fails, so the floor is filled every bucket — and the *set* of promoted bots must move.
            var bots = Cohort(20, activity: 0);

            var sets = Enumerable
                .Range(0, 40)
                .Select(i => BotPresence.OnlineBots(bots, Now.AddMinutes(i * 5)))
                .ToList();

            Assert.True(sets.Distinct(HashSetComparer.Instance).Count() > 1);
        }

        [Fact]
        public void ACohortNoLargerThanTheFloor_IsEntirelyOnline()
        {
            var bots = Cohort(BotPresence.MinimumOnline, activity: 0);

            var online = BotPresence.OnlineBots(bots, Now);

            Assert.Equal(BotPresence.MinimumOnline, online.Count);
        }

        private static List<BotRegistry.Bot> Cohort(int count, int activity) =>
            Enumerable
                .Range(0, count)
                .Select(i => new BotRegistry.Bot($"bot-{i}", activity))
                .ToList();

        /// <summary>Compares the online sets by membership, not by reference.</summary>
        private sealed class HashSetComparer : IEqualityComparer<HashSet<string>>
        {
            public static readonly HashSetComparer Instance = new();

            public bool Equals(HashSet<string>? x, HashSet<string>? y) =>
                x is not null && y is not null && x.SetEquals(y);

            public int GetHashCode(HashSet<string> obj) => obj.Count;
        }
    }
}
