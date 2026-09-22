using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for the CPU's turn engine: it plays exactly one legal move once the thinking time has passed — through
    /// the shared play pipeline, so out-of-turn and illegal moves are impossible — pushes it to the match group, and
    /// leaves every other match alone.
    ///
    /// `AdvanceAsync` takes its dependencies and the current time explicitly (no host, no timer), so the clocks are
    /// driven with fixed timestamps over an EF InMemory database and the pushes are recorded instead of broadcast.
    /// </summary>
    public class CpuTurnServiceTests
    {
        private const string PlayerOne = "argel";

        /// <summary>Player 1's five cards.</summary>
        private static readonly int[] HumanHand = [1, 2, 3, 4, 5];

        /// <summary>The CPU's five cards (disjoint ids, so a hand can never double up).</summary>
        private static readonly int[] CpuHand = [6, 7, 8, 9, 10];

        /// <summary>One of the human's two corner cards in the capture test: rank 1, so a CPU card beats it.</summary>
        private const int HumanCornerA = 21;

        /// <summary>
        /// The other corner. The CPU's own card sits between the two, so no single move can take both — which is what
        /// makes a card that changed hands without being next to the played square a bug rather than a capture.
        /// </summary>
        private const int HumanCornerB = 22;

        [Fact]
        public async Task Advance_PlaysTheDueMove_AndPushesIt()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1),
                currentPlayerTurn: CpuOpponent.Login
            );
            await AddHandAsync(context, match.Id, PlayerOne, HumanHand, usedCards: 1);
            await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand);
            await AddPlacementAsync(
                context,
                match.Id,
                cardId: HumanHand[0],
                playerId: PlayerOne,
                x: 1,
                y: 1,
                placedAt: now - TimeSpan.FromSeconds(10)
            );

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            var board = await context
                .CardPlacements.Where(placement => placement.MatchId == match.Id)
                .ToListAsync();
            var reply = Assert.Single(board, placement => placement.PlayerId == CpuOpponent.Login);

            // A legal move: the occupied cell was avoided, the card left its hand, and the turn is the human's again.
            Assert.NotEqual((1, 1), (reply.X, reply.Y));
            Assert.Equal(
                4,
                await context.PlayerHands.CountAsync(hand =>
                    hand.PlayerId == CpuOpponent.Login && !hand.IsUsed
                )
            );

            var settled = await context.Matches.SingleAsync(stored => stored.Id == match.Id);
            Assert.Equal(PlayerOne, settled.CurrentPlayerTurn);
            Assert.Equal("active", settled.Status);

            var push = Assert.Single(notifier.Moves);
            Assert.Equal(CpuOpponent.Login, push.PlayerId);
            Assert.Equal(reply.CardId, push.CardId);
            Assert.Equal((reply.X, reply.Y), (push.X, push.Y));
            Assert.False(push.Result.IsGameComplete);
            Assert.Empty(notifier.Other);
        }

        [Fact]
        public async Task Advance_WaitsForTheThinkTime()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1),
                currentPlayerTurn: CpuOpponent.Login
            );
            await AddHandAsync(context, match.Id, PlayerOne, HumanHand, usedCards: 1);
            await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand);
            await AddPlacementAsync(
                context,
                match.Id,
                cardId: HumanHand[0],
                playerId: PlayerOne,
                x: 1,
                y: 1,
                placedAt: now - TimeSpan.FromMilliseconds(500)
            );

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            // Half a second after the human's move the CPU is still "thinking": nothing was played, nothing pushed.
            Assert.Equal(
                1,
                await context.CardPlacements.CountAsync(placement => placement.MatchId == match.Id)
            );
            Assert.Equal(
                CpuOpponent.Login,
                (await context.Matches.SingleAsync(stored => stored.Id == match.Id))
                    .CurrentPlayerTurn
            );
            Assert.Empty(notifier.Moves);
        }

        [Fact]
        public async Task Advance_IgnoresTheHumansTurn()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1),
                currentPlayerTurn: PlayerOne
            );
            await AddHandAsync(context, match.Id, PlayerOne, HumanHand, usedCards: 1);
            await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand);

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            Assert.Empty(await context.CardPlacements.ToListAsync());
            Assert.Empty(notifier.Moves);
        }

        [Fact]
        public async Task Advance_IgnoresSettledMatches()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);

            // A CPU seat on a match that is not being played: waiting, completed and abandoned rows all look "ready"
            // by turn alone, and none of them may be touched.
            foreach (var status in new[] { "waiting", "completed", "abandoned" })
            {
                var match = await AddMatchAsync(
                    context,
                    status: status,
                    activatedAt: now - TimeSpan.FromMinutes(10),
                    currentPlayerTurn: CpuOpponent.Login
                );
                await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand);
            }

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            Assert.Empty(await context.CardPlacements.ToListAsync());
            Assert.Empty(notifier.Moves);
        }

        [Fact]
        public async Task Advance_IsIdempotentForTheSameTurn()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1),
                currentPlayerTurn: CpuOpponent.Login
            );
            await AddHandAsync(context, match.Id, PlayerOne, HumanHand, usedCards: 1);
            await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand);
            await AddPlacementAsync(
                context,
                match.Id,
                cardId: HumanHand[0],
                playerId: PlayerOne,
                x: 1,
                y: 1,
                placedAt: now - TimeSpan.FromSeconds(10)
            );

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);
            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            // One move per turn: the second pass finds the human on the clock and plays nothing.
            Assert.Equal(
                2,
                await context.CardPlacements.CountAsync(placement => placement.MatchId == match.Id)
            );
            Assert.Single(notifier.Moves);
        }

        [Fact]
        public async Task Advance_CompletesTheMatch_AndPushesGameCompleted()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1),
                currentPlayerTurn: CpuOpponent.Login
            );

            // The board is one card short and the CPU holds exactly one: its move finishes the match.
            await AddHandAsync(context, match.Id, PlayerOne, HumanHand);
            await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand, usedCards: 4);

            var cells = 0;
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    if (x == 2 && y == 2)
                    {
                        continue;
                    }

                    await AddPlacementAsync(
                        context,
                        match.Id,
                        cardId: HumanHand[cells % HumanHand.Length],
                        playerId: cells % 2 == 0 ? PlayerOne : CpuOpponent.Login,
                        x: x,
                        y: y,
                        placedAt: now - TimeSpan.FromSeconds(30 - cells)
                    );
                    cells++;
                }
            }

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            var settled = await context.Matches.SingleAsync(stored => stored.Id == match.Id);
            Assert.Equal("completed", settled.Status);
            Assert.NotNull(settled.CompletedAt);
            Assert.Equal(
                9,
                await context.CardPlacements.CountAsync(placement => placement.MatchId == match.Id)
            );

            var push = Assert.Single(notifier.Moves);
            Assert.True(push.Result.IsGameComplete);
            Assert.NotNull(push.Rewards);

            // The human is paid as usual (the CPU's own half is skipped), so the win/loss/tie table is untouched by it.
            Assert.True(await CoinsAsync(context, PlayerOne) > 0);
        }

        [Fact]
        public void ThinkTime_StaysInsideTheWindow_AndVariesPerMove()
        {
            var delays = new List<TimeSpan>();

            for (var matchId = 1; matchId <= 5; matchId++)
            {
                for (var placements = 0; placements < 9; placements++)
                {
                    var think = CpuOpponent.ThinkTime(matchId, placements);

                    Assert.True(think >= CpuOpponent.MinThink);
                    Assert.True(think <= CpuOpponent.MaxThink);
                    delays.Add(think);
                }
            }

            // Derived, but not constant: the delay has to differ from move to move to read like somebody thinking.
            Assert.True(delays.Distinct().Count() > 3);
        }

        [Fact]
        public void MoveDueAt_MeasuresFromTheNewestPlacement_ThenActivation()
        {
            var activated = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
            var match = new Match { Id = 4, Status = "active", ActivatedAt = activated };

            // An empty board falls back to the activation stamp…
            Assert.Equal(
                activated + CpuOpponent.ThinkTime(match.Id, 0),
                CpuOpponent.MoveDueAt(match, [])
            );

            // …and once cards are down, the newest placement is the clock.
            var placements = new List<CardPlacement>
            {
                new() { PlacedAt = activated.AddSeconds(5), X = 0, Y = 0 },
                new() { PlacedAt = activated.AddSeconds(9), X = 1, Y = 1 },
            };

            Assert.Equal(
                activated.AddSeconds(9) + CpuOpponent.ThinkTime(match.Id, 2),
                CpuOpponent.MoveDueAt(match, placements)
            );

            // A match with neither has nothing to measure from, so the CPU leaves it alone.
            var stale = new Match { Id = 5, Status = "active", ActivatedAt = null };
            Assert.Null(CpuOpponent.MoveDueAt(stale, []));
        }

        /// <summary>
        /// The CPU evaluates every candidate it has — each of its cards against each empty cell — and only the move it
        /// actually plays may flip anything. When a candidate was resolved on the match's own placements, the flips it
        /// *would* have made stayed made (and were saved with the real move), so cards next to squares the CPU merely
        /// considered changed hands along with the ones the move really took.
        /// </summary>
        [Fact]
        public async Task Advance_FlipsOnlyTheCardsItsMoveCaptured()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            await SeedWeakCornersAsync(context);
            var (gameRepository, gamePlayService, selector, notifier) = CreateEngine(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1),
                currentPlayerTurn: CpuOpponent.Login
            );
            // The human's two rank-1 cards sit in opposite corners with the CPU's own card between them: no single
            // move can take both, but the candidates around each corner each take one.
            await AddHandAsync(
                context,
                match.Id,
                PlayerOne,
                [HumanCornerA, HumanCornerB, 1, 2, 3],
                usedCards: 2
            );
            await AddHandAsync(context, match.Id, CpuOpponent.Login, CpuHand, usedCards: 1);
            await AddPlacementAsync(
                context,
                match.Id,
                HumanCornerA,
                PlayerOne,
                x: 0,
                y: 0,
                placedAt: now - TimeSpan.FromSeconds(10)
            );
            await AddPlacementAsync(
                context,
                match.Id,
                HumanCornerB,
                PlayerOne,
                x: 2,
                y: 2,
                placedAt: now - TimeSpan.FromSeconds(10)
            );
            await AddPlacementAsync(
                context,
                match.Id,
                CpuHand[0],
                CpuOpponent.Login,
                x: 1,
                y: 1,
                placedAt: now - TimeSpan.FromSeconds(9)
            );

            await AdvanceAsync(gameRepository, gamePlayService, selector, notifier, now);

            var played = Assert.Single(notifier.Moves);
            var board = await context
                .CardPlacements.Where(placement => placement.MatchId == match.Id)
                .ToListAsync();

            // Every card that changed hands is a neighbour of the square the move was played on…
            var flipped = board
                .Where(placement =>
                    placement.PlayerId == PlayerOne && placement.Owner == CpuOpponent.Login
                )
                .ToList();
            Assert.NotEmpty(flipped);
            Assert.All(
                flipped,
                placement =>
                    Assert.Equal(
                        1,
                        Math.Abs(placement.X - played.X) + Math.Abs(placement.Y - played.Y)
                    )
            );

            // …and the corner the CPU only considered taking is still the human's.
            Assert.Equal(
                new[] { (2, 2) },
                board
                    .Where(placement =>
                        placement.PlayerId == PlayerOne && placement.Owner == PlayerOne
                    )
                    .Select(placement => (placement.X, placement.Y))
            );
        }

        /// <summary>One pass of the engine with the scripted move choice these tests expect.</summary>
        private static async Task AdvanceAsync(
            GameRepository gameRepository,
            GamePlayService gamePlayService,
            CpuMoveSelector selector,
            RecordingMatchNotifier notifier,
            DateTime now
        ) =>
            await CpuTurnService.AdvanceAsync(
                gameRepository,
                gamePlayService,
                selector,
                notifier,
                new FixedRandom(0),
                now
            );

        /// <summary>The engine's collaborators, wired to this test's InMemory database.</summary>
        private static (
            GameRepository GameRepository,
            GamePlayService GamePlayService,
            CpuMoveSelector Selector,
            RecordingMatchNotifier Notifier
        ) CreateEngine(TripleTriadContext context)
        {
            var gameLogic = new GameLogicService();

            return (
                new GameRepository(context),
                new GamePlayService(
                    new GameRepository(context),
                    gameLogic,
                    new MatchRewardService(new PlayerRepository(context))
                ),
                new CpuMoveSelector(gameLogic),
                new RecordingMatchNotifier()
            );
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        /// <summary>
        /// One match row with the CPU in player 2. The caller states the status, the activation stamp and whose turn
        /// it is, which is everything the engine reads.
        /// </summary>
        private static async Task<Match> AddMatchAsync(
            TripleTriadContext context,
            string status,
            DateTime? activatedAt = null,
            string? currentPlayerTurn = null,
            string player2Id = CpuOpponent.Login
        )
        {
            var match = new Match
            {
                Player1Id = PlayerOne,
                Player2Id = player2Id,
                CurrentPlayerTurn = currentPlayerTurn ?? PlayerOne,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ActivatedAt = activatedAt,
                Player1Score = GameLogicService.HandSize,
                Player2Score = GameLogicService.HandSize,
            };

            context.Matches.Add(match);
            await context.SaveChangesAsync();

            return match;
        }

        /// <summary>Files a full hand for one player, marking the first <paramref name="usedCards"/> as played.</summary>
        private static async Task AddHandAsync(
            TripleTriadContext context,
            int matchId,
            string playerId,
            int[] cardIds,
            int usedCards = 0
        )
        {
            for (var index = 0; index < cardIds.Length; index++)
            {
                context.PlayerHands.Add(
                    new PlayerHand
                    {
                        MatchId = matchId,
                        PlayerId = playerId,
                        CardId = cardIds[index],
                        IsUsed = index < usedCards,
                    }
                );
            }

            await context.SaveChangesAsync();
        }

        /// <summary>One card on the board, aged by <paramref name="placedAt"/> — the CPU's thinking clock.</summary>
        private static async Task AddPlacementAsync(
            TripleTriadContext context,
            int matchId,
            int cardId,
            string playerId,
            int x,
            int y,
            DateTime placedAt
        )
        {
            context.CardPlacements.Add(
                new CardPlacement
                {
                    MatchId = matchId,
                    CardId = cardId,
                    PlayerId = playerId,
                    Owner = playerId,
                    X = x,
                    Y = y,
                    PlacedAt = placedAt,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>Both players and the catalogue the hands, the placements and the rewards point at.</summary>
        private static async Task SeedAsync(TripleTriadContext context)
        {
            context.Players.Add(
                new Player
                {
                    Login = PlayerOne,
                    Email = "argel@example.com",
                    PasswordHash = "hash",
                    Coins = 0,
                }
            );
            context.Players.Add(
                new Player
                {
                    Login = "squall",
                    Email = "squall@example.com",
                    PasswordHash = "hash",
                    Coins = 0,
                }
            );

            for (var cardId = 1; cardId <= 10; cardId++)
            {
                context.Cards.Add(
                    new Card
                    {
                        Id = cardId,
                        Name = $"Card {cardId}",
                        Image = $"ff8-deck/card-{cardId}.jpg",
                        TopValue = 5,
                        RightValue = 5,
                        BottomValue = 5,
                        LeftValue = 5,
                        Element = [],
                        Level = 1,
                    }
                );
            }

            await context.SaveChangesAsync();
        }

        /// <summary>
        /// The two rank-1 cards the capture test puts in the corners: the seeded catalogue is rank 5 on every side, so
        /// these are the only cards on that board a CPU card actually beats.
        /// </summary>
        private static async Task SeedWeakCornersAsync(TripleTriadContext context)
        {
            foreach (var cardId in new[] { HumanCornerA, HumanCornerB })
            {
                context.Cards.Add(
                    new Card
                    {
                        Id = cardId,
                        Name = $"Card {cardId}",
                        Image = $"ff8-deck/card-{cardId}.jpg",
                        TopValue = 1,
                        RightValue = 1,
                        BottomValue = 1,
                        LeftValue = 1,
                        Element = [],
                        Level = 1,
                    }
                );
            }

            await context.SaveChangesAsync();
        }

        private static async Task<int> CoinsAsync(TripleTriadContext context, string login) =>
            await context
                .Players.Where(player => player.Login == login)
                .Select(player => player.Coins)
                .SingleAsync();

        /// <summary>Records what the engine pushes, standing in for the SignalR hub (tests have no web host).</summary>
        private sealed class RecordingMatchNotifier : IMatchNotifier
        {
            /// <summary>Moves the engine played, in order.</summary>
            public List<MovePush> Moves { get; } = [];

            /// <summary>The other pushes — none of them the engine's business, so they are asserted empty.</summary>
            public List<string> Other { get; } = [];

            public Task HandReadyAsync(int matchId)
            {
                Other.Add($"ready:{matchId}");
                return Task.CompletedTask;
            }

            public Task AbandonedAsync(int matchId, string reason)
            {
                Other.Add($"abandoned:{matchId}");
                return Task.CompletedTask;
            }

            public Task CompletedEarlyAsync(
                Match match,
                MatchRewardService.MatchRewardResult? rewards,
                string reason
            )
            {
                Other.Add($"completed:{match.Id}");
                return Task.CompletedTask;
            }

            public Task CardPlayedAsync(MovePush move)
            {
                Moves.Add(move);
                return Task.CompletedTask;
            }
        }

        /// <summary>One scripted answer, so the move the engine plays is the one these tests can assert on.</summary>
        private sealed class FixedRandom(int value) : IRandomSource
        {
            public int Next(int exclusiveMax) => value % Math.Max(exclusiveMax, 1);
        }
    }
}
