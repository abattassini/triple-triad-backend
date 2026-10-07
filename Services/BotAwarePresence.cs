namespace TripleTriadApi.Services
{
    /// <summary>
    /// Presence for both kinds of player (plans/PLAN-025-bots/plan.md §3.2): a human is online while a live socket
    /// holds their login (<see cref="ConnectionPresence"/>), and a bot is online by the emulated rule over its activity
    /// (<see cref="BotPresence"/>). A decorator, so every existing caller — the friend list, a profile — keeps asking
    /// the one <see cref="IPlayerPresence"/> question and gets a bot's answer for free.
    /// </summary>
    public class BotAwarePresence(
        ConnectionPresence connections,
        BotRegistry registry,
        TimeProvider clock
    ) : IPlayerPresence
    {
        private readonly ConnectionPresence _connections = connections;
        private readonly BotRegistry _registry = registry;
        private readonly TimeProvider _clock = clock;

        /// <summary>A connection is only ever a human's — bots announce nothing — so this is the raw registry.</summary>
        public void AddConnection(string connectionId, string login) =>
            _connections.AddConnection(connectionId, login);

        public string? RemoveConnection(string connectionId) =>
            _connections.RemoveConnection(connectionId);

        /// <summary>The bots' answer from the emulation; everyone else's from the real sockets.</summary>
        public bool IsOnline(string login) =>
            _registry.IsBot(login)
                ? BotPresence.IsOnline(_registry.Bots, login, _clock.GetUtcNow().UtcDateTime)
                : _connections.IsOnline(login);

        /// <summary>A bot holds no sockets, so its count is always the (zero) connection count.</summary>
        public int ConnectionCount(string login) => _connections.ConnectionCount(login);
    }
}
