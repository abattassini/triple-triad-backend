using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The decorator (plans/PLAN-025-bots/plan.md §3.2): a human's online state is still the live sockets, a bot's is
    /// the emulated rule, and neither leaks into the other.
    /// </summary>
    public class BotAwarePresenceTests
    {
        private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AHuman_IsOnlineOnlyWhileAConnectionHoldsThem()
        {
            var presence = Create();

            Assert.False(presence.IsOnline("argel"));

            presence.AddConnection("c1", "argel");
            Assert.True(presence.IsOnline("argel"));

            presence.RemoveConnection("c1");
            Assert.False(presence.IsOnline("argel"));
        }

        [Fact]
        public void ABot_FollowsTheEmulatedRule_NotTheSockets()
        {
            var registry = new BotRegistry();
            registry.Load(
                [
                    new BotRegistry.Bot("always", 100),
                    new BotRegistry.Bot("never", 0),
                    // Fillers, so the floor is met by the rolls alone and never's zero activity is not masked by promotion.
                    new BotRegistry.Bot("filler-a", 100),
                    new BotRegistry.Bot("filler-b", 100),
                    new BotRegistry.Bot("filler-c", 100),
                ]
            );
            var presence = new BotAwarePresence(
                new ConnectionPresence(),
                registry,
                new FixedClock(Now)
            );

            Assert.True(presence.IsOnline("always"));
            Assert.False(presence.IsOnline("never"));

            // A connection is never a bot's: adding one only ever affects humans.
            presence.AddConnection("c1", "never");
            Assert.False(presence.IsOnline("never"));
        }

        private static BotAwarePresence Create() =>
            new(new ConnectionPresence(), new BotRegistry(), new FixedClock(Now));

        private sealed class FixedClock(DateTime utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
        }
    }
}
