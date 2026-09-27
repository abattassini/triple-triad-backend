using Microsoft.AspNetCore.SignalR;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Hubs
{
    public class GameHub : Hub
    {
        private readonly IGameRepository _gameRepository;
        private readonly GamePlayService _gamePlayService;
        private readonly MovePreviewService _movePreviewService;
        private readonly TokenService _tokenService;
        private readonly IPlayerRepository _playerRepository;

        public GameHub(
            IGameRepository gameRepository,
            GamePlayService gamePlayService,
            MovePreviewService movePreviewService,
            TokenService tokenService,
            IPlayerRepository playerRepository
        )
        {
            _gameRepository = gameRepository;
            _gamePlayService = gamePlayService;
            _movePreviewService = movePreviewService;
            _tokenService = tokenService;
            _playerRepository = playerRepository;
        }

        public async Task JoinMatch(int matchId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"match-{matchId}");

            var match = await _gameRepository.GetMatchByIdAsync(matchId);
            if (match is not null)
            {
                await Clients
                    .Group($"match-{matchId}")
                    .SendAsync(
                        "MatchJoined",
                        new
                        {
                            matchId = match.Id,
                            status = match.Status,
                            currentPlayer = match.CurrentPlayerTurn,
                            player1Score = match.Player1Score,
                            player2Score = match.Player2Score,
                            rules = match.Rules.ToNames(),
                        }
                    );
            }
        }

        public async Task LeaveMatch(int matchId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"match-{matchId}");
        }

        public async Task PlayCard(int matchId, int cardId, int x, int y, string accessToken)
        {
            // The connection middleware does not populate the hub's user context
            // for WebSocket transports, so we validate the JWT sent with the call.
            var payload = _tokenService.Validate(accessToken);
            if (payload is null)
            {
                Console.WriteLine("⛔ PlayCard rejected: invalid or missing JWT");
                await Clients.Caller.SendAsync("Error", "User not authenticated");
                return;
            }

            // A token minted before the player's last password change must not keep working here either. The REST side
            // gets this from the JWT middleware (see Program.cs); the hub validates its own token because WebSocket
            // connections do not populate the hub's user context, so it has to repeat the check — otherwise a password
            // reset would sign the player out everywhere except the game board.
            var player = await _playerRepository.FindByLoginAsync(payload.Login);
            if (player is null || player.SessionVersion != payload.SessionVersion)
            {
                Console.WriteLine("⛔ PlayCard rejected: session retired by a password change");
                await Clients.Caller.SendAsync("Error", "User not authenticated");
                return;
            }

            var playerId = payload.Login;

            Console.WriteLine(
                $"🎮 PlayCard called: matchId={matchId}, cardId={cardId}, x={x}, y={y}, playerId={playerId}"
            );

            var result = await _gamePlayService.PlayCardAsync(matchId, cardId, x, y, playerId);

            if (!result.IsSuccess)
            {
                Console.WriteLine($"❌ PlayCard failed: {result.ErrorMessage}");
                await Clients.Caller.SendAsync("Error", result.ErrorMessage);
                return;
            }

            Console.WriteLine("✅ PlayCard succeeded, broadcasting to group...");

            // Notify all players in the match about the successful move
            var group = Clients.Group($"match-{matchId}");

            await group.SendAsync(
                "CardPlayed",
                MatchPushes.CardPlayed(
                    result.UpdatedMatch!,
                    result.GameResult!,
                    playerId,
                    cardId,
                    x,
                    y
                )
            );

            if (result.GameResult!.IsGameComplete)
            {
                await group.SendAsync(
                    "GameCompleted",
                    MatchPushes.Completed(result.UpdatedMatch!, result.Rewards)
                );
            }
        }

        /// <summary>
        /// What the caller may play this turn, and what each move would do — the board's own answer, so it can land a
        /// card the instant it is dropped instead of waiting for the move to come back over SignalR.
        ///
        /// **The caller only.** The list is built from the asking player's hand, which is hidden information: a group
        /// send would hand the player who just moved the opponent's card ids, and through the catalogue their art and
        /// ranks. There is nothing to answer when it is not the caller's turn (or not their match) — a client that asked
        /// early simply keeps the board it has, and every move still goes through <see cref="PlayCard"/>.
        /// </summary>
        public async Task RequestLegalMoves(int matchId, string accessToken)
        {
            // Same two checks as PlayCard: the connection middleware does not populate the hub's user context for
            // WebSocket transports, so the JWT travels with the call, and a token minted before a password change
            // must not keep working here either.
            var payload = _tokenService.Validate(accessToken);
            if (payload is null)
            {
                Console.WriteLine("⛔ RequestLegalMoves rejected: invalid or missing JWT");
                await Clients.Caller.SendAsync("Error", "User not authenticated");
                return;
            }

            var player = await _playerRepository.FindByLoginAsync(payload.Login);
            if (player is null || player.SessionVersion != payload.SessionVersion)
            {
                Console.WriteLine("⛔ RequestLegalMoves rejected: session retired by a password change");
                await Clients.Caller.SendAsync("Error", "User not authenticated");
                return;
            }

            var preview = await _movePreviewService.PreviewAsync(matchId, payload.Login);
            if (preview is null)
            {
                Console.WriteLine(
                    $"ℹ️ RequestLegalMoves: nothing to preview for {payload.Login} in match {matchId}"
                );
                return;
            }

            Console.WriteLine(
                $"🧭 RequestLegalMoves: {preview.Moves.Count} move(s) for {payload.Login} in match {matchId}"
            );

            await Clients.Caller.SendAsync("LegalMoves", MatchPushes.LegalMoves(preview));
        }

        public async Task RequestMatchStatus(int matchId)
        {
            var match = await _gameRepository.GetMatchByIdAsync(matchId);
            if (match is not null)
            {
                var placements = await _gameRepository.GetCardPlacementsAsync(matchId);

                await Clients.Caller.SendAsync(
                    "MatchStatus",
                    new
                    {
                        match = new
                        {
                            match.Id,
                            match.Player1Id,
                            match.Player2Id,
                            match.CurrentPlayerTurn,
                            match.Status,
                            match.Player1Score,
                            match.Player2Score,
                            match.WinnerId,
                            rules = match.Rules.ToNames(),
                        },
                        placements = placements.Select(p => new
                        {
                            p.Id,
                            p.CardId,
                            p.PlayerId,
                            p.Owner,
                            p.X,
                            p.Y,
                            card = new
                            {
                                p.Card.Id,
                                p.Card.Name,
                                p.Card.Image,
                                p.Card.TopValue,
                                p.Card.RightValue,
                                p.Card.BottomValue,
                                p.Card.LeftValue,
                                p.Card.Element,
                                p.Card.Level,
                            },
                        }),
                    }
                );
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // Handle player disconnection logic here if needed
            await base.OnDisconnectedAsync(exception);
        }
    }
}
