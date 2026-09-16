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
    /// Tests for the collection endpoint the My Cards page reads (`GET api/player/cards`): the JSON shape that
    /// page depends on, the level ordering it groups by, that only the caller's cards come back, the empty
    /// collection, and the unauthenticated case.
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

        /// <summary>Files one owned card, adding its catalogue row too (the repository includes it).</summary>
        private static void AddOwnedCard(
            TripleTriadContext context,
            string login,
            int cardId,
            int level,
            int quantity
        )
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
