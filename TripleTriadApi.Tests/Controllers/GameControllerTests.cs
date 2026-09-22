using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Controllers;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Controllers
{
    /// <summary>
    /// Tests for the hand a player picks before a match: the list `POST api/game/match` files, the `PickHandLater`
    /// variant that files nothing (the waiting player picks once an opponent is there), the joiner's list on
    /// `POST api/game/match/{id}/join`, the later pick on `POST api/game/match/{id}/hand`,
    /// `POST api/game/match/{id}/cancel`, and the `handsReady`/`timedOut` flags `GET api/game/match/{id}` reports.
    /// Every rejected request also asserts that nothing was written.
    ///
    /// The controller is exercised directly with a hand-built <see cref="HttpContext"/> (the same pattern as
    /// <c>PlayerControllerTests</c> and <c>ShopControllerTests</c>) over an EF InMemory database; the SignalR pushes
    /// are recorded by <see cref="RecordingMatchNotifier"/> instead of a hub, so no web host is needed.
    /// </summary>
    public class GameControllerTests
    {
        private const string PlayerLogin = "argel";
        private const string OpponentLogin = "squall";
        private const string StrangerLogin = "seifer";

        /// <summary>Player 1's first five cards — the sent order of a hand is the order the player picked in.</summary>
        private static readonly int[] FirstHand = [1, 2, 3, 4, 5];

        /// <summary>Player 1's second five cards: what <c>SetHand</c> replaces the first hand with.</summary>
        private static readonly int[] SecondHand = [6, 7, 8, 9, 10];

        /// <summary>The opponent's five cards.</summary>
        private static readonly int[] OpponentHand = [11, 12, 13, 14, 15];

        /// <summary>A catalogue card nobody owns, for the "you can only play cards you own" case.</summary>
        private const int UnownedCardId = 16;

        [Fact]
        public async Task CreateMatch_WithFiveOwnedCards_FilesThatHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            // Deliberately out of order: the hand is filed exactly as it was sent.
            var sent = new[] { 4, 2, 5, 1, 3 };

            var result = await controller.CreateMatch(new CreateMatchRequest { CardIds = sent });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;
            var matchId = root.GetProperty("match").GetProperty("Id").GetInt32();

            Assert.Equal("waiting", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(
                sent,
                root.GetProperty("playerHand")
                    .EnumerateArray()
                    .Select(card => card.GetProperty("Id").GetInt32())
            );

            Assert.Equal(
                sent,
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == PlayerLogin)
                    .OrderBy(hand => hand.Id)
                    .Select(hand => hand.CardId)
                    .ToListAsync()
            );

            // One hand is not a ready match: the opponent has not joined yet, so nobody is waiting on a hand.
            var state = await ReadStateAsync(controller, matchId);
            Assert.False(state.HandsReady);
            Assert.False(state.TimedOut);
        }

        [Fact]
        public async Task CreateMatch_WithPickHandLater_LeavesNoHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.CreateMatch(
                new CreateMatchRequest { PickHandLater = true }
            );

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal("waiting", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(string.Empty, root.GetProperty("match").GetProperty("Player2Id").GetString());

            // No hand at all: it arrives later through POST match/{id}/hand.
            Assert.Equal(0, root.GetProperty("playerHand").GetArrayLength());
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task CreateMatch_WithoutCardIds_DrawsARandomHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.CreateMatch(new CreateMatchRequest());

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal("waiting", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(GameLogicService.HandSize, root.GetProperty("playerHand").GetArrayLength());
            Assert.Equal(
                GameLogicService.HandSize,
                await context.PlayerHands.CountAsync(hand => hand.PlayerId == PlayerLogin)
            );
        }

        [Fact]
        public async Task CreateMatch_WithFourCards_Returns400AndWritesNothing()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.CreateMatch(
                new CreateMatchRequest { CardIds = [1, 2, 3, 4] }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("exactly 5", json.RootElement.GetProperty("error").GetString());

            // The list is validated before the match exists, so a bad one leaves nothing behind.
            Assert.Empty(context.Matches);
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task CreateMatch_WithDuplicates_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.CreateMatch(
                new CreateMatchRequest { CardIds = [1, 2, 3, 4, 1] }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("same card twice", json.RootElement.GetProperty("error").GetString());

            Assert.Empty(context.Matches);
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task CreateMatch_WithAUnownedCard_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            // UnownedCardId is in the catalogue, so the failure is ownership and not a bogus id.
            var result = await controller.CreateMatch(
                new CreateMatchRequest { CardIds = [1, 2, 3, 4, UnownedCardId] }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("cards you own", json.RootElement.GetProperty("error").GetString());

            Assert.Empty(context.Matches);
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task CreateMatch_WithBothCardIdsAndPickHandLater_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            // The list and the flag describe the same hand twice, whichever opponent the match is for.
            var waiting = await controller.CreateMatch(
                new CreateMatchRequest { CardIds = FirstHand, PickHandLater = true }
            );
            var againstTheCpu = await controller.CreateMatch(
                new CreateMatchRequest
                {
                    OpponentId = CpuOpponent.Login,
                    CardIds = FirstHand,
                    PickHandLater = true,
                }
            );

            AssertContradictoryPickHandLater(waiting);
            AssertContradictoryPickHandLater(againstTheCpu);
            Assert.Empty(context.Matches);
        }

        [Fact]
        public async Task CreateMatch_AgainstTheCpuWithPickHandLater_FilesOnlyTheCpuHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            // The SelectHand flow against the CPU: it is seated as player 2 with a hand of its own, and the human's
            // five arrive through POST match/{id}/hand like any other pick.
            var result = await controller.CreateMatch(
                new CreateMatchRequest { OpponentId = CpuOpponent.Login, PickHandLater = true }
            );

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;
            var matchId = root.GetProperty("match").GetProperty("Id").GetInt32();

            Assert.Equal("active", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(
                CpuOpponent.Login,
                root.GetProperty("match").GetProperty("Player2Id").GetString()
            );
            Assert.Equal(0, root.GetProperty("playerHand").GetArrayLength());

            Assert.Equal(
                GameLogicService.HandSize,
                await context.PlayerHands.CountAsync(hand => hand.PlayerId == CpuOpponent.Login)
            );
            Assert.Equal(
                0,
                await context.PlayerHands.CountAsync(hand => hand.PlayerId == PlayerLogin)
            );

            // Nobody is ready until the human picks — and the pick is what makes the match ready.
            Assert.False((await ReadStateAsync(controller, matchId)).HandsReady);

            await controller.SetHand(matchId, new SetHandRequest { CardIds = FirstHand });

            Assert.True((await ReadStateAsync(controller, matchId)).HandsReady);
        }

        [Fact]
        public async Task CreateMatch_AgainstTheCpuWithACardList_FilesThatHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            // The older client's shape: the list files the human's hand straight away, and the CPU still gets its own.
            var result = await controller.CreateMatch(
                new CreateMatchRequest { OpponentId = CpuOpponent.Login, CardIds = FirstHand }
            );

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal("active", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(FirstHand.Length, root.GetProperty("playerHand").GetArrayLength());
            Assert.Equal(
                GameLogicService.HandSize,
                await context.PlayerHands.CountAsync(hand => hand.PlayerId == CpuOpponent.Login)
            );
            Assert.Equal(
                FirstHand.Length,
                await context.PlayerHands.CountAsync(hand => hand.PlayerId == PlayerLogin)
            );
        }

        [Fact]
        public async Task CreateMatch_AgainstTheCpu_DrawsItsHandFromTheStrongestLevels()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            // The rng takes the top of the range on every draw, so the CPU's five are the strongest cards the
            // catalogue has: level 10 down to level 6, one per level. A uniform draw could not produce that, so
            // this is what pins the AI path to the level-weighted draw instead of GetRandomHand.
            var controller = CreateController(context, PlayerLogin, random: new TopOfRangeRandom());

            await controller.CreateMatch(new CreateMatchRequest { OpponentId = CpuOpponent.Login });

            var cpuHand = await context
                .PlayerHands.Include(hand => hand.Card)
                .Where(hand => hand.PlayerId == CpuOpponent.Login)
                .ToListAsync();

            Assert.Equal(
                new[] { 10, 9, 8, 7, 6 },
                cpuHand.Select(hand => hand.Card!.Level).OrderByDescending(level => level)
            );
        }

        [Fact]
        public async Task CreateMatch_WithPickHandLaterAndAHumanOpponent_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            // A named human opponent has no picker of their own, so a hand-less match with them would leave a player
            // stuck: the flag is for a waiting match and for the CPU only.
            var result = await controller.CreateMatch(
                new CreateMatchRequest { OpponentId = OpponentLogin, PickHandLater = true }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("PickHandLater", json.RootElement.GetProperty("error").GetString());

            Assert.Empty(context.Matches);
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task JoinMatch_WithFiveOwnedCards_FilesTheJoinersHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var creator = CreateController(context, PlayerLogin);
            await creator.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);
            var sent = new[] { 15, 13, 11, 14, 12 };

            var result = await joiner.JoinMatch(matchId, new JoinMatchRequest { CardIds = sent });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal("active", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(
                sent,
                root.GetProperty("playerHand")
                    .EnumerateArray()
                    .Select(card => card.GetProperty("Id").GetInt32())
            );

            Assert.Equal(
                sent,
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == OpponentLogin)
                    .OrderBy(hand => hand.Id)
                    .Select(hand => hand.CardId)
                    .ToListAsync()
            );

            // Joining is what activates the match, and the hand-pick/idle timeouts are measured from that stamp.
            var match = await context.Matches.SingleAsync(stored => stored.Id == matchId);
            Assert.NotNull(match.ActivatedAt);
        }

        [Fact]
        public async Task JoinMatch_WithoutCardIds_DrawsARandomHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var creator = CreateController(context, PlayerLogin);
            await creator.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);

            // A body-less POST still binds (EmptyBodyBehavior.Allow) and draws a random hand: the behaviour an older
            // client — or any caller that never sends a list — still gets.
            var result = await joiner.JoinMatch(matchId);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal("active", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(GameLogicService.HandSize, root.GetProperty("playerHand").GetArrayLength());
            Assert.Equal(
                GameLogicService.HandSize,
                await context.PlayerHands.CountAsync(hand => hand.PlayerId == OpponentLogin)
            );
        }

        [Fact]
        public async Task JoinMatch_WithPickHandLater_JoinsWithoutAHand()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var creator = CreateController(context, PlayerLogin);
            await creator.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);

            // The SelectHand flow: joining seats the second player and files nothing, so both players pick in
            // parallel and neither hand is in the way of the other.
            var result = await joiner.JoinMatch(
                matchId,
                new JoinMatchRequest { PickHandLater = true }
            );

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal("active", root.GetProperty("match").GetProperty("Status").GetString());
            Assert.Equal(0, root.GetProperty("playerHand").GetArrayLength());
            Assert.Empty(context.PlayerHands);

            // Joining is what activates the match, even though no hand was filed.
            var match = await context.Matches.SingleAsync(stored => stored.Id == matchId);
            Assert.NotNull(match.ActivatedAt);

            // And nobody is ready until both hands arrive through POST match/{id}/hand.
            Assert.False((await ReadStateAsync(joiner, matchId)).HandsReady);
        }

        [Fact]
        public async Task JoinMatch_WithBothCardIdsAndPickHandLater_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var creator = CreateController(context, PlayerLogin);
            await creator.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);

            var result = await joiner.JoinMatch(
                matchId,
                new JoinMatchRequest { CardIds = OpponentHand, PickHandLater = true }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("PickHandLater", json.RootElement.GetProperty("error").GetString());

            // Refused before anything is written: the match still has no second player and no hand rows.
            Assert.Equal("waiting", (await ReadStateAsync(creator, matchId)).Status);
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task SetHand_ReplacesTheCallersUnusedCards()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var notifier = new RecordingMatchNotifier();
            var controller = CreateController(context, PlayerLogin, notifier);

            // The waiting player sits down with a hand, then re-picks once the opponent is there.
            await controller.CreateMatch(new CreateMatchRequest { CardIds = FirstHand });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);
            await joiner.JoinMatch(matchId, new JoinMatchRequest { CardIds = OpponentHand });

            var result = await controller.SetHand(
                matchId,
                new SetHandRequest { CardIds = SecondHand }
            );

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());

            // The old five are gone, the new five are filed, and the opponent's hand is not touched.
            Assert.Equal(
                SecondHand,
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == PlayerLogin)
                    .OrderBy(hand => hand.Id)
                    .Select(hand => hand.CardId)
                    .ToListAsync()
            );
            Assert.Equal(
                OpponentHand,
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == OpponentLogin)
                    .OrderBy(hand => hand.Id)
                    .Select(hand => hand.CardId)
                    .ToListAsync()
            );

            // The opponent's screen is told to stop waiting, and both hands are in now.
            Assert.Equal(new[] { matchId }, notifier.HandReadyMatches);
            Assert.True((await ReadStateAsync(controller, matchId)).HandsReady);
        }

        [Fact]
        public async Task SetHand_AfterAPlacement_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            await controller.CreateMatch(new CreateMatchRequest { CardIds = FirstHand });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);
            await joiner.JoinMatch(matchId, new JoinMatchRequest { CardIds = OpponentHand });

            // One of player 1's cards is already on the board, so the hand can no longer be swapped.
            context.PlayerHands.Add(
                new PlayerHand
                {
                    MatchId = matchId,
                    PlayerId = PlayerLogin,
                    CardId = SecondHand[0],
                    IsUsed = true,
                }
            );
            await context.SaveChangesAsync();

            var result = await controller.SetHand(
                matchId,
                new SetHandRequest { CardIds = SecondHand }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("already played", json.RootElement.GetProperty("error").GetString());

            // Untouched: the five cards from creation are still the hand.
            Assert.Equal(
                FirstHand,
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == PlayerLogin && !hand.IsUsed)
                    .OrderBy(hand => hand.Id)
                    .Select(hand => hand.CardId)
                    .ToListAsync()
            );
        }

        [Fact]
        public async Task SetHand_ForSomeoneElsesMatch_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, PlayerLogin);

            await controller.CreateMatch(new CreateMatchRequest { CardIds = FirstHand });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);
            await joiner.JoinMatch(matchId, new JoinMatchRequest { CardIds = OpponentHand });

            var stranger = CreateController(context, StrangerLogin);

            var result = await stranger.SetHand(
                matchId,
                new SetHandRequest { CardIds = SecondHand }
            );

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("not a player", json.RootElement.GetProperty("error").GetString());

            Assert.Empty(
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == StrangerLogin)
                    .ToListAsync()
            );
        }

        [Fact]
        public async Task SetHand_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, login: null);

            var result = await controller.SetHand(1, new SetHandRequest { CardIds = FirstHand });

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
            Assert.Empty(context.PlayerHands);
        }

        [Fact]
        public async Task GetMatch_ReportsHandsReadyOnlyWhenBothHandsAreFiled()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var creator = CreateController(context, PlayerLogin);

            // The SelectHand flow: the match is created without a hand, and joining it brings none either, so the two
            // players pick in parallel once the match has the two of them.
            await creator.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var matchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var joiner = CreateController(context, OpponentLogin);
            await joiner.JoinMatch(matchId, new JoinMatchRequest { PickHandLater = true });

            Assert.False((await ReadStateAsync(creator, matchId)).HandsReady);

            // Whoever finishes first files their five; the other is still picking, so the match is not ready yet.
            await joiner.SetHand(matchId, new SetHandRequest { CardIds = OpponentHand });

            Assert.Equal(
                OpponentHand,
                await context
                    .PlayerHands.Where(hand => hand.PlayerId == OpponentLogin)
                    .OrderBy(hand => hand.Id)
                    .Select(hand => hand.CardId)
                    .ToListAsync()
            );
            Assert.False((await ReadStateAsync(creator, matchId)).HandsReady);

            await creator.SetHand(matchId, new SetHandRequest { CardIds = FirstHand });

            Assert.True((await ReadStateAsync(creator, matchId)).HandsReady);

            // Playing a card marks its hand row used — that is "in play", not "no hand was filed", so the match stays
            // ready. (The timeout sweep reads the same flag and used to abandon live games over this.)
            var played = await context.PlayerHands.FirstAsync(hand =>
                hand.PlayerId == PlayerLogin && hand.CardId == FirstHand[0]
            );
            played.IsUsed = true;
            await context.SaveChangesAsync();

            Assert.True((await ReadStateAsync(creator, matchId)).HandsReady);
        }

        [Fact]
        public async Task CancelWaitingMatch_AbandonsIt_AndRejectsAMatchWithAnOpponent()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var notifier = new RecordingMatchNotifier();
            var controller = CreateController(context, PlayerLogin, notifier);

            await controller.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var waitingMatchId = await context.Matches.Select(match => match.Id).SingleAsync();

            var cancel = await controller.CancelMatch(waitingMatchId);

            var ok = Assert.IsType<OkObjectResult>(cancel.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());

            // Abandoned, not deleted: the sweep no longer sees it and its creator can search again.
            Assert.Equal("abandoned", (await ReadStateAsync(controller, waitingMatchId)).Status);
            var abandoned = Assert.Single(notifier.Abandoned);
            Assert.Equal(waitingMatchId, abandoned.MatchId);

            await controller.CreateMatch(new CreateMatchRequest { PickHandLater = true });
            var joinedMatchId = await context
                .Matches.Where(match => match.Id != waitingMatchId)
                .Select(match => match.Id)
                .SingleAsync();

            var joiner = CreateController(context, OpponentLogin);
            await joiner.JoinMatch(joinedMatchId, new JoinMatchRequest { CardIds = OpponentHand });
            notifier.Abandoned.Clear();

            // An opponent is there: this is a real game now, and only the creator could have cancelled it anyway.
            var tooLate = await controller.CancelMatch(joinedMatchId);
            var rejected = Assert.IsType<BadRequestObjectResult>(tooLate.Result);
            using var rejectedJson = JsonDocument.Parse(JsonSerializer.Serialize(rejected.Value));
            Assert.Contains(
                "waiting match",
                rejectedJson.RootElement.GetProperty("error").GetString()
            );

            var notMine = await joiner.CancelMatch(joinedMatchId);
            Assert.IsType<BadRequestObjectResult>(notMine.Result);

            Assert.Equal("active", (await ReadStateAsync(controller, joinedMatchId)).Status);
            Assert.Empty(notifier.Abandoned);
        }

        /// <summary>`handsReady`/`timedOut`/status as the SelectHand step reads them off `GET api/game/match/{id}`.</summary>
        private static async Task<(string Status, bool HandsReady, bool TimedOut)> ReadStateAsync(
            GameController controller,
            int matchId
        )
        {
            var result = await controller.GetMatch(matchId);
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var match = json.RootElement.GetProperty("match");

            return (
                match.GetProperty("Status").GetString()!,
                match.GetProperty("handsReady").GetBoolean(),
                match.GetProperty("timedOut").GetBoolean()
            );
        }

        private static void AssertContradictoryPickHandLater(ActionResult<object> result)
        {
            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("PickHandLater", json.RootElement.GetProperty("error").GetString());
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        /// <summary>The controller under test, with the authenticated login (or none) in its HttpContext.</summary>
        private static GameController CreateController(
            TripleTriadContext context,
            string? login,
            IMatchNotifier? notifier = null,
            IRandomSource? random = null
        )
        {
            var gameRepository = new GameRepository(context);
            var gameLogic = new GameLogicService();

            var controller = new GameController(
                gameRepository,
                new PlayerCardRepository(context),
                gameLogic,
                new GamePlayService(
                    gameRepository,
                    gameLogic,
                    new MatchRewardService(new PlayerRepository(context))
                ),
                new MatchStateService(gameRepository),
                notifier ?? new RecordingMatchNotifier(),
                random ?? new SystemRandomSource()
            );

            var claims = login is null
                ? new List<Claim>()
                : [new Claim(ClaimTypes.NameIdentifier, login)];

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims)),
                },
            };

            return controller;
        }

        /// <summary>
        /// Two players, the catalogue the tests pick from and the cards each player owns: player 1 holds
        /// <see cref="FirstHand"/> and <see cref="SecondHand"/>, the opponent holds <see cref="OpponentHand"/> and
        /// <see cref="UnownedCardId"/> belongs to nobody. Ids are disjoint, so no test can pick a card its player
        /// does not own by accident.
        /// </summary>
        private static async Task SeedAsync(TripleTriadContext context)
        {
            context.Players.Add(
                new Player
                {
                    Login = PlayerLogin,
                    Email = "argel@example.com",
                    PasswordHash = "hash",
                    Coins = 0,
                }
            );
            context.Players.Add(
                new Player
                {
                    Login = OpponentLogin,
                    Email = "squall@example.com",
                    PasswordHash = "hash",
                    Coins = 0,
                }
            );

            AddCatalogCards(context, [.. FirstHand, .. SecondHand, .. OpponentHand, UnownedCardId]);
            OwnCards(context, PlayerLogin, [.. FirstHand, .. SecondHand]);
            OwnCards(context, OpponentLogin, OpponentHand);

            await context.SaveChangesAsync();
        }

        /// <summary>Catalogue rows, one per id — the level follows the id so every card reads plausibly.</summary>
        private static void AddCatalogCards(TripleTriadContext context, params int[] cardIds)
        {
            foreach (var cardId in cardIds)
            {
                context.Cards.Add(
                    new Card
                    {
                        Id = cardId,
                        Name = $"Card {cardId}",
                        Image = $"ff8-deck/card-{cardId}.jpg",
                        TopValue = LevelOf(cardId),
                        RightValue = LevelOf(cardId),
                        BottomValue = LevelOf(cardId),
                        LeftValue = LevelOf(cardId),
                        Element = [],
                        Level = LevelOf(cardId),
                    }
                );
            }
        }

        /// <summary>One copy of each card — the picker needs the card, never a count.</summary>
        private static void OwnCards(TripleTriadContext context, string login, params int[] cardIds)
        {
            foreach (var cardId in cardIds)
            {
                context.PlayerCards.Add(
                    new PlayerCard
                    {
                        PlayerId = login,
                        CardId = cardId,
                        Quantity = 1,
                        FirstAcquiredAt = DateTime.UtcNow.AddDays(-1),
                        LastAcquiredAt = DateTime.UtcNow,
                    }
                );
            }
        }

        /// <summary>A level in 1..10 for the ids these tests use, so no seeded card has level 0.</summary>
        private static int LevelOf(int cardId) => (cardId - 1) % 10 + 1;

        /// <summary>
        /// An rng that always takes the top of the range, i.e. the strongest card the CPU's draw can reach — the
        /// level-weighted hand it produces is the catalogue's best five (one per level, strongest first).
        /// </summary>
        private sealed class TopOfRangeRandom : IRandomSource
        {
            public int Next(int exclusiveMax) => exclusiveMax - 1;
        }

        /// <summary>
        /// Records the pushes a REST call makes so the controller can be tested without a web host (and without a
        /// SignalR connection); the hub's own broadcasts are not this seam's business.
        /// </summary>
        private sealed class RecordingMatchNotifier : IMatchNotifier
        {
            /// <summary>Matches whose hand became ready, in call order.</summary>
            public List<int> HandReadyMatches { get; } = [];

            /// <summary>Matches abandoned with the reason the client is shown.</summary>
            public List<(int MatchId, string Reason)> Abandoned { get; } = [];

            /// <summary>Matches settled early (a forfeit) with the push reason.</summary>
            public List<(int MatchId, string Reason)> Completed { get; } = [];

            /// <summary>Moves the server played on a client's behalf (the CPU): recorded for the same reason.</summary>
            public List<MovePush> Moves { get; } = [];

            public Task HandReadyAsync(int matchId)
            {
                HandReadyMatches.Add(matchId);
                return Task.CompletedTask;
            }

            public Task CardPlayedAsync(MovePush move)
            {
                Moves.Add(move);
                return Task.CompletedTask;
            }

            public Task AbandonedAsync(int matchId, string reason)
            {
                Abandoned.Add((matchId, reason));
                return Task.CompletedTask;
            }

            public Task CompletedEarlyAsync(
                Match match,
                MatchRewardService.MatchRewardResult? rewards,
                string reason
            )
            {
                Completed.Add((match.Id, reason));
                return Task.CompletedTask;
            }
        }
    }
}
