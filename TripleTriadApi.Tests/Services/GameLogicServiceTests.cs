using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Unit tests for the capture pipeline in <see cref="GameLogicService"/>, focused on the SAME
    /// rule. The service is pure logic, so the tests build plain models without a database.
    ///
    /// Board coordinates: x = column (0-2), y = row (0-2). The played card sits in the center
    /// (1, 1) unless a test says otherwise, so its neighbors are top (1, 0), right (2, 1),
    /// bottom (1, 2) and left (0, 1).
    /// </summary>
    public class GameLogicServiceTests
    {
        private const string Player1 = "player1";
        private const string Player2 = "player2";

        private static readonly GameLogicService GameLogic = new();

        [Fact]
        public void PlayCard_StrictWins_CapturesEveryWeakerNeighbor_WithoutRules()
        {
            var result = PlayFourNeighborBoard();

            Assert.True(result.IsValid);
            Assert.Equal(4, result.CapturedCards.Count);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_StrictWins_StillCapture_WithSameEnabled()
        {
            var result = PlayFourNeighborBoard(MatchRule.Same);

            Assert.Equal(4, result.CapturedCards.Count);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SingleTie_CapturesNothing_WithoutRules()
        {
            var (result, tiePlacement, battleCaptures) = PlayLoneTieBoard();

            Assert.Equal(3, result.CapturedCards.Count);
            Assert.DoesNotContain(tiePlacement, result.CapturedCards);
            Assert.Equal(Player2, tiePlacement.Owner);
            Assert.All(battleCaptures, capture => Assert.Contains(capture, result.CapturedCards));
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SingleTie_DoesNotTriggerSame()
        {
            var (result, tiePlacement, _) = PlayLoneTieBoard(MatchRule.Same);

            Assert.Equal(3, result.CapturedCards.Count);
            Assert.DoesNotContain(tiePlacement, result.CapturedCards);
            Assert.Equal(Player2, tiePlacement.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_TwoTies_CaptureNothing_WithoutRules()
        {
            var (result, ties, _) = PlayTwoTieBoard();

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.All(ties, tie => Assert.Equal(Player2, tie.Owner));
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_TwoTies_TriggersSame_AndFlipsBothTiedCards()
        {
            var (result, ties, battleCaptures) = PlayTwoTieBoard(MatchRule.Same);

            Assert.Equal(4, result.CapturedCards.Count);
            Assert.All(ties, tie => Assert.Equal(Player1, tie.Owner));
            Assert.All(battleCaptures, capture => Assert.Equal(Player1, capture.Owner));
            Assert.Equal(new[] { MatchRule.Same }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_ThreeTies_FlipAllThree()
        {
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, top: 5, right: 5, bottom: 5, left: 9);
            var placements = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(2, 1, 1, 5, 5), Player2, 1, 0), // tie: played.Top vs bottom
                CreatePlacement(CreateCard(3, 5, 5, 1, 5), Player2, 2, 1), // tie: played.Right vs left
                CreatePlacement(CreateCard(4, 5, 5, 5, 5), Player2, 1, 2), // tie: played.Bottom vs top
                CreatePlacement(CreateCard(5, 1, 1, 1, 1), Player2, 0, 1), // battle win: 9 > 1
            };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);

            Assert.Equal(4, result.CapturedCards.Count);
            Assert.All(result.CapturedCards, capture => Assert.Equal(Player1, capture.Owner));
            Assert.Equal(new[] { MatchRule.Same }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_TiePlusStrictWin_OnlyTheWinIsCaptured_WithoutASecondTie()
        {
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, top: 5, right: 9, bottom: 1, left: 1);
            var tiePlacement = CreatePlacement(CreateCard(2, 1, 1, 5, 1), Player2, 1, 0);
            var winPlacement = CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 2, 1);
            var placements = new List<CardPlacement> { tiePlacement, winPlacement };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);

            Assert.Single(result.CapturedCards);
            Assert.Same(winPlacement, result.CapturedCards[0]);
            Assert.Equal(Player2, tiePlacement.Owner);
            Assert.Equal(Player1, winPlacement.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_InCorner_OnlyChecksInBoundsNeighbors()
        {
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, top: 1, right: 9, bottom: 9, left: 1);
            var placements = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(2, 1, 1, 1, 1), Player2, 1, 0),
                CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 0, 1),
            };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 0, 0);

            Assert.True(result.IsValid);
            Assert.Equal(2, result.CapturedCards.Count);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SingleTieInCorner_DoesNotTriggerSame()
        {
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, top: 1, right: 5, bottom: 5, left: 1);
            var tiedNeighbor = CreatePlacement(CreateCard(2, 1, 1, 1, 5), Player2, 1, 0);
            var placements = new List<CardPlacement> { tiedNeighbor };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 0, 0);

            Assert.Empty(result.CapturedCards);
            Assert.Equal(Player2, tiedNeighbor.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_OwnCardTie_CountsTowardSame_AndOnlyTheOpponentCardFlips()
        {
            // FF8 SAME: a tie with one of the player's own cards counts toward the "two or more", but an
            // own card is already the player's, so only the opponent's tied card is captured.
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, top: 5, right: 5, bottom: 1, left: 1);
            var ownCard = CreatePlacement(CreateCard(2, 1, 1, 5, 1), Player1, 1, 0); // tie: played.Top vs bottom
            var opponentCard = CreatePlacement(CreateCard(3, 1, 1, 1, 5), Player2, 2, 1); // tie: played.Right vs left
            var placements = new List<CardPlacement> { ownCard, opponentCard };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);

            Assert.Single(result.CapturedCards);
            Assert.Same(opponentCard, result.CapturedCards[0]);
            Assert.Equal(Player1, opponentCard.Owner);
            Assert.DoesNotContain(ownCard, result.CapturedCards);
            Assert.Equal(Player1, ownCard.Owner);
            Assert.Equal(new[] { MatchRule.Same }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_OwnCardTiesOnly_DoNotTriggerSame()
        {
            // Two own-card ties meet the two-or-more requirement, but there is no tied opponent card to
            // capture (FF8: "one or both of them have to be the opposite color"), so nothing fires.
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, top: 5, right: 5, bottom: 1, left: 1);
            var firstOwnCard = CreatePlacement(CreateCard(2, 1, 1, 5, 1), Player1, 1, 0); // tie
            var secondOwnCard = CreatePlacement(CreateCard(3, 1, 1, 1, 5), Player1, 2, 1); // tie
            var placements = new List<CardPlacement> { firstOwnCard, secondOwnCard };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);

            Assert.Empty(result.CapturedCards);
            Assert.Equal(Player1, firstOwnCard.Owner);
            Assert.Equal(Player1, secondOwnCard.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_OwnCard_IsNeverCaptured_EvenOnAStrictWin()
        {
            // Own cards are collisions now, but they are never reported as captured.
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, 9, 9, 1, 1);
            var ownCard = CreatePlacement(CreateCard(2, 1, 1, 1, 1), Player1, 1, 0); // 9 > 1, but own
            var opponentCard = CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 2, 1); // 9 > 1, capturable
            var placements = new List<CardPlacement> { ownCard, opponentCard };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);

            Assert.Single(result.CapturedCards);
            Assert.Same(opponentCard, result.CapturedCards[0]);
            Assert.Equal(Player1, ownCard.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameFlip_MovesScoresByTheFlippedCards()
        {
            var (result, _, _) = PlayTwoTieBoard(MatchRule.Same);

            // P1: 5 (start) + 5 owned on board (played + 4 captured) - 1 played = 9
            // P2: 5 (start) + 0 owned on board - 4 played = 1
            Assert.Equal(9, result.Player1Score);
            Assert.Equal(1, result.Player2Score);
        }

        [Fact]
        public void PlayCard_WithoutRules_ScoresOnlyReflectBasicCaptures()
        {
            var (result, _, _) = PlayTwoTieBoard();

            // P1: 5 (start) + 3 owned on board (played + 2 captured) - 1 played = 7
            // P2: 5 (start) + 2 owned on board - 4 played = 3
            Assert.Equal(7, result.Player1Score);
            Assert.Equal(3, result.Player2Score);
        }

        [Fact]
        public void PlayCard_CompletingMove_SetsScoresWinnerAndCompletion()
        {
            var match = CreateMatch(MatchRule.Same);
            var playedCard = CreateCard(1, 9, 9, 9, 9);
            var placements = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(2, 1, 1, 1, 1), Player1, 0, 0),
                CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player1, 2, 0),
                CreatePlacement(CreateCard(4, 1, 1, 1, 1), Player1, 0, 2),
                CreatePlacement(CreateCard(5, 1, 1, 1, 1), Player1, 2, 2),
                CreatePlacement(CreateCard(6, 1, 1, 1, 1), Player2, 1, 0),
                CreatePlacement(CreateCard(7, 1, 1, 1, 1), Player2, 2, 1),
                CreatePlacement(CreateCard(8, 1, 1, 1, 1), Player2, 1, 2),
                CreatePlacement(CreateCard(9, 1, 1, 1, 1), Player2, 0, 1),
            };

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);

            Assert.Equal(4, result.CapturedCards.Count);
            Assert.Empty(result.TriggeredRules);
            Assert.True(result.IsGameComplete);
            Assert.Equal(Player1, result.WinnerId);
            Assert.Equal(9, result.Player1Score);
            Assert.Equal(1, result.Player2Score);
        }

        private static GameLogicService.PlayCardResult PlayFourNeighborBoard(
            params MatchRule[] rules
        )
        {
            var match = CreateMatch(rules);
            var playedCard = CreateCard(1, 9, 9, 9, 9);
            var placements = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(2, 1, 1, 1, 1), Player2, 1, 0),
                CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 2, 1),
                CreatePlacement(CreateCard(4, 1, 1, 1, 1), Player2, 1, 2),
                CreatePlacement(CreateCard(5, 1, 1, 1, 1), Player2, 0, 1),
            };

            return GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);
        }

        private static (
            GameLogicService.PlayCardResult Result,
            CardPlacement Tie,
            List<CardPlacement> BattleCaptures
        ) PlayLoneTieBoard(params MatchRule[] rules)
        {
            var match = CreateMatch(rules);
            var playedCard = CreateCard(1, top: 5, right: 9, bottom: 9, left: 9);
            var tie = CreatePlacement(CreateCard(2, 1, 1, 5, 1), Player2, 1, 0);
            var battleCaptures = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 2, 1),
                CreatePlacement(CreateCard(4, 1, 1, 1, 1), Player2, 1, 2),
                CreatePlacement(CreateCard(5, 1, 1, 1, 1), Player2, 0, 1),
            };
            var placements = new List<CardPlacement> { tie };
            placements.AddRange(battleCaptures);

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);
            return (result, tie, battleCaptures);
        }

        private static (
            GameLogicService.PlayCardResult Result,
            List<CardPlacement> Ties,
            List<CardPlacement> BattleCaptures
        ) PlayTwoTieBoard(params MatchRule[] rules)
        {
            var match = CreateMatch(rules);
            var playedCard = CreateCard(1, top: 5, right: 5, bottom: 9, left: 9);
            var ties = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(2, 1, 1, 5, 1), Player2, 1, 0),
                CreatePlacement(CreateCard(3, 1, 1, 1, 5), Player2, 2, 1),
            };
            var battleCaptures = new List<CardPlacement>
            {
                CreatePlacement(CreateCard(4, 1, 1, 1, 1), Player2, 1, 2),
                CreatePlacement(CreateCard(5, 1, 1, 1, 1), Player2, 0, 1),
            };
            var placements = new List<CardPlacement>();
            placements.AddRange(ties);
            placements.AddRange(battleCaptures);

            var result = GameLogic.PlayCard(match, placements, playedCard, Player1, 1, 1);
            return (result, ties, battleCaptures);
        }

        private static Match CreateMatch(params MatchRule[] rules)
        {
            return new Match
            {
                Id = 1,
                Player1Id = Player1,
                Player2Id = Player2,
                CurrentPlayerTurn = Player1,
                Status = "active",
                Player1Score = 5,
                Player2Score = 5,
                Rules = rules.ToList(),
            };
        }

        private static Card CreateCard(int id, int top, int right, int bottom, int left)
        {
            return new Card
            {
                Id = id,
                Name = $"Card {id}",
                Image = $"card-{id}.jpg",
                TopValue = top,
                RightValue = right,
                BottomValue = bottom,
                LeftValue = left,
            };
        }

        private static CardPlacement CreatePlacement(Card card, string owner, int x, int y)
        {
            return new CardPlacement
            {
                Id = card.Id,
                MatchId = 1,
                CardId = card.Id,
                Card = card,
                PlayerId = owner,
                Owner = owner,
                X = x,
                Y = y,
                PlacedAt = DateTime.UtcNow,
            };
        }
    }
}
