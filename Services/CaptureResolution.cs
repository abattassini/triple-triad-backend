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
        /// <summary>
        /// Why one captured card flipped: the special rule that claimed it, or <c>null</c> for a basic
        /// battle capture. Identified by board position, which is unique per match.
        /// </summary>
        public sealed record CaptureCause(int X, int Y, MatchRule? Rule);

        // Board positions are unique per match (enforced by a unique index) and unpersisted
        // placements have no Id yet, so positions are the safe dedupe key.
        private readonly HashSet<(int X, int Y)> _capturedPositions = [];
        private readonly List<CardPlacement> _capturedCards = [];
        private readonly List<CardPlacement> _ruleCapturedCards = [];
        private readonly List<CaptureCause> _captureCauses = [];
        private readonly List<MatchRule> _triggeredRules = [];

        /// <summary>Every card captured by this move (basic battle + rule captures).</summary>
        public IReadOnlyList<CardPlacement> CapturedCards => _capturedCards;

        /// <summary>
        /// Subset of <see cref="CapturedCards"/> captured by a special rule (SAME and PLUS today, COMBO
        /// later). These are the seed cards a chaining rule such as COMBO continues from.
        /// </summary>
        public IReadOnlyList<CardPlacement> RuleCapturedCards => _ruleCapturedCards;

        /// <summary>
        /// Why each captured card flipped, in capture order (see <see cref="CaptureCause"/>). A card that
        /// both SAME and PLUS would flip is reported once, with SAME as its cause.
        /// </summary>
        public IReadOnlyList<CaptureCause> CaptureCauses => _captureCauses;

        /// <summary>Rules that fired while resolving this move (empty when none did).</summary>
        public IReadOnlyList<MatchRule> TriggeredRules => _triggeredRules;

        /// <summary>
        /// Registers a captured card, flips its ownership to the move's actor and records why it flipped:
        /// <paramref name="cause"/> is the special rule that claimed it, <c>null</c> for basic battle.
        /// Ownership is updated here (rather than after resolution) so later phases see the updated board.
        ///
        /// Returns false when an earlier — higher-precedence — phase already captured this position: the
        /// card keeps the cause of that first claim, which is what gives SAME precedence over PLUS.
        /// </summary>
        public bool TryAdd(CardPlacement placement, string actor, MatchRule? cause)
        {
            if (!_capturedPositions.Add((placement.X, placement.Y)))
            {
                return false;
            }

            placement.Owner = actor;
            _capturedCards.Add(placement);
            _captureCauses.Add(new CaptureCause(placement.X, placement.Y, cause));

            if (cause is not null)
            {
                _ruleCapturedCards.Add(placement);
            }

            return true;
        }

        /// <summary>
        /// Records that a special rule fired on this move. Call it only when the rule actually flipped a
        /// card, so <see cref="TriggeredRules"/> never names a rule that changed nothing.
        /// </summary>
        public void MarkRuleTriggered(MatchRule rule)
        {
            if (!_triggeredRules.Contains(rule))
            {
                _triggeredRules.Add(rule);
            }
        }
    }
}
