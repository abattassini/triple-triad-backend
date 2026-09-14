using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Accumulates the cards captured while resolving one played card. It is a mutable result object
    /// (instead of a plain list) so extra capture phases — the future COMBO rule, for example — can
    /// append to it without duplicating cards or reworking the callers.
    /// </summary>
    public class CaptureResolution
    {
        // Board positions are unique per match (enforced by a unique index) and unpersisted
        // placements have no Id yet, so positions are the safe dedupe key.
        private readonly HashSet<(int X, int Y)> _capturedPositions = [];
        private readonly List<CardPlacement> _capturedCards = [];
        private readonly List<CardPlacement> _ruleCapturedCards = [];

        /// <summary>Every card captured by this move (basic battle + rule captures).</summary>
        public IReadOnlyList<CardPlacement> CapturedCards => _capturedCards;

        /// <summary>
        /// Subset of <see cref="CapturedCards"/> captured by a special rule (SAME today, COMBO
        /// later). These are the seed cards the COMBO rule chains from.
        /// </summary>
        public IReadOnlyList<CardPlacement> RuleCapturedCards => _ruleCapturedCards;

        /// <summary>Rules that fired while resolving this move.</summary>
        public MatchRule TriggeredRules { get; private set; } = MatchRule.None;

        /// <summary>
        /// Registers a captured card and flips its ownership to the player who moved. Ownership is
        /// updated here (rather than after resolution) so later phases see the updated board.
        /// Returns false when the placement was already captured by a previous phase.
        /// </summary>
        public bool TryAdd(CardPlacement placement, string playerId, bool isRuleCapture)
        {
            if (!_capturedPositions.Add((placement.X, placement.Y)))
            {
                return false;
            }

            placement.Owner = playerId;
            _capturedCards.Add(placement);

            if (isRuleCapture)
            {
                _ruleCapturedCards.Add(placement);
            }

            return true;
        }

        /// <summary>Records that a special rule fired on this move.</summary>
        public void MarkRuleTriggered(MatchRule rule)
        {
            TriggeredRules |= rule;
        }
    }
}
