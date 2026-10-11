using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The scoring half of a bot's move choice: one implementation per <see cref="CPUPlayingProfile"/>
    /// (plans/PLAN-029-cpu-playing-profiles/plan.md). <see cref="BotMoveSelector"/> enumerates the legal moves and asks
    /// the profile's scorer — chosen by <see cref="CPUPlayingProfileScorers"/> — what each one is worth; a scorer is a
    /// pure function of a <see cref="MoveScoringContext"/> and touches nothing else.
    /// </summary>
    public abstract class MoveScorer
    {
        /// <summary>
        /// The weight of one capture: a move that flips <c>n</c> cards scores <c>n * CaptureWeight</c>. It is the
        /// backbone of every profile, so a capture outweighs the heuristics a profile adds on top of it.
        /// </summary>
        public const int CaptureWeight = 1000;

        /// <summary>The score of one candidate move under this profile.</summary>
        public abstract int Score(MoveScoringContext context);

        /// <summary>How many sides of a cell are on the board: a corner has 2, an edge 3, the centre 4.</summary>
        protected static int ExposedSides(int x, int y)
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
        protected static int Strength(Card card) =>
            card.TopValue + card.RightValue + card.BottomValue + card.LeftValue;
    }
}
