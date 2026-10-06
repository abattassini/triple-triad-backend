using Microsoft.AspNetCore.SignalR;
using TripleTriadApi.Hubs;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The push a notification needs from outside the hub: the row is written in a REST request, while the connection
    /// that should hear about it is held by the hub. It is an interface because tests have no web host — they record
    /// the calls instead, exactly like <see cref="IMatchNotifier"/>
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.3).
    ///
    /// The payload is deliberately only the new unread count. The row is the notification; this is the hint that says
    /// "read again", and the client re-reads rather than rendering from a push.
    /// </summary>
    public interface IPlayerNotifier
    {
        /// <summary>Tells the player's connections that their unread count is now <paramref name="unreadCount"/>.</summary>
        Task NotificationsChangedAsync(string recipientId, int unreadCount);
    }

    public class SignalRPlayerNotifier(IHubContext<GameHub> hub) : IPlayerNotifier
    {
        private readonly IHubContext<GameHub> _hub = hub;

        public Task NotificationsChangedAsync(string recipientId, int unreadCount) =>
            _hub
                .Clients.Group(GroupOf(recipientId))
                .SendAsync("NotificationsChanged", new { unreadCount });

        /// <summary>
        /// The group a player's own connections join when they subscribe, named here rather than in the hub so the hub
        /// and the notifier cannot disagree about it — the same reason
        /// <c>SignalRMatchNotifier.GroupOf</c> exists for match rooms.
        /// </summary>
        public static string GroupOf(string login) => $"player-{login}";
    }
}
