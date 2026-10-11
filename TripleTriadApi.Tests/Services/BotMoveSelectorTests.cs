using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for how the bot chooses its move. The selector is a pure function over the models — the board, the
    /// hand, the match — so these tests need no database and no host: they build a position, run it and assert on the
    /// move that comes back. Each name says what the choice is about: it takes a capture, prefers the bigger one,
    /// keeps its strong cards, and always plays a legal card.
    /// </summary>
    public class BotMoveSelectorTests
    {
        [Fact]
        public void Select_TakesTheCapture_WhenOneIsAvailable()
        {
            // One of argel's weak cards sits in the centre; only the bot's strong card can beat it, from any of the
            // four cells around it.
            var board = new List<CardPlacement> { PlacedAt(WeakCard(101), "argel", 1, 1) };
            var hand = new List<PlayerHand> { InHand(WeakCard(201)), InHand(StrongCard(202)) };

            var chosen = Select(ActiveMatch(), board, hand);

            Assert.Equal(202, chosen.CardId);
            Assert.Equal(1, chosen.Captures);
        }

        [Fact]
        public void Select_PrefersTheMoveThatCapturesMore()
        {
            // Two weak cards of argel's touch the top-left corner; the strong card played there takes both, and the
            // centre would take both too but risks four sides instead of two.
            var board = new List<CardPlacement>
            {
                PlacedAt(WeakCard(101), "argel", 1, 0),
                PlacedAt(WeakCard(102), "argel", 0, 1),
            };
            var hand = new List<PlayerHand> { InHand(StrongCard(201)), InHand(WeakCard(202)) };

            var chosen = Select(ActiveMatch(), board, hand);

            Assert.Equal((0, 0), (chosen.X, chosen.Y));
            Assert.Equal(2, chosen.Captures);
        }

        [Fact]
        public void Select_KeepsStrongCardsOffTheCentre_WhenNothingCaptures()
        {
            var hand = new List<PlayerHand> { InHand(StrongCard(201)), InHand(WeakCard(202)) };

            var chosen = Select(ActiveMatch(), [], hand);

            // Nothing to take, so the cheapest card goes down, and it goes where it risks the fewest sides: a corner.
            Assert.Equal(202, chosen.CardId);
            Assert.Contains(chosen.X, new[] { 0, 2 });
            Assert.Contains(chosen.Y, new[] { 0, 2 });
        }

        [Fact]
        public void Select_PlaysTheOnlyFreeCell_WhenTheBoardIsNearlyFull()
        {
            var board = new List<CardPlacement>();

            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    if (x == 2 && y == 2)
                    {
                        continue;
                    }

                    board.Add(
                        PlacedAt(
                            WeakCard(100 + x * 3 + y),
                            y % 2 == 0 ? "argel" : TestBots.Login,
                            x,
                            y
                        )
                    );
                }
            }

            var chosen = Select(ActiveMatch(), board, [InHand(StrongCard(201))]);

            Assert.Equal((2, 2), (chosen.X, chosen.Y));
            Assert.Equal(201, chosen.CardId);
        }

        [Fact]
        public void Select_BreaksTiesWithTheInjectedRandom()
        {
            // One card, an empty board: the four corners all score exactly the same, so the tie-break decides. The
            // candidates are in reading order — (0,0), (2,0), (0,2), (2,2).
            var hand = new List<PlayerHand> { InHand(WeakCard(201)) };

            var first = Select(ActiveMatch(), [], hand, random: new FixedRandom(0));
            var last = Select(ActiveMatch(), [], hand, random: new FixedRandom(3));

            Assert.Equal((0, 0), (first.X, first.Y));
            Assert.Equal((2, 2), (last.X, last.Y));
        }

        [Fact]
        public void Select_ReturnsNull_WhenThereIsNothingToPlay()
        {
            var selector = new BotMoveSelector(new GameLogicService());
            var match = ActiveMatch();

            // Every card already played…
            Assert.Null(
                selector.Select(
                    match,
                    [],
                    [InHand(WeakCard(201), isUsed: true)],
                    TestBots.Login,
                    new FixedRandom(0)
                )
            );

            // …or every cell already taken.
            var fullBoard = new List<CardPlacement>();
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    fullBoard.Add(PlacedAt(WeakCard(100 + x * 3 + y), "argel", x, y));
                }
            }

            Assert.Null(
                selector.Select(
                    match,
                    fullBoard,
                    [InHand(WeakCard(202))],
                    TestBots.Login,
                    new FixedRandom(0)
                )
            );
        }

        [Fact]
        public void Select_LooksAtTheBoardOnly_WithoutFlippingAnything()
        {
            // The board handed in is the match's own state, tracked by EF and saved with the move that is really
            // played. A candidate is resolved through the real pipeline, which flips the cards it captures — on the
            // copies the lookahead uses, so nothing here may come back changed.
            var board = new List<CardPlacement> { PlacedAt(WeakCard(101), "argel", 1, 1) };
            var hand = new List<PlayerHand> { InHand(StrongCard(201)) };

            Select(ActiveMatch(), board, hand);

            Assert.All(board, placement => Assert.Equal("argel", placement.Owner));
        }

        [Fact]
        public void Select_Formidable_PlaysTheCardThatMakesAnExistingCardInaccessible()
        {
            // The bot's weak card sits in a corner with only (0,1) left free beside it, and a weak card of argel's
            // blocks the other side. Playing at (0,1) seals the corner card in for good — nothing can ever be played
            // next to it again — which is the Formidable profile's own consideration.
            var board = new List<CardPlacement>
            {
                PlacedAt(WeakCard(102), TestBots.Login, 0, 0),
                PlacedAt(WeakCard(101), "argel", 1, 0),
            };
            var hand = new List<PlayerHand> { InHand(StrongCard(201)), InHand(WeakCard(202)) };

            var chosen = Select(ActiveMatch(), board, hand, profile: CPUPlayingProfile.Formidable);

            Assert.Equal((0, 1), (chosen.X, chosen.Y));
            Assert.Equal(202, chosen.CardId);
            Assert.Equal(0, chosen.Captures);
        }

        [Fact]
        public void Select_Decent_TakesTheCapture_InsteadOfSealingTheCorner()
        {
            // The same position: Decent has no term for the sealed corner card, so it does what it always did — puts
            // its strong card beside argel's weak one and takes it. Only the profile explains the other test's move.
            var board = new List<CardPlacement>
            {
                PlacedAt(WeakCard(102), TestBots.Login, 0, 0),
                PlacedAt(WeakCard(101), "argel", 1, 0),
            };
            var hand = new List<PlayerHand> { InHand(StrongCard(201)), InHand(WeakCard(202)) };

            var chosen = Select(ActiveMatch(), board, hand, profile: CPUPlayingProfile.Decent);

            Assert.Equal(201, chosen.CardId);
            Assert.Equal((2, 0), (chosen.X, chosen.Y));
            Assert.Equal(1, chosen.Captures);
        }

        [Fact]
        public void Select_Formidable_PrefersTheCellItCouldWinTheCardBackFrom()
        {
            // On an empty board every cell is equally safe for the played card, so Decent only weighs exposure.
            // Formidable also asks whether its other card could win the played card back: on the top or bottom edge the
            // 10 has two free neighbours to attack from, while a corner offers one.
            var hand = new List<PlayerHand>
            {
                InHand(CardWith(201, top: 5, right: 5, bottom: 5, left: 5)),
                InHand(CardWith(202, top: 1, right: 10, bottom: 1, left: 10)),
            };

            var chosen = Select(ActiveMatch(), [], hand, profile: CPUPlayingProfile.Formidable);

            Assert.Equal(201, chosen.CardId);
            Assert.Equal(1, chosen.X);
            Assert.Contains(chosen.Y, new[] { 0, 2 });
        }

        [Fact]
        public void Select_Decent_OnThatSameBoard_KeepsTheCardWhereItRisksFewestSides()
        {
            var hand = new List<PlayerHand>
            {
                InHand(CardWith(201, top: 5, right: 5, bottom: 5, left: 5)),
                InHand(CardWith(202, top: 1, right: 10, bottom: 1, left: 10)),
            };

            var chosen = Select(ActiveMatch(), [], hand, profile: CPUPlayingProfile.Decent);

            Assert.Equal(201, chosen.CardId);
            Assert.Contains(chosen.X, new[] { 0, 2 });
            Assert.Contains(chosen.Y, new[] { 0, 2 });
        }

        /// <summary>Run the selector and unwrap the move, with the scripted tie-break these tests expect by default.</summary>
        private static BotMoveSelector.Move Select(
            Match match,
            IReadOnlyCollection<CardPlacement> board,
            IReadOnlyCollection<PlayerHand> hand,
            IRandomSource? random = null,
            CPUPlayingProfile profile = CPUPlayingProfile.Decent
        )
        {
            var selector = new BotMoveSelector(new GameLogicService());
            var move = selector.Select(
                match,
                board,
                hand,
                TestBots.Login,
                random ?? new FixedRandom(0),
                profile
            );

            return Assert.IsType<BotMoveSelector.Move>(move);
        }

        /// <summary>The position the bot is always choosing in: an active match in which it is its turn.</summary>
        private static Match ActiveMatch() =>
            new()
            {
                Id = 1,
                Player1Id = "argel",
                Player2Id = TestBots.Login,
                CurrentPlayerTurn = TestBots.Login,
                Status = "active",
                Player1Score = GameLogicService.HandSize,
                Player2Score = GameLogicService.HandSize,
            };

        /// <summary>A card that can never capture anything: rank 1 on all four sides.</summary>
        private static Card WeakCard(int id) =>
            new()
            {
                Id = id,
                Name = $"Weak {id}",
                Image = $"ff8-deck/weak-{id}.jpg",
                TopValue = 1,
                RightValue = 1,
                BottomValue = 1,
                LeftValue = 1,
                Element = [],
                Level = 1,
            };

        /// <summary>A card that wins on every side.</summary>
        private static Card StrongCard(int id) =>
            new()
            {
                Id = id,
                Name = $"Strong {id}",
                Image = $"ff8-deck/strong-{id}.jpg",
                TopValue = 10,
                RightValue = 10,
                BottomValue = 10,
                LeftValue = 10,
                Element = [],
                Level = 10,
            };

        /// <summary>A card with explicit ranks on each side, for tests that turn on a single facing value.</summary>
        private static Card CardWith(int id, int top, int right, int bottom, int left) =>
            new()
            {
                Id = id,
                Name = $"Card {id}",
                Image = $"ff8-deck/card-{id}.jpg",
                TopValue = top,
                RightValue = right,
                BottomValue = bottom,
                LeftValue = left,
                Element = [],
                Level = 1,
            };

        private static PlayerHand InHand(Card card, bool isUsed = false) =>
            new()
            {
                PlayerId = TestBots.Login,
                CardId = card.Id,
                Card = card,
                IsUsed = isUsed,
            };

        private static CardPlacement PlacedAt(Card card, string owner, int x, int y) =>
            new()
            {
                CardId = card.Id,
                Card = card,
                PlayerId = owner,
                Owner = owner,
                X = x,
                Y = y,
                PlacedAt = DateTime.UtcNow,
            };

        /// <summary>One scripted answer, so a tie-break can be an exact expectation instead of a range.</summary>
        private sealed class FixedRandom(int value) : IRandomSource
        {
            public int Next(int exclusiveMax) => value % Math.Max(exclusiveMax, 1);
        }
    }
}
