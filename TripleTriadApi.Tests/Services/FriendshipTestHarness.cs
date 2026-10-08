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
            IPlayerNotifier? notifier = null,
            IPlayerPresence? presence = null
        ) =>
            new(
                new FriendshipRepository(context),
                new PlayerRepository(context),
                CreateNotificationService(context, notifier),
                // The real registry, not a fake: it is a couple of dictionaries with no dependencies, so a test that
                // wants somebody online says so by connecting them
                // (plans/PLAN-023-social-friends-list/plan.md §3.1).
                presence ?? new ConnectionPresence()
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
                notifier ?? new RecordingPlayerNotifier(),
                // The page stamps a challenge row from the match it is about (plans/PLAN-027-friend-challenge §3.6).
                new GameRepository(context)
            );

        /// <summary>
        /// The challenge graph over the same in-memory context, for the tests that drive sending, answering and
        /// expiring an invitation. One recorder is shared by the inbox and the challenge pushes, so a test can pass its
        /// own and read both back (plans/PLAN-027-friend-challenge/plan.md §3.5).
        /// </summary>
        public static ChallengeService CreateChallengeService(
            TripleTriadContext context,
            IPlayerNotifier? notifier = null,
            IPlayerPresence? presence = null,
            IRandomSource? random = null
        )
        {
            var recorder = notifier ?? new RecordingPlayerNotifier();
            var presenceState = presence ?? new ConnectionPresence();

            return new ChallengeService(
                new GameRepository(context),
                new PlayerRepository(context),
                presenceState,
                CreateFriendService(context, recorder, presenceState),
                CreateNotificationService(context, recorder),
                recorder,
                new GameLogicService(),
                random ?? new SystemRandomSource()
            );
        }
    }
}
