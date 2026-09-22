using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Unit tests for the capture pipeline in <see cref="GameLogicService"/>, focused on the SAME, PLUS and
    /// wall (SAME WALL / PLUS WALL) rules. The service is pure logic, so the tests build plain models without
    /// a database.
    ///
    /// Board coordinates: x = column (0-2), y = row (0-2). The played card sits in the center
    /// (1, 1) unless a test says otherwise, so its neighbors are top (1, 0), right (2, 1),
    /// bottom (1, 2) and left (0, 1). A card played on an edge touches one wall and a card played in a
    /// corner two; the wall counts as an A (10) card for the wall rules only.
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
        public void PlayCard_Plus_TwoMatchingSums_FlipsBothNeighbours_WithoutSame()
        {
            // 5 + 6 and 4 + 7 both make 11. Neither collision ties or wins on rank, so basic battle and
            // SAME stay out of it and PLUS alone flips both neighbours — each of which beats the played
            // card on its touching side.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player2, 2, 1);

            var (result, neighbours) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Plus
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.All(neighbours, neighbour => Assert.Equal(Player1, neighbour.Owner));
            Assert.Equal(new[] { MatchRule.Plus }, result.TriggeredRules);
            Assert.All(
                neighbours,
                neighbour => Assert.Equal(MatchRule.Plus, CaptureCauseOf(result, neighbour)!.Rule)
            );
        }

        [Fact]
        public void PlayCard_Plus_ThreeMatchingSums_FlipAllThree()
        {
            // 5 + 6, 4 + 7 and 3 + 8 all make 11.
            var playedCard = CreateCard(1, 5, 4, 3, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player2, 2, 1);
            var bottomNeighbour = CreatePlacement(BottomNeighbourCard(4, top: 8), Player2, 1, 2);

            var (result, neighbours) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour, bottomNeighbour],
                MatchRule.Plus
            );

            Assert.Equal(3, result.CapturedCards.Count);
            Assert.All(neighbours, neighbour => Assert.Equal(Player1, neighbour.Owner));
            Assert.Equal(new[] { MatchRule.Plus }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Plus_UnequalSums_CaptureNothing()
        {
            // 5 + 6 = 11 but 4 + 5 = 9, so no sum matches and there is no rank tie either.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 5), Player2, 2, 1);

            var (result, _) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Plus
            );

            Assert.Empty(result.CapturedCards);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Plus_SkipsCardsAlreadyCapturedByBasicBattle()
        {
            // Both collisions are strict wins (5 > 2, 4 > 3) and both sums are 7, so PLUS's condition holds
            // but basic battle already claimed both cards: the cause stays basic battle and PLUS is not
            // reported because it flipped nothing.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 2), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 3), Player2, 2, 1);

            var (result, neighbours) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Plus
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.All(neighbours, neighbour => Assert.Equal(Player1, neighbour.Owner));
            Assert.All(
                neighbours,
                neighbour =>
                {
                    var cause = CaptureCauseOf(result, neighbour);
                    Assert.NotNull(cause);
                    Assert.Null(cause.Rule);
                }
            );
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Plus_OwnCardSum_CountsTowardTheMatch_ButIsNeverFlipped()
        {
            // The player's own card contributes 5 + 6 = 11, matching the opponent's 4 + 7 = 11, so PLUS
            // fires — but only the opponent's card can flip.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var ownNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player1, 1, 0);
            var opponentNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player2, 2, 1);

            var (result, _) = PlayCenter(
                playedCard,
                [ownNeighbour, opponentNeighbour],
                MatchRule.Plus
            );

            Assert.Single(result.CapturedCards);
            Assert.Same(opponentNeighbour, result.CapturedCards[0]);
            Assert.Equal(Player1, opponentNeighbour.Owner);
            Assert.DoesNotContain(ownNeighbour, result.CapturedCards);
            Assert.Equal(Player1, ownNeighbour.Owner);
            Assert.Equal(new[] { MatchRule.Plus }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Plus_OwnCardSumsOnly_DoNotTrigger()
        {
            // Two own-card collisions share the sum 11, but there is nothing of the opponent's to flip.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var firstOwnNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player1, 1, 0);
            var secondOwnNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player1, 2, 1);

            var (result, _) = PlayCenter(
                playedCard,
                [firstOwnNeighbour, secondOwnNeighbour],
                MatchRule.Plus
            );

            Assert.Empty(result.CapturedCards);
            Assert.Equal(Player1, firstOwnNeighbour.Owner);
            Assert.Equal(Player1, secondOwnNeighbour.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Same_OnAPlusShapedBoard_DoesNothing()
        {
            // Independence: with only SAME enabled, a board that satisfies PLUS (no ties anywhere) is
            // simply an ordinary move — SAME never reads PLUS's condition.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player2, 2, 1);

            var (result, _) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Same
            );

            Assert.Empty(result.CapturedCards);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Plus_WithSameEnabled_OnAPlusShapedBoard_StillFiresPlus()
        {
            // PLUS needs no SAME trigger: with both rules enabled and nothing tied on rank, only PLUS runs.
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player2, 2, 1);

            var (result, neighbours) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Same,
                MatchRule.Plus
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.All(
                neighbours,
                neighbour => Assert.Equal(MatchRule.Plus, CaptureCauseOf(result, neighbour)!.Rule)
            );
            Assert.Equal(new[] { MatchRule.Plus }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameAndPlus_SharedCards_ReportSame_AndPlusTakesTheRest()
        {
            // Both rank ties share the sum 10 (5 + 5), and the losing 3 + 7 collision shares it too. SAME
            // is evaluated first, so it is the cause of the two tied cards; PLUS still flips the card SAME
            // could not take, even though that neighbour beats the played card on its touching side.
            var playedCard = CreateCard(1, 5, 5, 3, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 5), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 5), Player2, 2, 1);
            var bottomNeighbour = CreatePlacement(BottomNeighbourCard(4, top: 7), Player2, 1, 2);

            var (result, _) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour, bottomNeighbour],
                MatchRule.Same,
                MatchRule.Plus
            );

            Assert.Equal(3, result.CapturedCards.Count);
            Assert.Equal(MatchRule.Same, CaptureCauseOf(result, topNeighbour)!.Rule);
            Assert.Equal(MatchRule.Same, CaptureCauseOf(result, rightNeighbour)!.Rule);
            Assert.Equal(MatchRule.Plus, CaptureCauseOf(result, bottomNeighbour)!.Rule);
            Assert.Equal(new[] { MatchRule.Same, MatchRule.Plus }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameAndPlus_OverlapFullyClaimedBySame_DoesNotReportPlus()
        {
            // Every card in PLUS's matching sum is also a SAME tie, so SAME claims both and PLUS is not
            // reported at all: a rule that flipped nothing must not appear in triggeredRules.
            var playedCard = CreateCard(1, 5, 5, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 5), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 5), Player2, 2, 1);

            var (result, _) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Same,
                MatchRule.Plus
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.Equal(MatchRule.Same, CaptureCauseOf(result, topNeighbour)!.Rule);
            Assert.Equal(MatchRule.Same, CaptureCauseOf(result, rightNeighbour)!.Rule);
            Assert.Equal(new[] { MatchRule.Same }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Plus_Flip_MovesScoresByTheFlippedCards()
        {
            var playedCard = CreateCard(1, 5, 4, 1, 1);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 1, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 7), Player2, 2, 1);

            var (result, _) = PlayCenter(
                playedCard,
                [topNeighbour, rightNeighbour],
                MatchRule.Plus
            );

            // P1: 5 (start) + 3 owned on board (played + 2 flipped) - 1 played = 7
            // P2: 5 (start) + 0 owned on board - 2 played = 3
            Assert.Equal(7, result.Player1Score);
            Assert.Equal(3, result.Player2Score);
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

        [Fact]
        public void PlayCard_SameWall_WallTieCountsTowardSame_WithoutSameEnabled()
        {
            // SAME WALL is its own rule: the wall counts as an A (10) card, so the played card's A side
            // touching the wall is a tie, and together with the tied neighbour that is the two ties SAME
            // needs. SAME is disabled here, so only the wall rule can be responsible for the flip.
            var playedCard = CreateCard(1, top: 1, right: 5, bottom: 1, left: 10);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(2, left: 5), Player2, 1, 1);

            var result = PlayAt(playedCard, [rightNeighbour], 0, 1, MatchRule.SameWall);

            Assert.Single(result.CapturedCards);
            Assert.Same(rightNeighbour, result.CapturedCards[0]);
            Assert.Equal(Player1, rightNeighbour.Owner);
            Assert.Equal(MatchRule.SameWall, CaptureCauseOf(result, rightNeighbour)!.Rule);
            Assert.Equal(new[] { MatchRule.SameWall }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Same_IgnoresTheWall_WhenSameWallIsDisabled()
        {
            // Same board as the test above, but with only SAME enabled: the wall is not a collision unless a
            // wall rule is on, so the single card tie captures nothing.
            var playedCard = CreateCard(1, top: 1, right: 5, bottom: 1, left: 10);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(2, left: 5), Player2, 1, 1);

            var result = PlayAt(playedCard, [rightNeighbour], 0, 1, MatchRule.Same);

            Assert.Empty(result.CapturedCards);
            Assert.Equal(Player2, rightNeighbour.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameWall_WallTiePlusOwnCardTie_FlipsNothing()
        {
            // The wall tie and a tie with the player's own card make up the two ties, but a wall has no owner
            // and the own card is already the player's: nothing can flip, so the rule is not reported.
            var playedCard = CreateCard(1, top: 1, right: 5, bottom: 1, left: 10);
            var ownCard = CreatePlacement(RightNeighbourCard(2, left: 5), Player1, 1, 1);

            var result = PlayAt(playedCard, [ownCard], 0, 1, MatchRule.SameWall);

            Assert.Empty(result.CapturedCards);
            Assert.Equal(Player1, ownCard.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameWall_WallTieAlone_DoesNothing()
        {
            // A single wall tie is one collision: the wall cannot be captured and there is no second tie.
            var playedCard = CreateCard(1, top: 1, right: 1, bottom: 1, left: 10);

            var result = PlayAt(playedCard, [], 0, 1, MatchRule.SameWall);

            Assert.Empty(result.CapturedCards);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_PlusWall_WallSumMatchesACardSum_FlipsABeatingNeighbour()
        {
            // The wall is an A card, so the played card's 3 against it sums to 13 — and the neighbour's 10
            // against the played card's 3 sums to 13 as well. One matching sum of two flips the neighbour
            // even though it beats the played card on that side.
            var playedCard = CreateCard(1, top: 1, right: 3, bottom: 1, left: 3);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(2, left: 10), Player2, 1, 1);

            var result = PlayAt(playedCard, [rightNeighbour], 0, 1, MatchRule.PlusWall);

            Assert.Single(result.CapturedCards);
            Assert.Same(rightNeighbour, result.CapturedCards[0]);
            Assert.Equal(Player1, rightNeighbour.Owner);
            Assert.Equal(MatchRule.PlusWall, CaptureCauseOf(result, rightNeighbour)!.Rule);
            Assert.Equal(new[] { MatchRule.PlusWall }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_PlusWall_WallSumAlone_DoesNothing()
        {
            // The wall sum (3 + 10 = 13) has no partner, so no sum matches.
            var playedCard = CreateCard(1, top: 1, right: 1, bottom: 1, left: 3);

            var result = PlayAt(playedCard, [], 0, 1, MatchRule.PlusWall);

            Assert.Empty(result.CapturedCards);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_PlusWall_TwoWallSumsOnly_FlipNothing()
        {
            // A corner card touches two walls, so both sums are 4 + 10 = 14 and they match — but neither wall
            // is a card, so nothing flips and the rule is not reported.
            var playedCard = CreateCard(1, top: 4, right: 1, bottom: 1, left: 4);

            var result = PlayAt(playedCard, [], 0, 0, MatchRule.PlusWall);

            Assert.Empty(result.CapturedCards);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_PlusWall_OwnCardSumCountsTowardTheMatch_OnlyOpponentFlips()
        {
            // 4 + 10 = 14 for the wall, the own neighbour and the opponent neighbour: all three are in the
            // matching sum, but the wall and the own card cannot be captured.
            var playedCard = CreateCard(1, top: 1, right: 4, bottom: 4, left: 4);
            var ownCard = CreatePlacement(RightNeighbourCard(2, left: 10), Player1, 1, 1);
            var opponentCard = CreatePlacement(BottomNeighbourCard(3, top: 10), Player2, 0, 2);

            var result = PlayAt(playedCard, [ownCard, opponentCard], 0, 1, MatchRule.PlusWall);

            Assert.Single(result.CapturedCards);
            Assert.Same(opponentCard, result.CapturedCards[0]);
            Assert.Equal(Player1, opponentCard.Owner);
            Assert.DoesNotContain(ownCard, result.CapturedCards);
            Assert.Equal(Player1, ownCard.Owner);
            Assert.Equal(new[] { MatchRule.PlusWall }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_PlusAndSameWall_EachPhaseFlipsItsOwnCard_ReportsBoth()
        {
            // PLUS takes the two cards sharing the sum 11 (5 + 6 and 3 + 8) and leaves the tied 1 + 1 pair
            // alone, because that sum is not shared. SAME WALL then sees that tie plus the wall tie, so it
            // flips the tied card: one move can report two rules, each owning its own captures.
            var playedCard = CreateCard(1, top: 5, right: 3, bottom: 1, left: 10);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 6), Player2, 0, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 8), Player2, 1, 1);
            var bottomNeighbour = CreatePlacement(BottomNeighbourCard(4, top: 1), Player2, 0, 2);

            var result = PlayAt(
                playedCard,
                [topNeighbour, rightNeighbour, bottomNeighbour],
                0,
                1,
                MatchRule.Plus,
                MatchRule.SameWall
            );

            Assert.Equal(3, result.CapturedCards.Count);
            Assert.Equal(MatchRule.Plus, CaptureCauseOf(result, topNeighbour)!.Rule);
            Assert.Equal(MatchRule.Plus, CaptureCauseOf(result, rightNeighbour)!.Rule);
            Assert.Equal(MatchRule.SameWall, CaptureCauseOf(result, bottomNeighbour)!.Rule);
            Assert.Equal(new[] { MatchRule.Plus, MatchRule.SameWall }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_WallRule_DoesNotStealCardsFromSame()
        {
            // SAME claims every tied opponent card, so by the time the wall phase runs there is nothing left
            // for it to flip: the wall tie only raises the wall phase's tie count, and a rule that changed
            // nothing must not be reported.
            var playedCard = CreateCard(1, top: 5, right: 5, bottom: 1, left: 10);
            var topNeighbour = CreatePlacement(TopNeighbourCard(2, bottom: 5), Player2, 0, 0);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(3, left: 5), Player2, 1, 1);

            var result = PlayAt(
                playedCard,
                [topNeighbour, rightNeighbour],
                0,
                1,
                MatchRule.Same,
                MatchRule.SameWall
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.Equal(MatchRule.Same, CaptureCauseOf(result, topNeighbour)!.Rule);
            Assert.Equal(MatchRule.Same, CaptureCauseOf(result, rightNeighbour)!.Rule);
            Assert.Equal(new[] { MatchRule.Same }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_Corner_SeesTwoWalls()
        {
            // A corner card touches the top and left walls and both of its sides there are A, so the walls
            // alone supply the two ties: the single tied neighbour is enough to fire SAME WALL.
            var playedCard = CreateCard(1, top: 10, right: 3, bottom: 1, left: 10);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(2, left: 3), Player2, 1, 0);

            var result = PlayAt(playedCard, [rightNeighbour], 0, 0, MatchRule.SameWall);

            Assert.Single(result.CapturedCards);
            Assert.Same(rightNeighbour, result.CapturedCards[0]);
            Assert.Equal(new[] { MatchRule.SameWall }, result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameWall_Flip_MovesScoresByTheFlippedCards()
        {
            var playedCard = CreateCard(1, top: 1, right: 5, bottom: 1, left: 10);
            var rightNeighbour = CreatePlacement(RightNeighbourCard(2, left: 5), Player2, 1, 1);

            var result = PlayAt(playedCard, [rightNeighbour], 0, 1, MatchRule.SameWall);

            // P1: 5 (start) + 2 owned on board (played + 1 flipped) - 1 played = 6
            // P2: 5 (start) + 0 owned on board - 1 played = 4
            Assert.Equal(6, result.Player1Score);
            Assert.Equal(4, result.Player2Score);
        }

        [Fact]
        public void PlayCard_Same_FlipsTheTiedNeighboursOnly_NotTheirNeighbours()
        {
            // P1's centre card ties with the card above it (5 vs 5) and the card to its left (5 vs 5), so SAME fires
            // and flips both. The card above it beats the card to its right (9 > 1): if a flipped card chained, that
            // one would flip too — SAME does not chain (COMBO is the rule that does, and this match has no COMBO).
            var playedCard = CreateCard(1, top: 5, right: 1, bottom: 1, left: 5);
            var tiedAbove = CreatePlacement(
                CreateCard(2, top: 1, right: 9, bottom: 5, left: 1),
                Player2,
                1,
                0
            );
            var tiedLeft = CreatePlacement(
                CreateCard(3, top: 1, right: 5, bottom: 1, left: 1),
                Player2,
                0,
                1
            );
            var beyondTheTie = CreatePlacement(CreateCard(4, 1, 1, 1, 1), Player2, 2, 0);

            var result = PlayAt(
                playedCard,
                [tiedAbove, tiedLeft, beyondTheTie],
                1,
                1,
                MatchRule.Same
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.Contains(tiedAbove, result.CapturedCards);
            Assert.Contains(tiedLeft, result.CapturedCards);
            Assert.DoesNotContain(beyondTheTie, result.CapturedCards);

            // Exactly the two that flipped changed hands, and the scores count exactly those two captures.
            Assert.Equal(Player1, tiedAbove.Owner);
            Assert.Equal(Player1, tiedLeft.Owner);
            Assert.Equal(Player2, beyondTheTie.Owner);
            Assert.Equal(new[] { MatchRule.Same }, result.TriggeredRules);
            Assert.Equal(7, result.Player1Score);
            Assert.Equal(3, result.Player2Score);
        }

        [Fact]
        public void PlayCard_Plus_FlipsTheMatchingNeighboursOnly_NotTheirNeighbours()
        {
            // 3 + 8 and 4 + 7 both make 11, with neither collision tying or winning on rank, so PLUS alone flips the
            // card above and the card to the left. The card above beats the one to its right (9 > 1), which a chain
            // would flip as well.
            var playedCard = CreateCard(1, top: 3, right: 1, bottom: 1, left: 4);
            var matchingAbove = CreatePlacement(
                CreateCard(2, top: 1, right: 9, bottom: 8, left: 1),
                Player2,
                1,
                0
            );
            var matchingLeft = CreatePlacement(
                CreateCard(3, top: 1, right: 7, bottom: 1, left: 1),
                Player2,
                0,
                1
            );
            var beyondTheSum = CreatePlacement(CreateCard(4, 1, 1, 1, 1), Player2, 2, 0);

            var result = PlayAt(
                playedCard,
                [matchingAbove, matchingLeft, beyondTheSum],
                1,
                1,
                MatchRule.Plus
            );

            Assert.Equal(2, result.CapturedCards.Count);
            Assert.Contains(matchingAbove, result.CapturedCards);
            Assert.Contains(matchingLeft, result.CapturedCards);
            Assert.DoesNotContain(beyondTheSum, result.CapturedCards);
            Assert.Equal(Player1, matchingAbove.Owner);
            Assert.Equal(Player1, matchingLeft.Owner);
            Assert.Equal(Player2, beyondTheSum.Owner);
            Assert.Equal(new[] { MatchRule.Plus }, result.TriggeredRules);
            Assert.Equal(7, result.Player1Score);
            Assert.Equal(3, result.Player2Score);
        }

        [Fact]
        public void PlayCard_StrictWin_DoesNotChainFromTheCapturedCard()
        {
            // The same invariant for the phase before the rules: a plain battle capture (9 > 2, no rules enabled)
            // never cascades into the cards the captured card could beat (its right 9 > the neighbour's left 1).
            var playedCard = CreateCard(1, top: 9, right: 1, bottom: 1, left: 1);
            var beaten = CreatePlacement(
                CreateCard(2, top: 1, right: 9, bottom: 2, left: 1),
                Player2,
                1,
                0
            );
            var beyondTheWin = CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 2, 0);

            var result = PlayAt(playedCard, [beaten, beyondTheWin], 1, 1);

            Assert.Same(beaten, Assert.Single(result.CapturedCards));
            Assert.Equal(Player1, beaten.Owner);
            Assert.Equal(Player2, beyondTheWin.Owner);
            Assert.Empty(result.TriggeredRules);
        }

        [Fact]
        public void PlayCard_SameWall_FlipsTheWallTiedNeighbourOnly_NotItsNeighbours()
        {
            // From the top-left corner P1's left side faces the wall, which counts as an A (10) and ties with the
            // played 10, while the bottom side ties with the card below (5 vs 5) — two ties, so SAME WALL fires and
            // flips that neighbour (the wall itself has no card, so it is never captured). The card below beats the
            // one to its right (9 > 1), which a chain would flip too.
            var playedCard = CreateCard(1, top: 1, right: 1, bottom: 5, left: 10);
            var wallTied = CreatePlacement(
                CreateCard(2, top: 5, right: 9, bottom: 1, left: 1),
                Player2,
                0,
                1
            );
            var beyondTheTie = CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 1, 1);

            var result = PlayAt(playedCard, [wallTied, beyondTheTie], 0, 0, MatchRule.SameWall);

            Assert.Same(wallTied, Assert.Single(result.CapturedCards));
            Assert.Equal(Player1, wallTied.Owner);
            Assert.Equal(Player2, beyondTheTie.Owner);
            Assert.Equal(new[] { MatchRule.SameWall }, result.TriggeredRules);
            Assert.Equal(6, result.Player1Score);
            Assert.Equal(4, result.Player2Score);
        }

        [Fact]
        public void PlayCard_PlusWall_FlipsTheWallSummedNeighbourOnly_NotItsNeighbours()
        {
            // The same corner: the wall on the left sums to 5 + 10 with the played card's left 5, and the card below
            // sums to 5 + 10 with its top 10 — the same sum, so PLUS WALL fires and flips that neighbour. The card
            // below beats the one to its right (9 > 1), which a chain would flip too.
            var playedCard = CreateCard(1, top: 1, right: 1, bottom: 5, left: 5);
            var wallSummed = CreatePlacement(
                CreateCard(2, top: 10, right: 9, bottom: 1, left: 1),
                Player2,
                0,
                1
            );
            var beyondTheSum = CreatePlacement(CreateCard(3, 1, 1, 1, 1), Player2, 1, 1);

            var result = PlayAt(playedCard, [wallSummed, beyondTheSum], 0, 0, MatchRule.PlusWall);

            Assert.Same(wallSummed, Assert.Single(result.CapturedCards));
            Assert.Equal(Player1, wallSummed.Owner);
            Assert.Equal(Player2, beyondTheSum.Owner);
            Assert.Equal(new[] { MatchRule.PlusWall }, result.TriggeredRules);
            Assert.Equal(6, result.Player1Score);
            Assert.Equal(4, result.Player2Score);
        }

        /// <summary>
        /// The odds table of the CPU's opening hand: weight <c>level²</c> over the levels 1..10, so level 10 owns
        /// 25.97% of a slot and level 1 0.26%. Rising weights are what makes the hand come out strong — a flat draw
        /// would leave it at the catalogue's own level 5.6.
        /// </summary>
        [Fact]
        public void GetCpuHand_LevelWeights_ClimbWithTheSquareOfTheLevel()
        {
            var weights = Enumerable
                .Range(Card.MinLevel, Card.MaxLevel - Card.MinLevel + 1)
                .Select(CpuOpponent.HandLevelWeight)
                .ToList();

            Assert.Equal(new[] { 1, 4, 9, 16, 25, 36, 49, 64, 81, 100 }, weights);
            Assert.Equal(weights.Sum(), CpuOpponent.TotalHandLevelWeight);
            Assert.Equal(385, CpuOpponent.TotalHandLevelWeight);

            var total = CpuOpponent.TotalHandLevelWeight;
            Assert.Equal(25.97, Math.Round(weights[9] * 100.0 / total, 2));
            Assert.Equal(0.26, Math.Round(weights[0] * 100.0 / total, 2));
        }

        /// <summary>
        /// The level walk itself, pinned to the table: with one card per level the first slot's level is decided by
        /// the rng value alone, so these cases are exactly the cumulative boundaries of 1+4+9+…+100 = 385 slots.
        /// </summary>
        [Theory]
        [InlineData(0, 1)] // level 1 owns slot 0 (weight 1)
        [InlineData(1, 2)] // level 2 owns slots 1..4 (weight 4)
        [InlineData(4, 2)]
        [InlineData(5, 3)] // level 3 owns slots 5..13 (weight 9)
        [InlineData(13, 3)]
        [InlineData(14, 4)] // level 4 owns slots 14..29 (weight 16)
        [InlineData(29, 4)]
        [InlineData(30, 5)] // level 5 owns slots 30..54 (weight 25)
        [InlineData(54, 5)]
        [InlineData(55, 6)] // level 6 owns slots 55..90 (weight 36)
        [InlineData(90, 6)]
        [InlineData(91, 7)] // level 7 owns slots 91..139 (weight 49)
        [InlineData(139, 7)]
        [InlineData(140, 8)] // level 8 owns slots 140..203 (weight 64)
        [InlineData(203, 8)]
        [InlineData(204, 9)] // level 9 owns slots 204..284 (weight 81)
        [InlineData(284, 9)]
        [InlineData(285, 10)] // level 10 owns slots 285..384 (weight 100)
        [InlineData(384, 10)]
        public void GetCpuHand_FirstSlot_FollowsTheLevelTable(int pick, int expectedLevel)
        {
            var random = new ScriptedRandom(pick);

            var hand = GameLogic.GetCpuHand(OneCardPerLevelCatalogue(), random);

            Assert.Equal(CpuOpponent.TotalHandLevelWeight, random.Bounds[0]);
            Assert.Equal(expectedLevel, hand[0].Level);
        }

        /// <summary>The level is drawn first, then the card inside it — the level pick is the draw's first call.</summary>
        [Fact]
        public void GetCpuHand_PicksTheCardWithinTheChosenLevel()
        {
            // Levels 10 and 1 only, so the 101 slots are level 10's 100 and then level 1's single one.
            var catalogue = new List<Card>
            {
                LevelCard(1, 10),
                LevelCard(2, 10),
                LevelCard(3, 10),
                LevelCard(4, 1),
                LevelCard(5, 1),
                LevelCard(6, 1),
            };
            var random = new ScriptedRandom(1, 1);

            var hand = GameLogic.GetCpuHand(catalogue, random);

            Assert.Equal(2, hand[0].Id); // the second of level 10's three cards
            Assert.Equal(GameLogicService.HandSize, hand.Count);
            Assert.Equal(hand.Count, hand.Select(card => card.Id).Distinct().Count());
        }

        /// <summary>
        /// Top of the range every time: the hand walks down the levels, and a level leaves the draw as soon as its
        /// cards are used — which is what stops a strong-but-thin level from filling a whole hand.
        /// </summary>
        [Fact]
        public void GetCpuHand_TopOfEveryRange_BringsTheStrongestLevelsLeft()
        {
            var hand = GameLogic.GetCpuHand(OneCardPerLevelCatalogue(), new ScriptedRandom());

            Assert.Equal(new[] { 10, 9, 8, 7, 6 }, hand.Select(card => card.Level));
        }

        /// <summary>The mirror image: the bottom of every range walks up the levels instead.</summary>
        [Fact]
        public void GetCpuHand_BottomOfEveryRange_BringsTheWeakestLevelsLeft()
        {
            var random = new ScriptedRandom(0, 0, 0, 0, 0, 0, 0, 0, 0, 0); // one value per call, ten calls

            var hand = GameLogic.GetCpuHand(OneCardPerLevelCatalogue(), random);

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, hand.Select(card => card.Level));
        }

        /// <summary>
        /// The weights are summed over the levels the catalogue has, not over all ten: with no level 10 the top of
        /// the range is level 9, and a level-1 card is still only one of the 82 slots.
        /// </summary>
        [Fact]
        public void GetCpuHand_RenormalisesOverTheLevelsTheCatalogueHas()
        {
            var catalogue = new List<Card>
            {
                LevelCard(1, 1),
                LevelCard(2, 9),
                LevelCard(3, 9),
                LevelCard(4, 9),
                LevelCard(5, 9),
                LevelCard(6, 9),
            };
            var random = new ScriptedRandom();

            var hand = GameLogic.GetCpuHand(catalogue, random);

            Assert.Equal(1 + CpuOpponent.HandLevelWeight(9), random.Bounds[0]);
            Assert.Equal(GameLogicService.HandSize, hand.Count);
            Assert.All(hand, card => Assert.Equal(9, card.Level));
        }

        /// <summary>A catalogue smaller than a hand yields what it has — no draw runs past the end of a pool.</summary>
        [Fact]
        public void GetCpuHand_WithFewerCardsThanAHand_TakesThemAll()
        {
            var catalogue = new List<Card> { LevelCard(1, 4), LevelCard(2, 4), LevelCard(3, 9) };

            var hand = GameLogic.GetCpuHand(catalogue, new ScriptedRandom());

            Assert.Equal(3, hand.Count);
            Assert.All(hand, card => Assert.Contains(card, catalogue));
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

        /// <summary>
        /// Plays <paramref name="playedCard"/> at the given square and returns the result, so a test can put a
        /// card on the board's edge or in a corner — where the wall rules (SAME WALL / PLUS WALL) apply.
        /// </summary>
        private static GameLogicService.PlayCardResult PlayAt(
            Card playedCard,
            List<CardPlacement> placements,
            int x,
            int y,
            params MatchRule[] rules
        )
        {
            var match = CreateMatch(rules);
            return GameLogic.PlayCard(match, placements, playedCard, Player1, x, y);
        }

        /// <summary>
        /// Plays <paramref name="playedCard"/> from the centre of the board with the given neighbours and
        /// returns the result plus those neighbours, so tests can assert owners and causes by position.
        /// </summary>
        private static (
            GameLogicService.PlayCardResult Result,
            List<CardPlacement> Neighbours
        ) PlayCenter(Card playedCard, List<CardPlacement> neighbours, params MatchRule[] rules)
        {
            var match = CreateMatch(rules);
            var result = GameLogic.PlayCard(match, neighbours, playedCard, Player1, 1, 1);
            return (result, neighbours);
        }

        /// <summary>
        /// Why the card at that position flipped (null rule = basic battle), or null when the position was
        /// not captured at all.
        /// </summary>
        private static CaptureResolution.CaptureCause? CaptureCauseOf(
            GameLogicService.PlayCardResult result,
            CardPlacement placement
        )
        {
            return result.CaptureCauses.SingleOrDefault(cause =>
                cause.X == placement.X && cause.Y == placement.Y
            );
        }

        /// <summary>Card for the neighbour above the centre — only its bottom value is compared.</summary>
        private static Card TopNeighbourCard(int id, int bottom) => CreateCard(id, 1, 1, bottom, 1);

        /// <summary>Card for the neighbour right of the centre — only its left value is compared.</summary>
        private static Card RightNeighbourCard(int id, int left) => CreateCard(id, 1, 1, 1, left);

        /// <summary>Card for the neighbour below the centre — only its top value is compared.</summary>
        private static Card BottomNeighbourCard(int id, int top) => CreateCard(id, top, 1, 1, 1);

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

        /// <summary>A catalogue card whose level is all these hand tests care about — the ranks never matter.</summary>
        private static Card LevelCard(int id, int level) =>
            new()
            {
                Id = id,
                Name = $"Card {id}",
                Image = $"card-{id}.jpg",
                TopValue = 1,
                RightValue = 1,
                BottomValue = 1,
                LeftValue = 1,
                Level = level,
            };

        /// <summary>One catalogue card per level 1..10 — with a single card per level, a card's id is its level.</summary>
        private static List<Card> OneCardPerLevelCatalogue() =>
            Enumerable
                .Range(Card.MinLevel, Card.MaxLevel - Card.MinLevel + 1)
                .Select(level => LevelCard(level, level))
                .ToList();

        /// <summary>
        /// Hands out the scripted rng values in order — each asserted to be inside the range the draw asked for — and
        /// takes the top of the range once the script runs out, so a test only scripts the draws it asserts on.
        /// </summary>
        private sealed class ScriptedRandom(params int[] values) : IRandomSource
        {
            private readonly Queue<int> _values = new(values);

            /// <summary>Every exclusive upper bound the draw asked for, in call order.</summary>
            public List<int> Bounds { get; } = [];

            public int Next(int exclusiveMax)
            {
                Bounds.Add(exclusiveMax);

                if (_values.Count == 0)
                {
                    return exclusiveMax - 1;
                }

                var value = _values.Dequeue();
                Assert.InRange(value, 0, exclusiveMax - 1);

                return value;
            }
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
