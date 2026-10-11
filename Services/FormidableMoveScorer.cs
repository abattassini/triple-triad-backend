using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Decent plus two considerations (plans/PLAN-029-cpu-playing-profiles/plan.md):
    /// <list type="bullet">
    /// <item>
    /// the cards the move renders <em>inaccessible</em> (see <see cref="GameLogicService.InaccessibleOwned"/>) — the
    /// <em>increase</em> is what counts, so sealing one card in is worth <see cref="InaccessibleWeight"/> and sealing
    /// two is worth twice that;
    /// </item>
    /// <item>
    /// a recapture bonus: how many sides of the card it just played its own remaining hand could win back from — one
    /// side pays <see cref="RecaptureSingleWeight"/>, two or more pay <see cref="RecaptureHigherWeight"/>.
    /// </item>
    /// </list>
    /// The <see cref="DecentMoveScorer"/> score is the base, composed rather than re-derived, so the profile is provably
    /// Decent plus those two terms.
    /// </summary>
    public sealed class FormidableMoveScorer(DecentMoveScorer decent) : MoveScorer
    {
        private readonly DecentMoveScorer _decent = decent;

        /// <summary>
        /// The weight of one card a move renders <em>inaccessible</em>. Deliberately a little above
        /// <see cref="MoveScorer.CaptureWeight"/>: a card that can never be taken back is worth slightly more than one
        /// taken now.
        /// </summary>
        public const int InaccessibleWeight = 1100;

        /// <summary>
        /// The bonus when the bot's remaining hand could win the card it just played back from exactly one side: a
        /// little insurance, worth less than a capture.
        /// </summary>
        public const int RecaptureSingleWeight = 600;

        /// <summary>
        /// The bonus when it could win it back from two or more sides — slightly above
        /// <see cref="RecaptureSingleWeight"/>, and still below a capture (<see cref="MoveScorer.CaptureWeight"/>) and
        /// below <see cref="InaccessibleWeight"/>.
        /// </summary>
        public const int RecaptureHigherWeight = 950;

        /// <summary>
        /// The direction of each orthogonal neighbour, and the two ranks a recapture at that neighbour compares: the
        /// played card's side facing the neighbour (<c>PlayedSide</c>) and the side of a card placed there that would
        /// face back (<c>AttackerSide</c>). A card there captures the played one when
        /// <c>AttackerSide &gt; PlayedSide</c>.
        /// </summary>
        private static readonly (
            int Dx,
            int Dy,
            string PlayedSide,
            string AttackerSide
        )[] RecaptureDirections =
        [
            (0, -1, "TopValue", "BottomValue"),
            (1, 0, "RightValue", "LeftValue"),
            (0, 1, "BottomValue", "TopValue"),
            (-1, 0, "LeftValue", "RightValue"),
        ];

        public override int Score(MoveScoringContext context)
        {
            var after = ResolvedBoard(context);

            var inaccessibleDelta =
                GameLogicService.InaccessibleOwned(after, context.Actor)
                - GameLogicService.InaccessibleOwned(context.Board, context.Actor);

            return _decent.Score(context)
                + inaccessibleDelta * InaccessibleWeight
                + RecaptureBonus(after, context);
        }

        /// <summary>
        /// The board as the candidate would leave it: the placement it adds and every flip it caused. The enumeration
        /// resolves each candidate on its own copy and hands back only the result, so the resulting board is rebuilt
        /// here — position is the identity (unique per match), and a captured card reports the owner the pipeline gave
        /// it.
        /// </summary>
        private static List<CardPlacement> ResolvedBoard(MoveScoringContext context)
        {
            var after = GameLogicService.CopyBoard(context.Board);

            foreach (var captured in context.Outcome.Result.CapturedCards)
            {
                var flipped = after.FirstOrDefault(placement =>
                    placement.X == captured.X && placement.Y == captured.Y
                );

                if (flipped is not null)
                {
                    flipped.Owner = captured.Owner;
                }
            }

            after.Add(
                new CardPlacement
                {
                    MatchId = context.MatchId,
                    CardId = context.Outcome.CardId,
                    Card = context.Outcome.Card,
                    PlayerId = context.Actor,
                    Owner = context.Actor,
                    X = context.Outcome.X,
                    Y = context.Outcome.Y,
                    PlacedAt = DateTime.UtcNow,
                }
            );

            return after;
        }

        /// <summary>
        /// The recapture term: the bonus for a card the bot could win back with its remaining hand. One side pays
        /// <see cref="RecaptureSingleWeight"/>, two or more pay <see cref="RecaptureHigherWeight"/>, and none pays
        /// nothing.
        /// </summary>
        private static int RecaptureBonus(List<CardPlacement> after, MoveScoringContext context)
        {
            var sides = RecapturableSides(after, context);

            if (sides >= 2)
            {
                return RecaptureHigherWeight;
            }

            return sides == 1 ? RecaptureSingleWeight : 0;
        }

        /// <summary>
        /// How many sides of the just-played card a card still in the bot's hand could win back from. A side counts only
        /// when its neighbour cell is still <em>empty</em> (a card can only be played there) and at least one remaining
        /// card beats the played card on that shared side by basic battle (<c>attack &gt; defence</c>). The SAME/PLUS
        /// captures a real move could add are deliberately out of scope: this is a heuristic, not the pipeline. The card
        /// being played is excluded, so a card is never its own insurance.
        /// </summary>
        private static int RecapturableSides(List<CardPlacement> after, MoveScoringContext context)
        {
            var sides = 0;

            foreach (var (dx, dy, playedSide, attackerSide) in RecaptureDirections)
            {
                var neighbourX = context.Outcome.X + dx;
                var neighbourY = context.Outcome.Y + dy;

                if (
                    neighbourX < 0
                    || neighbourX >= GameLogicService.BoardSize
                    || neighbourY < 0
                    || neighbourY >= GameLogicService.BoardSize
                )
                {
                    continue;
                }

                // Occupied: no card can ever be played next to the played card from here.
                if (after.Any(placement => placement.X == neighbourX && placement.Y == neighbourY))
                {
                    continue;
                }

                var defence = ValueOfSide(context.Outcome.Card, playedSide);
                if (
                    context.PlayableHand.Any(row =>
                        row.CardId != context.Outcome.CardId
                        && row.Card is not null
                        && ValueOfSide(row.Card, attackerSide) > defence
                    )
                )
                {
                    sides++;
                }
            }

            return sides;
        }

        /// <summary>One rank of a card by its side name — the same names the battle directions use.</summary>
        private static int ValueOfSide(Card card, string side) =>
            side switch
            {
                "TopValue" => card.TopValue,
                "RightValue" => card.RightValue,
                "BottomValue" => card.BottomValue,
                "LeftValue" => card.LeftValue,
                _ => 0,
            };
    }
}
