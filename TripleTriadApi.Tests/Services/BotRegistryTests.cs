using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The in-memory cohort cache (plans/PLAN-029-cpu-playing-profiles/plan.md): besides presence it now answers how a
    /// bot plays, and anything it does not know plays the default.
    /// </summary>
    public class BotRegistryTests
    {
        [Fact]
        public void ProfileOf_ReturnsWhatTheCohortWasLoadedWith()
        {
            var registry = new BotRegistry();
            registry.Load(
                [
                    new BotRegistry.Bot("decent-bot", 50),
                    new BotRegistry.Bot("formidable-bot", 50, CPUPlayingProfile.Formidable),
                ]
            );

            Assert.Equal(CPUPlayingProfile.Decent, registry.ProfileOf("decent-bot"));
            Assert.Equal(CPUPlayingProfile.Formidable, registry.ProfileOf("formidable-bot"));
        }

        [Fact]
        public void ProfileOf_IsDecent_ForALoginThatIsNotABot()
        {
            var registry = new BotRegistry();
            registry.Load([new BotRegistry.Bot("a-bot", 50, CPUPlayingProfile.Formidable)]);

            Assert.False(registry.IsBot("argel"));
            Assert.Equal(CPUPlayingProfile.Decent, registry.ProfileOf("argel"));
        }

        [Fact]
        public void ABotBuiltWithoutAProfile_PlaysDecent()
        {
            // The presence-only callers build a bot from a login and an activity.
            Assert.Equal(CPUPlayingProfile.Decent, new BotRegistry.Bot("bot", 10).Profile);
        }
    }
}
