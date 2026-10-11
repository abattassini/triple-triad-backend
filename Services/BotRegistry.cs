using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The bots this process knows about — their logins and activity values — held in memory so presence can be
    /// answered synchronously (plans/PLAN-025-bots/plan.md §3.2). Loaded once at startup from the seeded rows, since
    /// nothing else creates a bot; a later plan that changes a bot at runtime would re-load it here.
    ///
    /// A singleton, like <see cref="ConnectionPresence"/>: it caches state the whole process shares, not a per-request
    /// view, which is what lets the synchronous <see cref="IPlayerPresence.IsOnline"/> answer for a bot with no read.
    /// </summary>
    public class BotRegistry
    {
        private volatile IReadOnlyList<Bot> _bots = [];
        private volatile Dictionary<string, Bot> _byLogin = new(StringComparer.Ordinal);

        /// <summary>
        /// One bot, as the process needs it: who it is, how active it is, and how it plays. <see cref="Profile"/>
        /// defaults to <see cref="CPUPlayingProfile.Decent"/>, so the presence-only callers that build one from a login
        /// and an activity read exactly as before.
        /// </summary>
        public readonly record struct Bot(
            string Login,
            int Activity,
            CPUPlayingProfile Profile = CPUPlayingProfile.Decent
        );

        /// <summary>Everyone the registry knows, in no particular order.</summary>
        public IReadOnlyList<Bot> Bots => _bots;

        /// <summary>True when this login belongs to a bot — the presence decorator's one hot-path question.</summary>
        public bool IsBot(string login) => _byLogin.ContainsKey(login);

        /// <summary>
        /// How the bot plays (plans/PLAN-029-cpu-playing-profiles/plan.md). <see cref="CPUPlayingProfile.Decent"/> for a
        /// login that is not a bot — and for a cohort seeded before profiles existed, which is why the fallback is the
        /// default rather than an error.
        /// </summary>
        public CPUPlayingProfile ProfileOf(string login) =>
            _byLogin.TryGetValue(login, out var bot) ? bot.Profile : CPUPlayingProfile.Decent;

        /// <summary>Replaces the cohort. Called at startup, after the seeder has run.</summary>
        public void Load(IEnumerable<Bot> bots)
        {
            var list = bots.ToList();

            // Set the list first, then the index: a concurrent reader that misses the index still sees a coherent list.
            _bots = list;
            _byLogin = list.ToDictionary(bot => bot.Login, bot => bot, StringComparer.Ordinal);
        }
    }
}
