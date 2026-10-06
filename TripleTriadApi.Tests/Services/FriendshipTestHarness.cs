using TripleTriadApi.Data;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The friend wiring over a test's own in-memory context, in one place: two controller test classes build a
    /// <c>PlayerController</c> and both now have to hand it a <see cref="FriendService"/>, and neither should grow its
    /// own copy of that graph (plans/PLAN-022-notifications-and-friends/plan.md §3.2).
    ///
    /// The notifier defaults to the recording one — a test that cares what was pushed passes its own instance so it can
    /// read the pushes back.
    /// </summary>
    internal static class FriendshipTestHarness
    {
        public static FriendService CreateFriendService(
            TripleTriadContext context,
            IPlayerNotifier? notifier = null
        ) =>
            new(
                new FriendshipRepository(context),
                new PlayerRepository(context),
                CreateNotificationService(context, notifier)
            );

        /// <summary>
        /// The same graph's other half, for a test that has to read the inbox a friend action wrote into — which is how
        /// "answering a request takes it out of the list" is proved end to end rather than one service at a time.
        /// </summary>
        public static NotificationService CreateNotificationService(
            TripleTriadContext context,
            IPlayerNotifier? notifier = null
        ) =>
            new(
                new NotificationRepository(context),
                new FriendshipRepository(context),
                new PlayerRepository(context),
                notifier ?? new RecordingPlayerNotifier()
            );
    }
}
