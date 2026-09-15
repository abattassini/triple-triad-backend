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
            /// Special rules that fired while resolving this move (empty when none did).
            /// </summary>
            public List<MatchRule> TriggeredRules { get; set; } = [];

            /// <summary>
            /// Why each captured card flipped: the rule that claimed it, or <c>null</c> for a basic
            /// battle capture. A card both SAME and PLUS would flip is reported once, as SAME's.
            /// </summary>
            public List<CaptureResolution.CaptureCause> CaptureCauses { get; set; } = [];
        }

        /// <summary>
        /// One play: the actor (the player who played the card), the card, the square it goes to and the
        /// board as it stood before the card landed. A move is the single input the capture pipeline
        /// resolves — collisions, basic battle and every enabled special rule hang off it.
        /// </summary>
        private sealed record Move(
            string Actor,
            Card Card,
            int X,
            int Y,
            List<CardPlacement> Board
        );

        public PlayCardResult PlayCard(
            Match match,
            List<CardPlacement> currentPlacements,
            Card card,
            string actor,
            int x,
            int y
        )
        {
            // The whole action is one Move: actor + card + square, resolved against the board it lands on.
            var move = new Move(actor, card, x, y, currentPlacements);

            // Step 1: Validate the move
            var validationResult = ValidateMove(match, move);
            if (!validationResult.IsValid)
            {
                return validationResult;
            }

            // Step 2: Place the card, giving the board the move is resolved and scored against
            var board = BuildPlacementsList(move.Board, CreateCardPlacement(match.Id, move));

            // Step 3: Resolve the move (collisions, basic battle, enabled rules) and update ownership
            var captureResolution = ResolveMove(match, move, board);

            // Step 4: Calculate final scores
            var (player1Score, player2Score) = CalculatePlayerScores(
                board,
                match.Player1Id,
                match.Player2Id
            );

            // Step 5: Build and return result
            return BuildPlayCardResult(
                captureResolution,
                player1Score,
                player2Score,
                board.Count,
                match.Player1Id,
                match.Player2Id
            );
        }

        /// <summary>
        /// Validates if the move is legal (position available and correct turn)
        /// </summary>
        private static PlayCardResult ValidateMove(Match match, Move move)
        {
            if (!IsValidMove(move.Board, move.X, move.Y))
            {
                return new PlayCardResult { ErrorMessage = "Position is already occupied" };
            }

            if (match.CurrentPlayerTurn != move.Actor)
            {
                return new PlayCardResult { ErrorMessage = "Not your turn" };
            }

            return new PlayCardResult { IsValid = true };
        }

        /// <summary>
        /// Creates the placement the move adds to the board; the actor owns and has played it
        /// </summary>
        private static CardPlacement CreateCardPlacement(int matchId, Move move)
        {
            return new CardPlacement
            {
                MatchId = matchId,
                CardId = move.Card.Id,
                Card = move.Card,
                PlayerId = move.Actor,
                Owner = move.Actor,
                X = move.X,
                Y = move.Y,
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
        /// Number of collisions that must share the same touching-value sum to trigger the PLUS rule.
        /// Deliberately separate from <see cref="SameMinimumTies"/> so tuning one rule cannot affect the
        /// other (rules must work independently of each other).
        /// </summary>
        private const int PlusMinimumMatchingSums = 2;

        /// <summary>
        /// Resolves every capture the move causes, in phases: basic battles first, then the special rules
        /// enabled on the match, SAME before PLUS. Ownership is flipped as cards are captured (see
        /// <see cref="CaptureResolution.TryAdd"/>) so later phases see the updated board, future rules such
        /// as COMBO can chain from the cards a rule captured, and a card both rules would flip keeps the
        /// cause of the first phase that claimed it.
        ///
        /// Every phase sees every collision, own cards included; each phase decides what it may flip
        /// (ownership decides capturability, not whether two cards collide).
        /// </summary>
        private static CaptureResolution ResolveMove(
            Match match,
            Move move,
            List<CardPlacement> board
        )
        {
            var resolution = new CaptureResolution();
            var collisions = GetCollisions(move, board);

            // Phase 1 - Basic battle: only an opponent's card can be captured, and only on a strict win.
            foreach (var collision in collisions)
            {
                if (collision.IsOpponentCardOf(move.Actor) && collision.IsStrictWin)
                {
                    resolution.TryAdd(collision.Placement, move.Actor, cause: null);
                }
            }

            // Phase 2 - Special rules, each independent: SAME, then PLUS (SameWall / COMBO plug in here).
            EvaluateRuleCaptures(match.Rules, move, collisions, resolution);

            return resolution;
        }

        /// <summary>
        /// One neighbour touched by the played card: the neighbour placement, the attacking value of the
        /// card the move plays and the defending value of the neighbour on the touching sides. A collision
        /// exists for every occupied neighbour, whoever owns it — ownership decides what can be captured,
        /// not whether the two cards collide.
        /// </summary>
        private sealed record Collision(CardPlacement Placement, int AttackValue, int DefenseValue)
        {
            /// <summary>Only the opponent's cards can ever be captured.</summary>
            public bool IsOpponentCardOf(string playerId) => Placement.Owner != playerId;

            /// <summary>Equal touching values — what the SAME rule looks for.</summary>
            public bool IsTie => AttackValue == DefenseValue;

            /// <summary>Higher attack than defense — a basic battle capture.</summary>
            public bool IsStrictWin => AttackValue > DefenseValue;
        }

        /// <summary>
        /// Collects one collision per in-bounds neighbour of the move's square on <paramref name="board"/>
        /// (the board the move is resolved against), whoever owns that neighbour. Own cards are included
        /// because the SAME rule counts a tie with one of them toward its two-or-more requirement; callers
        /// that need capture candidates filter with <see cref="Collision.IsOpponentCardOf"/>.
        /// </summary>
        private static List<Collision> GetCollisions(Move move, List<CardPlacement> board)
        {
            var collisions = new List<Collision>();

            foreach (var direction in GetBattleDirections(move.Card))
            {
                int neighborX = move.X + direction.Dx;
                int neighborY = move.Y + direction.Dy;

                // Skip if out of bounds
                if (!IsPositionInBounds(neighborX, neighborY))
                {
                    continue;
                }

                var neighborPlacement = board.FirstOrDefault(p =>
                    p.X == neighborX && p.Y == neighborY
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
                TriggeredRules = captureResolution.TriggeredRules.ToList(),
                CaptureCauses = captureResolution.CaptureCauses.ToList(),
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
        /// Evaluates every special rule enabled on the match, adding its captures to the resolution. Each
        /// rule runs off its own flag and never reads another rule's results, so a match with only SAME or
        /// only PLUS behaves exactly as that rule describes. Rules receive every collision (own cards
        /// included) and decide for themselves what to flip.
        ///
        /// The order matters for attribution only: SAME runs before PLUS, so when both would flip the same
        /// card the first claim wins and SAME is its recorded cause (see
        /// <see cref="CaptureResolution.TryAdd"/>). This is the extension point for future rules
        /// (SameWall, COMBO).
        /// </summary>
        private static void EvaluateRuleCaptures(
            List<MatchRule> rules,
            Move move,
            List<Collision> collisions,
            CaptureResolution resolution
        )
        {
            // A rule is enabled when it appears in the match's list of rules.
            if (rules.Contains(MatchRule.Same))
            {
                EvaluateSameRule(collisions, move, resolution);
            }

            if (rules.Contains(MatchRule.Plus))
            {
                EvaluatePlusRule(collisions, move, resolution);
            }

            // FUTURE: SameWall (board edges count as A) and COMBO (chain the cards in
            // resolution.RuleCapturedCards) are evaluated here, each adding its captures through
            // resolution.TryAdd(..., cause).
        }

        /// <summary>
        /// SAME (FF8): when the played card touches two or more cards and the touching values are equal
        /// in at least two of those collisions, the tied cards flip. Ties with the player's own cards
        /// count toward the two-or-more but are already the player's, so only the tied opponent cards
        /// are captured — and at least one tied neighbour has to be the opponent's (FF8's "one or both
        /// of them have to be the opposite color"). A single tie captures nothing.
        /// </summary>
        private static void EvaluateSameRule(
            List<Collision> collisions,
            Move move,
            CaptureResolution resolution
        )
        {
            var tiedCollisions = collisions.Where(collision => collision.IsTie).ToList();

            if (tiedCollisions.Count < SameMinimumTies)
            {
                return;
            }

            var flips = 0;
            foreach (var collision in tiedCollisions)
            {
                // Own-card ties meet the threshold but cannot flip anything.
                if (
                    collision.IsOpponentCardOf(move.Actor)
                    && resolution.TryAdd(collision.Placement, move.Actor, MatchRule.Same)
                )
                {
                    flips++;
                }
            }

            // Only report the rule when it actually flipped a card.
            if (flips > 0)
            {
                resolution.MarkRuleTriggered(MatchRule.Same);
            }
        }

        /// <summary>
        /// PLUS (FF8): when two or more collisions share the same touching-value sum (attack + defense),
        /// every card involved in those matching sums flips. The rank comparison is irrelevant, so this can
        /// flip a neighbour that beats the played card on that side. Cards of the player may contribute a
        /// sum but are never flipped (they are already that player's), so a matching sum needs at least one
        /// opponent card to do anything — and the rule is reported only when it flipped something.
        /// </summary>
        private static void EvaluatePlusRule(
            List<Collision> collisions,
            Move move,
            CaptureResolution resolution
        )
        {
            // A plus is a group of two or more collisions whose touching values share the same sum.
            var matchingSums = collisions
                .GroupBy(collision => collision.AttackValue + collision.DefenseValue)
                .Where(group => group.Count() >= PlusMinimumMatchingSums)
                .SelectMany(group => group);

            var flips = 0;
            foreach (var collision in matchingSums)
            {
                if (
                    collision.IsOpponentCardOf(move.Actor)
                    && resolution.TryAdd(collision.Placement, move.Actor, MatchRule.Plus)
                )
                {
                    flips++;
                }
            }

            // Only report the rule when it actually flipped a card.
            if (flips > 0)
            {
                resolution.MarkRuleTriggered(MatchRule.Plus);
            }
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
