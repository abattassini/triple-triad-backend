using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The two payloads a board listens to, built in one place because two senders use them: the hub, for the moves a
    /// player makes over SignalR, and <see cref="SignalRMatchNotifier"/>, for the moves the server makes on a client's
    /// behalf (the CPU) and for the matches it settles on its own (a timeout). They are the anonymous objects the hub
    /// has always sent, so every field name here is a field name in the client — change one and the board changes.
    /// </summary>
    public static class MatchPushes
    {
        /// <summary>`CardPlayed`: what moved and where, the new scores and turn, and whether it finished the match.</summary>
        public static object CardPlayed(
            Match match,
            GameLogicService.PlayCardResult result,
            string playerId,
            int cardId,
            int x,
            int y
        ) =>
            new
            {
                playerId,
                cardId,
                x,
                y,
                capturedCards = result.CapturedCards.Select(placement => new
                {
                    id = placement.Id,
                    x = placement.X,
                    y = placement.Y,
                    newOwner = playerId,
                }),
                triggeredRules = result.TriggeredRules.ToNames(),
                player1Score = result.Player1Score,
                player2Score = result.Player2Score,
                currentPlayer = match.CurrentPlayerTurn,
                isGameComplete = result.IsGameComplete,
                winnerId = result.WinnerId,
            };

        /// <summary>
        /// `GameCompleted`: the final scores, the rewards both sides were granted, and the reason when the server
        /// settled the match rather than a player filling the board (<c>"timeout"</c>); <c>null</c> on a played-out
        /// match, which is how a client tells the two apart.
        /// </summary>
        public static object Completed(
            Match match,
            MatchRewardService.MatchRewardResult? rewards,
            string? reason = null
        ) =>
            new
            {
                winnerId = match.WinnerId,
                player1Score = match.Player1Score,
                player2Score = match.Player2Score,
                completedAt = match.CompletedAt,
                reason,
                rewards =
                    rewards is null
                        ? null
                        : new
                        {
                            player1Coins = rewards.Player1Coins,
                            player1Experience = rewards.Player1Experience,
                            player2Coins = rewards.Player2Coins,
                            player2Experience = rewards.Player2Experience,
                        },
            };
    }
}
