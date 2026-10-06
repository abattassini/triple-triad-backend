using System.Collections.Concurrent;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Who is online, as far as this process can tell: a player is online while at least one live hub connection holds
    /// their login (plans/PLAN-023-social-friends-list/plan.md §3.1). An interface because tests have no hub — the same
    /// reason <see cref="IPlayerNotifier"/> and <c>IMatchNotifier</c> exist — so the friend list's online flag and the
    /// presence push can both be exercised with a fake.
    /// </summary>
    public interface IPlayerPresence
    {
        /// <summary>Records that this connection belongs to this player. Called once per connection.</summary>
        void AddConnection(string connectionId, string login);

        /// <summary>
        /// Forgets a connection and answers the login it held, **or null if it was never recorded** — which is how the
        /// caller tells "nobody left" from "nothing to do".
        /// </summary>
        string? RemoveConnection(string connectionId);

        /// <summary>True while this player holds at least one connection.</summary>
        bool IsOnline(string login);

        /// <summary>How many connections this player holds — for tests and diagnostics, not for the API.</summary>
        int ConnectionCount(string login);
    }

    /// <summary>
    /// The in-memory implementation: two maps, one counting. Registered as a **singleton**, because the state it holds
    /// is the process's own set of sockets and would be meaningless per-request.
    ///
    /// A **count** rather than a flag, because one player legitimately holds several connections at once (two tabs, a
    /// phone and a laptop): they are online while the last of them is still there, and a second tab opening or closing
    /// changes nothing anyone else should hear about.
    ///
    /// What this deliberately does not do: it does not survive a restart, and it knows only about the connections *this*
    /// process holds. Both are the single-process assumption the app already makes (§5 D3, PLAN-022 D7); the day a
    /// second instance exists, the fix is a SignalR backplane, not more state here.
    /// </summary>
    public class ConnectionPresence : IPlayerPresence
    {
        private readonly ConcurrentDictionary<string, string> _byConnection =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void AddConnection(string connectionId, string login)
        {
            // A reconnect is a new connection id, and the same connection id is never added twice — but ignore the
            // possibility rather than throw over it: a duplicate would only inflate a count.
            if (!_byConnection.TryAdd(connectionId, login))
            {
                return;
            }

            _counts.AddOrUpdate(login, 1, (_, count) => count + 1);
        }

        public string? RemoveConnection(string connectionId)
        {
            if (!_byConnection.TryRemove(connectionId, out var login))
            {
                return null;
            }

            // The count can only reach zero through a removal that the map above accepted, so a decrement here always
            // has a positive value to work with — unless a duplicate add was refused, which is why the floor is zero.
            var remaining = _counts.AddOrUpdate(login, 0, (_, count) => count - 1);
            if (remaining <= 0)
            {
                _counts.TryRemove(login, out _);
            }

            return login;
        }

        public bool IsOnline(string login) => ConnectionCount(login) > 0;

        public int ConnectionCount(string login) =>
            _counts.TryGetValue(login, out var count) ? Math.Max(count, 0) : 0;
    }
}
