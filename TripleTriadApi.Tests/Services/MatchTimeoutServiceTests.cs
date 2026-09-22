using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for the timeout sweep: the waiting match nobody joined (S1), the hand that never arrived (S2), the
    /// match nobody moved in (S3/S4), what it leaves alone, and that a second pass settles and pays nobody twice.
    /// Also covers the read-only state a client polls (<see cref="MatchStateService.GetStateAsync"/>), which must
    /// agree with the sweep on every deadline.
    ///
    /// `SweepAsync` takes its dependencies and the current time explicitly (no host, no timer), so the deadlines are
    /// driven with fixed timestamps over an EF InMemory database, and the SignalR pushes are recorded by
    /// <see cref="RecordingMatchNotifier"/>.
    /// </summary>
    public class MatchTimeoutServiceTests
    {
        private const string PlayerOne = "argel";
        private const string PlayerTwo = "squall";

        /// <summary>Player 1's five cards.</summary>
        private static readonly int[] FirstHand = [1, 2, 3, 4, 5];

        /// <summary>Player 2's five cards (disjoint ids, so a hand can never double up).</summary>
        private static readonly int[] SecondHand = [6, 7, 8, 9, 10];

        [Fact]
        public async Task Sweep_AbandonsAWaitingMatchPastItsDeadline()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);
            var match = await AddMatchAsync(
                context,
                status: "waiting",
                createdAt: now - MatchTimeouts.WaitingForOpponent - TimeSpan.FromMinutes(1)
            );

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            Assert.Equal("abandoned", await StatusOfAsync(context, match.Id));

            // Out of the waiting list, and out of its creator's way: the guard that blocks a new search sees nothing.
            Assert.Empty(await gameRepository.GetWaitingMatchesAsync());
            Assert.Null(await gameRepository.GetActiveMatchForPlayerAsync(PlayerOne));

            var abandoned = Assert.Single(notifier.Abandoned);
            Assert.Equal(match.Id, abandoned.MatchId);
            await AssertUnsettledAsync(context, match.Id);
        }

        [Fact]
        public async Task Sweep_AbandonsAnActiveMatchWhosePickNeverCame()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(15),
                activatedAt: now - MatchTimeouts.HandPick - TimeSpan.FromMinutes(1)
            );

            // Player 1 filed a hand, player 2 never did — the match was created for a player who picks later.
            await AddHandAsync(context, match.Id, PlayerOne, FirstHand);

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            Assert.Equal("abandoned", await StatusOfAsync(context, match.Id));
            Assert.Single(notifier.Abandoned);
            Assert.Empty(notifier.Completed);
            await AssertUnsettledAsync(context, match.Id);
        }

        [Fact]
        public async Task Sweep_AwardsAForfeit_WhenNobodyMoved()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(15),
                activatedAt: now - MatchTimeouts.TurnIdle - TimeSpan.FromMinutes(1),
                currentPlayerTurn: PlayerTwo
            );

            // Both hands are in but the board is still empty: player 2 never made a move.
            await AddHandAsync(context, match.Id, PlayerOne, FirstHand);
            await AddHandAsync(context, match.Id, PlayerTwo, SecondHand);

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            // The player who is not on the clock takes it, and the reward table is applied exactly once.
            var settled = await ReadAsync(context, match.Id);
            Assert.Equal("completed", settled.Status);
            Assert.Equal(PlayerOne, settled.WinnerId);
            Assert.NotNull(settled.CompletedAt);

            // The forfeit leaves the scores exactly as they were — no card was played — so a client reads the outcome
            // from `WinnerId` and not from a 5-5 score line (which would look like a draw).
            Assert.Equal(GameLogicService.HandSize, settled.Player1Score);
            Assert.Equal(GameLogicService.HandSize, settled.Player2Score);

            Assert.Equal(MatchRewardService.WinCoins, await CoinsAsync(context, PlayerOne));
            Assert.Equal(MatchRewardService.LossCoins, await CoinsAsync(context, PlayerTwo));

            var completed = Assert.Single(notifier.Completed);
            Assert.Equal("timeout", completed.Reason);
            Assert.Empty(notifier.Abandoned);
        }

        [Fact]
        public async Task Sweep_AwardsAForfeit_WhenPlayStalled()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(30),
                activatedAt: now - TimeSpan.FromMinutes(30),
                currentPlayerTurn: PlayerTwo
            );

            await AddHandAsync(context, match.Id, PlayerOne, FirstHand);
            await AddHandAsync(context, match.Id, PlayerTwo, SecondHand);

            // Two cards went down early on and nothing has happened since: the newest placement is the clock.
            await AddPlacementAsync(
                context,
                match.Id,
                cardId: FirstHand[0],
                playerId: PlayerOne,
                placedAt: now - TimeSpan.FromMinutes(25)
            );
            await AddPlacementAsync(
                context,
                match.Id,
                cardId: SecondHand[0],
                playerId: PlayerTwo,
                x: 1,
                placedAt: now - MatchTimeouts.TurnIdle - TimeSpan.FromMinutes(1)
            );

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            var settled = await ReadAsync(context, match.Id);
            Assert.Equal("completed", settled.Status);
            Assert.Equal(PlayerOne, settled.WinnerId);
            Assert.Equal(MatchRewardService.WinCoins, await CoinsAsync(context, PlayerOne));
            Assert.Equal("timeout", Assert.Single(notifier.Completed).Reason);
        }

        [Fact]
        public async Task Sweep_LeavesFreshMatchesAlone()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);

            // Inside its ten minutes: some other client may still join this one.
            var waiting = await AddMatchAsync(
                context,
                status: "waiting",
                createdAt: now - TimeSpan.FromMinutes(1)
            );

            // Active a minute ago and one hand short: the pick is still in time.
            var picking = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(30),
                activatedAt: now - TimeSpan.FromMinutes(1)
            );
            await AddHandAsync(context, picking.Id, PlayerOne, FirstHand);

            // Active half an hour ago, but somebody moved a minute ago — the newest placement is the clock, so this
            // match is not stalled even though it has been open far longer than the idle timeout.
            var playing = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(30),
                activatedAt: now - TimeSpan.FromMinutes(30),
                currentPlayerTurn: PlayerTwo
            );
            await AddHandAsync(context, playing.Id, PlayerOne, FirstHand);
            await AddHandAsync(context, playing.Id, PlayerTwo, SecondHand);
            await AddPlacementAsync(
                context,
                playing.Id,
                cardId: FirstHand[0],
                playerId: PlayerOne,
                placedAt: now - TimeSpan.FromMinutes(1)
            );

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            Assert.Equal("waiting", await StatusOfAsync(context, waiting.Id));
            Assert.Equal("active", await StatusOfAsync(context, picking.Id));
            Assert.Equal("active", await StatusOfAsync(context, playing.Id));
            Assert.Empty(notifier.Abandoned);
            Assert.Empty(notifier.Completed);
            await AssertUnsettledAsync(context, playing.Id);
        }

        [Fact]
        public async Task Sweep_IsIdempotent()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);

            var waiting = await AddMatchAsync(
                context,
                status: "waiting",
                createdAt: now - MatchTimeouts.WaitingForOpponent - TimeSpan.FromMinutes(5)
            );

            var stalled = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(30),
                activatedAt: now - MatchTimeouts.TurnIdle - TimeSpan.FromMinutes(1),
                currentPlayerTurn: PlayerTwo
            );
            await AddHandAsync(context, stalled.Id, PlayerOne, FirstHand);
            await AddHandAsync(context, stalled.Id, PlayerTwo, SecondHand);

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            var coinsAfterFirst = await CoinsAsync(context, PlayerOne);
            var completedAfterFirst = notifier.Completed.Count;
            var abandonedAfterFirst = notifier.Abandoned.Count;
            Assert.Equal(MatchRewardService.WinCoins, coinsAfterFirst);
            Assert.Equal(1, completedAfterFirst);

            // A much later pass finds both matches in a terminal status, so nothing is settled — or paid — twice.
            await MatchTimeoutService.SweepAsync(
                gameRepository,
                rewards,
                notifier,
                now + TimeSpan.FromHours(1)
            );

            Assert.Equal("completed", await StatusOfAsync(context, stalled.Id));
            Assert.Equal(PlayerOne, (await ReadAsync(context, stalled.Id)).WinnerId);
            Assert.Equal(coinsAfterFirst, await CoinsAsync(context, PlayerOne));
            Assert.Equal(MatchRewardService.LossCoins, await CoinsAsync(context, PlayerTwo));

            // Exactly one push each, both from the first pass.
            Assert.Equal(1, completedAfterFirst);
            Assert.Equal(1, abandonedAfterFirst);
            Assert.Equal(completedAfterFirst, notifier.Completed.Count);
            Assert.Equal(abandonedAfterFirst, notifier.Abandoned.Count);
            Assert.Equal("abandoned", await StatusOfAsync(context, waiting.Id));
        }

        [Fact]
        public async Task Sweep_LeavesARunningMatchAlone_AfterCardsArePlayed()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, rewards, notifier) = CreateSweep(context);

            // A game under way: both hands are in, four of their cards are already on the board (which marks those
            // hand rows used) and the last placement was a minute ago.
            var match = await AddMatchAsync(
                context,
                status: "active",
                createdAt: now - TimeSpan.FromMinutes(30),
                activatedAt: now - TimeSpan.FromMinutes(30),
                currentPlayerTurn: PlayerTwo
            );
            await AddHandAsync(context, match.Id, PlayerOne, FirstHand, usedCards: 3);
            await AddHandAsync(context, match.Id, PlayerTwo, SecondHand, usedCards: 1);
            await AddPlacementAsync(
                context,
                match.Id,
                cardId: FirstHand[0],
                playerId: PlayerOne,
                placedAt: now - TimeSpan.FromMinutes(1)
            );

            await MatchTimeoutService.SweepAsync(gameRepository, rewards, notifier, now);

            // A card leaving the hand for the board must never read as "the hand was never picked" (S2): the match is
            // still being played, so the sweep leaves it alone.
            Assert.Equal("active", await StatusOfAsync(context, match.Id));
            Assert.Empty(notifier.Abandoned);
            Assert.Empty(notifier.Completed);
            await AssertUnsettledAsync(context, match.Id);
        }

        [Fact]
        public async Task GetState_KeepsHandsReady_AfterCardsArePlayed()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, _, _) = CreateSweep(context);
            var state = new MatchStateService(gameRepository);

            // The hand is filed once: using a card marks the row played, it does not remove it from the hand.
            var match = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - TimeSpan.FromMinutes(1)
            );
            await AddHandAsync(context, match.Id, PlayerOne, FirstHand, usedCards: 4);
            await AddHandAsync(context, match.Id, PlayerTwo, SecondHand, usedCards: 2);

            var read = await state.GetStateAsync(match, now);

            Assert.True(read.HandsReady);
            Assert.False(read.TimedOut);
        }

        [Fact]
        public async Task GetState_ReportsTimedOutForEachDeadline()
        {
            using var context = CreateContext();
            var now = DateTime.UtcNow;
            await SeedAsync(context);
            var (gameRepository, _, _) = CreateSweep(context);
            var state = new MatchStateService(gameRepository);

            // S1: a waiting match nobody joined, ten minutes on.
            var waiting = await AddMatchAsync(
                context,
                status: "waiting",
                createdAt: now - MatchTimeouts.WaitingForOpponent - TimeSpan.FromMinutes(1)
            );
            var waitingState = await state.GetStateAsync(waiting, now);
            Assert.True(waitingState.TimedOut);
            Assert.False(waitingState.HandsReady);

            // S2: active, and the second hand never arrived.
            var picking = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - MatchTimeouts.HandPick - TimeSpan.FromMinutes(1)
            );
            await AddHandAsync(context, picking.Id, PlayerOne, FirstHand);
            var pickingState = await state.GetStateAsync(picking, now);
            Assert.True(pickingState.TimedOut);
            Assert.False(pickingState.HandsReady);

            // S3: both hands filed, and nobody has moved since it went active.
            var idle = await AddMatchAsync(
                context,
                status: "active",
                activatedAt: now - MatchTimeouts.TurnIdle - TimeSpan.FromMinutes(1)
            );
            await AddHandAsync(context, idle.Id, PlayerOne, FirstHand);
            await AddHandAsync(context, idle.Id, PlayerTwo, SecondHand);
            var idleState = await state.GetStateAsync(idle, now);
            Assert.True(idleState.TimedOut);
            Assert.True(idleState.HandsReady);

            // A match that just went active is neither ready nor out of time.
            var freshPicking = await AddMatchAsync(context, status: "active");
            var freshState = await state.GetStateAsync(freshPicking, now);
            Assert.False(freshState.TimedOut);
            Assert.False(freshState.HandsReady);
        }

        /// <summary>The three collaborators the sweep writes through, wired to this test's InMemory database.</summary>
        private static (
            GameRepository GameRepository,
            MatchRewardService Rewards,
            RecordingMatchNotifier Notifier
        ) CreateSweep(TripleTriadContext context)
        {
            return (
                new GameRepository(context),
                new MatchRewardService(new PlayerRepository(context)),
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
        /// One match row. A waiting match has no opponent yet; every other match has both players. Hands and
        /// placements are filed by the caller, so each test states exactly how far a match got before the clock ran
        /// out.
        /// </summary>
        private static async Task<Match> AddMatchAsync(
            TripleTriadContext context,
            string status,
            DateTime? createdAt = null,
            DateTime? activatedAt = null,
            string? currentPlayerTurn = null
        )
        {
            var match = new Match
            {
                Player1Id = PlayerOne,
                Player2Id = status == "waiting" ? string.Empty : PlayerTwo,
                CurrentPlayerTurn = currentPlayerTurn ?? PlayerOne,
                Status = status,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                ActivatedAt = activatedAt,
                Player1Score = GameLogicService.HandSize,
                Player2Score = GameLogicService.HandSize,
            };

            context.Matches.Add(match);
            await context.SaveChangesAsync();

            return match;
        }

        /// <summary>Files a full hand (five rows) for one player; <paramref name="usedCards"/> of them are marked as
        /// already played, which is what a match in progress looks like.</summary>
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

        /// <summary>One card on the board, aged by <paramref name="placedAt"/> — the stall clock's reference.</summary>
        private static async Task AddPlacementAsync(
            TripleTriadContext context,
            int matchId,
            int cardId,
            string playerId,
            DateTime placedAt,
            int x = 0,
            int y = 0
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

        /// <summary>Both players and the catalogue the hands and placements point at.</summary>
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
                    Login = PlayerTwo,
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

        private static async Task<Match> ReadAsync(TripleTriadContext context, int matchId) =>
            await context.Matches.SingleAsync(stored => stored.Id == matchId);

        private static async Task<string> StatusOfAsync(TripleTriadContext context, int matchId) =>
            await context
                .Matches.Where(match => match.Id == matchId)
                .Select(match => match.Status)
                .SingleAsync();

        private static async Task<int> CoinsAsync(TripleTriadContext context, string login) =>
            await context
                .Players.Where(player => player.Login == login)
                .Select(player => player.Coins)
                .SingleAsync();

        /// <summary>What a match nobody could play must not have earned: no winner, no coins, no counters.</summary>
        private static async Task AssertUnsettledAsync(TripleTriadContext context, int matchId)
        {
            var match = await ReadAsync(context, matchId);
            Assert.Null(match.WinnerId);
            Assert.Null(match.CompletedAt);

            foreach (var player in await context.Players.ToListAsync())
            {
                Assert.Equal(0, player.Coins);
                Assert.Equal(0, player.Experience);
                Assert.Equal(0, player.Wins);
                Assert.Equal(0, player.Losses);
                Assert.Equal(0, player.Ties);
            }
        }

        /// <summary>Records what the sweep pushes, standing in for the SignalR hub (tests have no web host).</summary>
        private sealed class RecordingMatchNotifier : IMatchNotifier
        {
            /// <summary>Matches abandoned, with the reason the client is told.</summary>
            public List<(int MatchId, string Reason)> Abandoned { get; } = [];

            /// <summary>Matches settled early (a forfeit), with the push reason.</summary>
            public List<(int MatchId, string Reason)> Completed { get; } = [];

            public Task HandReadyAsync(int matchId) => Task.CompletedTask;

            public Task AbandonedAsync(int matchId, string reason)
            {
                Abandoned.Add((matchId, reason));
                return Task.CompletedTask;
            }

            public Task CompletedEarlyAsync(
                Match match,
                MatchRewardService.MatchRewardResult? rewards,
                string reason
            )
            {
                Completed.Add((match.Id, reason));
                return Task.CompletedTask;
            }
        }
    }
}
