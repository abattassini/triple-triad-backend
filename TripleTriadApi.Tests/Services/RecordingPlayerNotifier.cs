using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// A stand-in for the real notifier: it records what would have been pushed instead of talking to a hub, which is
    /// the whole reason <see cref="IPlayerNotifier"/> exists — tests have no web host, exactly as
    /// <c>RecordingEmailSender</c> stands in for mail.
    ///
    /// Every push is kept in order, so a test can ask not only *whether* a player was told but *how often* — which is
    /// how "asking twice does not notify twice" is proved.
    /// </summary>
    public class RecordingPlayerNotifier : IPlayerNotifier
    {
        private readonly List<Push> _pushes = [];
        private readonly List<PresencePush> _presencePushes = [];

        /// <summary>Every inbox push, oldest first.</summary>
        public IReadOnlyList<Push> Pushes => _pushes;

        /// <summary>Every presence push, oldest first.</summary>
        public IReadOnlyList<PresencePush> PresencePushes => _presencePushes;

        public Task NotificationsChangedAsync(string recipientId, int unreadCount)
        {
            _pushes.Add(new Push(recipientId, unreadCount));
            return Task.CompletedTask;
        }

        public Task FriendPresenceChangedAsync(string recipientId, string login, bool online)
        {
            _presencePushes.Add(new PresencePush(recipientId, login, online));
            return Task.CompletedTask;
        }

        /// <summary>The inbox pushes one player received, in order.</summary>
        public List<Push> For(string recipientId) =>
            _pushes.Where(push => push.RecipientId == recipientId).ToList();

        /// <summary>The presence pushes one player received, in order.</summary>
        public List<PresencePush> PresenceFor(string recipientId) =>
            _presencePushes.Where(push => push.RecipientId == recipientId).ToList();

        public sealed record Push(string RecipientId, int UnreadCount);

        public sealed record PresencePush(string RecipientId, string Login, bool Online);
    }
}
