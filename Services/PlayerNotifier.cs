using Microsoft.AspNetCore.SignalR;
using TripleTriadApi.Hubs;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The push a player needs from outside the hub: a row is written (or a socket appears) somewhere the connection
    /// that should hear about it is not. It is an interface because tests have no web host — they record the calls
    /// instead, exactly like <see cref="IMatchNotifier"/>
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.3).
    ///
    /// Both payloads are deliberately tiny. The row is the notification and the socket is the presence, so these are
    /// the hints that say "read again" and "redraw that row", and the client re-reads rather than rendering from a push
    /// (plans/PLAN-023-social-friends-list/plan.md §3.3).
    /// </summary>
    public interface IPlayerNotifier
    {
        /// <summary>Tells the player's connections that their unread count is now <paramref name="unreadCount"/>.</summary>
        Task NotificationsChangedAsync(string recipientId, int unreadCount);

        /// <summary>
        /// Tells one player's connections that a friend of theirs has come online or gone offline
        /// (plans/PLAN-023-social-friends-list/plan.md §3.3). The second kind of hint this seam carries, for the same
        /// audience — one player's own connections, whatever page they are on — which is why it is a method here rather
        /// than a notifier of its own.
        /// </summary>
        Task FriendPresenceChangedAsync(string recipientId, string login, bool online);

        /// <summary>
        /// Somebody challenged the recipient to a match — the hint that opens the challenge dialog over whatever they
        /// are doing (plans/PLAN-027-friend-challenge/plan.md §3.9). Sent only when the challenged player is **free**:
        /// a player already in a match learns about the invitation from their inbox instead.
        ///
        /// <paramref name="rules"/> names the rules the challenger chose, so the dialog can say what the match will be
        /// played under before the challenged answers (plans/PLAN-028-challenge-rules-and-friend-list/plan.md §3.5).
        /// </summary>
        Task ChallengeReceivedAsync(
            string recipientId,
            int matchId,
            string challenger,
            string[] rules
        );

        /// <summary>The recipient's outgoing challenge was accepted: the match is on, and both sides pick a hand.</summary>
        Task ChallengeAcceptedAsync(string recipientId, int matchId);

        /// <summary>The recipient's outgoing challenge was refused — the match's status is `refused`.</summary>
        Task ChallengeRefusedAsync(string recipientId, int matchId);

        /// <summary>The recipient's incoming challenge was withdrawn by the challenger.</summary>
        Task ChallengeCancelledAsync(string recipientId, int matchId);

        /// <summary>
        /// A pending challenge the recipient is in ended on its own — the twenty-minute window, or a sign-out
        /// (§3.2 #4/#5). Both seats are told; whoever is not connected simply hears nothing.
        /// </summary>
        Task ChallengeExpiredAsync(string recipientId, int matchId);
    }

    public class SignalRPlayerNotifier(IHubContext<GameHub> hub) : IPlayerNotifier
    {
        private readonly IHubContext<GameHub> _hub = hub;

        public Task NotificationsChangedAsync(string recipientId, int unreadCount) =>
            _hub
                .Clients.Group(GroupOf(recipientId))
                .SendAsync("NotificationsChanged", new { unreadCount });

        public Task FriendPresenceChangedAsync(string recipientId, string login, bool online) =>
            _hub
                .Clients.Group(GroupOf(recipientId))
                .SendAsync("FriendPresenceChanged", new { login, online });

        public Task ChallengeReceivedAsync(
            string recipientId,
            int matchId,
            string challenger,
            string[] rules
        ) =>
            _hub
                .Clients.Group(GroupOf(recipientId))
                .SendAsync(
                    "ChallengeReceived",
                    new
                    {
                        matchId,
                        challenger,
                        rules,
                    }
                );

        public Task ChallengeAcceptedAsync(string recipientId, int matchId) =>
            _hub
                .Clients.Group(GroupOf(recipientId))
                .SendAsync("ChallengeAccepted", new { matchId });

        public Task ChallengeRefusedAsync(string recipientId, int matchId) =>
            _hub.Clients.Group(GroupOf(recipientId)).SendAsync("ChallengeRefused", new { matchId });

        public Task ChallengeCancelledAsync(string recipientId, int matchId) =>
            _hub
                .Clients.Group(GroupOf(recipientId))
                .SendAsync("ChallengeCancelled", new { matchId });

        public Task ChallengeExpiredAsync(string recipientId, int matchId) =>
            _hub.Clients.Group(GroupOf(recipientId)).SendAsync("ChallengeExpired", new { matchId });

        /// <summary>
        /// The group a player's own connections join when they subscribe, named here rather than in the hub so the hub
        /// and the notifier cannot disagree about it — the same reason
        /// <c>SignalRMatchNotifier.GroupOf</c> exists for match rooms.
        /// </summary>
        public static string GroupOf(string login) => $"player-{login}";
    }
}
