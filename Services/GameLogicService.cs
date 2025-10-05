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
            var capturedCards = ProcessCaptures(card, x, y, playerId, allPlacements);

            // Step 5: Calculate final scores
            var (player1Score, player2Score) = CalculatePlayerScores(
                allPlacements,
                match.Player1Id,
                match.Player2Id
            );

            // Step 6: Build and return result
            return BuildPlayCardResult(
                capturedCards,
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

        /// <summary>
        /// Processes card captures and updates ownership
        /// </summary>
        private List<CardPlacement> ProcessCaptures(
            Card playedCard,
            int x,
            int y,
            string playerId,
            List<CardPlacement> allPlacements
        )
        {
            var capturedCards = CalculateCaptures(playedCard, x, y, playerId, allPlacements);

            // Update ownership of captured cards
            foreach (var capture in capturedCards)
            {
                capture.Owner = playerId;
            }

            return capturedCards;
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
            List<CardPlacement> capturedCards,
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
                CapturedCards = capturedCards,
                Player1Score = player1Score,
                Player2Score = player2Score,
                IsGameComplete = isGameComplete,
                WinnerId = winnerId,
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
        /// Calculates which opponent cards are captured by the played card
        /// </summary>
        private List<CardPlacement> CalculateCaptures(
            Card playedCard,
            int x,
            int y,
            string playerId,
            List<CardPlacement> allPlacements
        )
        {
            var captures = new List<CardPlacement>();
            var directions = GetBattleDirections(playedCard);

            foreach (var direction in directions)
            {
                int neighborX = x + direction.dx;
                int neighborY = y + direction.dy;

                // Skip if out of bounds
                if (!IsPositionInBounds(neighborX, neighborY))
                {
                    continue;
                }

                // Try to capture the neighbor card
                var capturedCard = TryCaptureNeighbor(
                    neighborX,
                    neighborY,
                    direction.cardValue,
                    direction.opponentSide,
                    playerId,
                    allPlacements
                );

                if (capturedCard != null)
                {
                    captures.Add(capturedCard);
                }
            }

            return captures;
        }

        /// <summary>
        /// Defines the four battle directions (top, right, bottom, left) with card values
        /// </summary>
        private static dynamic[] GetBattleDirections(Card playedCard)
        {
            return new[]
            {
                new
                {
                    dx = 0,
                    dy = -1,
                    cardValue = playedCard.TopValue,
                    opponentSide = "BottomValue",
                }, // Top
                new
                {
                    dx = 1,
                    dy = 0,
                    cardValue = playedCard.RightValue,
                    opponentSide = "LeftValue",
                }, // Right
                new
                {
                    dx = 0,
                    dy = 1,
                    cardValue = playedCard.BottomValue,
                    opponentSide = "TopValue",
                }, // Bottom
                new
                {
                    dx = -1,
                    dy = 0,
                    cardValue = playedCard.LeftValue,
                    opponentSide = "RightValue",
                }, // Left
            };
        }

        /// <summary>
        /// Calculates the neighbor position based on direction deltas
        /// </summary>
        private static (int x, int y) CalculateNeighborPosition(int x, int y, int dx, int dy)
        {
            return (x + dx, y + dy);
        }

        /// <summary>
        /// Checks if a position is within the 3x3 board bounds
        /// </summary>
        private static bool IsPositionInBounds(int x, int y)
        {
            return x >= 0 && x <= 2 && y >= 0 && y <= 2;
        }

        /// <summary>
        /// Attempts to capture a neighbor card if the battle value wins
        /// </summary>
        private static CardPlacement? TryCaptureNeighbor(
            int neighborX,
            int neighborY,
            int attackValue,
            string opponentSide,
            string playerId,
            List<CardPlacement> allPlacements
        )
        {
            // Find the card at the neighbor position (must be opponent's)
            CardPlacement? neighborPlacement = allPlacements.FirstOrDefault(p =>
                p.X == neighborX && p.Y == neighborY && p.Owner != playerId
            );

            if (neighborPlacement?.Card == null)
            {
                return null;
            }

            int defenseValue = GetCardValueBySide(neighborPlacement.Card, opponentSide);

            // Battle: attack > defense = capture
            if (attackValue > defenseValue)
            {
                return neighborPlacement;
            }

            return null;
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
