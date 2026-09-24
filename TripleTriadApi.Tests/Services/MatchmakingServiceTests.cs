using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for the matchmaking gate: a search either joins an opponent who is already waiting or starts one match to
    /// be found in — and two searches arriving at the same instant still produce <em>one</em> match rather than two
    /// players each holding their own and waiting for each other.
    ///
    /// The concurrency cases are the reason this file exists. They cannot be written against a single context, because
    /// two requests in production are two contexts: each task therefore builds its own, over a shared in-memory
    /// database, which is what makes the gate actually contended rather than decorative.
    /// </summary>
    public class MatchmakingServiceTests
    {
        private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task WithNobodyWaiting_StartsAMatchToBeFoundIn()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());
            var (service, _) = CreateService(context);

            var match = await service.FindOrCreateWaitingMatchAsync("argel", [MatchRule.Same], Now);

            Assert.Equal("argel", match.Player1Id);
            Assert.Equal(string.Empty, match.Player2Id);
            Assert.Equal("waiting", match.Status);
            Assert.Null(match.ActivatedAt);
            Assert.True(MatchRuleExtensions.SameSet([MatchRule.Same], match.Rules));
            Assert.Single(context.Matches);
        }

        [Fact]
        public async Task WithACompatibleMatchWaiting_JoinsItInsteadOfStartingAnother()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());
            var waiting = await SeedWaitingMatchAsync(context, "opponent", MatchRule.Same);
            var (service, _) = CreateService(context);

            var match = await service.FindOrCreateWaitingMatchAsync("argel", [MatchRule.Same], Now);

            // The same row, now active with both players seated: one match, not two.
            Assert.Equal(waiting.Id, match.Id);
            Assert.Equal("opponent", match.Player1Id);
            Assert.Equal("argel", match.Player2Id);
            Assert.Equal("active", match.Status);

            // The activation stamp is what both of the match's timeouts are measured from, so it has to be set here.
            Assert.Equal(Now, match.ActivatedAt);
            Assert.Single(context.Matches);
        }

        [Fact]
        public async Task WithOnlyDifferentRulesWaiting_StartsItsOwnMatch()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());
            var waiting = await SeedWaitingMatchAsync(context, "opponent", MatchRule.Plus);
            var (service, _) = CreateService(context);

            var match = await service.FindOrCreateWaitingMatchAsync("argel", [MatchRule.Same], Now);

            // A waiting match playing by other rules is somebody else's game, and is left exactly as it was.
            Assert.NotEqual(waiting.Id, match.Id);
            Assert.Equal("waiting", match.Status);
            Assert.Equal("waiting", (await context.Matches.FindAsync(waiting.Id))!.Status);
            Assert.Equal(2, await context.Matches.CountAsync());
        }

        [Fact]
        public async Task NeverJoinsItsOwnWaitingMatch()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());
            var own = await SeedWaitingMatchAsync(context, "argel", MatchRule.Same);
            var (service, _) = CreateService(context);

            var match = await service.FindOrCreateWaitingMatchAsync("argel", [MatchRule.Same], Now);

            // A player cannot be their own opponent. The earlier search is given up rather than joined, and a fresh
            // match is started — which is what the old client-side flow had to special-case with its own check.
            Assert.NotEqual(own.Id, match.Id);
            Assert.Equal("waiting", match.Status);
            Assert.Equal("abandoned", (await context.Matches.FindAsync(own.Id))!.Status);
            Assert.Equal(2, await context.Matches.CountAsync());
        }

        [Fact]
        public async Task GivesUpAnEarlierMatchInEitherSeat()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());
            var asPlayerOne = await SeedWaitingMatchAsync(context, "argel", MatchRule.Same);
            var asPlayerTwo = await SeedWaitingMatchAsync(
                context,
                "somebody-else",
                MatchRule.Same,
                player2: "argel"
            );
            var (service, notifier) = CreateService(context);

            await service.FindOrCreateWaitingMatchAsync("argel", [MatchRule.Same], Now);

            // A player may only ever be in one match, whichever seat they were sitting in — and whoever was waiting on
            // them is told, so they are not left waiting for a game that will never be played.
            Assert.Equal("abandoned", (await context.Matches.FindAsync(asPlayerOne.Id))!.Status);
            Assert.Equal("abandoned", (await context.Matches.FindAsync(asPlayerTwo.Id))!.Status);
            Assert.Equal(2, notifier.Abandoned.Count);
            Assert.All(
                notifier.Abandoned,
                push => Assert.Equal(MatchmakingService.AbandonedByNewSearchReason, push.Reason)
            );
        }

        [Fact]
        public async Task TwoSearchesArrivingTogether_ProduceOneMatch()
        {
            // The bug this guards. Both players used to read the waiting list before either had written anything, so
            // each started a match and then waited for the other — two rows, nobody playing. Both searches now run
            // under the gate, so the second one sees the first one's match and joins it.
            var databaseName = Guid.NewGuid().ToString();
            var root = new InMemoryDatabaseRoot();
            var players = new[] { "player-a", "player-b" };

            var seats = await Task.WhenAll(
                players.Select(login => Task.Run(() => SearchAsync(databaseName, root, login)))
            );

            using var verify = CreateContext(databaseName, root);
            var match = Assert.Single(await verify.Matches.ToListAsync());

            Assert.Equal("active", match.Status);

            // Both searches were seated in the same match — the point of the whole exercise.
            Assert.Equal(match.Id, Assert.Single(seats.Distinct()));
            Assert.NotEqual(match.Player1Id, match.Player2Id);
            Assert.Contains(match.Player1Id, players);
            Assert.Contains(match.Player2Id, players);
        }

        [Fact]
        public async Task FourSearchesArrivingTogether_PairUpAndNobodyIsLeftWaiting()
        {
            var databaseName = Guid.NewGuid().ToString();
            var root = new InMemoryDatabaseRoot();
            var players = new[] { "p1", "p2", "p3", "p4" };

            await Task.WhenAll(
                players.Select(login => Task.Run(() => SearchAsync(databaseName, root, login)))
            );

            using var verify = CreateContext(databaseName, root);
            var matches = await verify.Matches.ToListAsync();

            // Two games, both under way, and every player in exactly one of them. Who pairs with whom depends on the
            // order the gate lets them in, but the shape does not — and nobody is left stranded on a match of their
            // own, which is what the reported bug looked like.
            Assert.Equal(2, matches.Count);
            Assert.All(matches, match => Assert.Equal("active", match.Status));

            var seated = matches
                .SelectMany(match => new[] { match.Player1Id, match.Player2Id })
                .ToList();

            Assert.Equal(players.OrderBy(login => login), seated.OrderBy(login => login));
        }

        /// <summary>
        /// One search in its own context, as a request would be. Separate contexts are the point: sharing one would
        /// test the gate against a single connection and would not reproduce two requests in flight at all.
        /// </summary>
        private static async Task<int> SearchAsync(
            string databaseName,
            InMemoryDatabaseRoot root,
            string login
        )
        {
            using var context = CreateContext(databaseName, root);
            var (service, _) = CreateService(context);

            var match = await service.FindOrCreateWaitingMatchAsync(login, [MatchRule.Same], Now);

            return match.Id;
        }

        /// <summary>An opponent who is already waiting — the thing a search is meant to find.</summary>
        private static async Task<Match> SeedWaitingMatchAsync(
            TripleTriadContext context,
            string player1,
            MatchRule rule,
            string? player2 = null
        )
        {
            var match = new Match
            {
                Player1Id = player1,
                Player2Id = player2 ?? string.Empty,
                CurrentPlayerTurn = player1,
                Status = player2 is null ? "waiting" : "active",
                CreatedAt = Now.AddMinutes(-1),
                ActivatedAt = player2 is null ? null : Now.AddMinutes(-1),
                Rules = [rule],
            };

            context.Matches.Add(match);
            await context.SaveChangesAsync();

            return match;
        }

        private static (MatchmakingService Service, RecordingMatchNotifier Notifier) CreateService(
            TripleTriadContext context,
            RecordingMatchNotifier? notifier = null
        )
        {
            var matchNotifier = notifier ?? new RecordingMatchNotifier();

            return (
                new MatchmakingService(
                    new GameRepository(context),
                    matchNotifier,
                    context,
                    NullLogger<MatchmakingService>.Instance
                ),
                matchNotifier
            );
        }

        /// <summary>
        /// A context over a named in-memory database. The name (and root) is how several contexts see one store, which
        /// is what lets the concurrency tests reproduce two requests at once.
        /// </summary>
        private static TripleTriadContext CreateContext(string databaseName, InMemoryDatabaseRoot? root = null) =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(databaseName, root)
                    .Options
            );

        /// <summary>
        /// Records the pushes matchmaking makes — only abandonment here, which is the one this service sends. The rest
        /// of the interface is implemented so the notifier can stand in for the real one without a web host.
        /// </summary>
        private sealed class RecordingMatchNotifier : IMatchNotifier
        {
            /// <summary>Matches given up, with the reason the other player is shown.</summary>
            public List<(int MatchId, string Reason)> Abandoned { get; } = [];

            public Task AbandonedAsync(int matchId, string reason)
            {
                Abandoned.Add((matchId, reason));
                return Task.CompletedTask;
            }

            public Task HandReadyAsync(int matchId) => Task.CompletedTask;

            public Task CardPlayedAsync(MovePush move) => Task.CompletedTask;

            public Task CompletedEarlyAsync(
                Match match,
                MatchRewardService.MatchRewardResult? rewards,
                string reason
            ) => Task.CompletedTask;
        }
    }
}
