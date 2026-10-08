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
using TripleTriadApi.Tests.Services;
using TripleTriadApi.Validators;

namespace TripleTriadApi.Tests.Controllers
{
    /// <summary>
    /// Tests for the player endpoints the client reads: `register` (which hands a brand-new account its starting
    /// inventory of 0 cards and six packs), the collection the My Cards page reads (`GET api/player/cards`), the
    /// summary behind its level picker, and `GET api/player/profile/{login}` — the public profile another player's
    /// opponent panel shows, whose shape has to stay closed (no email, no balance, no packs).
    ///
    /// The controller is exercised directly with a hand-built <see cref="HttpContext"/> (same pattern as
    /// <c>ShopControllerTests</c>) over an EF InMemory database, so no web host is needed.
    /// </summary>
    public class PlayerControllerTests
    {
        private const string PlayerLogin = "argel";

        /// <summary>The player whose profile is looked up by someone else — the opponent's side of the panel.</summary>
        private const string RivalLogin = "rival";

        [Fact]
        public async Task Cards_ReturnsTheShapeTheMyCardsPageReads()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            AddOwnedCard(context, PlayerLogin, cardId: 7, level: 3, quantity: 2);
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Cards();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var entries = json.RootElement;

            Assert.Equal(1, entries.GetArrayLength());
            var entry = entries[0];

            // The two fields the page aggregates on.
            Assert.Equal(2, entry.GetProperty("quantity").GetInt32());
            Assert.Equal(3, entry.GetProperty("card").GetProperty("level").GetInt32());

            Assert.True(entry.TryGetProperty("firstAcquiredAt", out _));
            Assert.True(entry.TryGetProperty("lastAcquiredAt", out _));

