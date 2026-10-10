using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Challenging a friend (plans/PLAN-027-friend-challenge/plan.md): who may be challenged, what the row looks like,
    /// how an answer moves it, and the three ways an unanswered invitation ends. The service is driven directly over
    /// EF InMemory through <see cref="FriendshipTestHarness"/>, so no host and no HTTP are involved.
    /// </summary>
    public class ChallengeServiceTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";
        private const string Stranger = "stranger";
        private const string Bystander = "bystander";

        [Fact]
        public async Task Challenge_AFriendWhoIsOnline_CreatesAPendingMatchAndAnInboxRow()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var presence = Online(Rival);
            var notifier = new RecordingPlayerNotifier();

            var result = await Service(context, notifier, presence)
                .ChallengeAsync(Me, Rival, [], Now);

            Assert.True(result.Succeeded);

            var match = await context.Matches.SingleAsync();
            Assert.Equal("pending", match.Status);
            Assert.Equal(Me, match.Player1Id);
            Assert.Equal(Rival, match.Player2Id);
            Assert.Equal(match.Id, result.MatchId);

            // The row is the durable half: it is what the bell counts and what can still be answered later.
            var row = await context.Notifications.SingleAsync();
            Assert.Equal(NotificationTypes.MatchChallenge, row.Type);
            Assert.Equal(Rival, row.RecipientId);
            Assert.Equal(Me, row.ActorId);
            Assert.Equal(match.Id, row.SubjectId);

            // And the push is the extra one, for a player who is free to take it.
            var push = Assert.Single(notifier.ChallengesFor(Rival));
            Assert.Equal("received", push.Kind);
            Assert.Equal(Me, push.Challenger);
        }

        [Fact]
        public async Task Challenge_WithRules_StoresThemOnTheRowAndPushesThem()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var notifier = new RecordingPlayerNotifier();

            var result = await Service(context, notifier, Online(Rival))
                .ChallengeAsync(Me, Rival, [MatchRule.Same, MatchRule.Plus], Now);

            Assert.True(result.Succeeded);

            // The choice rides on the invitation's own row, so acceptance needs no second write (PLAN-028 §3.1).
            var match = await context.Matches.SingleAsync();
            Assert.Equal(new List<MatchRule> { MatchRule.Same, MatchRule.Plus }, match.Rules);

            // And the push names them, so the challenged's dialog can say what the game will be played under.
            var push = Assert.Single(notifier.ChallengesFor(Rival));
            Assert.Equal(new[] { "Same", "Plus" }, push.Rules);
        }

        [Fact]
        public async Task Challenge_WithoutRules_IsABasicMatch()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var notifier = new RecordingPlayerNotifier();

            await Service(context, notifier, Online(Rival)).ChallengeAsync(Me, Rival, [], Now);

            Assert.Empty((await context.Matches.SingleAsync()).Rules);
            Assert.Empty(Assert.Single(notifier.ChallengesFor(Rival)).Rules!);
        }

        [Fact]
        public async Task Challenge_WhenTheChallengedIsInAMatch_WritesTheRowButDoesNotPush()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            await SeedActiveMatchAsync(context, Rival, Bystander);
            var notifier = new RecordingPlayerNotifier();

            var result = await Service(context, notifier, Online(Rival))
                .ChallengeAsync(Me, Rival, [], Now);

            Assert.True(result.Succeeded);
            Assert.Equal(
                "pending",
                (await context.Matches.SingleAsync(m => m.Status == "pending")).Status
            );
            Assert.Single(context.Notifications);

            // No dialog pops over a match in progress: the invitation waits in the inbox instead (§1.3).
            Assert.Empty(notifier.ChallengesFor(Rival));
        }

        [Fact]
        public async Task Challenge_ForSomeoneWhoIsNotAFriend_IsRefused()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Stranger);

            var result = await Service(context, presence: Online(Stranger))
                .ChallengeAsync(Me, Stranger, [], Now);

            Assert.Equal(ChallengeService.ChallengeFailure.NotFriends, result.Failure);
            Assert.Empty(context.Matches);
            Assert.Empty(context.Notifications);
        }

        [Fact]
        public async Task Challenge_ForAFriendWhoIsOffline_IsRefused()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);

            var result = await Service(context).ChallengeAsync(Me, Rival, [], Now);

            Assert.Equal(ChallengeService.ChallengeFailure.Offline, result.Failure);
            Assert.Empty(context.Matches);
        }

        [Fact]
        public async Task Challenge_ForYourself_IsRefused()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me);

            var result = await Service(context).ChallengeAsync(Me, Me, [], Now);

            Assert.Equal(ChallengeService.ChallengeFailure.Yourself, result.Failure);
            Assert.Empty(context.Matches);
        }

        [Fact]
        public async Task Challenge_WhileTheChallengerIsInAMatch_IsRefused()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival, Bystander);
            await BefriendAsync(context, Me, Rival);
            await SeedActiveMatchAsync(context, Me, Bystander);

            var result = await Service(context, presence: Online(Rival))
                .ChallengeAsync(Me, Rival, [], Now);

            Assert.Equal(ChallengeService.ChallengeFailure.Busy, result.Failure);
            Assert.Empty(context.Notifications);
        }

        [Fact]
        public async Task Accept_TurnsTheMatchActiveAndTellsTheChallenger()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = Service(context, notifier, Online(Rival));
            var challenge = await service.ChallengeAsync(Me, Rival, [], Now);

            var result = await service.AcceptAsync(challenge.MatchId, Rival, Now);

            Assert.True(result.Succeeded);
            Assert.Equal("active", result.Status);

            var match = await context.Matches.SingleAsync();
            Assert.Equal("active", match.Status);
            Assert.NotNull(match.ActivatedAt);
            // The opener is drawn at acceptance, once both seats are known to be playing (PLAN-024).
            Assert.Contains(match.CurrentPlayerTurn, new[] { Me, Rival });

            // Answering the invitation clears it from the inbox and the badge.
            Assert.NotNull((await context.Notifications.SingleAsync()).ReadAt);
            Assert.Equal("accepted", Assert.Single(notifier.ChallengesFor(Me)).Kind);
        }

        [Fact]
        public async Task Accept_WhenTheChallengedIsInAMatch_IsRefused()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival, Bystander);
            await BefriendAsync(context, Me, Rival);
            var service = Service(context, presence: Online(Rival));
            var challenge = await service.ChallengeAsync(Me, Rival, [], Now);
            await SeedActiveMatchAsync(context, Rival, Bystander);

            var result = await service.AcceptAsync(challenge.MatchId, Rival, Now);

            Assert.Equal(ChallengeService.ChallengeFailure.Busy, result.Failure);
            Assert.Equal(
                "pending",
                (await context.Matches.SingleAsync(m => m.Id == challenge.MatchId)).Status
            );
        }

        [Fact]
        public async Task Accept_BySomebodyElse_IsNotYours()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival, Stranger);
            await BefriendAsync(context, Me, Rival);
            var service = Service(context, presence: Online(Rival));
            var challenge = await service.ChallengeAsync(Me, Rival, [], Now);

            var result = await service.AcceptAsync(challenge.MatchId, Stranger, Now);

            Assert.Equal(ChallengeService.ChallengeFailure.NotYours, result.Failure);
            Assert.Equal("pending", (await context.Matches.SingleAsync()).Status);
        }

        [Fact]
        public async Task Accept_AfterTheWindowHasPassed_ExpiresItAndIsRefused()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = Service(context, notifier, Online(Rival));
            // Sent well past the twenty-minute window (§3.2 #4).
            var challenge = await service.ChallengeAsync(
                Me,
                Rival,
                [],
                Now - MatchTimeouts.PendingChallenge - TimeSpan.FromMinutes(1)
            );

            var result = await service.AcceptAsync(challenge.MatchId, Rival, Now);

            Assert.Equal(ChallengeService.ChallengeFailure.NotPending, result.Failure);
            Assert.Equal("abandoned", (await context.Matches.SingleAsync()).Status);
            Assert.Equal("expired", Assert.Single(notifier.ChallengesFor(Me)).Kind);
        }

        [Fact]
        public async Task Refuse_SetsTheStatusToRefusedAndTellsTheChallenger()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = Service(context, notifier, Online(Rival));
            var challenge = await service.ChallengeAsync(Me, Rival, [], Now);

            var result = await service.RefuseAsync(challenge.MatchId, Rival);

            Assert.True(result.Succeeded);
            Assert.Equal("refused", (await context.Matches.SingleAsync()).Status);
            Assert.Equal("refused", Assert.Single(notifier.ChallengesFor(Me)).Kind);
            Assert.NotNull((await context.Notifications.SingleAsync()).ReadAt);
        }

        [Fact]
        public async Task Cancel_AbandonsTheInvitationAndTellsTheChallenged()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);
            var notifier = new RecordingPlayerNotifier();
            var service = Service(context, notifier, Online(Rival));
            var challenge = await service.ChallengeAsync(Me, Rival, [], Now);

            var result = await service.CancelAsync(challenge.MatchId, Rival);

            // Only the challenger may withdraw it: the invited player answers it, they do not cancel it.
            Assert.Equal(ChallengeService.ChallengeFailure.NotYours, result.Failure);

            Assert.True((await service.CancelAsync(challenge.MatchId, Me)).Succeeded);
            Assert.Equal("abandoned", (await context.Matches.SingleAsync()).Status);
            // Rival's pushes are "received" (when the invitation was sent) then "cancelled" (when it was withdrawn).
            Assert.Equal("cancelled", notifier.ChallengesFor(Rival).Last().Kind);
        }

        [Fact]
        public async Task ExpireTimedOutAsync_EndsOnlyTheOnesPastTheirWindow()
        {
            using var context = CreateContext();
            var games = new GameRepository(context);
            var stale = await games.CreateChallengeMatchAsync(
                Me,
                Rival,
                [],
                Now - MatchTimeouts.PendingChallenge - TimeSpan.FromMinutes(1)
            );
            var fresh = await games.CreateChallengeMatchAsync(Me, Rival, [], Now);
            context.Notifications.Add(
                new Notification
                {
                    RecipientId = Rival,
                    Type = NotificationTypes.MatchChallenge,
                    ActorId = Me,
                    SubjectId = stale.Id,
                    CreatedAt = stale.CreatedAt,
                }
            );
            await context.SaveChangesAsync();
            var notifier = new RecordingPlayerNotifier();

            var expired = await Service(context, notifier).ExpireTimedOutAsync(Now);

            Assert.Equal(1, expired);
            Assert.Equal(
                "abandoned",
                (await context.Matches.SingleAsync(m => m.Id == stale.Id)).Status
            );
            Assert.Equal(
                "pending",
                (await context.Matches.SingleAsync(m => m.Id == fresh.Id)).Status
            );

            // Both seats are told, because either of them may be looking at a dialog about it.
            Assert.Equal("expired", Assert.Single(notifier.ChallengesFor(Me)).Kind);
            Assert.Equal("expired", Assert.Single(notifier.ChallengesFor(Rival)).Kind);

            // And the dead row stops counting against the badge.
            Assert.NotNull((await context.Notifications.SingleAsync()).ReadAt);
        }

        [Fact]
        public async Task ExpireInvolvingAsync_EndsEverySeatThePlayerHolds()
        {
            using var context = CreateContext();
            var games = new GameRepository(context);
            // One Rival sent and one Rival received: signing out ends both (§3.2 #5).
            await games.CreateChallengeMatchAsync(Rival, Me, [], Now - TimeSpan.FromMinutes(1));
            await games.CreateChallengeMatchAsync(
                Bystander,
                Rival,
                [],
                Now - TimeSpan.FromMinutes(1)
            );

            var ended = await Service(context).ExpireInvolvingAsync(Rival);

            Assert.Equal(2, ended);
            Assert.Equal(2, context.Matches.Count(match => match.Status == "abandoned"));
        }

        [Fact]
        public async Task Challenge_WhileHoldingAnInvitationTheyReceived_EndsItFirst()
        {
            using var context = CreateContext();
            var games = new GameRepository(context);
            await SeedAsync(context, Me, Rival, Bystander);
            await BefriendAsync(context, Me, Rival);
            // Somebody else's invitation to Me is already outstanding.
            var received = await games.CreateChallengeMatchAsync(Bystander, Me, [], Now);

            // Sending one of its own supersedes it: one match per player (§13).
            var result = await Service(context, presence: Online(Rival))
                .ChallengeAsync(Me, Rival, [], Now);

            Assert.True(result.Succeeded);
            Assert.Equal(
                "abandoned",
                (await context.Matches.SingleAsync(m => m.Id == received.Id)).Status
            );
        }

        /// <summary>A fixed instant, so the twenty-minute window is arithmetic rather than a race with the clock.</summary>
        private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static ChallengeService Service(
            TripleTriadContext context,
            RecordingPlayerNotifier? notifier = null,
            IPlayerPresence? presence = null
        ) => FriendshipTestHarness.CreateChallengeService(context, notifier, presence);

        /// <summary>Presence with these logins connected — the "is this friend online" half of the guard.</summary>
        private static ConnectionPresence Online(params string[] logins)
        {
            var presence = new ConnectionPresence();

            foreach (var login in logins)
            {
                presence.AddConnection($"c-{login}", login);
            }

            return presence;
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedAsync(TripleTriadContext context, params string[] logins)
        {
            foreach (var login in logins)
            {
                context.Players.Add(
                    new Player
                    {
                        Login = login,
                        Email = $"{login}@example.com",
                        PasswordHash = "hash",
                    }
                );
            }

            await context.SaveChangesAsync();
        }

        /// <summary>An accepted friendship, in the canonical order the table's unique index wants.</summary>
        private static async Task BefriendAsync(
            TripleTriadContext context,
            string first,
            string second
        )
        {
            var (a, b) =
                string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);

            context.Friendships.Add(
                new Friendship
                {
                    PlayerA = a,
                    PlayerB = b,
                    Status = FriendshipStatus.Accepted,
                    RequestedBy = a,
                    CreatedAt = DateTime.UtcNow,
                    RespondedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>A match in progress, so the "is busy" guard has something to find.</summary>
        private static async Task SeedActiveMatchAsync(
            TripleTriadContext context,
            string player1,
            string player2
        )
        {
            context.Matches.Add(
                new Match
                {
                    Player1Id = player1,
                    Player2Id = player2,
                    CurrentPlayerTurn = player1,
                    Status = "active",
                    ActivatedAt = DateTime.UtcNow,
                    Player1Score = 5,
                    Player2Score = 5,
                }
            );

            await context.SaveChangesAsync();
        }
    }
}
