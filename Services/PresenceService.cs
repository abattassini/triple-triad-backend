using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// What happens when a socket appears or disappears: the registry is updated and **that player's friends are told**
    /// (plans/PLAN-023-social-friends-list/plan.md §3.3).
    ///
    /// It lives here rather than in <c>GameHub</c> for two reasons. The rule "tell my friends, once per real transition"
    /// is a rule, and rules in this house are testable without a host; and the hub's own job — validating the JWT that
    /// arrives on a call — should not also be deciding who hears about the result.
    ///
    /// The hub's hooks are the only callers: <c>SubscribeToNotifications</c> marks a connection online (after its
    /// existing JWT and <c>SessionVersion</c> checks, so nothing unauthenticated ever becomes visible), and
    /// <c>OnDisconnectedAsync</c> marks it offline.
    /// </summary>
    public class PresenceService(
        IPlayerPresence presence,
        IFriendshipRepository friendships,
        IPlayerNotifier notifier
    )
    {
        private readonly IPlayerPresence _presence = presence;
        private readonly IFriendshipRepository _friendships = friendships;
        private readonly IPlayerNotifier _notifier = notifier;

        /// <summary>
        /// Records the connection, and tells the player's friends only when this is their **first** — which is what the
        /// connection count is for: a second tab is not news, and neither is one of two tabs closing.
        /// </summary>
        public async Task ConnectAsync(string connectionId, string login)
        {
            var wasOnline = _presence.IsOnline(login);
            _presence.AddConnection(connectionId, login);

            if (wasOnline)
            {
                return;
            }

            await AnnounceAsync(login, online: true);
        }

        /// <summary>
        /// Forgets the connection and tells the player's friends **only when it was their last**. An id nobody recorded
        /// does nothing at all — a connection that never subscribed has no login to announce.
        /// </summary>
        public async Task DisconnectAsync(string connectionId)
        {
            var login = _presence.RemoveConnection(connectionId);

            if (login is null || _presence.IsOnline(login))
            {
                return;
            }

            await AnnounceAsync(login, online: false);
        }

        /// <summary>
        /// One push per friend. The friends come from the same read the list endpoint uses, so "who might care" has one
        /// definition rather than two.
        /// </summary>
        private async Task AnnounceAsync(string login, bool online)
        {
            var friendships = await _friendships.ListAcceptedForAsync(login);

            foreach (var friendship in friendships)
            {
                var friend =
                    friendship.PlayerA == login ? friendship.PlayerB : friendship.PlayerA;

                await _notifier.FriendPresenceChangedAsync(friend, login, online);
            }
        }
    }
}
