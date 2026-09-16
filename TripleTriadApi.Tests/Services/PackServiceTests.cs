using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for the card shop: the level weights and the odds table, the level-first pack draw, and the purchase
    /// flow (charging coins, filing the cards and reporting what the pack did to the collection).
    ///
    /// The draw is driven by a scripted <see cref="IRandomSource"/>, and the repositories are the real ones over
    /// an EF InMemory context, so the collection increments and the coin guard run exactly as they do in the app.
    /// Two cards are seeded per level with ascending ids, so level L owns the ids <c>2L-1</c> and <c>2L</c>.
    /// </summary>
    public class PackServiceTests
    {
        private const string PlayerLogin = "argel";

        [Fact]
        public void GetOffer_MatchesTheRequestedLevelWeights()
        {
            using var context = CreateContext();
            var service = CreateService(context, new ScriptedRandom(0));

            var offer = service.GetOffer();

            Assert.Equal(PackService.PackPrice, offer.Price);
            Assert.Equal(1500, offer.Price);
            Assert.Equal(PackService.CardsPerPack, offer.CardCount);
            Assert.Equal(5, offer.CardCount);

            Assert.Equal(4900, PackService.TotalLevelWeight);
            Assert.Equal(10, offer.LevelOdds.Count);
            Assert.Equal(PackService.TotalLevelWeight, offer.LevelOdds.Sum(odds => odds.Weight));
            Assert.InRange(offer.LevelOdds.Sum(odds => odds.ChancePercent), 99.95, 100.05);

            for (var index = 0; index < offer.LevelOdds.Count; index++)
            {
                var level = PackService.MinCardLevel + index;
                var odds = offer.LevelOdds[index];

                Assert.Equal(level, odds.Level);
                Assert.Equal(600 - 20 * level, odds.Weight);
                Assert.Equal(Math.Round((600 - 20 * level) * 100.0 / 4900, 2), odds.ChancePercent);
            }

            // The ends of the table, i.e. the requested formula as percentages.
            Assert.Equal(11.84, offer.LevelOdds[0].ChancePercent);
            Assert.Equal(8.16, offer.LevelOdds[9].ChancePercent);
        }

        [Fact]
        public async Task PurchasePack_DrawsTheLevelTheWeightPointsAt()
        {
            // The level picks sit on either side of the cumulative boundaries: the first level owns 0..579, the
            // second 580..1139, level 9 ends at 4499 and level 10 owns 4500..4899.
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(
                context,
                new ScriptedRandom(Script([(0, 0), (579, 0), (580, 0), (4499, 0), (4899, 0)]))
            );

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.True(result.Succeeded);
            Assert.Equal(
                new[] { 1, 1, 2, 9, 10 },
                result.Cards.Select(packCard => packCard.Card.Level)
            );
        }

        [Fact]
        public async Task PurchasePack_PicksTheFirstCardOfTheLevel_WhenTheIndexIsZero()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(4899, 0))));

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.All(result.Cards, packCard => Assert.Equal(10, packCard.Card.Level));
            Assert.All(result.Cards, packCard => Assert.Equal(19, packCard.Card.Id));
        }

        [Fact]
        public async Task PurchasePack_AllowsDuplicatesInsideOnePack()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(4899, 0))));

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.Equal(5, result.Cards.Count);
            Assert.All(result.Cards, packCard => Assert.Equal(19, packCard.Card.Id));
            Assert.All(result.Cards, packCard => Assert.True(packCard.IsNew));

            // The same card five times is one row with quantity 5, reported as a running total.
            Assert.Equal(
                new[] { 1, 2, 3, 4, 5 },
                result.Cards.Select(packCard => packCard.QuantityOwned)
            );

            var stored = await context.PlayerCards.SingleAsync();
            Assert.Equal(PlayerLogin, stored.PlayerId);
            Assert.Equal(19, stored.CardId);
            Assert.Equal(5, stored.Quantity);
        }

        [Fact]
        public async Task PurchasePack_IncrementsAnAlreadyOwnedCard()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 3000);
            context.PlayerCards.Add(CreateOwnedCard(cardId: 19, quantity: 2));
            await context.SaveChangesAsync();

            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(4899, 0))));

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.False(result.Cards[0].IsNew);
            Assert.Equal(
                new[] { 3, 4, 5, 6, 7 },
                result.Cards.Select(packCard => packCard.QuantityOwned)
            );

            var stored = await context.PlayerCards.SingleAsync(playerCard =>
                playerCard.CardId == 19
            );
            Assert.Equal(7, stored.Quantity);
        }

        [Fact]
        public async Task PurchasePack_ChargesExactlyThePackPrice()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(0, 0))));

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.True(result.Succeeded);
            Assert.Equal(2000 - PackService.PackPrice, result.CoinsAfter);
            Assert.Equal(500, result.CoinsAfter);
            Assert.Equal(500, (await context.Players.SingleAsync()).Coins);
        }

        [Fact]
        public async Task PurchasePack_WithoutEnoughCoins_FailsAndWritesNothing()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: PackService.PackPrice - 1);
            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(0, 0))));

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.False(result.Succeeded);
            Assert.Contains(PackService.PackPrice.ToString(), result.ErrorMessage);
            Assert.Empty(result.Cards);
            Assert.Empty(context.PlayerCards);
            Assert.Equal(PackService.PackPrice - 1, (await context.Players.SingleAsync()).Coins);
        }

        [Fact]
        public async Task PurchasePack_ForAnUnknownPlayer_Fails()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(0, 0))));

            var result = await service.PurchaseAsync("nobody");

            Assert.False(result.Succeeded);
            Assert.Equal("Player not found", result.ErrorMessage);
            Assert.Empty(context.PlayerCards);
        }

        [Fact]
        public async Task PurchasePack_WithAnEmptyCatalogue_FailsWithoutCharging()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(context, new ScriptedRandom(Script(FiveDrawsAt(0, 0))));

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.False(result.Succeeded);
            Assert.Empty(result.Cards);
            Assert.Empty(context.PlayerCards);
            Assert.Equal(2000, (await context.Players.SingleAsync()).Coins);
        }

        [Fact]
        public async Task PurchasePack_RenormalisesOverTheLevelsThatExist()
        {
            // Without level 5 the weights still add up to 580+560+540+520 + 480+460+440+420+400 = 4400, so the top
            // of the range still reaches level 10 and level 5 can never come back.
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue([1, 2, 3, 4, 6, 7, 8, 9, 10]));
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(
                context,
                new ScriptedRandom(Script([(4399, 0), (2200, 0), (4399, 0), (2200, 0), (4399, 0)]))
            );

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.True(result.Succeeded);
            Assert.Equal(
                new[] { 10, 6, 10, 6, 10 },
                result.Cards.Select(packCard => packCard.Card.Level)
            );
            Assert.DoesNotContain(result.Cards, packCard => packCard.Card.Level == 5);
        }

        [Fact]
        public async Task PurchasePack_FilesEachDistinctDrawnCard()
        {
            // Three copies of card 1 and two of card 2 (every draw stays inside level 1).
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 2000);
            var service = CreateService(
                context,
                new ScriptedRandom(Script([(0, 0), (0, 0), (0, 1), (579, 1), (0, 0)]))
            );

            var result = await service.PurchaseAsync(PlayerLogin);

            Assert.Equal(5, result.Cards.Count);
            Assert.Equal(
                new[] { 1, 1, 2, 2, 1 },
                result.Cards.Select(packCard => packCard.Card.Id)
            );

            var stored = await context
                .PlayerCards.OrderBy(playerCard => playerCard.CardId)
                .ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.Equal(1, stored[0].CardId);
            Assert.Equal(3, stored[0].Quantity);
            Assert.Equal(2, stored[1].CardId);
            Assert.Equal(2, stored[1].Quantity);
        }

        [Fact]
        public async Task GetForPlayerAsync_ReturnsOnlyThatPlayersCards_OrderedByLevel()
        {
            using var context = CreateContext();
            await SeedCatalogueAsync(context, CreateCatalogue(Levels()));
            await SeedPlayerAsync(context, coins: 0);

            context.PlayerCards.Add(CreateOwnedCard(cardId: 19, quantity: 1)); // level 10
            context.PlayerCards.Add(CreateOwnedCard(cardId: 1, quantity: 2)); // level 1
            context.PlayerCards.Add(CreateOwnedCard(cardId: 3, quantity: 1)); // level 2
            context.PlayerCards.Add(
                new PlayerCard
                {
                    PlayerId = "someone-else",
                    CardId = 5,
                    Quantity = 1,
                    FirstAcquiredAt = DateTime.UtcNow,
                    LastAcquiredAt = DateTime.UtcNow,
                }
            );
            await context.SaveChangesAsync();

            var owned = await new PlayerCardRepository(context).GetForPlayerAsync(PlayerLogin);

            Assert.Equal(new[] { 1, 3, 19 }, owned.Select(playerCard => playerCard.CardId));
            Assert.All(owned, playerCard => Assert.NotNull(playerCard.Card));
        }

        /// <summary>Hands out scripted rng values in order, asserting each one is inside the requested range.</summary>
        private sealed class ScriptedRandom(params int[] values) : IRandomSource
        {
            private readonly Queue<int> _values = new(values);

            public int Next(int exclusiveMax)
            {
                Assert.NotEmpty(_values);

                var value = _values.Dequeue();
                Assert.InRange(value, 0, exclusiveMax - 1);

                return value;
            }
        }

        /// <summary>One in-memory database per test, so nothing leaks between them.</summary>
        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        /// <summary>The service under test, wired to the real repositories over the given context.</summary>
        private static PackService CreateService(
            TripleTriadContext context,
            IRandomSource random
        ) =>
            new(
                new GameRepository(context),
                new PlayerRepository(context),
                new PlayerCardRepository(context),
                random
            );

        private static async Task SeedCatalogueAsync(
            TripleTriadContext context,
            IEnumerable<Card> catalogue
        )
        {
            context.Cards.AddRange(catalogue);
            await context.SaveChangesAsync();
        }

        private static async Task SeedPlayerAsync(TripleTriadContext context, int coins)
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
            await context.SaveChangesAsync();
        }

        /// <summary>Two cards per listed level, ids ascending (level L owns the ids 2L-1 and 2L).</summary>
        private static List<Card> CreateCatalogue(params int[] levels)
        {
            var cards = new List<Card>();

            foreach (var level in levels)
            {
                cards.Add(CreateCard(cards.Count + 1, level));
                cards.Add(CreateCard(cards.Count + 1, level));
            }

            return cards;
        }

        private static int[] Levels() =>
            [
                .. Enumerable.Range(
                    PackService.MinCardLevel,
                    PackService.MaxCardLevel - PackService.MinCardLevel + 1
                ),
            ];

        private static Card CreateCard(int id, int level) =>
            new()
            {
                Id = id,
                Name = $"Card {id}",
                Image = $"ff8-deck/card-{id}.jpg",
                TopValue = level,
                RightValue = level,
                BottomValue = level,
                LeftValue = level,
                Element = [],
                Level = level,
            };

        private static PlayerCard CreateOwnedCard(int cardId, int quantity) =>
            new()
            {
                PlayerId = PlayerLogin,
                CardId = cardId,
                Quantity = quantity,
                FirstAcquiredAt = DateTime.UtcNow.AddDays(-1),
                LastAcquiredAt = DateTime.UtcNow.AddDays(-1),
            };

        /// <summary>Flattens "level pick + card index" pairs into the rng sequence one pack consumes.</summary>
        private static int[] Script((int LevelPick, int CardIndex)[] draws) =>
            [.. draws.SelectMany(draw => new[] { draw.LevelPick, draw.CardIndex })];

        /// <summary>The same draw repeated for a whole pack.</summary>
        private static (int LevelPick, int CardIndex)[] FiveDrawsAt(int levelPick, int cardIndex) =>
            [.. Enumerable.Repeat((levelPick, cardIndex), PackService.CardsPerPack)];
    }
}
