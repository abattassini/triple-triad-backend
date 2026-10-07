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
        private volatile Dictionary<string, int> _byLogin = new(StringComparer.Ordinal);

        /// <summary>One bot, as presence needs it: who it is, and how active it is.</summary>
        public readonly record struct Bot(string Login, int Activity);

        /// <summary>Everyone the registry knows, in no particular order.</summary>
        public IReadOnlyList<Bot> Bots => _bots;

        /// <summary>True when this login belongs to a bot — the presence decorator's one hot-path question.</summary>
        public bool IsBot(string login) => _byLogin.ContainsKey(login);

        /// <summary>Replaces the cohort. Called at startup, after the seeder has run.</summary>
        public void Load(IEnumerable<Bot> bots)
        {
            var list = bots.ToList();

            // Set the list first, then the index: a concurrent reader that misses the index still sees a coherent list.
            _bots = list;
            _byLogin = list.ToDictionary(
                bot => bot.Login,
                bot => bot.Activity,
                StringComparer.Ordinal
            );
        }
    }
}
