using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
        private readonly IPlayerCardRepository _playerCardRepository;
        private readonly GameLogicService _gameLogic;
        private readonly GamePlayService _gamePlayService;
        private readonly MatchStateService _matchState;
        private readonly IMatchNotifier _notifier;
        private readonly IRandomSource _random;
        private readonly MatchmakingService _matchmaking;

        public GameController(
            IGameRepository gameRepository,
            IPlayerCardRepository playerCardRepository,
            GameLogicService gameLogic,
            GamePlayService gamePlayService,
            MatchStateService matchState,
            IMatchNotifier notifier,
            IRandomSource random,
            MatchmakingService matchmaking
        )
        {
            _gameRepository = gameRepository;
            _playerCardRepository = playerCardRepository;
            _gameLogic = gameLogic;
            _gamePlayService = gamePlayService;
            _matchState = matchState;
            _notifier = notifier;
            _random = random;
            _matchmaking = matchmaking;
        }

        /// <summary>
        /// Validates a list of card ids a client wants to play with: exactly <see cref="GameLogicService.HandSize"/>
        /// of them, no repeats, all in the catalogue and all owned by the caller. Nothing is written here — a caller
        /// rejects the request (400) before it creates or changes anything.
        /// </summary>
        private async Task<(List<Card> Hand, string? Error)> ReadHandAsync(
            string playerId,
            IReadOnlyList<int> cardIds
        )
        {
            if (cardIds.Count != GameLogicService.HandSize)
            {
                return ([], $"Select exactly {GameLogicService.HandSize} cards.");
            }

            if (cardIds.Distinct().Count() != cardIds.Count)
            {
                return ([], "Your hand cannot contain the same card twice.");
            }

            var catalogue = await _gameRepository.GetCardsByIdsAsync(cardIds);
            if (catalogue.Count != cardIds.Count)
            {
                return ([], "Unknown card id.");
            }

            var owned = await _playerCardRepository.GetOwnedCardIdsAsync(playerId, cardIds);
            if (owned.Count != cardIds.Count)
            {
                return ([], "You can only play cards you own.");
            }

            // The sent order is kept, so the board shows the hand in the order the player picked it.
            return (cardIds.Select(id => catalogue.First(card => card.Id == id)).ToList(), null);
        }

        // The JWT subject is the player's login, which is also the playerId
        // used across the game layer.
        private string? GetCurrentUserId()
        {
            return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        }

        // Giving up a player's unfinished matches used to live here. It moved to MatchmakingService because it is part
        // of the matchmaking decision rather than of any one endpoint: it has to happen inside the same gate as the
        // find-or-create, or a search could give up a match that another search is about to claim.

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

                // Rules are optional; unknown names are rejected so a typo never silently creates a
                // match with different rules than the client asked for.
                if (!MatchRuleExtensions.TryParseAll(request.Rules, out var rules))
                {
                    return BadRequest(
                        new
                        {
                            error =
                                "Unknown rule. Supported rules: "
                                + string.Join(", ", MatchRuleExtensions.SupportedRuleNames()),
                        }
                    );
                }

                // Determine opponent: null = waiting for PvP, "AI" = the CPU, which is seated as player 2 straight away
                string? opponent = request.OpponentId;

                // The hand the client picked, or "I will pick once there is an opponent". A waiting PvP match and a
                // match against the CPU both support the flag — in both cases the only hand missing is the human's,
                // and the pick arrives through POST match/{id}/hand. Any other named opponent would leave a real
                // player sitting hand-less, and a list plus the flag describes the same hand twice.
                if (request.PickHandLater && request.CardIds is { Length: > 0 })
                {
                    return BadRequest(
                        new { error = "PickHandLater cannot be combined with a card list." }
                    );
                }

                if (request.PickHandLater && !string.IsNullOrEmpty(opponent) && opponent != CpuOpponent.Login)
                {
                    return BadRequest(
                        new
                        {
                            error =
                                "PickHandLater only applies to a match waiting for an opponent, or to a match against the CPU.",
                        }
                    );
                }

                var chosenHand = new List<Card>();
                var hasChosenHand = false;
                if (request.CardIds is { Length: > 0 })
                {
                    var chosenRead = await ReadHandAsync(playerId, request.CardIds);
                    if (chosenRead.Error is not null)
                    {
                        return BadRequest(new { error = chosenRead.Error });
                    }

                    chosenHand = chosenRead.Hand;
                    hasChosenHand = true;
                }

                // Starting a Quick Match gives up on whatever the player was still in. This used to be a flat
                // `400 "Player already has an active match"`, which left a player who walked away from a match — or
                // whose opponent did — waiting for the sweep before they could search again. Everything they are in is
                // abandoned instead, and the opposing side is told. Deliberately after the validation above, so a
                // rejected request still writes nothing.
                await _matchmaking.AbandonUnfinishedAsync(playerId);

                // Create new match with the requested rules
                var match = await _gameRepository.CreateMatchAsync(playerId, opponent, rules);

                var allCards = await _gameRepository.GetAllCardsAsync();

                // Player 1 sits down with the cards they picked, a random draw, or nothing at all when they pick once
                // an opponent is there. Player 2 only has a hand at creation on the AI path, and there it is the CPU's
                // level-weighted draw — the uniform draw above is the one a human gets.
                List<Card> player1Hand;
                if (hasChosenHand)
                {
                    player1Hand = chosenHand;
                }
                else if (request.PickHandLater)
                {
                    player1Hand = [];
                }
                else
                {
                    player1Hand = _gameLogic.GetRandomHand(allCards);
                }

                List<Card> player2Hand =
                    match.Status == "active" ? _gameLogic.GetCpuHand(allCards, _random) : [];

                if (player1Hand.Count > 0 || player2Hand.Count > 0)
                {
                    await _gameRepository.CreatePlayerHandsAsync(
                        match.Id,
                        match.Player1Id,
                        match.Player2Id,
                        player1Hand,
                        player2Hand
                    );
                }
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
                            rules = match.Rules.ToNames(),
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

        /// <summary>
        /// Quick Match: the player is asking to be put into a game, not asking for a match record.
        ///
        /// This is the entire search in one request — find a compatible opponent who is waiting, join them, or start a
        /// match to be found in. It replaced the client's old read-then-decide-then-write sequence, in which two
        /// players searching at the same instant could both conclude "nobody is waiting" and each start their own
        /// match, leaving the two of them waiting for each other. The decision now happens on the server, under a
        /// gate, so it cannot be made twice; see <see cref="MatchmakingService"/> for the full account.
        ///
        /// No hand is filed here: the SelectHand screen picks once the match has both players, on both sides, through
        /// <c>POST match/{id}/hand</c>. The response mirrors create and join so the client can read the answer the same
        /// way either time — <c>waiting</c> means "you are the one being found", <c>active</c> means "somebody was
        /// already waiting and you are in their game".
        /// </summary>
        [Authorize]
        [HttpPost("match/quick")]
        public async Task<ActionResult<object>> QuickMatch([FromBody] QuickMatchRequest? request = null)
        {
            try
            {
                var playerId = GetCurrentUserId();
                if (string.IsNullOrEmpty(playerId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                if (!MatchRuleExtensions.TryParseAll(request?.Rules, out var rules))
                {
                    return BadRequest(
                        new
                        {
                            error =
                                "Unknown rule. Supported rules: "
                                + string.Join(", ", MatchRuleExtensions.SupportedRuleNames()),
                        }
                    );
                }

                var match = await _matchmaking.FindOrCreateWaitingMatchAsync(
                    playerId,
                    rules,
                    DateTime.UtcNow
                );

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
                            rules = match.Rules.ToNames(),
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
            var state = await _matchState.GetStateAsync(match, DateTime.UtcNow);

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
                        // What the SelectHand step waits on: both hands filed, and whether a deadline has passed.
                        handsReady = state.HandsReady,
                        timedOut = state.TimedOut,
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
                    triggeredRules = result.GameResult!.TriggeredRules.ToNames(),
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

        /// <summary>
        /// Files the five cards a player picked once the match had both players — the SelectHand screen calls this on
        /// both sides of a Quick Match, so an active match with no hand yet is waiting on exactly this call. The list
        /// is validated exactly like the one create/join take, so a hand can only ever hold cards the player owns,
        /// and retrying is safe because the unused rows are replaced.
        /// </summary>
        [Authorize]
        [HttpPost("match/{matchId}/hand")]
        public async Task<ActionResult<object>> SetHand(int matchId, [FromBody] SetHandRequest request)
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

                if (match.Player1Id != playerId && match.Player2Id != playerId)
                {
                    return BadRequest(new { error = "You are not a player in this match." });
                }

                if (match.Status != "active")
                {
                    return BadRequest(new { error = "This match is not waiting for a hand." });
                }

                if (match.PlayerHands.Any(hand => hand.PlayerId == playerId && hand.IsUsed))
                {
                    return BadRequest(new { error = "You have already played a card in this match." });
                }

                var handRead = await ReadHandAsync(playerId, request.CardIds);
                if (handRead.Error is not null)
                {
                    return BadRequest(new { error = handRead.Error });
                }

                await _gameRepository.ReplacePlayerHandAsync(matchId, playerId, handRead.Hand);

                // The opponent may be sitting on "waiting for your opponent to pick their cards" (or still picking):
                // both hands are in now, so whoever is waiting can go to the board.
                await _notifier.HandReadyAsync(matchId);

                return Ok(new { success = true, matchId, cardIds = handRead.Hand.Select(card => card.Id).ToList() });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gives up on a waiting match (the Lobby Cancel button): only the creator of a match nobody has joined may
        /// do it, and the match is abandoned rather than deleted so the history survives.
        /// </summary>
        [Authorize]
        [HttpPost("match/{matchId}/cancel")]
        public async Task<ActionResult<object>> CancelMatch(int matchId)
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

                if (match.Player1Id != playerId || !string.IsNullOrEmpty(match.Player2Id))
                {
                    return BadRequest(new { error = "Only your own waiting match can be cancelled." });
                }

                if (match.Status != "waiting")
                {
                    return BadRequest(new { error = "This match has already started." });
                }

                match.Status = "abandoned";
                await _gameRepository.UpdateMatchAsync(match);
                await _notifier.AbandonedAsync(matchId, "the search was cancelled");

                return Ok(new { success = true, matchId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
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
                    rules = m.Rules.ToNames(),
                })
                .ToList();

            return Ok(result);
        }

        [Authorize]
        [HttpPost("match/{matchId}/join")]
        public async Task<ActionResult<object>> JoinMatch(
            int matchId,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JoinMatchRequest? request = null
        )
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

                // The joiner's hand. `PickHandLater` is the SelectHand flow: both players pick once the match has the
                // two of them, so this request files nothing and POST match/{id}/hand brings the five cards. A list
                // files them here and now, and neither means the random draw an older client expects. Read (and
                // validated) before the match is touched, so a bad request leaves it waiting exactly as it was.
                if (request is { PickHandLater: true, CardIds: { Length: > 0 } })
                {
                    return BadRequest(
                        new { error = "PickHandLater cannot be combined with a card list." }
                    );
                }

                var allCards = await _gameRepository.GetAllCardsAsync();
                List<Card> player2Hand;
                if (request?.CardIds is { Length: > 0 })
                {
                    var joinRead = await ReadHandAsync(playerId, request.CardIds);
                    if (joinRead.Error is not null)
                    {
                        return BadRequest(new { error = joinRead.Error });
                    }

                    player2Hand = joinRead.Hand;
                }
                else if (request?.PickHandLater == true)
                {
                    player2Hand = [];
                }
                else
                {
                    player2Hand = _gameLogic.GetRandomHand(allCards);
                }

                // The joiner is starting a Quick Match too, so their own unfinished matches are given up exactly as on
                // create: a player may only ever be in one match at a time, and joining while an old one is still open
                // would leave two. Like create, this sits after the validation above, so a rejected join writes
                // nothing and the match being joined is left exactly as it was.
                await _matchmaking.AbandonUnfinishedAsync(playerId);

                // Seat the second player and stamp the activation: the hand-pick timeout and the "nobody moved"
                // timeout are both measured from here.
                match.Player2Id = playerId;
                match.Status = "active";
                match.ActivatedAt = DateTime.UtcNow;

                if (player2Hand.Count > 0)
                {
                    await _gameRepository.CreatePlayerHandsAsync(
                        matchId,
                        match.Player1Id,
                        playerId,
                        [],
                        player2Hand
                    );
                }

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
                            rules = match.Rules.ToNames(),
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

        /// <summary>
        /// Optional rules to enable for the match (e.g. <c>["Same"]</c>). Unknown names are rejected.
        /// </summary>
        public string[]? Rules { get; set; }

        /// <summary>The five cards the creator plays with; omitted ⇒ the server draws a random hand.</summary>
        public int[]? CardIds { get; set; }

        /// <summary>
        /// True when the creator picks their hand once an opponent is there (the SelectHand flow): the match is
        /// created waiting with no hand at all, and <c>POST match/{id}/hand</c> files it later.
        /// </summary>
        public bool PickHandLater { get; set; }
    }

    /// <summary>
    /// Body of <c>POST api/game/match/quick</c>: the rules the searcher wants to play by.
    ///
    /// There is deliberately no card list and no "pick later" flag — Quick Match always seats both players and lets
    /// the SelectHand screen file the hands, so the match is created without a hand on either side.
    /// </summary>
    public class QuickMatchRequest
    {
        /// <summary>Rules to play by; omitted or null means the basic rules only. Unknown names are rejected.</summary>
        public string[]? Rules { get; set; }
    }

    /// <summary>Body of <c>POST api/game/match/{id}/hand</c>: the five cards the waiting player picked.</summary>
    public class SetHandRequest
    {
        public int[] CardIds { get; set; } = [];
    }

    /// <summary>
    /// Body of <c>POST api/game/match/{id}/join</c>. A card list files that hand straight away, <c>PickHandLater</c>
    /// joins with no hand at all (the SelectHand flow, where both players pick and then file through
    /// <c>POST match/{id}/hand</c>), and an empty body still draws a random hand for an older client.
    /// </summary>
    public class JoinMatchRequest
    {
        public int[]? CardIds { get; set; }

        /// <summary>True when the joiner picks once the match has both players, in the SelectHand screen.</summary>
        public bool PickHandLater { get; set; }
    }

    public class PlayCardRequest
    {
        public int CardId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }
}
