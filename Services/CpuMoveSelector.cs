using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// How the CPU picks its move: every card it still holds against every empty cell, scored by what that move would
    /// actually do. The candidates come from <see cref="GameLogicService.EnumerateMoves"/> — the same enumeration a
    /// client's preview is built from, each resolved through <see cref="GameLogicService.PlayCard"/> and the real rules
    /// — so a SAME or PLUS flip counts as a capture here exactly as it would in play, and a change to the rules can
    /// never leave the CPU evaluating a board that no longer behaves the way it assumes.
    ///
    /// It is a deliberately one-ply player (plans/PLAN-012-cpu-opponent/plan.md §5.2): it takes what it can see, keeps its
    /// strong cards where they are hardest to attack, and this class is the only place to make it smarter.
    /// </summary>
    public class CpuMoveSelector(GameLogicService gameLogic)
    {
        private readonly GameLogicService _gameLogic = gameLogic;

        /// <summary>One legal move: the card, the cell, and how many cards it would flip.</summary>
        public sealed record Move(int CardId, int X, int Y, int Captures);

        /// <summary>
        /// The move to play, or null when the CPU has nothing to play with. Captures decide first; between equally
        /// capturing moves it keeps the strongest card in the least exposed cell (a corner is attacked from two sides,
        /// the centre from four), and ties are settled with the injected randomness so two matches never play out the
        /// same way.
        /// </summary>
        public Move? Select(
            Match match,
            IReadOnlyCollection<CardPlacement> board,
            IReadOnlyCollection<PlayerHand> hand,
            IRandomSource random
        )
        {
            // Every candidate is resolved against its own copy of the board by the enumeration: PlayCard flips the
            // ownership of the cards it captures *in place*, so the match's own placements — tracked by EF and saved
            // with the move that is really played — must never be what a candidate is resolved on, and two candidates
            // must not score against each other's captures either.
            var scored = _gameLogic
                .EnumerateMoves(match, board, hand, CpuOpponent.Login)
                .Select(outcome =>
                {
                    var captures = outcome.Result.CapturedCards.Count;

                    return (
                        Move: new Move(outcome.CardId, outcome.X, outcome.Y, captures),
                        Score: captures * 1000 - Strength(outcome.Card) * ExposedSides(outcome.X, outcome.Y)
                    );
                })
                .ToList();

            if (scored.Count == 0)
            {
                return null;
            }

            var best = scored.Max(candidate => candidate.Score);
            var tied = scored
                .Where(candidate => candidate.Score == best)
                .Select(candidate => candidate.Move)
                .ToList();

            return tied[random.Next(tied.Count)];
        }

        /// <summary>How many sides of a cell are on the board: a corner has 2, an edge 3, the centre 4.</summary>
        private static int ExposedSides(int x, int y)
        {
            var onVerticalEdge = x == 0 || x == GameLogicService.BoardSize - 1;
            var onHorizontalEdge = y == 0 || y == GameLogicService.BoardSize - 1;

            if (onVerticalEdge && onHorizontalEdge)
            {
                return 2;
            }

            return onVerticalEdge || onHorizontalEdge ? 3 : 4;
        }

        /// <summary>The four ranks added up: how much of a card is being risked by playing it.</summary>
        private static int Strength(Card card) =>
            card.TopValue + card.RightValue + card.BottomValue + card.LeftValue;
    }
}