            var card = entry.GetProperty("card");
            Assert.Equal(7, card.GetProperty("id").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("image").GetString()));
            Assert.InRange(card.GetProperty("topValue").GetInt32(), 1, 10);
            Assert.InRange(card.GetProperty("rightValue").GetInt32(), 1, 10);
            Assert.InRange(card.GetProperty("bottomValue").GetInt32(), 1, 10);
            Assert.InRange(card.GetProperty("leftValue").GetInt32(), 1, 10);
            Assert.True(card.TryGetProperty("element", out _));
        }

        [Fact]
        public async Task Cards_OrdersByLevelThenCardId()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);

            // Inserted out of order on purpose: the page relies on the level grouping being sorted for it.
            AddOwnedCard(context, PlayerLogin, cardId: 10, level: 10, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 3, level: 2, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 1, level: 1, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 4, level: 2, quantity: 1);
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Cards();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            var levels = json
                .RootElement.EnumerateArray()
                .Select(entry => entry.GetProperty("card").GetProperty("level").GetInt32())
                .ToList();
            var cardIds = json
                .RootElement.EnumerateArray()
                .Select(entry => entry.GetProperty("card").GetProperty("id").GetInt32())
                .ToList();

            Assert.Equal(new[] { 1, 2, 2, 10 }, levels);
            Assert.Equal(new[] { 1, 3, 4, 10 }, cardIds);
        }

        [Fact]
        public async Task Cards_ReturnsOnlyTheCurrentPlayersRows()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, "someone-else");
            AddOwnedCard(context, PlayerLogin, cardId: 1, level: 1, quantity: 2);
            AddOwnedCard(context, "someone-else", cardId: 2, level: 1, quantity: 1);
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Cards();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(1, json.RootElement.GetArrayLength());
            Assert.Equal(1, json.RootElement[0].GetProperty("card").GetProperty("id").GetInt32());
            Assert.Equal(2, json.RootElement[0].GetProperty("quantity").GetInt32());
        }

        [Fact]
        public async Task Cards_WithNoOwnedCards_ReturnsAnEmptyArray()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Cards();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            // The My Cards page's empty state.
            Assert.Equal(0, json.RootElement.GetArrayLength());
        }

        [Fact]
        public async Task Cards_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.Cards();

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task Cards_WithALevelFilter_ReturnsOnlyThatLevel()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            AddOwnedCard(context, PlayerLogin, cardId: 1, level: 1, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 5, level: 3, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 6, level: 3, quantity: 2);
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Cards(level: 3);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            // What the page loads when level 3 is picked: only that level, still ordered by card id.
            Assert.Equal(2, json.RootElement.GetArrayLength());
            Assert.All(
                json.RootElement.EnumerateArray(),
                entry => Assert.Equal(3, entry.GetProperty("card").GetProperty("level").GetInt32())
            );
            Assert.Equal(
                new[] { 5, 6 },
                json.RootElement.EnumerateArray()
                    .Select(entry => entry.GetProperty("card").GetProperty("id").GetInt32())
            );
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        public async Task Cards_WithALevelOutsideTheCatalogue_Returns400(int level)
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Cards(level);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("between 1 and 10", json.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task CardsSummary_ReturnsTotalsAndTheOwnedLevelsWithCatalogueCounts()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, "someone-else");

            // Owned: level 1 has two distinct cards (one held twice), level 3 one card. The catalogue holds three
            // level-1 cards and three level-3 cards (the extra ones the player lacks), plus an unrelated level-2
            // card, so the summary's totals come from the catalogue rather than from what is owned.
            AddOwnedCard(context, PlayerLogin, cardId: 1, level: 1, quantity: 2);
            AddOwnedCard(context, PlayerLogin, cardId: 2, level: 1, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 5, level: 3, quantity: 1);
            AddOwnedCard(context, "someone-else", cardId: 7, level: 3, quantity: 5);
            AddCatalogCard(context, cardId: 3, level: 1);
            await context.SaveChangesAsync();
            AddCatalogCard(context, cardId: 4, level: 2);
            AddCatalogCard(context, cardId: 6, level: 3);
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.CardsSummary();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal(3, root.GetProperty("distinctCards").GetInt32());
            Assert.Equal(4, root.GetProperty("copiesOwned").GetInt32());

            var levels = root.GetProperty("levels");
            Assert.Equal(2, levels.GetArrayLength()); // level 2 is owned by nobody, so it is not offered
            Assert.Equal(1, levels[0].GetProperty("level").GetInt32());
            Assert.Equal(2, levels[0].GetProperty("ownedCount").GetInt32());
            Assert.Equal(3, levels[0].GetProperty("totalCount").GetInt32());
            Assert.Equal(3, levels[1].GetProperty("level").GetInt32());
            Assert.Equal(1, levels[1].GetProperty("ownedCount").GetInt32());
            Assert.Equal(3, levels[1].GetProperty("totalCount").GetInt32());
        }

        [Fact]
        public async Task CardsSummary_WithNoOwnedCards_ReturnsZeroesAndNoLevels()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.CardsSummary();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(0, json.RootElement.GetProperty("distinctCards").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("copiesOwned").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("levels").GetArrayLength());
        }

        [Fact]
        public async Task CardsSummary_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.CardsSummary();

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task Profile_ReturnsThePublicShapeTheOpponentPanelReads()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, RivalLogin);

            // A record worth reading, an avatar, and two distinct cards (one held twice), so the count has to ignore
            // copies exactly as the self profile's does. The rival also *has* an email, a coin balance and packs:
            // none of that may reach another player's screen.
            var rival = await context.Players.SingleAsync(player => player.Login == RivalLogin);
            rival.Experience = 250;
            rival.Wins = 12;
            rival.Losses = 3;
            rival.Ties = 1;
            rival.AvatarUrl = "avatars/rival.png";
            rival.Coins = 999;
            AddOwnedCard(context, RivalLogin, cardId: 1, level: 1, quantity: 2);
            AddOwnedCard(context, RivalLogin, cardId: 5, level: 3, quantity: 1);
            AddOwnedCard(context, PlayerLogin, cardId: 9, level: 2, quantity: 1);
            context.PlayerPacks.Add(
                new PlayerPack
                {
                    PlayerId = RivalLogin,
                    PackCode = PackService.StandardPackCode,
                    Quantity = 4,
                    FirstAcquiredAt = DateTime.UtcNow,
                    LastAcquiredAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Profile(RivalLogin);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            // Everything the opponent panel draws: the name, the avatar, the raw XP the level is derived from, the
            // record, and how many distinct cards the rival owns (2 — the copy held twice must not inflate it).
            Assert.Equal(RivalLogin, root.GetProperty("login").GetString());
            Assert.Equal("avatars/rival.png", root.GetProperty("avatarUrl").GetString());
            Assert.Equal(250, root.GetProperty("experience").GetInt32());
            Assert.Equal(12, root.GetProperty("wins").GetInt32());
            Assert.Equal(3, root.GetProperty("losses").GetInt32());
            Assert.Equal(1, root.GetProperty("ties").GetInt32());
            Assert.Equal(2, root.GetProperty("cardsOwned").GetInt32());

            // …plus the pair's state from the *caller's* point of view, which is what the panel's friend button draws.
            // These two have never asked each other, so it starts as none (PLAN-022 §3.7).
            Assert.Equal(FriendshipStates.None, root.GetProperty("friendship").GetString());

            // The shape is closed: what this endpoint does not name does not travel — the email, the balance and the
            // pack count stay with their owner. Written as an exact set rather than absence checks so that a field
            // added later has to be a deliberate decision about someone else seeing it, which is exactly how
            // `friendship` arrived here.
            Assert.Equal(
                new[]
                {
                    "avatarUrl",
                    "cardsOwned",
                    "experience",
                    "friendship",
                    "isBot",
                    "login",
                    "losses",
                    "online",
                    "ties",
                    "wins",
                },
                root.EnumerateObject().Select(property => property.Name).OrderBy(name => name)
            );
        }

        [Fact]
        public async Task Profile_ReportsTheFriendshipStateFromTheCallersPointOfView()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, RivalLogin);

            // One pending request, read back from both sides: the player who asked is told `requested`, the player who
            // was asked is told `incoming`. That is what the single canonical row buys, and what the panel's button
            // depends on — the same row means two different things depending on who is looking.
            context.Friendships.Add(
                new Friendship
                {
                    PlayerA = PlayerLogin,
                    PlayerB = RivalLogin,
                    Status = FriendshipStatus.Pending,
                    RequestedBy = PlayerLogin,
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();

            var asCaller = await CreateController(context, PlayerLogin).Profile(RivalLogin);
            var asRival = await CreateController(context, RivalLogin).Profile(PlayerLogin);

            Assert.Equal(FriendshipStates.Requested, ReadFriendship(asCaller));
            Assert.Equal(FriendshipStates.Incoming, ReadFriendship(asRival));
        }

        /// <summary>The `friendship` field of an `Ok` profile answer.</summary>
        private static string? ReadFriendship(ActionResult<object> result)
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            return json.RootElement.GetProperty("friendship").GetString();
        }

        [Theory]
        [InlineData("nobody-here")] // an unknown login
        [InlineData(TestBots.Login)] // the bot sentinel: an identity, never a profile
        public async Task Profile_WithNoSuchPlayer_Returns404(string login)
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Profile(login);

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }

        [Fact]
        public async Task Profile_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.Profile(RivalLogin);

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task Search_FindsPlayersCaseInsensitively_BotsIncluded_SelfExcluded()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin); // argel — the caller, deliberately matching the query
            await SeedPlayerAsync(context, "Argon", avatarUrl: "avatars/argon.png");
            await SeedPlayerAsync(context, "argonaut");
            await SeedPlayerAsync(context, "sparring-bot", isBot: true);
            await SeedPlayerAsync(context, "zebra"); // no match

            var controller = CreateController(context, PlayerLogin);

            // Mixed case on purpose: the query must still match lower-case and upper-case logins alike.
            var result = await controller.Search("AR");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var players = json.RootElement.GetProperty("players").EnumerateArray().ToList();

            Assert.Equal(3, players.Count);
            Assert.DoesNotContain(
                players,
                player => player.GetProperty("login").GetString() == PlayerLogin
            );

            var argon = players.Single(player =>
                player.GetProperty("login").GetString() == "Argon"
            );
            Assert.Equal("avatars/argon.png", argon.GetProperty("avatarUrl").GetString());
            Assert.False(argon.GetProperty("isBot").GetBoolean());

            // A bot is an ordinary row, so it is a hit like anyone.
            var bot = players.Single(player =>
                player.GetProperty("login").GetString() == "sparring-bot"
            );
            Assert.True(bot.GetProperty("isBot").GetBoolean());
        }

        [Fact]
        public async Task Search_WithAnEmptyQuery_IsEmpty()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, "Argon");

            var result = await CreateController(context, PlayerLogin).Search("");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Empty(json.RootElement.GetProperty("players").EnumerateArray());
        }

        [Fact]
        public async Task Search_FromASingleCharacter_FindsMatches()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, "Argon");

            // One character is enough to search (plans/PLAN-026-player-search-and-online-page/plan.md §11).
            var result = await CreateController(context, PlayerLogin).Search("A");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var players = json.RootElement.GetProperty("players").EnumerateArray().ToList();

            Assert.Single(players);
            Assert.Equal("Argon", players[0].GetProperty("login").GetString());
        }

        [Fact]
        public async Task Search_WithoutALogin_Is401()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);

            var result = await CreateController(context, login: null).Search("ar");

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task Online_ForAnOperator_ListsThePlayersConnectedRightNow()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, "argel", avatarUrl: "avatars/argel.png");
            await SeedPlayerAsync(context, "rival");
            await SeedPlayerAsync(context, "offline-guy");

            var presence = new ConnectionPresence();
            presence.AddConnection("c1", "argel");
            presence.AddConnection("c2", "rival");

            var result = await CreateController(context, "argel", presence).Online();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var logins = json
                .RootElement.GetProperty("players")
                .EnumerateArray()
                .Select(player => player.GetProperty("login").GetString())
                .ToList();

            // Ordinally ordered, and nobody offline — the third player holds no connection.
            Assert.Equal(new[] { "argel", "rival" }, logins);
        }

        [Fact]
        public async Task Online_ForANonOperator_Is403()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, "rival");

            var presence = new ConnectionPresence();
            presence.AddConnection("c1", "rival");

            var result = await CreateController(context, "rival", presence).Online();

            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(403, status.StatusCode);
        }

        [Fact]
        public async Task Online_WithoutALogin_Is401()
        {
            using var context = CreateContext();

            var result = await CreateController(context, login: null).Online();

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task Me_ReportsTheCardAndPackCounts()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, PlayerLogin);
            await SeedPlayerAsync(context, "someone-else");

            // Two distinct cards owned, one of them twice (copies must not inflate the count), plus a foreign row.
            AddOwnedCard(context, PlayerLogin, cardId: 1, level: 1, quantity: 2);
            AddOwnedCard(context, PlayerLogin, cardId: 5, level: 3, quantity: 1);
            AddOwnedCard(context, "someone-else", cardId: 7, level: 3, quantity: 4);
            context.PlayerPacks.Add(
                new PlayerPack
                {
                    PlayerId = PlayerLogin,
                    PackCode = PackService.StandardPackCode,
                    Quantity = 3,
                    FirstAcquiredAt = DateTime.UtcNow,
                    LastAcquiredAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();

            var controller = CreateController(context, PlayerLogin);

            var result = await controller.Me();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            // The two figures the stats pills read: distinct cards (not copies) and unopened packs.
            Assert.Equal(2, root.GetProperty("cardsOwned").GetInt32());
            Assert.Equal(3, root.GetProperty("packsOwned").GetInt32());

            // The rest of the profile is unchanged.
            Assert.Equal(PlayerLogin, root.GetProperty("login").GetString());
            Assert.True(root.TryGetProperty("coins", out _));
            Assert.True(root.TryGetProperty("avatarUrl", out _));
        }

        [Fact]
        public async Task Register_GrantsSixStartingPacksAndNoCards()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.Register(
                new RegisterPlayerRequest
                {
                    Login = "newcomer",
                    Email = "newcomer@example.com",
                    Password = "password1",
                }
            );

            var created = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(201, created.StatusCode);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(created.Value));
            var root = json.RootElement;

            // The profile the client receives already carries the starting inventory, so no screen has to guess it.
            Assert.Equal("newcomer", root.GetProperty("login").GetString());
            Assert.Equal(0, root.GetProperty("coins").GetInt32());
            Assert.Equal(0, root.GetProperty("cardsOwned").GetInt32());
            Assert.Equal(6, PackService.StartingPacks);
            Assert.Equal(PackService.StartingPacks, root.GetProperty("packsOwned").GetInt32());

            // One stack of six, and not a single card: the cards are drawn when each pack is opened.
            var stack = await context.PlayerPacks.SingleAsync();
            Assert.Equal("newcomer", stack.PlayerId);
            Assert.Equal(PackService.StandardPackCode, stack.PackCode);
            Assert.Equal(PackService.StartingPacks, stack.Quantity);
            Assert.Empty(context.PlayerCards);
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        /// <summary>The controller under test, with the authenticated login (or none) in its HttpContext.</summary>
        private static PlayerController CreateController(
            TripleTriadContext context,
            string? login,
            IPlayerPresence? presence = null
        )
        {
            var controller = new PlayerController(
                new PlayerRepository(context),
                new PlayerCardRepository(context),
                new PlayerPackRepository(context),
                new GameRepository(context),
                new PasswordHasherService(),
                new RegisterPlayerRequestValidator(),
                new ResetPasswordRequestValidator(),
                new TokenService(),
                PasswordRecoveryTestHarness.CreateService(context, new RecordingEmailSender()),
                CreateFriendService(context),
                presence ?? new ConnectionPresence()
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
        /// The friend service the profile action now needs, wired over the same in-memory context. The notifier is the
        /// recording one: the profile read never pushes, and if it ever started to, the assertion here would see it.
        /// </summary>
        private static FriendService CreateFriendService(TripleTriadContext context) =>
            FriendshipTestHarness.CreateFriendService(context);

        private static async Task SeedPlayerAsync(
            TripleTriadContext context,
            string login,
            string? avatarUrl = null,
            bool isBot = false
        )
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = $"{login}@example.com",
                    PasswordHash = "hash",
                    Coins = 0,
                    AvatarUrl = avatarUrl,
                    IsBot = isBot,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>Adds a catalogue card with no ownership (used for the per-level totals).</summary>
        private static void AddCatalogCard(TripleTriadContext context, int cardId, int level)
        {
            context.Cards.Add(
                new Card
                {
                    Id = cardId,
                    Name = $"Card {cardId}",
                    Image = $"ff8-deck/card-{cardId}.jpg",
                    TopValue = level,
                    RightValue = level,
                    BottomValue = level,
                    LeftValue = level,
                    Element = [],
                    Level = level,
                }
            );
        }

        /// <summary>Files one owned card, adding its catalogue row too (the repository includes it).</summary>
        private static void AddOwnedCard(
            TripleTriadContext context,
            string login,
            int cardId,
            int level,
            int quantity
        )
        {
            AddCatalogCard(context, cardId, level);

            context.PlayerCards.Add(
                new PlayerCard
                {
                    PlayerId = login,
                    CardId = cardId,
                    Quantity = quantity,
                    FirstAcquiredAt = DateTime.UtcNow.AddDays(-1),
                    LastAcquiredAt = DateTime.UtcNow,
                }
            );
        }
    }
}
