using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The CPU opponent, in one place: the login it plays under, how long it "thinks", how often its turn is looked
    /// for, and whether its matches pay the human. The tunables live here the way the timeouts live in
    /// <see cref="MatchTimeouts"/> and the reward table lives in <see cref="MatchRewardService"/>.
    ///
    /// The login is an **identity, not a display name**: it is what <c>PlayerHand.PlayerId</c>,
    /// <c>CardPlacement.PlayerId</c>/<c>Owner</c>, <c>Match.CurrentPlayerTurn</c> and <c>Match.WinnerId</c> hold, which
    /// is why a match against the CPU needs no schema of its own. The name a human reads is the client's business
    /// (see plans/cpu-opponent-plan.md §11 for the "looks like a person" work).
    /// </summary>
    public static class CpuOpponent
    {
        /// <summary>The sentinel the create endpoint seats as player 2, and the actor of every move it plays.</summary>
        public const string Login = "AI";

        /// <summary>
        /// How often its turn is looked for. Its effect is gated by the think window below, so a slower poll only
        /// makes the CPU answer a little later rather than changing how the delay is measured.
        /// </summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        /// <summary>The shortest the CPU waits before answering — long enough to read as somebody thinking.</summary>
        public static readonly TimeSpan MinThink = TimeSpan.FromMilliseconds(1200);

        /// <summary>The longest it waits — see <see cref="MinThink"/>.</summary>
        public static readonly TimeSpan MaxThink = TimeSpan.FromMilliseconds(3200);

        /// <summary>
        /// Whether a match against the CPU pays the human like a real one (coins, XP and the W/L/T counter). Flip it
        /// to false and <see cref="MatchRewardService"/> returns nothing at all for a CPU match; the CPU's own half is
        /// skipped either way. One switch, because the economy is the requester's call — kept paying for now.
        /// </summary>
        public static readonly bool RewardsForCpuMatches = true;

        /// <summary>
        /// The weight of one card level in the CPU's opening hand: <c>level²</c> rather than the flat draw a
        /// human gets, so the opponent turns up with something like the deck a strong player brings. A slot of
        /// its hand lands on level 10 25.97% of the time and on level 1 0.26% of the time, which leaves the five
        /// cards at level 7.9 on average with three of them level 8 or better. The draw itself is
        /// <see cref="GameLogicService.GetCpuHand"/>.
        /// </summary>
        public static int HandLevelWeight(int level) => level * level;

        /// <summary>
        /// Total weight of every level, i.e. the <c>385</c> of the <c>level² / 385</c> table (1+4+…+100). The draw
        /// sums this over the levels the catalogue actually has, so a level with no cards can never be picked.
        /// </summary>
        public static int TotalHandLevelWeight =>
            Enumerable.Range(Card.MinLevel, Card.MaxLevel - Card.MinLevel + 1).Sum(HandLevelWeight);

        /// <summary>True when this match is waiting on the CPU to move.</summary>
        public static bool IsCpuTurn(Match match) =>
            match.Status == "active" && match.CurrentPlayerTurn == Login;

        /// <summary>
        /// How long this particular move takes, derived from the match and how many cards are already on the board.
        /// Every move therefore gets its own delay with nothing to store, and a restart (or a redeploy) mid-think
        /// cannot change when that move was due.
        /// </summary>
        public static TimeSpan ThinkTime(int matchId, int placements)
        {
            var windowMs = (long)(MaxThink - MinThink).TotalMilliseconds;
            if (windowMs <= 0)
            {
                return MinThink;
            }

            var offset = (uint)unchecked(matchId * 31 + placements * 7) % (uint)windowMs;
            return MinThink + TimeSpan.FromMilliseconds(offset);
        }

        /// <summary>
        /// When this match's CPU move is due: the newest placement plus that move's thinking time, or the activation
        /// stamp while the board is still empty. Null when there is nothing to measure from — a match with neither a
        /// placement nor an activation — which is the one case the CPU leaves alone.
        /// </summary>
        public static DateTime? MoveDueAt(Match match, IReadOnlyCollection<CardPlacement> placements)
        {
            var reference =
                placements.Count == 0
                    ? match.ActivatedAt
                    : placements.Max(placement => placement.PlacedAt);

            return reference is null ? null : reference + ThinkTime(match.Id, placements.Count);
        }

        /// <summary>True once the CPU is allowed to play.</summary>
        public static bool IsMoveDue(Match match, IReadOnlyCollection<CardPlacement> placements, DateTime now) =>
            MoveDueAt(match, placements) is { } due && due <= now;
    }
}
