using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// How the bot picks its move: every card it still holds against every empty cell, scored by what that move would
    /// actually do. The candidates come from <see cref="GameLogicService.EnumerateMoves"/> — the same enumeration a
    /// client's preview is built from, each resolved through <see cref="GameLogicService.PlayCard"/> and the real rules
    /// — so a SAME or PLUS flip counts as a capture here exactly as it would in play, and a change to the rules can
    /// never leave the bot evaluating a board that no longer behaves the way it assumes.
    ///
    /// <para>
    /// This class chooses the move; it does not decide what a move is <em>worth</em>. That is the
    /// <see cref="CPUPlayingProfile"/>'s scorer — <see cref="CPUPlayingProfileScorers"/> picks it from the profile, and
    /// each profile has its own <see cref="MoveScorer"/> (plans/PLAN-029-cpu-playing-profiles/plan.md). One ply, no
    /// lookahead: this is the one place a move is enumerated and chosen, and the scorers are the one place a profile's
    /// values are defined.
    /// </para>
    /// </summary>
    public class BotMoveSelector(GameLogicService gameLogic)
    {
        private readonly GameLogicService _gameLogic = gameLogic;

        /// <summary>One legal move: the card, the cell, and how many cards it would flip.</summary>
        public sealed record Move(int CardId, int X, int Y, int Captures);

        /// <summary>
        /// The move to play, or null when the bot has nothing to play with. Each candidate is scored by the scorer the
        /// <paramref name="profile"/> names — the default, <see cref="CPUPlayingProfile.Decent"/>, is the original
        /// scoring — the highest score wins, and ties are settled with the injected randomness so two matches never
        /// play out the same way.
        /// </summary>
        public Move? Select(
            Match match,
            IReadOnlyCollection<CardPlacement> board,
            IReadOnlyCollection<PlayerHand> hand,
            string actor,
            IRandomSource random,
            CPUPlayingProfile profile = CPUPlayingProfile.Decent
        )
        {
            var scorer = CPUPlayingProfileScorers.For(profile);

            // The cards the bot could still play. A profile whose scoring looks ahead at them (Formidable's recapture)
            // reads them from the context, so they are gathered once here.
            var playable = hand.Where(row => !row.IsUsed && row.Card is not null).ToList();

            // Every candidate is resolved against its own copy of the board by the enumeration: PlayCard flips the
            // ownership of the cards it captures *in place*, so the match's own placements — tracked by EF and saved
            // with the move that is really played — must never be what a candidate is resolved on, and two candidates
            // must not score against each other's captures either.
            var scored = _gameLogic
                .EnumerateMoves(match, board, hand, actor)
                .Select(outcome =>
                {
                    var context = new MoveScoringContext(match.Id, outcome, board, playable, actor);

                    return (
                        Move: new Move(
                            outcome.CardId,
                            outcome.X,
                            outcome.Y,
                            outcome.Result.CapturedCards.Count
                        ),
                        Score: scorer.Score(context)
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
    }
}
