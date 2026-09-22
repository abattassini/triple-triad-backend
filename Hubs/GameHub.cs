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
        private readonly TokenService _tokenService;

        public GameHub(
            IGameRepository gameRepository,
            GamePlayService gamePlayService,
            TokenService tokenService
        )
        {
            _gameRepository = gameRepository;
            _gamePlayService = gamePlayService;
            _tokenService = tokenService;
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
            var playerId = _tokenService.ValidateToken(accessToken);
            if (string.IsNullOrEmpty(playerId))
            {
                Console.WriteLine("⛔ PlayCard rejected: invalid or missing JWT");
                await Clients.Caller.SendAsync("Error", "User not authenticated");
                return;
            }

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
