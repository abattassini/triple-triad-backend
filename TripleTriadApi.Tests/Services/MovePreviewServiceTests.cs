using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for the turn preview: what a player may play this turn, and what each of those moves would do
    /// (plans/PLAN-017-instant-move-feedback/plan.md — the board's own answer, so a client can land a dropped card
    /// without waiting for the round trip).
    ///
    /// Two things have to hold for it to be safe to render on sight. First, it answers **only** the player whose turn it
    /// is — the list is built from that player's hand, which is hidden information, so answering anybody else (or
    /// answering before their turn) would leak it. Second, every move in the list has to be **what actually happens when
    /// it is played**: that is what pins the preview to the real pipeline, and it is checked here by replaying every
    /// enumerated move on a fresh copy of the board and comparing the whole result.
    /// </summary>
    public class MovePreviewServiceTests
    {
        private const string Human = "argel";
        private const string Stranger = "novice";

        /// <summary>Rank 5 on every side — ties with itself, so SAME is reachable with two of them.</summary>
        private const int EvenCard = 1;

        /// <summary>The human's other card, so a two-card hand is visible.</summary>
        private const int SecondEvenCard = 2;

        /// <summary>Rank 10 on every side — it beats a rank-5 neighbour from any side, so it captures.</summary>
        private const int StrongCardId = 3;

        [Fact]
        public async Task Preview_AnswersThePlayerOnTurn()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard, SecondEvenCard]);
            await AddPlacementAsync(context, match.Id, EvenCard, TestBots.Login, x: 0, y: 1);

            var preview = await CreateService(context).PreviewAsync(match.Id, Human);

            var answered = Assert.IsType<MovePreviewService.Preview>(preview);
            Assert.Equal(Human, answered.PlayerId);
            Assert.Equal(1, answered.Placements);
            Assert.Equal(TestBots.Login, answered.NextPlayer);

            // Two cards against the eight cells that are left.
            Assert.Equal(16, answered.Moves.Count);
            Assert.Contains(answered.Moves, move => move.CardId == SecondEvenCard && (move.X, move.Y) == (2, 2));
        }

        [Fact]
        public async Task Preview_SaysNothingForTheOpponent()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard]);

            // The list would be built from argel's hand, so the bot may not be handed it.
            Assert.Null(await CreateService(context).PreviewAsync(match.Id, TestBots.Login));
        }

        [Fact]
        public async Task Preview_SaysNothingForANonParticipant()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard]);

            Assert.Null(await CreateService(context).PreviewAsync(match.Id, Stranger));
        }

        [Theory]
        [InlineData("waiting")]
        [InlineData("completed")]
        [InlineData("abandoned")]
        public async Task Preview_SaysNothingWhenTheMatchIsNotActive(string status)
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: status, currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard]);

            Assert.Null(await CreateService(context).PreviewAsync(match.Id, Human));
        }

        [Fact]
        public async Task Preview_CoversEveryUnusedCardAgainstEveryEmptyCell()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard, SecondEvenCard]);

            var preview = await CreateService(context).PreviewAsync(match.Id, Human);

            var answered = Assert.IsType<MovePreviewService.Preview>(preview);

            // An empty board: two cards, nine cells, and every cell is a candidate.
            Assert.Equal(18, answered.Moves.Count);
            Assert.Equal(
                GameLogicService.EmptyCells([]),
                answered.Moves.Select(move => (move.X, move.Y)).Distinct()
            );
        }

        [Fact]
        public async Task Preview_MatchesWhatPlayingTheMoveActuallyDoes()
        {
            // The property that makes the preview safe to render on sight: for **every** move it offers, playing that
            // move through the pipeline produces exactly what the preview promised. If the two ever disagreed, the board
            // would show a result the server then contradicts — the failure mode this plan exists to avoid — so every
            // enumerated move is replayed here and compared whole.
            using var context = CreateContext();
            await SeedAsync(context);

            // SAME is in play, and the bot's two rank-5 cards sit where a rank-5 card played in the centre ties with
            // both of them: the preview has to offer a move that is a rule capture, not merely a battle.
            var match = await AddMatchAsync(
                context,
                status: "active",
                currentPlayerTurn: Human,
                rules: [MatchRule.Same, MatchRule.Plus]
            );
            await AddHandAsync(context, match.Id, Human, [EvenCard, SecondEvenCard]);
            await AddPlacementAsync(context, match.Id, EvenCard, TestBots.Login, x: 0, y: 1);
            await AddPlacementAsync(context, match.Id, SecondEvenCard, TestBots.Login, x: 1, y: 0);

            var gameLogic = new GameLogicService();
            var preview = await CreateService(context).PreviewAsync(match.Id, Human);
            var answered = Assert.IsType<MovePreviewService.Preview>(preview);

            var board = await context
                .CardPlacements.Where(placement => placement.MatchId == match.Id)
                .ToListAsync();

            Assert.Contains(answered.Moves, move => move.Result.TriggeredRules.Contains(MatchRule.Same));

            foreach (var move in answered.Moves)
            {
                // A fresh copy each time, exactly as the enumeration resolves them.
                var replay = gameLogic.PlayCard(
                    match,
                    GameLogicService.CopyBoard(board),
                    move.Card,
                    Human,
                    move.X,
                    move.Y
                );

                Assert.True(replay.IsValid);
                Assert.Equal(
                    move.Result.CapturedCards.Select(captured => (captured.X, captured.Y, captured.Owner)),
                    replay.CapturedCards.Select(captured => (captured.X, captured.Y, captured.Owner))
                );
                Assert.Equal(move.Result.TriggeredRules, replay.TriggeredRules);
                Assert.Equal(move.Result.Player1Score, replay.Player1Score);
                Assert.Equal(move.Result.Player2Score, replay.Player2Score);
                Assert.Equal(move.Result.IsGameComplete, replay.IsGameComplete);
                Assert.Equal(move.Result.WinnerId, replay.WinnerId);
            }
        }

        [Fact]
        public async Task Preview_LeavesTheMatchUntouched()
        {
            // Resolving a candidate flips the cards it captures *in place*, so every candidate has to be resolved on a
            // copy: a preview that resolved on the match's own rows would hand its imaginary captures to the next write.
            // The hand here is a rank-10 card next to a rank-5 one, so the preview does offer captures — an owner that
            // came back flipped is exactly the leak this checks for.
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [StrongCardId]);
            await AddPlacementAsync(context, match.Id, EvenCard, TestBots.Login, x: 1, y: 1);

            var preview = Assert.IsType<MovePreviewService.Preview>(
                await CreateService(context).PreviewAsync(match.Id, Human)
            );

            Assert.Contains(preview.Moves, move => move.Result.CapturedCards.Count > 0);

            var stored = await context
                .CardPlacements.Where(placement => placement.MatchId == match.Id)
                .ToListAsync();

            Assert.All(stored, placement => Assert.Equal(TestBots.Login, placement.Owner));
            Assert.False(context.ChangeTracker.HasChanges());
        }

        [Fact]
        public async Task EnumerateMoves_IsTheListTheBotPicksFrom()
        {
            // The refactor guard: the bot's choice is one of the moves the preview offers. Both callers share
            // `EnumerateMoves`, so a bot move outside the enumeration would mean the two had drifted apart.
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(
                context,
                status: "active",
                currentPlayerTurn: TestBots.Login
            );
            await AddHandAsync(context, match.Id, TestBots.Login, [EvenCard, SecondEvenCard]);
            await AddPlacementAsync(context, match.Id, EvenCard, Human, x: 1, y: 1);

            var preview = Assert.IsType<MovePreviewService.Preview>(
                await CreateService(context).PreviewAsync(match.Id, TestBots.Login)
            );

            var board = await context
                .CardPlacements.Where(placement => placement.MatchId == match.Id)
                .ToListAsync();
            var hand = await context
                .PlayerHands.Where(row => row.MatchId == match.Id && row.PlayerId == TestBots.Login)
                .ToListAsync();

            var chosen = Assert.IsType<BotMoveSelector.Move>(
                new BotMoveSelector(new GameLogicService()).Select(
                    match,
                    board,
                    hand,
                    TestBots.Login,
                    new ScriptedRandom()
                )
            );

            Assert.Contains(
                preview.Moves,
                move => move.CardId == chosen.CardId && (move.X, move.Y) == (chosen.X, chosen.Y)
            );
        }

        [Fact]
        public async Task EnumerateMoves_IsEmpty_WhenItIsNotTheActorsTurn()
        {
            // The pipeline's own turn check decides a candidate's legality, so an actor who is not on turn is offered
            // nothing at all.
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);

            var moves = new GameLogicService().EnumerateMoves(
                match,
                [],
                [new PlayerHand { PlayerId = Human, CardId = EvenCard, Card = Card(EvenCard) }],
                TestBots.Login
            );

            Assert.Empty(moves);
        }

        [Fact]
        public async Task Preview_IsEmptyWhenTheBoardIsFull()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard]);

            for (var y = 0; y < GameLogicService.BoardSize; y++)
            {
                for (var x = 0; x < GameLogicService.BoardSize; x++)
                {
                    await AddPlacementAsync(
                        context,
                        match.Id,
                        SecondEvenCard,
                        Human,
                        x,
                        y
                    );
                }
            }

            var preview = await CreateService(context).PreviewAsync(match.Id, Human);

            Assert.Empty(Assert.IsType<MovePreviewService.Preview>(preview).Moves);
        }

        [Fact]
        public async Task Preview_IsEmptyWhenTheHandIsSpent()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var match = await AddMatchAsync(context, status: "active", currentPlayerTurn: Human);
            await AddHandAsync(context, match.Id, Human, [EvenCard, SecondEvenCard]);

            // A spent hand is an answer, not a refusal: the client reads an empty list as "no preview to use" and the
            // move then takes the ordinary path.
            var handRows = await context
                .PlayerHands.Where(row => row.MatchId == match.Id && row.PlayerId == Human)
                .ToListAsync();

            foreach (var row in handRows)
            {
                row.IsUsed = true;
            }

            await context.SaveChangesAsync();

            var preview = await CreateService(context).PreviewAsync(match.Id, Human);

            Assert.Empty(Assert.IsType<MovePreviewService.Preview>(preview).Moves);
        }

        private static MovePreviewService CreateService(TripleTriadContext context) =>
            new(new GameRepository(context), new GameLogicService());

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        /// <summary>The two seats and a small catalogue of cards that tie with each other.</summary>
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
                context.Cards.Add(cardId == StrongCardId ? StrongCard(cardId) : Card(cardId));
            }

            await context.SaveChangesAsync();
        }

        private static Card Card(int cardId) =>
            new()
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
            };

        /// <summary>The one card that beats a rank-5 neighbour on every side.</summary>
        private static Card StrongCard(int cardId) =>
            new()
            {
                Id = cardId,
                Name = $"Card {cardId}",
                Image = $"ff8-deck/card-{cardId}.jpg",
                TopValue = 10,
                RightValue = 10,
                BottomValue = 10,
                LeftValue = 10,
                Element = [],
                Level = 10,
            };

        private static async Task<Match> AddMatchAsync(
            TripleTriadContext context,
            string status,
            string? currentPlayerTurn = TestBots.Login,
            List<MatchRule>? rules = null
        )
        {
            var match = new Match
            {
                Player1Id = Human,
                Player2Id = TestBots.Login,
                CurrentPlayerTurn = currentPlayerTurn,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ActivatedAt = DateTime.UtcNow,
                Player1Score = GameLogicService.HandSize,
                Player2Score = GameLogicService.HandSize,
                Rules = rules ?? [],
            };

            context.Matches.Add(match);
            await context.SaveChangesAsync();

            return match;
        }

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

        private static async Task AddPlacementAsync(
            TripleTriadContext context,
            int matchId,
            int cardId,
            string owner,
            int x,
            int y
        )
        {
            context.CardPlacements.Add(
                new CardPlacement
                {
                    MatchId = matchId,
                    CardId = cardId,
                    PlayerId = owner,
                    Owner = owner,
                    X = x,
                    Y = y,
                    PlacedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>One scripted answer, so the bot's tie-break is an exact expectation instead of a range.</summary>
        private sealed class ScriptedRandom : IRandomSource
        {
            public int Next(int exclusiveMax) => 0;
        }
    }
}
