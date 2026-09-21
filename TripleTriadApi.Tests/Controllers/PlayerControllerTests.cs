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
using TripleTriadApi.Validators;

namespace TripleTriadApi.Tests.Controllers
{
    /// <summary>
    /// Tests for the player endpoints the client reads: `register` (which hands a brand-new account its starting
    /// inventory of 0 cards and six packs) and the collection the My Cards page reads
    /// (`GET api/player/cards`) — the JSON shapes those screens depend on, the level ordering the collection groups
    /// by, that only the caller's cards come back, the empty collection, and the unauthenticated cases.
    ///
    /// The controller is exercised directly with a hand-built <see cref="HttpContext"/> (same pattern as
    /// <c>ShopControllerTests</c>) over an EF InMemory database, so no web host is needed.
    /// </summary>
    public class PlayerControllerTests
    {
        private const string PlayerLogin = "argel";

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
        private static PlayerController CreateController(TripleTriadContext context, string? login)
        {
            var controller = new PlayerController(
                new PlayerRepository(context),
                new PlayerCardRepository(context),
                new PlayerPackRepository(context),
                new GameRepository(context),
                new PasswordHasherService(),
                new RegisterPlayerRequestValidator(),
                new TokenService()
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

        private static async Task SeedPlayerAsync(TripleTriadContext context, string login)
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = $"{login}@example.com",
                    PasswordHash = "hash",
                    Coins = 0,
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
