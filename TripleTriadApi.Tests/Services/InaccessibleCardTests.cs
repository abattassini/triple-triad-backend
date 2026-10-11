using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The board predicate the Formidable profile leans on (plans/PLAN-029-cpu-playing-profiles/plan.md): a card is
    /// <em>inaccessible</em> when every in-bounds orthogonal neighbour cell already holds a card, so no future card can
    /// ever be played beside it and — with no COMBO rule — no future collision can capture it. It is derived from the
    /// board alone, so these tests build a position and ask.
    /// </summary>
    public class InaccessibleCardTests
    {
        [Fact]
        public void IsInaccessible_IsTrueOnceEveryInBoundsNeighbourHoldsACard()
        {
            // The centre has four neighbours; until the last is filled it can still be attacked.
            var board = new List<CardPlacement> { PlacedAt(1, 0), PlacedAt(0, 1), PlacedAt(2, 1) };
            Assert.False(GameLogicService.IsInaccessible(board, 1, 1));

            board.Add(PlacedAt(1, 2));
            Assert.True(GameLogicService.IsInaccessible(board, 1, 1));
        }

        [Fact]
        public void IsInaccessible_CountsACorner_WithOnlyTwoNeighbours()
        {
            Assert.False(GameLogicService.IsInaccessible([PlacedAt(1, 0)], 0, 0));

            Assert.True(GameLogicService.IsInaccessible([PlacedAt(1, 0), PlacedAt(0, 1)], 0, 0));
        }

        [Fact]
        public void IsInaccessible_DoesNotRequireTheCellItselfToBeOccupied()
        {
            var board = new List<CardPlacement> { PlacedAt(1, 0), PlacedAt(0, 1) };

            // (0,0) is empty but sealed: nothing can ever be played there again either.
            Assert.DoesNotContain(board, placement => placement.X == 0 && placement.Y == 0);
            Assert.True(GameLogicService.IsInaccessible(board, 0, 0));
        }

        [Fact]
        public void InaccessibleOwned_CountsOnlyTheOwnersCards()
        {
            // (0,0) and (2,2) are both sealed corners; one belongs to the bot, the other to argel.
            var board = new List<CardPlacement>
            {
                PlacedAt(0, 0, "bot"),
                PlacedAt(1, 0),
                PlacedAt(0, 1),
                PlacedAt(2, 2, "argel"),
                PlacedAt(1, 2),
                PlacedAt(2, 1),
            };

            Assert.True(GameLogicService.IsInaccessible(board, 0, 0));
            Assert.True(GameLogicService.IsInaccessible(board, 2, 2));
            Assert.Equal(1, GameLogicService.InaccessibleOwned(board, "bot"));
            Assert.Equal(1, GameLogicService.InaccessibleOwned(board, "argel"));
            Assert.Equal(0, GameLogicService.InaccessibleOwned(board, "nobody"));
        }

        [Fact]
        public void InaccessibleOwned_IsZeroOnAnEmptyBoard()
        {
            Assert.Equal(0, GameLogicService.InaccessibleOwned([], "bot"));
        }

        /// <summary>One card on a cell — only position and ownership are read by the predicate.</summary>
        private static CardPlacement PlacedAt(int x, int y, string owner = "argel") =>
            new()
            {
                CardId = 1,
                Card = new Card
                {
                    Id = 1,
                    Name = "Card 1",
                    Image = "ff8-deck/card-1.jpg",
                    TopValue = 1,
                    RightValue = 1,
                    BottomValue = 1,
                    LeftValue = 1,
                    Element = [],
                    Level = 1,
                },
                PlayerId = owner,
                Owner = owner,
                X = x,
                Y = y,
                PlacedAt = DateTime.UtcNow,
            };
    }
}
