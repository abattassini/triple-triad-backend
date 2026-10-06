using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The inbox's own rules (plans/PLAN-022-notifications-and-friends/plan.md §3.3): one row and one hint per event,
    /// a repeat suppressed while the first is still unread, the number the badge reads, the stamps an action leaves,
    /// and a page that draws itself — avatar included — without a web host or a mocking library.
    /// </summary>
    public class NotificationServiceTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";

        [Fact]
        public async Task Create_WritesTheRowAndPushesTheCount()
        {
            using var context = CreateContext();
            var notifier = new RecordingPlayerNotifier();
            var service = CreateService(context, notifier);

            var notification = await service.CreateAsync(
                Me,
                NotificationTypes.FriendRequest,
                Rival,
                subjectId: 7
            );

            Assert.Equal(Me, notification.RecipientId);
            Assert.Equal(NotificationTypes.FriendRequest, notification.Type);
            Assert.Equal(Rival, notification.ActorId);
            Assert.Equal(7, notification.SubjectId);
            Assert.Null(notification.ReadAt);

            var push = Assert.Single(notifier.For(Me));
            Assert.Equal(1, push.UnreadCount);
        }

        [Fact]
        public async Task Create_WhileTheSameIsStillUnread_DoesNotWriteOrPushAgain()
        {
            using var context = CreateContext();
            var notifier = new RecordingPlayerNotifier();
            var service = CreateService(context, notifier);

            var first = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 7);
            var second = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 7);

            Assert.Equal(first.Id, second.Id);
            Assert.Single(context.Notifications);
            // The hint is sent once per row written: a duplicate changes nothing, so it says nothing.
            Assert.Single(notifier.For(Me));
        }

        [Fact]
        public async Task Create_AfterTheEarlierOneWasRead_IsWritableAgain()
        {
            using var context = CreateContext();
            var notifier = new RecordingPlayerNotifier();
            var service = CreateService(context, notifier);

            var first = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 7);
            await service.MarkReadAsync(Me, first.Id);
            var second = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 7);

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, context.Notifications.Count());
            Assert.Equal(1, notifier.For(Me).Last().UnreadCount);
        }

        [Fact]
        public async Task GetPage_IsNewestFirstWithTheActorsAvatar()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Rival, avatarUrl: "avatars/rival.png");
            var service = CreateService(context);

            var older = await service.CreateAsync(Me, NotificationTypes.FriendAccepted, Rival, 1);
            var newer = await service.CreateAsync(Me, NotificationTypes.FriendAccepted, Rival, 2);

            var page = await service.GetPageAsync(Me, limit: null, beforeId: null);

            Assert.Equal(2, page.UnreadCount);
            Assert.Equal(new[] { newer.Id, older.Id }, page.Entries.Select(entry => entry.Notification.Id));
            // The actor's avatar travels with the row, so the panel draws what that player's own profile shows.
            Assert.All(page.Entries, entry => Assert.Equal("avatars/rival.png", entry.ActorAvatarUrl));
        }

        [Fact]
        public async Task GetPage_ShowsAWaitingRequestAsIncoming()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            await SeedFriendshipAsync(context, 42, FriendshipStatus.Pending, requestedBy: Rival);
            var request = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, subjectId: 42);

            var page = await service.GetPageAsync(Me, limit: null, beforeId: null);

            // A row per request, so the panel has a friendship to answer… and one it may still answer.
            Assert.Equal("incoming", Assert.Single(page.Entries).FriendshipState);
            Assert.Equal(NotificationTypes.FriendRequest, Assert.Single(page.Entries).Notification.Type);
            Assert.Equal(request.Id, Assert.Single(page.Entries).Notification.Id);
        }

        [Fact]
        public async Task GetPage_StampsAnAcceptedRowWithThePairsCurrentState()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            await SeedFriendshipAsync(context, 42, FriendshipStatus.Accepted, requestedBy: Rival);

            await service.CreateAsync(Me, NotificationTypes.FriendAccepted, Rival, subjectId: 42);

            var page = await service.GetPageAsync(Me, limit: null, beforeId: null);

            // The pair's state travels with the row, which is the same stamp a request carries while it waits.
            Assert.Equal(FriendshipStates.Friends, Assert.Single(page.Entries).FriendshipState);
        }

        [Fact]
        public async Task GetPage_HidesTheRequestOnceItWasAnswered()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            // The state the accept path leaves behind: accepted, and the request it answered already stamped read.
            await SeedFriendshipAsync(context, 42, FriendshipStatus.Accepted, requestedBy: Rival);
            var request = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, subjectId: 42);
            request.ReadAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            var page = await service.GetPageAsync(Me, limit: null, beforeId: null);

            // Nothing left to answer, so nothing listed — while the row itself is still there as the record.
            Assert.Empty(page.Entries);
            Assert.Equal(0, page.UnreadCount);
            Assert.Single(context.Notifications);
        }

        [Fact]
        public async Task GetPage_HidesAWithdrawnRequestAndKeepsEveryOtherKind()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            // No friendship row at all: the sender withdrew the request (or the two unfriended), which is `none`.
            await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, subjectId: 42);
            await service.CreateAsync(Me, NotificationTypes.FriendAccepted, Rival, subjectId: 43);

            var page = await service.GetPageAsync(Me, limit: null, beforeId: null);

            // Only the request falls out. An acceptance is news nobody acts on — its row is how the requester learns
            // the answer — so it stays, carrying the pair's state as it now stands.
            var entry = Assert.Single(page.Entries);
            Assert.Equal(NotificationTypes.FriendAccepted, entry.Notification.Type);
            Assert.Equal(FriendshipStates.None, entry.FriendshipState);
        }

        /// <summary>The pair row a notification's `SubjectId` points at — a request is answered by changing its state.</summary>
        private static async Task SeedFriendshipAsync(
            TripleTriadContext context,
            int id,
            string status,
            string requestedBy
        )
        {
            context.Friendships.Add(
                new Friendship
                {
                    Id = id,
                    PlayerA = Me,
                    PlayerB = Rival,
                    Status = status,
                    RequestedBy = requestedBy,
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task GetPage_ForAKindThatIsNotAboutFriendship_HasNoState()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            await service.CreateAsync(Me, "something_else", Rival, subjectId: null);

            var page = await service.GetPageAsync(Me, limit: null, beforeId: null);

            Assert.Null(Assert.Single(page.Entries).FriendshipState);
        }

        [Fact]
        public async Task GetPage_WithLimitZero_AnswersTheCountAlone()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 1);
            await service.CreateAsync(Me, NotificationTypes.FriendAccepted, Rival, 2);

            // What a badge refresh costs: the number, and no rows.
            var page = await service.GetPageAsync(Me, limit: 0, beforeId: null);

            Assert.Equal(2, page.UnreadCount);
            Assert.Empty(page.Entries);
        }

        [Fact]
        public async Task GetPage_IsCappedAtTheMaximumPageSize()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            for (var index = 0; index < NotificationService.MaxPageSize + 5; index++)
            {
                await service.CreateAsync(Me, "something_else", $"actor-{index:00}", subjectId: null);
            }

            var page = await service.GetPageAsync(Me, limit: 500, beforeId: null);

            Assert.Equal(NotificationService.MaxPageSize, page.Entries.Count);
            Assert.Equal(NotificationService.MaxPageSize + 5, page.UnreadCount);
        }

        [Fact]
        public async Task MarkRead_ForSomeoneElsesNotification_IsNotFound()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            var notification = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 1);

            // The row exists, but not in *this* inbox — so it reads as absent rather than as forbidden.
            Assert.Null(await service.MarkReadAsync(Rival, notification.Id));
            Assert.Null((await context.Notifications.SingleAsync()).ReadAt);
        }

        [Fact]
        public async Task MarkRead_StampsTheRowAndPushesTheNewCount()
        {
            using var context = CreateContext();
            var notifier = new RecordingPlayerNotifier();
            var service = CreateService(context, notifier);

            var notification = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 1);

            var unreadCount = await service.MarkReadAsync(Me, notification.Id);

            Assert.Equal(0, unreadCount);
            Assert.NotNull((await context.Notifications.SingleAsync()).ReadAt);
            Assert.Equal(0, notifier.For(Me).Last().UnreadCount);

            // Reading it twice reports the same number and pushes nothing further.
            var pushesBefore = notifier.For(Me).Count;
            Assert.Equal(0, await service.MarkReadAsync(Me, notification.Id));
            Assert.Equal(pushesBefore, notifier.For(Me).Count);
        }

        [Fact]
        public async Task MarkAllRead_StampsEveryRowInTheInbox()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 1);
            await service.CreateAsync(Me, "something_else", Rival, null);
            await service.CreateAsync(Rival, "something_else", Me, null);

            var result = await service.MarkAllReadAsync(Me);

            Assert.Equal(2, result.MarkedRead);
            Assert.Equal(0, result.UnreadCount);

            // And only this inbox: the other player's row is untouched.
            var rivalPage = await service.GetPageAsync(Rival, limit: 0, beforeId: null);
            Assert.Equal(1, rivalPage.UnreadCount);
        }

        [Fact]
        public async Task MarkAnswered_StampsOnlyTheExactRow()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            var target = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 7);
            // Same recipient, same actor — a different subject must not be touched.
            var other = await service.CreateAsync(Me, NotificationTypes.FriendRequest, Rival, 8);

            var marked = await service.MarkAnsweredAsync(Me, NotificationTypes.FriendRequest, Rival, 7);

            Assert.Equal(1, marked);
            Assert.NotNull((await context.Notifications.SingleAsync(n => n.Id == target.Id)).ReadAt);
            Assert.Null((await context.Notifications.SingleAsync(n => n.Id == other.Id)).ReadAt);
        }

        private static NotificationService CreateService(
            TripleTriadContext context,
            RecordingPlayerNotifier? notifier = null
        ) =>
            new(
                new Repositories.NotificationRepository(context),
                new Repositories.FriendshipRepository(context),
                new Repositories.PlayerRepository(context),
                notifier ?? new RecordingPlayerNotifier()
            );

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedPlayerAsync(
            TripleTriadContext context,
            string login,
            string? avatarUrl = null
        )
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = $"{login}@example.com",
                    PasswordHash = "hash",
                    AvatarUrl = avatarUrl,
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }
    }
}
