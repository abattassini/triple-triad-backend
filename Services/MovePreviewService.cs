using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// What a player may play this turn, and what each move would do — the board's own answer to "what happens if I
    /// drop this card here", so a client can land a card the instant it is dropped instead of waiting for the move to
    /// come back over SignalR.
    ///
    /// It is a **read**: nothing is written and no tracked row is touched (every candidate is resolved on a copy of the
    /// board, see <see cref="GameLogicService.CopyBoard"/>). The rules stay on the server — the preview is the very
    /// pipeline the move itself will go through (<see cref="GameLogicService.EnumerateMoves"/>), so what a board shows
    /// instantly is what the next write produces, and a change to the rules can never leave the client rendering a
    /// board the server disagrees with.
    ///
    /// The list is built from the asking player's **hand**, which is hidden information, so it is only ever sent to that
    /// player (<c>GameHub.RequestLegalMoves</c> answers the caller) and never to the match group.
    /// </summary>
    public class MovePreviewService(IGameRepository gameRepository, GameLogicService gameLogic)
    {
        private readonly IGameRepository _gameRepository = gameRepository;
        private readonly GameLogicService _gameLogic = gameLogic;

        /// <summary>
        /// One turn's preview: the match, who it is for, the board it was computed from (the staleness key a client
        /// compares its own board against), whose turn is next, and the moves.
        /// </summary>
        public sealed record Preview(
            Match Match,
            string PlayerId,
            int Placements,
            string NextPlayer,
            List<GameLogicService.MoveOutcome> Moves
        );

        /// <summary>
        /// The caller's legal moves, or null when there is nothing to preview **for them**: an unknown match, a match
        /// that is not active, a board that is not the caller's turn, or a caller who is not one of its two players.
        /// A player on turn with nothing playable gets an **empty list** — that is an answer, not a refusal, and the
        /// client treats it as "no preview to use".
        /// </summary>
        public async Task<Preview?> PreviewAsync(int matchId, string playerId)
        {
            var match = await _gameRepository.GetMatchByIdAsync(matchId);

            if (match is null || match.Status != "active" || match.CurrentPlayerTurn != playerId)
            {
                return null;
            }

            if (playerId != match.Player1Id && playerId != match.Player2Id)
            {
                return null;
            }

            var board = match.CardPlacements.ToList();
            var hand = match.PlayerHands.Where(row => row.PlayerId == playerId).ToList();

            return new Preview(
                match,
                playerId,
                board.Count,
                // The pipeline's own arithmetic, so the turn a preview announces is the turn the write will set.
                _gameLogic.GetNextPlayer(playerId, match.Player1Id, match.Player2Id),
                _gameLogic.EnumerateMoves(match, board, hand, playerId)
            );
        }
    }
}
