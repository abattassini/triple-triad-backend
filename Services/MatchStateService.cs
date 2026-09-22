using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>What a reader needs to know about a match beyond the row itself.</summary>
    public sealed record MatchState(bool HandsReady, bool TimedOut);

    /// <summary>
    /// Reads a match's hand/timeout state for the endpoints and the hub, and never writes: settling an expired match
    /// (and paying any forfeit) belongs to <see cref="MatchTimeoutService"/>, so a GET stays free of side effects no
    /// matter how often a client polls it.
    /// </summary>
    public class MatchStateService(IGameRepository gameRepository)
    {
        private readonly IGameRepository _gameRepository = gameRepository;

        /// <summary>Both hands filed, and whether a deadline has already passed.</summary>
        public async Task<MatchState> GetStateAsync(Match match, DateTime now)
        {
            var handsReady = await GetHandsReadyAsync(match);
            var lastActivity = await GetLastActivityAsync(match);

            return new MatchState(handsReady, IsTimedOut(match, handsReady, lastActivity, now));
        }

        /// <summary>
        /// The decision the sweep makes too, from timestamps alone — so it can be evaluated over a match whose hands
        /// and placements are already loaded (the sweep) as well as over a freshly read one (the endpoints).
        /// </summary>
        public static bool IsTimedOut(
            Match match,
            bool handsReady,
            DateTime? lastActivityAt,
            DateTime now
        )
        {
            if (match.Status == "waiting")
            {
                return match.CreatedAt <= MatchTimeouts.WaitingCutoff(now);
            }

            if (match.Status != "active")
            {
                return false;
            }

            if (!handsReady)
            {
                // An active match with no activation stamp predates this rule, so there is nothing to measure.
                return match.ActivatedAt is not null
                    && match.ActivatedAt <= MatchTimeouts.HandPickCutoff(now);
            }

            var reference = lastActivityAt ?? match.ActivatedAt;
            return reference is not null && reference <= MatchTimeouts.TurnIdleCutoff(now);
        }

        /// <summary>
        /// True when this player has their five hand rows filed — **played or not**: a card moving to the board marks
        /// its row used, so counting only the unused ones would read a match in progress as "no hand was ever filed"
        /// (which is what the S2 timeout used to abandon mid-game).
        /// </summary>
        public static bool HasFiledHand(Match match, string playerId) =>
            match.PlayerHands.Count(hand => hand.PlayerId == playerId) >= GameLogicService.HandSize;

        private async Task<bool> GetHandsReadyAsync(Match match)
        {
            if (string.IsNullOrEmpty(match.Player2Id))
            {
                return false;
            }

            var player1Hand = await _gameRepository.GetFiledHandCountAsync(
                match.Id,
                match.Player1Id
            );
            var player2Hand = await _gameRepository.GetFiledHandCountAsync(
                match.Id,
                match.Player2Id
            );

            return player1Hand >= GameLogicService.HandSize
                && player2Hand >= GameLogicService.HandSize;
        }

        /// <summary>The newest placement, or null when the board is still empty.</summary>
        private async Task<DateTime?> GetLastActivityAsync(Match match)
        {
            var placements = await _gameRepository.GetCardPlacementsAsync(match.Id);
            return placements.Count == 0 ? null : placements.Max(placement => placement.PlacedAt);
        }
    }
}
