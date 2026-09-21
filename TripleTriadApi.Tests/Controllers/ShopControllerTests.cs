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
    /// Tests for the shop endpoints' wire contract: the JSON the Card Shop page reads from the offer and from a
    /// purchase, plus the 400/401 shapes. The controller is exercised directly with a hand-built
    /// <see cref="HttpContext"/>, so no web host is needed, and the service behind it is the real one over an EF
    /// InMemory database.
    /// </summary>
    public class ShopControllerTests
    {
        private const string PlayerLogin = "argel";

        [Fact]
        public async Task GetPack_ReturnsTheOfferWithEveryLevel()
        {
            using var context = CreateContext();
            await SeedAsync(context, coins: 4000);
            var controller = CreateController(context, PlayerLogin);
            await controller.PurchasePack();

            var result = await controller.GetPack();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal(1500, root.GetProperty("price").GetInt32());
            Assert.Equal(5, root.GetProperty("cardCount").GetInt32());

            // The offer reports what the caller already holds, so the shop page can say so without a second call.
            Assert.Equal(1, root.GetProperty("packsOwned").GetInt32());

            var odds = root.GetProperty("levelOdds");
            Assert.Equal(10, odds.GetArrayLength());
            Assert.Equal(1, odds[0].GetProperty("level").GetInt32());
            Assert.Equal(580, odds[0].GetProperty("weight").GetInt32());
            Assert.Equal(11.84, odds[0].GetProperty("chancePercent").GetDouble());
            Assert.Equal(10, odds[9].GetProperty("level").GetInt32());
            Assert.Equal(400, odds[9].GetProperty("weight").GetInt32());
        }

        [Fact]
        public async Task PurchasePack_WithEnoughCoins_ReturnsTheNewBalanceAndPackCount()
        {
            using var context = CreateContext();
            await SeedAsync(context, coins: 2000);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.PurchasePack();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(PackService.PackPrice, root.GetProperty("price").GetInt32());
            Assert.Equal(500, root.GetProperty("coinsAfter").GetInt32());
            Assert.Equal(1, root.GetProperty("packsOwned").GetInt32());

            // The purchase hands out a pack, not cards: no `cards` field, nothing filed, one pack in the inventory.
            Assert.False(root.TryGetProperty("cards", out _));
            Assert.Empty(context.PlayerCards);

            var stack = await context.PlayerPacks.SingleAsync();
            Assert.Equal(PlayerLogin, stack.PlayerId);
            Assert.Equal(PackService.StandardPackCode, stack.PackCode);
            Assert.Equal(1, stack.Quantity);
        }

        [Fact]
        public async Task OpenPack_ReturnsTheFiveCardsInTheShapeTheRevealReads()
        {
            using var context = CreateContext();
            await SeedAsync(context, coins: 2000);
            var controller = CreateController(context, PlayerLogin);
            await controller.PurchasePack();

            var result = await controller.OpenPack(new OpenPackRequest());

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(PackService.StandardPackCode, root.GetProperty("packCode").GetString());
            Assert.Equal(0, root.GetProperty("packsOwned").GetInt32());

            var cards = root.GetProperty("cards");
            Assert.Equal(PackService.CardsPerPack, cards.GetArrayLength());
            Assert.All(
                cards.EnumerateArray(),
                card =>
                {
                    Assert.True(card.GetProperty("id").GetInt32() > 0);
                    Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("name").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("image").GetString()));
                    Assert.InRange(card.GetProperty("level").GetInt32(), 1, 10);
                    Assert.True(card.GetProperty("quantityOwned").GetInt32() >= 1);
                    Assert.True(card.TryGetProperty("isNew", out _));
                }
            );

            // The pack is consumed and the cards are filed: one row per distinct card of the pack.
            Assert.Empty(context.PlayerPacks);

            var distinctDrawnCards = cards
                .EnumerateArray()
                .Select(card => card.GetProperty("id").GetInt32())
                .Distinct()
                .Count();
            Assert.Equal(distinctDrawnCards, await context.PlayerCards.CountAsync());
        }

        [Fact]
        public async Task OpenPack_WithNoPacks_Returns400()
        {
            using var context = CreateContext();
            await SeedAsync(context, coins: 2000);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.OpenPack(new OpenPackRequest());

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("pack", json.RootElement.GetProperty("error").GetString());
            Assert.Empty(context.PlayerCards);
        }

        [Fact]
        public async Task OpenPack_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.OpenPack(new OpenPackRequest());

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task GetInventory_ListsThePacksThePlayerHolds()
        {
            using var context = CreateContext();
            await SeedAsync(context, coins: 4000);
            var controller = CreateController(context, PlayerLogin);
            await controller.PurchasePack();
            await controller.PurchasePack();

            var result = await controller.GetInventory();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var entry = Assert.Single(json.RootElement.EnumerateArray());

            Assert.Equal(PackService.StandardPackCode, entry.GetProperty("code").GetString());
            Assert.Equal(PackService.StandardPackName, entry.GetProperty("name").GetString());
            Assert.Equal(PackService.CardsPerPack, entry.GetProperty("cardCount").GetInt32());
            Assert.Equal(2, entry.GetProperty("quantity").GetInt32());
        }

        [Fact]
        public async Task GetInventory_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.GetInventory();

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        [Fact]
        public async Task PurchasePack_WithoutEnoughCoins_Returns400AndWritesNothing()
        {
            using var context = CreateContext();
            await SeedAsync(context, coins: 100);
            var controller = CreateController(context, PlayerLogin);

            var result = await controller.PurchasePack();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
            Assert.Contains("1500", json.RootElement.GetProperty("error").GetString());
            Assert.Empty(context.PlayerCards);
            Assert.Empty(context.PlayerPacks);
            Assert.Equal(100, (await context.Players.SingleAsync()).Coins);
        }

        [Fact]
        public async Task PurchasePack_WithoutALogin_Returns401()
        {
            using var context = CreateContext();
            var controller = CreateController(context, login: null);

            var result = await controller.PurchasePack();

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        /// <summary>The controller under test, with the authenticated login (or none) in its HttpContext.</summary>
        private static ShopController CreateController(TripleTriadContext context, string? login)
        {
            var controller = new ShopController(
                new PackService(
                    new GameRepository(context),
                    new PlayerRepository(context),
                    new PlayerCardRepository(context),
                    new PlayerPackRepository(context),
                    new SystemRandomSource()
                )
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

        /// <summary>One player and one card of every level.</summary>
        private static async Task SeedAsync(TripleTriadContext context, int coins)
        {
            context.Players.Add(
                new Player
                {
                    Login = PlayerLogin,
                    Email = "argel@example.com",
                    PasswordHash = "hash",
                    Coins = coins,
                }
            );

            for (var level = PackService.MinCardLevel; level <= PackService.MaxCardLevel; level++)
            {
                context.Cards.Add(
                    new Card
                    {
                        Id = level,
                        Name = $"Card {level}",
                        Image = $"ff8-deck/card-{level}.jpg",
                        TopValue = level,
                        RightValue = level,
                        BottomValue = level,
                        LeftValue = level,
                        Element = [],
                        Level = level,
                    }
                );
            }

            await context.SaveChangesAsync();
        }
    }
}
