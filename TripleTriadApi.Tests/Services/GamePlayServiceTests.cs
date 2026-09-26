using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for the play pipeline's claim on a turn: a move is only ever written by the writer that took the turn, so
    /// one turn cannot be played twice and a board can never disagree with the hand row and the turn that describe it.
    ///
    /// The case that prompted them (plans/PLAN-016-one-writer-per-turn/plan.md): during a match against the CPU the AI
    /// placed the *same card* twice, in two different cells, while its hand row was spent only once — so the board
    /// showed four CPU cards against two unused hand rows, and the client (which counts the opponent's placements)
    /// said one card was left. Four placements with three spent rows is exactly what two writers of one turn leave
    /// behind, and nothing in the pipeline stopped them: the turn check is a read, and a snapshot taken before the
    /// other writer's move passes it.
    ///
    /// Two `GamePlayService`s over two `DbContext`s sharing **one** InMemory store stand in for the two backends on one
    /// database; the second one is handed the snapshot it loaded *before* the first move — which is what the change
    /// tracker does by itself for a row a context has already read.
    /// </summary>
    public class GamePlayServiceTests
    {
        private const string Human = "argel";

        /// <summary>One of the CPU's cards; the value-5 catalogue means no move captures anything.</summary>
        private const int CpuCard = 6;

        /// <summary>The CPU's other card, so a spent hand row is visible in the counts.</summary>
        private const int CpuSecondCard = 7;

        [Fact]
        public async Task PlayCard_RefusesTheSecondCardOfATurn_WhenItsWriterHoldsAStaleSnapshot()
        {
            var race = await RaceTwoWritersAsync(firstCard: CpuCard, secondCard: CpuSecondCard);

            Assert.True(race.First.IsSuccess);
            Assert.Equal(Human, race.First.UpdatedMatch!.CurrentPlayerTurn);

            // The second writer's card is its own and still unused, so nothing but the turn claim can refuse it. Without
            // the claim this move landed, and one turn held two cards.
            Assert.False(race.Second.IsSuccess);
            Assert.Equal("Not your turn", race.Second.ErrorMessage);

            await AssertOneCardOneHandRowAsync(race);
        }

        [Fact]
        public async Task PlayCard_RefusesACardAlreadyOnTheBoard_EvenFromAStaleSnapshot()
        {
            // The same race offering the *same* card — the reported match's shape. Its tell was arithmetic: the client
            // counted the CPU's cards from the placements (four, so "one card left"), while the database had spent only
            // three of the five hand rows. Either guard may refuse the second copy (the spent hand row, or the turn
            // claim); what must never happen is the second copy landing and a board that outruns its own hand.
            var race = await RaceTwoWritersAsync(firstCard: CpuCard, secondCard: CpuCard);

            Assert.False(race.Second.IsSuccess);

            await AssertOneCardOneHandRowAsync(race);
        }

        [Fact]
        public async Task PlayCard_PlaysTheMove_AndLeavesTheTurnWithTheOtherPlayer()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());

            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [1, 2]);

            var result = await CreatePlayService(context)
                .PlayCardAsync(match.Id, 1, x: 0, y: 0, playerId: Human);

            Assert.True(result.IsSuccess);
            Assert.Equal(CpuOpponent.Login, result.UpdatedMatch!.CurrentPlayerTurn);

            var placement = await context
                .CardPlacements.AsNoTracking()
                .SingleAsync(row => row.MatchId == match.Id);
            Assert.Equal((0, 0, Human), (placement.X, placement.Y, placement.PlayerId));

            // The turn was taken once and the card left the hand in the same write, so board and hand agree.
            var unused = await context
                .PlayerHands.AsNoTracking()
                .Where(hand => hand.MatchId == match.Id && hand.PlayerId == Human && !hand.IsUsed)
                .ToListAsync();
            Assert.Equal([2], unused.Select(hand => hand.CardId));
        }

        [Fact]
        public async Task PlayCard_RefusesTheMove_WhenTheMatchIsAlreadySettled()
        {
            using var context = CreateContext(Guid.NewGuid().ToString());

            await SeedAsync(context);
            // A settled match whose turn was left on the looser's side is the shape a stray client move arrives in.
            var match = await AddMatchAsync(context, status: "completed");
            await AddHandAsync(context, match.Id, CpuOpponent.Login, [CpuCard]);

            var result = await CreatePlayService(context)
                .PlayCardAsync(match.Id, CpuCard, x: 0, y: 0, playerId: CpuOpponent.Login);

            Assert.False(result.IsSuccess);
            Assert.Empty(
                await context
                    .CardPlacements.AsNoTracking()
                    .Where(row => row.MatchId == match.Id)
                    .ToListAsync()
            );
        }

        /// <summary>
        /// The two writers of one CPU turn, over one InMemory database: the first backend plays
        /// <paramref name="firstCard"/> (both cards belong to the CPU and are still in hand), and the second — whose
        /// context read the match *before* that move — plays <paramref name="secondCard"/> afterwards. The second
        /// context keeps the copy it read (the change tracker does not overwrite properties of a tracked entity), which
        /// is the snapshot a second backend on the same database is holding while the first one is mid-move.
        /// </summary>
        private static async Task<RaceOutcome> RaceTwoWritersAsync(int firstCard, int secondCard)
        {
            var database = Guid.NewGuid().ToString();
            using var firstBackend = CreateContext(database);
            using var secondBackend = CreateContext(database);

            await SeedAsync(firstBackend);
            var match = await AddMatchAsync(firstBackend, status: "active");
            await AddHandAsync(firstBackend, match.Id, CpuOpponent.Login, [CpuCard, CpuSecondCard]);

            // The snapshot the second writer will move from, taken before anything is played.
            await new GameRepository(secondBackend).GetMatchByIdAsync(match.Id);

            var first = await CreatePlayService(firstBackend)
                .PlayCardAsync(match.Id, firstCard, x: 2, y: 1, playerId: CpuOpponent.Login);
            var second = await CreatePlayService(secondBackend)
                .PlayCardAsync(match.Id, secondCard, x: 1, y: 2, playerId: CpuOpponent.Login);

            return new RaceOutcome(first, second, database, match.Id);
        }

        /// <summary>
        /// What the race must leave behind, whatever the second writer offered: one card from one hand row, and the turn
        /// with the other player. Two cards, or a card whose hand row was never spent, is the reported bug's shape.
        /// </summary>
        private static async Task AssertOneCardOneHandRowAsync(RaceOutcome race)
        {
            using var readBack = CreateContext(race.Database);

            var played = Assert.Single(
                await readBack
                    .CardPlacements.AsNoTracking()
                    .Where(placement =>
                        placement.MatchId == race.MatchId && placement.PlayerId == CpuOpponent.Login
                    )
                    .ToListAsync()
            );
            Assert.Equal((2, 1), (played.X, played.Y));

            Assert.Equal(
                1,
                await readBack
                    .PlayerHands.AsNoTracking()
                    .CountAsync(hand =>
                        hand.MatchId == race.MatchId
                        && hand.PlayerId == CpuOpponent.Login
                        && !hand.IsUsed
                    )
            );
            Assert.Equal(
                Human,
                (
                    await readBack
                        .Matches.AsNoTracking()
                        .SingleAsync(stored => stored.Id == race.MatchId)
                ).CurrentPlayerTurn
            );
        }

        /// <summary>One two-writer race: what each writer was told, and where to read the result back.</summary>
        private sealed record RaceOutcome(
            GamePlayService.PlayCardServiceResult First,
            GamePlayService.PlayCardServiceResult Second,
            string Database,
            int MatchId
        );

        /// <summary>The pipeline under test, wired to one context — one "backend".</summary>
        private static GamePlayService CreatePlayService(TripleTriadContext context) =>
            new(
                new GameRepository(context),
                new GameLogicService(),
                new MatchRewardService(new PlayerRepository(context))
            );

        /// <summary>
        /// One context per backend: the same InMemory database *name* is what makes them share a store, the way two
        /// app instances share one PostgreSQL database.
        /// </summary>
        private static TripleTriadContext CreateContext(string database) =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(database)
                    .Options
            );

        /// <summary>
        /// A match with the CPU in player 2 — the seat the sentinel plays. The caller states the status and whose turn
        /// it is, which is everything the pipeline and the claim read.
        /// </summary>
        private static async Task<Match> AddMatchAsync(
            TripleTriadContext context,
            string status,
            string? currentPlayerTurn = CpuOpponent.Login
        )
        {
            var match = new Match
            {
                Player1Id = Human,
                Player2Id = CpuOpponent.Login,
                CurrentPlayerTurn = currentPlayerTurn,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ActivatedAt = DateTime.UtcNow,
                Player1Score = GameLogicService.HandSize,
                Player2Score = GameLogicService.HandSize,
            };

            context.Matches.Add(match);
            await context.SaveChangesAsync();

            return match;
        }

        /// <summary>Files a hand of unused cards for one player.</summary>
        private static async Task AddHandAsync(
            TripleTriadContext context,
            int matchId,
            string playerId,
            int[] cardIds
        )
        {
            foreach (var cardId in cardIds)
            {
                context.PlayerHands.Add(
                    new PlayerHand
                    {
                        MatchId = matchId,
                        PlayerId = playerId,
                        CardId = cardId,
                        IsUsed = false,
                    }
                );
            }

            await context.SaveChangesAsync();
        }

        /// <summary>
        /// The two seats and a catalogue of rank-5 cards: nothing in these tests is about a battle, only about who may
        /// write a move.
        /// </summary>
        private static async Task SeedAsync(TripleTriadContext context)
        {
            context.Players.Add(
                new Player
                {
                    Login = Human,
                    Email = "argel@example.com",
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
    }
}
