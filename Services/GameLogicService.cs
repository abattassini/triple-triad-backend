using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    public class GameLogicService
    {
        public class PlayCardResult
        {
            public bool IsValid { get; set; }
            public string? ErrorMessage { get; set; }
            public List<CardPlacement> CapturedCards { get; set; } = new();
            public int Player1Score { get; set; }
            public int Player2Score { get; set; }
            public bool IsGameComplete { get; set; }
            public string? WinnerId { get; set; }

            /// <summary>
            /// Special rules that fired while resolving this move (bitmask; empty when none did).
            /// </summary>
            public MatchRule TriggeredRules { get; set; } = MatchRule.None;
        }

        public PlayCardResult PlayCard(
            Match match,
            List<CardPlacement> currentPlacements,
            Card card,
            string playerId,
            int x,
            int y
        )
        {
            // Step 1: Validate the move
            var validationResult = ValidateMove(match, currentPlacements, playerId, x, y);
            if (!validationResult.IsValid)
            {
                return validationResult;
            }

            // Step 2: Create the card placement
            var newPlacement = CreateCardPlacement(match.Id, card, playerId, x, y);

            // Step 3: Calculate all placements (current + new)
            var allPlacements = BuildPlacementsList(currentPlacements, newPlacement);

            // Step 4: Calculate captures and update ownership
            var captureResolution = ProcessCaptures(match, card, x, y, playerId, allPlacements);

            // Step 5: Calculate final scores
            var (player1Score, player2Score) = CalculatePlayerScores(
                allPlacements,
                match.Player1Id,
                match.Player2Id
            );

            // Step 6: Build and return result
            return BuildPlayCardResult(
                captureResolution,
                player1Score,
                player2Score,
                allPlacements.Count,
                match.Player1Id,
                match.Player2Id
            );
        }

        /// <summary>
        /// Validates if the move is legal (position available and correct turn)
        /// </summary>
        private static PlayCardResult ValidateMove(
            Match match,
            List<CardPlacement> placements,
            string playerId,
            int x,
            int y
        )
        {
            if (!IsValidMove(placements, x, y))
            {
                return new PlayCardResult { ErrorMessage = "Position is already occupied" };
            }

            if (match.CurrentPlayerTurn != playerId)
            {
                return new PlayCardResult { ErrorMessage = "Not your turn" };
            }

            return new PlayCardResult { IsValid = true };
        }

        /// <summary>
        /// Creates a new card placement at the specified position
        /// </summary>
        private static CardPlacement CreateCardPlacement(
            int matchId,
            Card card,
            string playerId,
            int x,
            int y
        )
        {
            return new CardPlacement
            {
                MatchId = matchId,
                CardId = card.Id,
                Card = card,
                PlayerId = playerId,
                Owner = playerId,
                X = x,
                Y = y,
                PlacedAt = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// Combines current placements with the new placement
        /// </summary>
        private static List<CardPlacement> BuildPlacementsList(
            List<CardPlacement> currentPlacements,
            CardPlacement newPlacement
        )
        {
            var allPlacements = currentPlacements.ToList();
            allPlacements.Add(newPlacement);
            return allPlacements;
        }

        /// <summary>Number of tied collisions required to trigger the SAME rule.</summary>
        private const int SameMinimumTies = 2;

        /// <summary>
        /// Resolves all captures for the played card in phases: basic battles first, then the special
        /// rules enabled on the match. Ownership is flipped as cards are captured (see
        /// <see cref="CaptureResolution.TryAdd"/>) so later phases see the updated board and future
        /// rules such as COMBO can chain from the cards a rule captured.
        /// </summary>
        private CaptureResolution ProcessCaptures(
            Match match,
            Card playedCard,
            int x,
            int y,
            string playerId,
            List<CardPlacement> allPlacements
        )
        {
            var resolution = new CaptureResolution();
            var collisions = GetCollisions(playedCard, x, y, playerId, allPlacements);

            // Phase 1 - Basic battle: a higher attack value captures the opponent's card.
            foreach (var collision in collisions)
            {
                if (collision.AttackValue > collision.DefenseValue)
                {
                    resolution.TryAdd(collision.Placement, playerId, isRuleCapture: false);
                }
            }

            // Phase 2 - Special rules (SAME today; Plus / SameWall / COMBO plug in here later).
            EvaluateRuleCaptures(match.Rules, collisions, playerId, resolution);

            return resolution;
        }

        /// <summary>
        /// One neighbor touched by the played card: the neighbor placement plus the attacking value of
        /// the played card and the defending value of the neighbor on the touching sides.
        /// </summary>
        private sealed record Collision(CardPlacement Placement, int AttackValue, int DefenseValue);

        /// <summary>
        /// Collects one collision per in-bounds position occupied by an opponent card. This is the
        /// "neighbor" notion of the game: only cards owned by the opponent can be captured.
        /// </summary>
        private static List<Collision> GetCollisions(
            Card playedCard,
            int x,
            int y,
            string playerId,
            List<CardPlacement> allPlacements
        )
        {
            var collisions = new List<Collision>();

            foreach (var direction in GetBattleDirections(playedCard))
            {
                int neighborX = x + direction.Dx;
                int neighborY = y + direction.Dy;

                // Skip if out of bounds
                if (!IsPositionInBounds(neighborX, neighborY))
                {
                    continue;
                }

                // Only cards owned by the opponent can be captured
                var neighborPlacement = allPlacements.FirstOrDefault(p =>
                    p.X == neighborX && p.Y == neighborY && p.Owner != playerId
                );

                if (neighborPlacement?.Card is null)
                {
                    continue;
                }

                collisions.Add(
                    new Collision(
                        neighborPlacement,
                        direction.CardValue,
                        GetCardValueBySide(neighborPlacement.Card, direction.OpponentSide)
                    )
                );
            }

            return collisions;
        }

        /// <summary>
        /// Calculates scores for both players and returns as tuple
        /// </summary>
        private static (int player1Score, int player2Score) CalculatePlayerScores(
            List<CardPlacement> placements,
            string player1Id,
            string player2Id
        )
        {
            CalculateScores(
                placements,
                player1Id,
                player2Id,
                out int player1Score,
                out int player2Score
            );
            return (player1Score, player2Score);
        }

        /// <summary>
        /// Builds the final PlayCardResult with all calculated data
        /// </summary>
        private static PlayCardResult BuildPlayCardResult(
            CaptureResolution captureResolution,
            int player1Score,
            int player2Score,
            int totalPlacements,
            string player1Id,
            string player2Id
        )
        {
            bool isGameComplete = totalPlacements >= 9;
            string? winnerId = null;

            if (isGameComplete)
            {
                winnerId = DetermineWinnerByScore(player1Score, player2Score, player1Id, player2Id);
            }

            return new PlayCardResult
            {
                IsValid = true,
                CapturedCards = captureResolution.CapturedCards.ToList(),
                Player1Score = player1Score,
                Player2Score = player2Score,
                IsGameComplete = isGameComplete,
                WinnerId = winnerId,
                TriggeredRules = captureResolution.TriggeredRules,
            };
        }

        /// <summary>
        /// Determines the winner based on scores
        /// </summary>
        private static string? DetermineWinnerByScore(
            int player1Score,
            int player2Score,
            string player1Id,
            string player2Id
        )
        {
            if (player1Score > player2Score)
            {
                return player1Id;
            }
            if (player2Score > player1Score)
            {
                return player2Id;
            }
            return null; // Draw
        }

        private static bool IsValidMove(List<CardPlacement> placements, int x, int y)
        {
            return !placements.Any(p => p.X == x && p.Y == y);
        }

        /// <summary>
        /// Evaluates every special rule enabled on the match, adding its captures to the resolution.
        /// This is the extension point for future rules (Plus, SameWall, COMBO).
        /// </summary>
        private static void EvaluateRuleCaptures(
            MatchRule rules,
            List<Collision> collisions,
            string playerId,
            CaptureResolution resolution
        )
        {
            if (rules.HasFlag(MatchRule.Same))
            {
                EvaluateSameRule(collisions, playerId, resolution);
            }

            // FUTURE: Plus (two collisions with the same attack + defense sum), SameWall (board
            // edges count as A) and COMBO (chain the cards in resolution.RuleCapturedCards) are
            // evaluated here, each adding its captures through resolution.TryAdd(..., true).
        }

        /// <summary>
        /// SAME: when two or more collisions tie (attack == defense), the tied neighbor cards are
        /// captured by the player who played the card. A single tie captures nothing.
        /// </summary>
        private static void EvaluateSameRule(
            List<Collision> collisions,
            string playerId,
            CaptureResolution resolution
        )
        {
            var tiedCollisions = collisions
                .Where(collision => collision.AttackValue == collision.DefenseValue)
                .ToList();

            if (tiedCollisions.Count < SameMinimumTies)
            {
                return;
            }

            foreach (var collision in tiedCollisions)
            {
                resolution.TryAdd(collision.Placement, playerId, isRuleCapture: true);
            }

            resolution.MarkRuleTriggered(MatchRule.Same);
        }

        /// <summary>
        /// A battle direction: the board offset, the played card's value on that side and the
        /// neighbor side it is compared against.
        /// </summary>
        private readonly record struct BattleDirection(
            int Dx,
            int Dy,
            int CardValue,
            string OpponentSide
        );

        /// <summary>
        /// Defines the four battle directions (top, right, bottom, left) with card values
        /// </summary>
        private static BattleDirection[] GetBattleDirections(Card playedCard)
        {
            return
            [
                new BattleDirection(0, -1, playedCard.TopValue, "BottomValue"), // Top
                new BattleDirection(1, 0, playedCard.RightValue, "LeftValue"), // Right
                new BattleDirection(0, 1, playedCard.BottomValue, "TopValue"), // Bottom
                new BattleDirection(-1, 0, playedCard.LeftValue, "RightValue"), // Left
            ];
        }

        /// <summary>
        /// Checks if a position is within the 3x3 board bounds
        /// </summary>
        private static bool IsPositionInBounds(int x, int y)
        {
            return x >= 0 && x <= 2 && y >= 0 && y <= 2;
        }

        private static int GetCardValueBySide(Card card, string side)
        {
            return side switch
            {
                "TopValue" => card.TopValue,
                "RightValue" => card.RightValue,
                "BottomValue" => card.BottomValue,
                "LeftValue" => card.LeftValue,
                _ => 0,
            };
        }

        private static void CalculateScores(
            List<CardPlacement> placements,
            string player1Id,
            string player2Id,
            out int player1Score,
            out int player2Score
        )
        {
            // In Triple Triad:
            // - Both players start with 5 points (their 5 cards in hand)
            // - When you capture an opponent's card: your score +1, opponent's score -1
            // - Total scores always = 10
            // Score = 5 (starting) + cards you control on board - cards you've placed

            int player1CardsOnBoard = placements.Count(p => p.Owner == player1Id);
            int player1CardsPlayed = placements.Count(p => p.PlayerId == player1Id);
            player1Score = 5 + player1CardsOnBoard - player1CardsPlayed;

            int player2CardsOnBoard = placements.Count(p => p.Owner == player2Id);
            int player2CardsPlayed = placements.Count(p => p.PlayerId == player2Id);
            player2Score = 5 + player2CardsOnBoard - player2CardsPlayed;
        }

        public List<Card> GetRandomHand(List<Card> availableCards, int handSize = 5)
        {
            var random = new Random();
            return availableCards.OrderBy(x => random.Next()).Take(handSize).ToList();
        }

        public string GetNextPlayer(string currentPlayer, string player1Id, string player2Id)
        {
            return currentPlayer == player1Id ? player2Id : player1Id;
        }

        public bool IsGameComplete(List<CardPlacement> placements)
        {
            return placements.Count >= 9;
        }

        public string? DetermineWinner(
            int player1Score,
            int player2Score,
            string player1Id,
            string player2Id
        )
        {
            if (player1Score > player2Score)
            {
                return player1Id;
            }
            if (player2Score > player1Score)
            {
                return player2Id;
            }
            return null; // Draw
        }
    }
}
