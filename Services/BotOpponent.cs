using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// How the machine plays: how long it "thinks", how often its turn is looked for, and how strong a hand it draws.
    /// The tunables live here the way the timeouts live in <see cref="MatchTimeouts"/> and the reward table lives in
    /// <see cref="MatchRewardService"/>.
    ///
    /// A bot is an ordinary <c>Players</c> row now (plans/PLAN-025-bots/plan.md), so this class no longer carries a
    /// login sentinel: a move's actor is the match's own current player, and the sweep finds the matches to play by
    /// asking which logins are bots. What is left here is only the machine's *behaviour*.
    /// </summary>
    public static class BotOpponent
    {
        /// <summary>
        /// How often its turn is looked for. Its effect is gated by the think window below, so a slower poll only
        /// makes the bot answer a little later rather than changing how the delay is measured.
        /// </summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        /// <summary>The shortest the bot waits before answering — long enough to read as somebody thinking.</summary>
        public static readonly TimeSpan MinThink = TimeSpan.FromMilliseconds(1200);

        /// <summary>The longest it waits — see <see cref="MinThink"/>.</summary>
        public static readonly TimeSpan MaxThink = TimeSpan.FromMilliseconds(3200);

        /// <summary>
        /// A flat pause on top of the window above, so every bot move reads as somebody thinking rather than as the
        /// server answering (agreed 2026-09-27: the requester found its moves "happening too fast"). The window above
        /// still varies move to move; this only shifts the whole band, and it is added inside
        /// <see cref="ThinkTime"/> so <c>MoveDueAt</c> stays derived from the row — a restart or a redeploy mid-think
        /// still changes nothing about when the move was due.
        /// </summary>
        public static readonly TimeSpan ThinkingPause = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The weight of one card level in a bot's opening hand: <c>level²</c> rather than the flat draw a
        /// human gets, so the opponent turns up with something like the deck a strong player brings. A slot of
        /// its hand lands on level 10 25.97% of the time and on level 1 0.26% of the time, which leaves the five
        /// cards at level 7.9 on average with three of them level 8 or better. The draw itself is
        /// <see cref="GameLogicService.GetBotHand"/>.
        /// </summary>
        public static int HandLevelWeight(int level) => level * level;

        /// <summary>
        /// Total weight of every level, i.e. the <c>385</c> of the <c>level² / 385</c> table (1+4+…+100). The draw
        /// sums this over the levels the catalogue actually has, so a level with no cards can never be picked.
        /// </summary>
        public static int TotalHandLevelWeight =>
            Enumerable.Range(Card.MinLevel, Card.MaxLevel - Card.MinLevel + 1).Sum(HandLevelWeight);

        /// <summary>
        /// How long this particular move takes, derived from the match and how many cards are already on the board.
        /// Every move therefore gets its own delay with nothing to store, and a restart (or a redeploy) mid-think
        /// cannot change when that move was due. <see cref="ThinkingPause"/> is added on top of the window, so the
        /// delay reads as a person thinking rather than as the server replying.
        /// </summary>
        public static TimeSpan ThinkTime(int matchId, int placements)
        {
            var windowMs = (long)(MaxThink - MinThink).TotalMilliseconds;
            if (windowMs <= 0)
            {
                return ThinkingPause + MinThink;
            }

            var offset = (uint)unchecked(matchId * 31 + placements * 7) % (uint)windowMs;
            return ThinkingPause + MinThink + TimeSpan.FromMilliseconds(offset);
        }

        /// <summary>
        /// When this match's bot move is due: the newest placement plus that move's thinking time, or the activation
        /// stamp while the board is still empty. Null when there is nothing to measure from — a match with neither a
        /// placement nor an activation — which is the one case the bot leaves alone.
        /// </summary>
        public static DateTime? MoveDueAt(Match match, IReadOnlyCollection<CardPlacement> placements)
        {
            var reference =
                placements.Count == 0
                    ? match.ActivatedAt
                    : placements.Max(placement => placement.PlacedAt);

            return reference is null ? null : reference + ThinkTime(match.Id, placements.Count);
        }

        /// <summary>True once the bot is allowed to play.</summary>
        public static bool IsMoveDue(Match match, IReadOnlyCollection<CardPlacement> placements, DateTime now) =>
            MoveDueAt(match, placements) is { } due && due <= now;
    }
}
