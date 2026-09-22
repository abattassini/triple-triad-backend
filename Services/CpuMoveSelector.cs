using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// How the CPU picks its move: each card it still holds against each empty cell, scored by what that move would
    /// actually do. Every candidate goes through <see cref="GameLogicService.PlayCard"/> — the real pipeline, rules
    /// and all — so a SAME or PLUS flip counts as a capture here exactly as it would in play, and a change to the
    /// rules can never leave the CPU evaluating a board that no longer behaves the way it assumes.
    ///
    /// Candidates are resolved on <em>copies</em> of the board, because that pipeline flips the cards it captures in
    /// place: the lookahead must leave the match's own placements, and the next candidate's view of them, exactly as
    /// they were (see <see cref="CopyBoard"/>).
    ///
    /// It is a deliberately one-ply player (plans/cpu-opponent-plan.md §5.2): it takes what it can see, keeps its
    /// strong cards where they are hardest to attack, and this class is the only place to make it smarter.
    /// </summary>
    public class CpuMoveSelector(GameLogicService gameLogic)
    {
        private readonly GameLogicService _gameLogic = gameLogic;

        /// <summary>One legal move: the card, the cell, and how many cards it would flip.</summary>
        public sealed record Move(int CardId, int X, int Y, int Captures);

        /// <summary>How wide the board is — the cells are <c>0..2</c> on both axes.</summary>
        private const int BoardSize = 3;

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
            var emptyCells = EmptyCells(board);
            var playable = hand.Where(row => !row.IsUsed && row.Card is not null).ToList();

            if (emptyCells.Count == 0 || playable.Count == 0)
            {
                return null;
            }

            var scored = new List<(Move Move, int Score)>();

            foreach (var row in playable)
            {
                var strength = Strength(row.Card);

                foreach (var (x, y) in emptyCells)
                {
                    // Each candidate is resolved against its own copy of the board. PlayCard flips the ownership of
                    // the cards it captures *in place*, so the match's own placements — tracked by EF and saved with
                    // the move that is really played — must never be what a candidate is resolved on, and two
                    // candidates must not score against each other's captures either.
                    var result = _gameLogic.PlayCard(
                        match,
                        CopyBoard(board),
                        row.Card,
                        CpuOpponent.Login,
                        x,
                        y
                    );

                    if (!result.IsValid)
                    {
                        continue;
                    }

                    var captures = result.CapturedCards.Count;
                    var score = captures * 1000 - strength * ExposedSides(x, y);
                    scored.Add((new Move(row.CardId, x, y, captures), score));
                }
            }

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

        /// <summary>
        /// A throwaway copy of the board for one candidate move: everything ownership and position are read from is
        /// copied, while the <see cref="Card"/> graph is shared — no rule mutates a card. See <see cref="Select"/>
        /// for why the lookahead may not use the match's own placements.
        /// </summary>
        private static List<CardPlacement> CopyBoard(IReadOnlyCollection<CardPlacement> board) =>
            board
                .Select(placement => new CardPlacement
                {
                    Id = placement.Id,
                    MatchId = placement.MatchId,
                    CardId = placement.CardId,
                    Card = placement.Card,
                    PlayerId = placement.PlayerId,
                    Owner = placement.Owner,
                    X = placement.X,
                    Y = placement.Y,
                    PlacedAt = placement.PlacedAt,
                })
                .ToList();

        /// <summary>The board's free cells in reading order — the order ties are broken in.</summary>
        private static List<(int X, int Y)> EmptyCells(IReadOnlyCollection<CardPlacement> board) =>
        [
            .. from y in Enumerable.Range(0, BoardSize)
               from x in Enumerable.Range(0, BoardSize)
               where !board.Any(placement => placement.X == x && placement.Y == y)
               select (x, y),
        ];

        /// <summary>How many sides of a cell are on the board: a corner has 2, an edge 3, the centre 4.</summary>
        private static int ExposedSides(int x, int y)
        {
            var onVerticalEdge = x == 0 || x == BoardSize - 1;
            var onHorizontalEdge = y == 0 || y == BoardSize - 1;

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
