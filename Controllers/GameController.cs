using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GameController : ControllerBase
    {
        private readonly IGameRepository _gameRepository;
        private readonly GameLogicService _gameLogic;
        private readonly GamePlayService _gamePlayService;

        public GameController(
            IGameRepository gameRepository,
            GameLogicService gameLogic,
            GamePlayService gamePlayService
        )
        {
            _gameRepository = gameRepository;
            _gameLogic = gameLogic;
            _gamePlayService = gamePlayService;
        }

        // The JWT subject is the player's login, which is also the playerId
        // used across the game layer.
        private string? GetCurrentUserId()
        {
            return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        }

        [HttpGet("cards")]
        public async Task<ActionResult<List<Card>>> GetCards()
        {
            // Cards are static game data (not user-specific), so this endpoint
            // stays public so the app can render the card gallery pre-auth.
            var cards = await _gameRepository.GetAllCardsAsync();
            return Ok(cards);
        }

        [Authorize]
        [HttpPost("match")]
        public async Task<ActionResult<object>> CreateMatch([FromBody] CreateMatchRequest request)
        {
            try
            {
                var playerId = GetCurrentUserId();
                if (string.IsNullOrEmpty(playerId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                // Check if player already has an active match
                var existingMatch = await _gameRepository.GetActiveMatchForPlayerAsync(playerId);
                if (existingMatch is not null)
                {
                    return BadRequest(new { error = "Player already has an active match" });
                }

                // Determine opponent: null = waiting for PvP, "AI" = vs AI
                string? opponent = request.OpponentId;

                // Create new match
                var match = await _gameRepository.CreateMatchAsync(playerId, opponent);

                // Get all available cards and create random hands
                var allCards = await _gameRepository.GetAllCardsAsync();
                var player1Hand = _gameLogic.GetRandomHand(allCards, 5);

                // For PvP waiting matches, only create player1's hand
                // Player2's hand will be created when they join
                List<Card> player2Hand =
                    match.Status == "active" ? _gameLogic.GetRandomHand(allCards, 5) : [];

                // Create player hands in database
                await _gameRepository.CreatePlayerHandsAsync(
                    match.Id,
                    match.Player1Id,
                    match.Player2Id,
                    player1Hand,
                    player2Hand
                ); // Get the player's hand directly from repository
                var playerHand = await _gameRepository.GetPlayerHandAsync(match.Id, playerId);

                return Ok(
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
                        },
                        playerHand = playerHand
                            .Where(ph => !ph.IsUsed)
                            .Select(ph => new
                            {
                                ph.Card.Id,
                                ph.Card.Name,
                                ph.Card.Image,
                                ph.Card.TopValue,
                                ph.Card.RightValue,
                                ph.Card.BottomValue,
                                ph.Card.LeftValue,
                                ph.Card.Element,
                                ph.Card.Level,
                            })
                            .ToList(),
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [Authorize]
        [HttpGet("match/{matchId}")]
        public async Task<ActionResult<object>> GetMatch(int matchId)
        {
            var match = await _gameRepository.GetMatchByIdAsync(matchId);
            if (match is null)
            {
                return NotFound(new { error = "Match not found" });
            }

            var placements = await _gameRepository.GetCardPlacementsAsync(matchId);

            return Ok(
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
                        match.CreatedAt,
                        match.CompletedAt,
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

        [Authorize]
        [HttpGet("match/{matchId}/hand")]
        public async Task<ActionResult<List<object>>> GetPlayerHand(int matchId)
        {
            var userId = GetCurrentUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            // The authenticated user can only ever see their own hand.
            var hand = await _gameRepository.GetPlayerHandAsync(matchId, userId);
            var cards = hand.Select(ph => new
                {
                    ph.Card.Id,
                    ph.Card.Name,
                    ph.Card.Image,
                    ph.Card.TopValue,
                    ph.Card.RightValue,
                    ph.Card.BottomValue,
                    ph.Card.LeftValue,
                    ph.Card.Element,
                    ph.Card.Level,
                    isUsed = ph.IsUsed,
                })
                .ToList();
            return Ok(cards);
        }

        [Authorize]
        [HttpPost("match/{matchId}/play")]
        public async Task<ActionResult<object>> PlayCard(
            int matchId,
            [FromBody] PlayCardRequest request
        )
        {
            var playerId = GetCurrentUserId();
            if (string.IsNullOrEmpty(playerId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var result = await _gamePlayService.PlayCardAsync(
                matchId,
                request.CardId,
                request.X,
                request.Y,
                playerId
            );

            if (!result.IsSuccess)
            {
                return BadRequest(new { error = result.ErrorMessage });
            }

            return Ok(
                new
                {
                    success = true,
                    capturedCards = result.GameResult!.CapturedCards.Select(c => new
                    {
                        c.Id,
                        c.X,
                        c.Y,
                    }),
                    player1Score = result.GameResult.Player1Score,
                    player2Score = result.GameResult.Player2Score,
                    currentPlayer = result.UpdatedMatch!.CurrentPlayerTurn,
                    isGameComplete = result.GameResult.IsGameComplete,
                    winnerId = result.GameResult.WinnerId,
                    rewards =
                        result.Rewards is null
                            ? null
                            : new
                            {
                                player1Coins = result.Rewards.Player1Coins,
                                player1Experience = result.Rewards.Player1Experience,
                                player2Coins = result.Rewards.Player2Coins,
                                player2Experience = result.Rewards.Player2Experience,
                            },
                }
            );
        }

        [Authorize]
        [HttpGet("matches/waiting")]
        public async Task<ActionResult<List<object>>> GetWaitingMatches()
        {
            var matches = await _gameRepository.GetWaitingMatchesAsync();
            var result = matches
                .Select(m => new
                {
                    m.Id,
                    m.Player1Id,
                    m.CreatedAt,
                })
                .ToList();

            return Ok(result);
        }

        [Authorize]
        [HttpPost("match/{matchId}/join")]
        public async Task<ActionResult<object>> JoinMatch(int matchId)
        {
            try
            {
                var playerId = GetCurrentUserId();
                if (string.IsNullOrEmpty(playerId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var match = await _gameRepository.GetMatchByIdAsync(matchId);
                if (match is null)
                {
                    return NotFound(new { error = "Match not found" });
                }

                if (match.Status != "waiting")
                {
                    return BadRequest(new { error = "Match is not available for joining" });
                }

                if (match.Player1Id == playerId)
                {
                    return BadRequest(new { error = "Cannot join your own match" });
                }

                // Update match with second player
                match.Player2Id = playerId;
                match.Status = "active";

                // Create hand for the joining player
                var allCards = await _gameRepository.GetAllCardsAsync();
                var player2Hand = _gameLogic.GetRandomHand(allCards, 5);

                await _gameRepository.CreatePlayerHandsAsync(
                    matchId,
                    match.Player1Id,
                    playerId,
                    new List<Card>(),
                    player2Hand
                );
                await _gameRepository.UpdateMatchAsync(match);

                // Get the player's hand directly
                var playerHand = await _gameRepository.GetPlayerHandAsync(matchId, playerId);

                return Ok(
                    new
                    {
                        match = new
                        {
                            match.Id,
                            match.Player1Id,
                            match.Player2Id,
                            match.CurrentPlayerTurn,
                            match.Status,
                        },
                        playerHand = playerHand
                            .Where(ph => !ph.IsUsed)
                            .Select(ph => new
                            {
                                ph.Card.Id,
                                ph.Card.Name,
                                ph.Card.Image,
                                ph.Card.TopValue,
                                ph.Card.RightValue,
                                ph.Card.BottomValue,
                                ph.Card.LeftValue,
                                ph.Card.Element,
                                ph.Card.Level,
                            })
                            .ToList(),
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class CreateMatchRequest
    {
        public string? OpponentId { get; set; }
    }

    public class PlayCardRequest
    {
        public int CardId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }
}
