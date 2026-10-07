using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The payloads a board is built from, in one place because more than one sender uses them: the hub, for the moves a
    /// player makes over SignalR and for the answer to <c>RequestLegalMoves</c>, and <see cref="SignalRMatchNotifier"/>,
    /// for the moves the server makes on a client's behalf (the bot) and for the matches it settles on its own (a
    /// timeout). The pushes are the anonymous objects the hub has always sent, so every field name here is a field name
    /// in the client — change one and the board changes.
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
        /// `LegalMoves`: the answer to `RequestLegalMoves` — every move the caller may make this turn and what each
        /// would do. The per-move fields are the ones `CardPlayed` sends, so a client renders a previewed move with the
        /// same code it renders a push with; the two differences are deliberate:
        ///
        /// - `capturedCards` carries **cells**, not placement rows — a preview has no rows, and the board flips by cell.
        /// - `nextPlayer` is whose turn it will be once the move is played (where `CardPlayed`'s `currentPlayer` is
        ///   whose turn it already is), because a preview describes a board that has not happened yet.
        ///
        /// `placements` is the board the list was computed from, so a client can tell a stale list from a live one.
        /// </summary>
        public static object LegalMoves(MovePreviewService.Preview preview) =>
            new
            {
                matchId = preview.Match.Id,
                playerId = preview.PlayerId,
                placements = preview.Placements,
                nextPlayer = preview.NextPlayer,
                moves = preview.Moves.Select(move => new
                {
                    cardId = move.CardId,
                    x = move.X,
                    y = move.Y,
                    capturedCards = move.Result.CapturedCards.Select(placement => new
                    {
                        x = placement.X,
                        y = placement.Y,
                    }),
                    triggeredRules = move.Result.TriggeredRules.ToNames(),
                    player1Score = move.Result.Player1Score,
                    player2Score = move.Result.Player2Score,
                    isGameComplete = move.Result.IsGameComplete,
                    winnerId = move.Result.WinnerId,
                }),
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
