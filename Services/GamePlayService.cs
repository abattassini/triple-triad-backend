using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    public class GamePlayService
    {
        private readonly IGameRepository _gameRepository;
        private readonly GameLogicService _gameLogic;
        private readonly MatchRewardService _matchRewardService;

        public GamePlayService(
            IGameRepository gameRepository,
            GameLogicService gameLogic,
            MatchRewardService matchRewardService
        )
        {
            _gameRepository = gameRepository;
            _gameLogic = gameLogic;
            _matchRewardService = matchRewardService;
        }

        public class PlayCardServiceResult
        {
            public bool IsSuccess { get; set; }
            public string? ErrorMessage { get; set; }
            public GameLogicService.PlayCardResult? GameResult { get; set; }
            public Match? UpdatedMatch { get; set; }

            // Populated only on the move that completes the match.
            public MatchRewardService.MatchRewardResult? Rewards { get; set; }
        }

        public async Task<PlayCardServiceResult> PlayCardAsync(
            int matchId,
            int cardId,
            int x,
            int y,
            string playerId
        )
        {
            try
            {
                // 1. Validate match exists
                var match = await _gameRepository.GetMatchByIdAsync(matchId);
                if (match is null)
                {
                    return new PlayCardServiceResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Match not found",
                    };
                }

                // 2. Validate card exists
                var card = await _gameRepository.GetCardByIdAsync(cardId);
                if (card is null)
                {
                    return new PlayCardServiceResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Card not found",
                    };
                }

                // 3. Validate player has this card in their hand
                var playerHand = await _gameRepository.GetPlayerHandAsync(matchId, playerId);
                if (!playerHand.Any(ph => ph.CardId == cardId && !ph.IsUsed))
                {
                    return new PlayCardServiceResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Card not in player's hand or already used",
                    };
                }

                // 4. Calculate game logic
                var currentPlacements = await _gameRepository.GetCardPlacementsAsync(matchId);
                var gameResult = _gameLogic.PlayCard(
                    match,
                    currentPlacements,
                    card,
                    playerId,
                    x,
                    y
                );

                if (!gameResult.IsValid)
                {
                    return new PlayCardServiceResult
                    {
                        IsSuccess = false,
                        ErrorMessage = gameResult.ErrorMessage,
                    };
                }

                // 5. Persist all changes to database (awards rewards when the match completes)
                var rewards = await PersistGameChanges(
                    matchId,
                    cardId,
                    playerId,
                    x,
                    y,
                    gameResult,
                    match,
                    playerHand
                );

                // 6. Return success with updated match
                var updatedMatch = await _gameRepository.GetMatchByIdAsync(matchId);

                return new PlayCardServiceResult
                {
                    IsSuccess = true,
                    GameResult = gameResult,
                    UpdatedMatch = updatedMatch,
                    Rewards = rewards,
                };
            }
            catch (Exception ex)
            {
                return new PlayCardServiceResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"An error occurred: {ex.Message}",
                };
            }
        }

        private async Task<MatchRewardService.MatchRewardResult?> PersistGameChanges(
            int matchId,
            int cardId,
            string playerId,
            int x,
            int y,
            GameLogicService.PlayCardResult gameResult,
            Match match,
            List<PlayerHand> playerHand
        )
        {
            // Add the card placement
            var newPlacement = new CardPlacement
            {
                MatchId = matchId,
                CardId = cardId,
                PlayerId = playerId,
                Owner = playerId,
                X = x,
                Y = y,
                PlacedAt = DateTime.UtcNow,
            };
            await _gameRepository.AddCardPlacementAsync(newPlacement);

            // Update captured cards ownership
            if (gameResult.CapturedCards.Count != 0)
            {
                await _gameRepository.UpdateCardPlacementOwnershipAsync(gameResult.CapturedCards);
            }

            // Mark card as used in player's hand
            var usedCard = playerHand.FirstOrDefault(ph => ph.CardId == cardId && !ph.IsUsed);
            if (usedCard is not null)
            {
                usedCard.IsUsed = true;
                await _gameRepository.UpdatePlayerHandAsync(usedCard);
            }

            // Update match state
            match.Player1Score = gameResult.Player1Score;
            match.Player2Score = gameResult.Player2Score;
            match.CurrentPlayerTurn = _gameLogic.GetNextPlayer(
                playerId,
                match.Player1Id,
                match.Player2Id
            );

            MatchRewardService.MatchRewardResult? rewards = null;

            if (gameResult.IsGameComplete && match.Status != "completed")
            {
                match.Status = "completed";
                match.CompletedAt = DateTime.UtcNow;
                match.WinnerId = gameResult.WinnerId;

                // Award coins/XP + W/L/T exactly once, before persisting the final match state.
                rewards = await _matchRewardService.AwardForMatchAsync(match);
            }

            await _gameRepository.UpdateMatchAsync(match);

            return rewards;
        }
    }
}
