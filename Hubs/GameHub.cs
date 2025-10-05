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

        public GameHub(IGameRepository gameRepository, GamePlayService gamePlayService)
        {
            _gameRepository = gameRepository;
            _gamePlayService = gamePlayService;
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
                        }
                    );
            }
        }

        public async Task LeaveMatch(int matchId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"match-{matchId}");
        }

        public async Task PlayCard(int matchId, int cardId, int x, int y, string playerId)
        {
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
            await Clients
                .Group($"match-{matchId}")
                .SendAsync(
                    "CardPlayed",
                    new
                    {
                        playerId,
                        cardId,
                        x,
                        y,
                        capturedCards = result.GameResult!.CapturedCards.Select(c => new
                        {
                            id = c.Id,
                            x = c.X,
                            y = c.Y,
                            newOwner = playerId,
                        }),
                        player1Score = result.GameResult.Player1Score,
                        player2Score = result.GameResult.Player2Score,
                        currentPlayer = result.UpdatedMatch!.CurrentPlayerTurn,
                        isGameComplete = result.GameResult.IsGameComplete,
                        winnerId = result.GameResult.WinnerId,
                    }
                );
            ;

            if (result.GameResult.IsGameComplete)
            {
                await Clients
                    .Group($"match-{matchId}")
                    .SendAsync(
                        "GameCompleted",
                        new
                        {
                            winnerId = result.GameResult.WinnerId,
                            player1Score = result.GameResult.Player1Score,
                            player2Score = result.GameResult.Player2Score,
                            completedAt = result.UpdatedMatch.CompletedAt,
                        }
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
