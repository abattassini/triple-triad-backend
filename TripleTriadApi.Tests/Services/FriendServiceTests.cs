using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The rules of a friendship (plans/PLAN-022-notifications-and-friends/plan.md §3.2): one row per pair whichever
    /// way it is asked, what a pending row means to each side, the mutual ask that accepts instead of queueing, and
    /// every guard. Exercised directly over EF InMemory with the push **recorded** — no web host, no mocking library,
    /// the same shape the pack tests take.
    /// </summary>
    public class FriendServiceTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";

        [Fact]
        public async Task Request_CreatesOnePendingRowAndTellsTheOtherPlayer()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            var notifier = new RecordingPlayerNotifier();
            var service = FriendshipTestHarness.CreateFriendService(context, notifier);

            var result = await service.RequestAsync(Me, Rival);

            Assert.True(result.Succeeded);
            Assert.Equal(Rival, result.Login);
            Assert.Equal(FriendshipStates.Requested, result.Friendship);

            var friendship = await context.Friendships.SingleAsync();
            Assert.Equal(FriendshipStatus.Pending, friendship.Status);
            Assert.Equal(Me, friendship.RequestedBy);
            // Held canonically: "argel" sorts before "rival" ordinally, so the pair has one order regardless of who
            // asked — which is what makes the unique index mean "one friendship" rather than "one direction".
            Assert.Equal(Me, friendship.PlayerA);
            Assert.Equal(Rival, friendship.PlayerB);

            var notification = await context.Notifications.SingleAsync();
            Assert.Equal(Rival, notification.RecipientId);
            Assert.Equal(NotificationTypes.FriendRequest, notification.Type);
            Assert.Equal(Me, notification.ActorId);
            Assert.Equal(friendship.Id, notification.SubjectId);
            Assert.Null(notification.ReadAt);

            // One push, to the player who has something waiting, carrying the count as it now stands.
            var push = Assert.Single(notifier.For(Rival));
            Assert.Equal(1, push.UnreadCount);
            Assert.Empty(notifier.For(Me));
        }

        [Fact]
        public async Task Request_FromTheOtherSide_LandsOnTheSameRow()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var service = FriendshipTestHarness.CreateFriendService(context);

            var result = await service.RequestAsync(Rival, Me);

            var friendship = await context.Friendships.SingleAsync();
            Assert.Equal(Me, friendship.PlayerA);
            Assert.Equal(Rival, friendship.PlayerB);
            Assert.Equal(Rival, friendship.RequestedBy);
            Assert.Equal(FriendshipStates.Requested, result.Friendship);
        }

        [Fact]
        public async Task Request_Twice_KeepsOneRowAndNotifiesOnce()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = FriendshipTestHarness.CreateFriendService(context, notifier);

            await service.RequestAsync(Me, Rival);
            var again = await service.RequestAsync(Me, Rival);

            Assert.Equal(FriendshipStates.Requested, again.Friendship);
            Assert.Single(context.Friendships);
            Assert.Single(context.Notifications);
            // And the inbox is not told a second time: nothing changed.
            Assert.Single(notifier.For(Rival));
        }

        [Fact]
        public async Task Request_WhenTheyHadAlreadyAsked_AcceptsInsteadOfQueueingASecond()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = FriendshipTestHarness.CreateFriendService(context, notifier);

            await service.RequestAsync(Rival, Me);
            var answer = await service.RequestAsync(Me, Rival);

            // Both players pressing *Add friend* ends as friends — nobody has to find the Accept button.
            Assert.Equal(FriendshipStates.Friends, answer.Friendship);

            var friendship = await context.Friendships.SingleAsync();
            Assert.Equal(FriendshipStatus.Accepted, friendship.Status);
            Assert.NotNull(friendship.RespondedAt);
            Assert.Equal(Rival, friendship.RequestedBy);

            // The requester is told; the answering player is told twice — first that a request arrived, then that
            // their own waiting one has been answered and its row stamped read, which changes their count too.
            Assert.Single(notifier.For(Rival));
            Assert.Equal(2, notifier.For(Me).Count);
            var accepted = await context.Notifications.SingleAsync(notification =>
                notification.RecipientId == Rival
                && notification.Type == NotificationTypes.FriendAccepted
            );
            Assert.Equal(Me, accepted.ActorId);
            Assert.Null(accepted.ReadAt);

            var waiting = await context.Notifications.SingleAsync(notification =>
                notification.RecipientId == Me
            );
            Assert.NotNull(waiting.ReadAt);
            Assert.Equal(0, notifier.For(Me).Last().UnreadCount);
        }

        [Fact]
        public async Task Request_Yourself_IsRefusedWithoutWritingAnything()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            var service = FriendshipTestHarness.CreateFriendService(context);

            var result = await service.RequestAsync(Me, Me);

            Assert.False(result.Succeeded);
            Assert.Equal(FriendService.FriendFailure.Yourself, result.Failure);
            Assert.Empty(context.Friendships);
            Assert.Empty(context.Notifications);
        }

        [Fact]
        public async Task Request_ForTheCpuSentinel_IsRefusedAsAnUnknownPlayer()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            var service = FriendshipTestHarness.CreateFriendService(context);

            // The CPU plays under a sentinel login with no Players row, so it is refused by the same check that refuses
            // any unknown login — an identity that is not a player, rather than a special case.
            var result = await service.RequestAsync(Me, CpuOpponent.Login);

            Assert.False(result.Succeeded);
            Assert.Equal(FriendService.FriendFailure.UnknownPlayer, result.Failure);
            Assert.Empty(context.Friendships);
        }

        [Fact]
        public async Task Request_WithTheCapAlreadyReached_IsRefused()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            // The cap's worth of requests already waiting for an answer (the rows need no player rows of their own —
            // what is counted is rows, not accounts).
            for (var index = 0; index < FriendService.MaxPendingRequests; index++)
            {
                context.Friendships.Add(
                    new Friendship
                    {
                        PlayerA = $"stranger-{index:00}",
                        PlayerB = Me,
                        Status = FriendshipStatus.Pending,
                        RequestedBy = Me,
                        CreatedAt = DateTime.UtcNow,
                    }
                );
            }

            await context.SaveChangesAsync();
            var service = FriendshipTestHarness.CreateFriendService(context);

            var result = await service.RequestAsync(Me, Rival);

            Assert.False(result.Succeeded);
            Assert.Equal(FriendService.FriendFailure.TooManyRequests, result.Failure);
            Assert.Equal(FriendService.MaxPendingRequests, context.Friendships.Count());
        }

        [Fact]
        public async Task Accept_MakesThemFriendsAndTellsTheRequester()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = FriendshipTestHarness.CreateFriendService(context, notifier);

            await service.RequestAsync(Rival, Me);
            var result = await service.AcceptAsync(Me, Rival);

            Assert.True(result.Succeeded);
            Assert.Equal(FriendshipStates.Friends, result.Friendship);
            Assert.Equal(FriendshipStatus.Accepted, (await context.Friendships.SingleAsync()).Status);

            var accepted = await context.Notifications.SingleAsync(notification =>
                notification.RecipientId == Rival
                && notification.Type == NotificationTypes.FriendAccepted
            );
            Assert.Equal(Me, accepted.ActorId);
            Assert.Equal(1, notifier.For(Rival).Last().UnreadCount);

            // …and the accepter's own waiting request is stamped read, so the badge stops asking.
            Assert.NotNull(
                (await context.Notifications.SingleAsync(n => n.RecipientId == Me)).ReadAt
            );
        }

        [Fact]
        public async Task Accept_WithNothingWaiting_IsAConflict()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var service = FriendshipTestHarness.CreateFriendService(context);

            // Nothing has been asked at all…
            var nothingWaiting = await service.AcceptAsync(Me, Rival);

            // …and an accept is only ever an answer to somebody *else's* request, never to your own.
            await service.RequestAsync(Me, Rival);
            var ownRequest = await service.AcceptAsync(Me, Rival);

            Assert.Equal(FriendService.FriendFailure.Conflict, nothingWaiting.Failure);
            Assert.Equal(FriendService.FriendFailure.Conflict, ownRequest.Failure);
            Assert.Equal(FriendshipStatus.Pending, (await context.Friendships.SingleAsync()).Status);
        }

        [Fact]
        public async Task Accept_WhenAlreadyFriends_IsAConflict()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var service = FriendshipTestHarness.CreateFriendService(context);

            await service.RequestAsync(Me, Rival);
            await service.AcceptAsync(Rival, Me);
            var again = await service.AcceptAsync(Rival, Me);

            Assert.Equal(FriendService.FriendFailure.Conflict, again.Failure);
            Assert.Equal(FriendshipStatus.Accepted, (await context.Friendships.SingleAsync()).Status);
        }

        [Fact]
        public async Task Accept_TakesTheRequestOutOfTheAnsweringPlayersInbox()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var friends = FriendshipTestHarness.CreateFriendService(context);
            var inbox = FriendshipTestHarness.CreateNotificationService(context);

            // Rival asks me: the request is mine to answer, and therefore mine to look at.
            await friends.RequestAsync(Rival, Me);
            var waiting = await inbox.GetPageAsync(Me, limit: null, beforeId: null);

            await friends.AcceptAsync(Me, Rival);
            var answered = await inbox.GetPageAsync(Me, limit: null, beforeId: null);

            // It was there while it waited, and it counted against the badge…
            Assert.Equal(NotificationTypes.FriendRequest, Assert.Single(waiting.Entries).Notification.Type);
            Assert.Equal(1, waiting.UnreadCount);

            // …and answering it takes it out of the list. The row itself stays in the database — nothing is deleted —
            // it is simply no longer something to show (NotificationService.IsStillListed).
            Assert.Empty(answered.Entries);
            Assert.Equal(0, answered.UnreadCount);

            // The other side has news of their own, and it stays: an acceptance is nothing anybody acts on.
            var theirs = await inbox.GetPageAsync(Rival, limit: null, beforeId: null);
            Assert.Equal(
                NotificationTypes.FriendAccepted,
                Assert.Single(theirs.Entries).Notification.Type
            );
            Assert.Equal(FriendshipStates.Friends, Assert.Single(theirs.Entries).FriendshipState);
        }

        [Fact]
        public async Task Decline_TakesTheRequestOutOfTheAnsweringPlayersInbox()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var friends = FriendshipTestHarness.CreateFriendService(context);
            var inbox = FriendshipTestHarness.CreateNotificationService(context);

            await friends.RequestAsync(Rival, Me);
            await friends.RemoveAsync(Me, Rival);
            var answered = await inbox.GetPageAsync(Me, limit: null, beforeId: null);

            Assert.Empty(answered.Entries);
            Assert.Equal(0, answered.UnreadCount);

            // And the requester is told nothing at all — there is no `friend_declined` (§5 D4), so their inbox is empty
            // rather than carrying a line that would only say "ask again".
            Assert.Empty((await inbox.GetPageAsync(Rival, limit: null, beforeId: null)).Entries);
        }

        [Fact]
        public async Task Remove_DeletesTheRowAndStampsTheInboxRead()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = FriendshipTestHarness.CreateFriendService(context, notifier);

            await service.RequestAsync(Me, Rival);
            var result = await service.RemoveAsync(Rival, Me);

            Assert.True(result.Succeeded);
            Assert.Equal(FriendshipStates.None, result.Friendship);
            Assert.Empty(context.Friendships);

            // The request stays in the database as the record — stamped, so it stops counting — rather than vanishing
            // along with the row it was about. That is the whole reason nothing points at the friendship with a FK, and
            // it is also why the list can stop showing it without losing anything (`NotificationService.IsStillListed`).
            var request = await context.Notifications.SingleAsync();
            Assert.Equal(NotificationTypes.FriendRequest, request.Type);
            Assert.NotNull(request.ReadAt);
        }

        [Fact]
        public async Task Remove_WithNoRow_IsIdempotent()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);
            var service = FriendshipTestHarness.CreateFriendService(context);

            var result = await service.RemoveAsync(Me, Rival);

            Assert.True(result.Succeeded);
            Assert.Equal(FriendshipStates.None, result.Friendship);
        }

        [Fact]
        public async Task Remove_ForAnUnknownPlayer_IsRefused()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            var service = FriendshipTestHarness.CreateFriendService(context);

            var result = await service.RemoveAsync(Me, "nobody-here");

            Assert.False(result.Succeeded);
            Assert.Equal(FriendService.FriendFailure.UnknownPlayer, result.Failure);
        }

        [Theory]
        [InlineData(null, FriendshipStates.None)]
        [InlineData("pending-me", FriendshipStates.Requested)]
        [InlineData("pending-them", FriendshipStates.Incoming)]
        [InlineData("accepted", FriendshipStates.Friends)]
        public void StateFor_ReadsTheRowFromTheCallersSide(string? shape, string expected)
        {
            var friendship = shape switch
            {
                "pending-me" => new Friendship
                {
                    PlayerA = Me,
                    PlayerB = Rival,
                    Status = FriendshipStatus.Pending,
                    RequestedBy = Me,
                },
                "pending-them" => new Friendship
                {
                    PlayerA = Me,
                    PlayerB = Rival,
                    Status = FriendshipStatus.Pending,
                    RequestedBy = Rival,
                },
                "accepted" => new Friendship
                {
                    PlayerA = Me,
                    PlayerB = Rival,
                    Status = FriendshipStatus.Accepted,
                    RequestedBy = Rival,
                },
                _ => null,
            };

            Assert.Equal(expected, FriendService.StateFor(friendship, Me));
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedPlayerAsync(TripleTriadContext context, string login)
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = $"{login}@example.com",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }
    }
}
