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

                // 5. Take the turn: the claim that makes a turn single-writer. Everything above read state nobody else can
                // change while it is this player's turn (the opponent's own claim is guarded on the turn they would need
                // to take), so the only writer that can still be holding a snapshot of this turn is another writer *of
                // this turn* — a second backend on the same database (a local one and the deployed one, say) or a
                // double-sent move. Exactly one of them wins this flip and nothing is written by the ones that lose, so
                // a card can never be placed twice for one hand row. See plans/PLAN-016-one-writer-per-turn/plan.md.
                if (
                    !await _gameRepository.TryTakeTurnAsync(
                        matchId,
                        playerId,
                        _gameLogic.GetNextPlayer(playerId, match.Player1Id, match.Player2Id)
                    )
                )
                {
                    return new PlayCardServiceResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Not your turn",
                    };
                }

                // 6. Persist all changes to database (awards rewards when the match completes)
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

                // 7. Return success with updated match
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
            // The move, in the four pieces it changes: the card it places, the cards it captures, the hand row it spends
            // and the match row. They are written together, at the end (see PersistMoveAsync), because a board that shows
            // a card whose hand row was never spent — or a turn that flipped without a card — is not a state a move may
            // leave behind.
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

            var usedCard = playerHand.FirstOrDefault(ph => ph.CardId == cardId && !ph.IsUsed);
            if (usedCard is not null)
            {
                usedCard.IsUsed = true;
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

            await _gameRepository.PersistMoveAsync(
                newPlacement,
                gameResult.CapturedCards,
                usedCard,
                match
            );

            return rewards;
        }
    }
}
